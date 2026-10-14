param(
    [Parameter(Mandatory = $true)][string]$TargetAppData,
    [string]$SnapshotPath = (Join-Path $PSScriptRoot 'SeedData')
)

function Expand-SeedAttachment([string]$Source, [string]$Destination, [long]$ExpectedBytes) {
    $sourceStream = [IO.File]::OpenRead($Source)
    try {
        $compressed = [IO.Compression.GZipStream]::new($sourceStream, [IO.Compression.CompressionMode]::Decompress)
        try {
            $output = [IO.File]::Open($Destination, 'CreateNew', 'Write', 'None')
            try {
                $buffer = [byte[]]::new(81920)
                $written = [long]0
                while (($read = $compressed.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $read
                    if ($written -gt $ExpectedBytes) { throw 'Expanded attachment exceeds its declared size.' }
                    $output.Write($buffer, 0, $read)
                }
            } finally { $output.Dispose() }
        } finally { $compressed.Dispose() }
    } finally { $sourceStream.Dispose() }
}

$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($TargetAppData).TrimEnd('\', '/')
$snapshot = [IO.Path]::GetFullPath($SnapshotPath).TrimEnd('\', '/')
$parent = Split-Path -Parent $target
if (-not $parent -or $target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\', '/')) {
    throw 'TargetAppData must be a dedicated application data directory.'
}
if ($target.Equals($snapshot, [StringComparison]::OrdinalIgnoreCase) -or
    $snapshot.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The restore target must not contain the source snapshot.'
}
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$lockPath = Join-Path $parent ((Split-Path -Leaf $target) + '.seed.lock')
$restoreLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
$stage = $null
try {
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Container)) {
            throw 'TargetAppData exists as a file.'
        }
        if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'A linked application data directory cannot be initialized.'
        }
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
            Write-Output 'Gateway data skipped: existing App_Data is preserved.'
            return
        }
    }
    $manifestPath = Join-Path $snapshot 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.formatVersion -notin 1, 2) { throw 'Unsupported gateway snapshot format.' }
    $entries = @($manifest.files | Where-Object { $_.path.StartsWith('App_Data/', [StringComparison]::Ordinal) })
    if ($entries.Count -eq 0) { throw 'The snapshot contains no gateway application data.' }
    $sourceRoot = Join-Path $snapshot 'App_Data'
    $sourcePrefix = [IO.Path]::GetFullPath($sourceRoot) + [IO.Path]::DirectorySeparatorChar
    $destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $source = [IO.Path]::GetFullPath((Join-Path $snapshot $entry.path))
        if (-not $source.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Snapshot file path escapes App_Data.'
        }
        $restored = $source
        if ($entry.compression) {
            if ($manifest.formatVersion -ne 2 -or $entry.compression -ne 'gzip' -or
                $entry.path -cnotmatch '^App_Data/evaluation-attachments/[0-9a-f]{32}\.bin\.gz$' -or
                $entry.uncompressedBytes -notmatch '^\d+$' -or
                [long]$entry.uncompressedBytes -gt 200MB -or
                $entry.uncompressedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
                throw "Invalid compressed attachment manifest: $($entry.path)"
            }
            $restored = $source.Substring(0, $source.Length - 3)
        }
        if (-not $destinations.Add($restored)) { throw "Duplicate restore destination: $($entry.path)" }
        $file = Get-Item -LiteralPath $source -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Snapshot entry is not a regular file: $($entry.path)"
        }
        if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Snapshot integrity check failed: $($entry.path)"
        }
    }
    $stagePrefix = Join-Path $parent '.gateway-seed-'
    $stage = [IO.Path]::GetFullPath($stagePrefix + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($entry in $entries) {
        $relative = $entry.path.Substring('App_Data/'.Length)
        if ($entry.compression) { $relative = $relative.Substring(0, $relative.Length - 3) }
        $destination = [IO.Path]::GetFullPath((Join-Path $stage $relative))
        if (-not $destination.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Restore destination escapes the staging directory.'
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        $expectedBytes, $expectedHash = $entry.bytes, $entry.sha256
        if ($entry.compression) {
            $expectedBytes, $expectedHash = $entry.uncompressedBytes, $entry.uncompressedSha256
            Expand-SeedAttachment (Join-Path $snapshot $entry.path) $destination $expectedBytes
        } else {
            [IO.File]::Copy((Join-Path $snapshot $entry.path), $destination, $false)
        }
        if ((Get-Item -LiteralPath $destination).Length -ne $expectedBytes -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expectedHash) {
            throw "Copied file integrity check failed: $relative"
        }
    }
    if (Test-Path -LiteralPath $target) {
        # Nonrecursive deletion fails if another writer has added any files.
        [IO.Directory]::Delete($target, $false)
    }
    [IO.Directory]::Move($stage, $target)
    $stage = $null
    Write-Output "Gateway data restored: $($entries.Count) files into $target"
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if (-not $resolvedStage.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside the restore staging directory.'
        }
        [IO.Directory]::Delete($resolvedStage, $true)
    }
    $restoreLock.Dispose()
}
