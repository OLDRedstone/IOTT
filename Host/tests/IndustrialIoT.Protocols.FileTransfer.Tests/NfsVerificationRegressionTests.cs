namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using IndustrialIoT.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

internal static class NfsVerificationRegressionTests
{
    public static async Task RunAll()
    {
        await Regression.Run("NFS ordinary local directory never qualifies as a verification mount", LocalDirectory);
        await Regression.Run("NFS mount parser binds filesystem, server, mount ID and inode", MountIdentity);
        await Regression.Run("NFS local lease fixture round trip and platform cleanup (not network evidence)", LeaseRoundTrip);
        await Regression.Run("NFS lease fixture rejects mount change before writing", ChangedMount);
        await Regression.Run("NFS cancelled lease uses an independent cleanup token", CancelledLease);
        if (OperatingSystem.IsLinux())
            await Regression.Run("NFS Linux cleanup retains both owned file and replacement", ReplacedPath);
    }

    private static Task LocalDirectory()
    {
        using var fixture = new Fixture();
        var config = new DeviceConnectionConfig
        {
            Host = "fixture.invalid", Port = 2049, ExtendedProperties = new() { ["MountPoint"] = fixture.Root },
        };
        try
        {
            NfsTransferDriver.CaptureVerificationMount(config);
            throw new InvalidOperationException("A local directory was reported as verified NFS.");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
        Regression.Require(Directory.GetFileSystemEntries(fixture.Root).Length == 0, "Identity probe wrote files.");
        return Task.CompletedTask;
    }

    private static Task MountIdentity()
    {
        var mounted = NfsMountIdentity.ParseLinux(
            ["42 1 0:51 / /mnt/cnc rw - nfs4 cnc:/exports/programs rw"], "42", "500", "/mnt/cnc/programs", "cnc");
        var replacement = NfsMountIdentity.ParseLinux(
            ["43 1 0:52 / /mnt/cnc rw - nfs4 cnc:/exports/programs rw"], "43", "500", "/mnt/cnc/programs", "cnc");
        Regression.Require(mounted.Fingerprint != replacement.Fingerprint, "Remount did not change identity.");
        foreach (var line in new[]
        {
            "42 1 0:51 / /mnt/cnc rw - ext4 /dev/sda1 rw",
            "42 1 0:51 / /mnt/cnc rw - nfs4 another:/exports/programs rw",
            "43 1 0:51 / /mnt/cnc rw - nfs4 cnc:/exports/programs rw",
        })
        {
            try
            {
                NfsMountIdentity.ParseLinux([line], "42", "500", "/mnt/cnc/programs", "cnc");
                throw new InvalidOperationException("Unidentified/wrong-source mount was accepted.");
            }
            catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private static async Task LeaseRoundTrip()
    {
        using var fixture = new Fixture();
        var lease = fixture.CreateLease(() => true);
        var bytes = Enumerable.Range(0, 90000).Select(value => (byte)value).ToArray();
        using var source = new MemoryStream(bytes);
        try
        {
            Regression.Require(await lease.UploadAsync(source, source.Length, default) == bytes.Length, "Upload length mismatch.");
            using var destination = new MemoryStream();
            await lease.DownloadAsync(destination, default);
            Regression.Require(destination.ToArray().SequenceEqual(bytes), "Filesystem lease read-back content mismatch.");
        }
        finally { await VerifyCleanup(fixture, lease); }
    }

    private static async Task ChangedMount()
    {
        using var fixture = new Fixture();
        var lease = fixture.CreateLease(() => false);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        try
        {
            await lease.UploadAsync(source, source.Length, default);
            throw new InvalidOperationException("Changed mount accepted a write.");
        }
        catch (IOException) { }
        await VerifyCleanup(fixture, lease);
    }

    private static async Task CancelledLease()
    {
        using var fixture = new Fixture();
        var lease = fixture.CreateLease(() => true);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        try
        {
            await lease.UploadAsync(source, source.Length, cancel.Token);
            throw new InvalidOperationException("Cancelled NFS lease accepted an upload.");
        }
        catch (OperationCanceledException) { }
        finally { await VerifyCleanup(fixture, lease); }
    }

    private static async Task VerifyCleanup(Fixture fixture, NfsTransferDriver.NfsVerificationLease lease)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await lease.CleanupAsync(cleanup.Token);
            Regression.Require(OperatingSystem.IsWindows(), "Cleanup was falsely confirmed without owned-handle deletion.");
        }
        catch (IOException error) when (!OperatingSystem.IsWindows())
        {
            Regression.Require(error.Message.Contains(fixture.FilePath) && File.Exists(fixture.FilePath),
                "Unconfirmed platform cleanup must retain the file and report its path.");
            using var reopened = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return;
        }
        Regression.Require(!File.Exists(fixture.FilePath), "Owned Windows file remains after cleanup.");
    }

    private static async Task ReplacedPath()
    {
        using var fixture = new Fixture();
        var lease = fixture.CreateLease(() => true);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        await lease.UploadAsync(source, source.Length, default);
        var ownedPath = fixture.FilePath + ".owned";
        File.Move(fixture.FilePath, ownedPath);
        await File.WriteAllBytesAsync(fixture.FilePath, new byte[] { 9, 8, 7 });
        await VerifyCleanup(fixture, lease);
        Regression.Require((await File.ReadAllBytesAsync(fixture.FilePath)).SequenceEqual(new byte[] { 9, 8, 7 })
            && (await File.ReadAllBytesAsync(ownedPath)).SequenceEqual(new byte[] { 1, 2, 3 }),
            "Linux cleanup changed the replacement or the owned file.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nfs-lease-fixture-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Root, "verify-" + new string('a', 32) + ".nc");
        public Fixture() => Directory.CreateDirectory(Root);
        public NfsTransferDriver.NfsVerificationLease CreateLease(Func<bool> unchanged)
        {
            var options = FileOptions.Asynchronous | FileOptions.WriteThrough;
            if (OperatingSystem.IsWindows()) options |= FileOptions.DeleteOnClose;
            var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, options);
            // Explicit lease-only fixture: no production NFS identity gate is bypassed by configuration.
            return new(stream, FilePath, unchanged);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
