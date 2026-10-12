namespace IndustrialIoT.Protocols.FileTransfer;

using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

public sealed partial class NfsTransferDriver : IVerificationFileTransfer
{
    private string? _verificationHost;
    private string? _expectedVerificationMount;

    public static string CaptureVerificationMount(DeviceConnectionConfig config)
    {
        var mount = config.ExtendedProperties.GetValueOrDefault("MountPoint");
        if (string.IsNullOrWhiteSpace(mount))
            throw new IOException("NFS verification requires MountPoint on the Host service machine.");
        var root = ResolveWithinRoot(Path.GetFullPath(mount),
            config.ExtendedProperties.GetValueOrDefault("RootRelativePath") ?? "");
        if (!Directory.Exists(root)) throw new IOException("The configured NFS root does not exist.");
        return NfsMountIdentity.Read(root, config.Host).Fingerprint;
    }

    public void BindVerificationMount(string fingerprint) => _expectedVerificationMount = fingerprint;

    public Task<IVerificationFileLease> CreateVerificationFileAsync(
        string directory, string fileName, CancellationToken ct)
    {
        VerificationTransferPolicy.Validate(directory, fileName);
        var target = ResolvePath(directory, fileName);
        return Task.Run<IVerificationFileLease>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var root = NfsMountIdentity.Read(_rootDir!, _verificationHost!);
            if (_expectedVerificationMount is null || root.Fingerprint != _expectedVerificationMount)
                throw new IOException("NFS mount identity changed or was not bound to this verification snapshot.");
            var parent = NfsMountIdentity.Read(Path.GetDirectoryName(target)!, _verificationHost!);
            if (parent.MountKey != root.MountKey)
                throw new IOException("Verification directory is on a different filesystem or NFS mount.");
            var options = FileOptions.Asynchronous | FileOptions.WriteThrough;
            if (OperatingSystem.IsWindows()) options |= FileOptions.DeleteOnClose;
            var stream = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                BufferSize, options);
            try
            {
                var identity = NfsMountIdentity.ReadHandle(stream.SafeFileHandle, target, _verificationHost!);
                var expectedPath = parent.ResolvedPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar + fileName;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (identity.MountKey != root.MountKey || !identity.ResolvedPath.Equals(expectedPath, comparison))
                    throw new IOException("NFS verification file was created outside the captured filesystem identity.");
                // No cancellation check after acquisition: the caller must receive its cleanup lease.
                return new NfsVerificationLease(stream, target,
                    () => NfsMountIdentity.Read(_rootDir!, _verificationHost!).Fingerprint == _expectedVerificationMount);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                try { stream.Dispose(); }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                { _logger.LogWarning("NFS candidate handle close failed at {Path}: {ErrorType}", target, cleanupError.GetType().Name); }
                _logger.LogWarning("NFS verification file identity was not confirmed at {Path}; no path-based cleanup attempted", target);
                throw new NfsVerificationFileAcquisitionException(target, error);
            }
        }, ct);
    }

    internal sealed class NfsVerificationLease(FileStream stream, string path,
        Func<bool> mountUnchanged) : IVerificationFileLease
    {
        private bool _attempted;
        private bool _uploaded;
        private bool _closed;
        private bool _cleaned;
        public string RemotePath => path;

        public async Task<long> UploadAsync(Stream source, long length, CancellationToken ct)
        {
            if (_attempted || _closed) throw new InvalidOperationException("A verification lease allows one upload.");
            ct.ThrowIfCancellationRequested();
            if (!source.CanSeek || source.Position != 0 || source.Length != length ||
                length is <= 0 or > VerificationTransferPolicy.MaximumFileBytes)
                throw new ArgumentException("Verification source length or position is invalid.");
            if (!mountUnchanged()) throw new IOException("NFS mount changed before verification upload.");
            _attempted = true;
            await source.CopyToAsync(stream, BufferSize, ct);
            await stream.FlushAsync(ct);
            await Task.Run(() => stream.Flush(flushToDisk: true), ct);
            ct.ThrowIfCancellationRequested();
            if (stream.Length != length) throw new IOException("NFS verification upload length is inconsistent.");
            _uploaded = true;
            return stream.Length;
        }

        public async Task DownloadAsync(Stream destination, CancellationToken ct)
        {
            if (!_uploaded || _closed) throw new InvalidOperationException("No completed verification upload is available.");
            if (!mountUnchanged()) throw new IOException("NFS mount changed before verification read-back.");
            await NfsDirectReader.CopyToAsync(stream, destination, ct);
            ct.ThrowIfCancellationRequested();
        }

        public Task CleanupAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (_closed)
            {
                if (!_cleaned) throw new IOException("NFS verification cleanup was not confirmed; retained path: " + path);
                return;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows())
                {
                    // POSIX cannot unlink the held file handle without a path replacement race.
                    throw new IOException("NFS verification file retained: this platform has no owned-handle deletion; path: " + path);
                }
            }
            finally
            {
                stream.Dispose(); // Windows DeleteOnClose targets the owned kernel file handle.
                _closed = true;
            }
            _cleaned = !File.Exists(path);
            if (!_cleaned) throw new IOException("NFS owned verification file removal was not confirmed.");
        });
    }
}

public sealed class NfsVerificationFileAcquisitionException(string candidatePath, Exception innerException)
    : IOException("NFS verification file identity and cleanup were not confirmed; candidate path: " + candidatePath, innerException)
{
    public string CandidatePath { get; } = candidatePath;
}
