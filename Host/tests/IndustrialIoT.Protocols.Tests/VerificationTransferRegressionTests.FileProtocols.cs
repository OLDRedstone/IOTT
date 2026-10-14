using System.Security.Cryptography;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Host.Services;
using IndustrialIoT.Protocols.FileTransfer;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class VerificationTransferRegressionTests
{
    private static async Task FileProtocolCoverageAsync()
    {
        var repository = new Repository(DeviceFor(445));
        repository.Device.Protocol = ProtocolType.SMB;
        var corrupt = false;
        var factory = new Factory(() =>
        {
            var driver = new FakeDriver(ProtocolType.SMB);
            driver.Lease.CorruptReadBack = corrupt;
            return driver;
        });
        var service = new VerificationTransferService(factory, repository, NullLogger.Instance);
        using var source = new PatternStream(104858);
        var request = await RequestAsync(service, repository.Device.Id, source);
        var result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(result.Success && result.IntegrityVerified && result.Protocol == "SMB"
            && result.CleanupSucceeded == true, "SMB must pass through the shared measured round-trip path.");
        corrupt = true;
        source.Position = 0;
        request = await RequestAsync(service, repository.Device.Id, source);
        result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(result.HasEvidence && !result.Success && !result.IntegrityVerified
            && result.DownloadedBytes == result.UploadedBytes, "SMB equal-length corruption must fail Host SHA256 validation.");
        source.Position = 0;
        request = await RequestAsync(service, repository.Device.Id, source);
        var created = factory.Created;
        repository.Device.ConnectionConfig.ExtendedProperties["ShareName"] = "changed-after-snapshot";
        result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(result.ConfigurationChanged && !result.HasEvidence && factory.Created == created,
            "SMB share changes after a snapshot must cause zero target connections.");

        repository.Device.ConnectionConfig = repository.Device.ConnectionConfig with { Port = 1445 };
        created = factory.Created;
        var portSnapshot = await service.GetSnapshotAsync(repository.Device.Id, default);
        Require(portSnapshot is { Supported: false } && portSnapshot.Detail.Contains("445") && portSnapshot.Detail.Contains("1445"),
            "SMB non-default port must explain the unsupported frozen endpoint.");
        source.Position = 0;
        request = await RequestAsync(service, repository.Device.Id, source);
        result = await service.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(!result.Supported && !result.HasEvidence && factory.Created == created,
            "SMB non-445 configuration must never resolve/connect a driver or upload to port 445.");

        repository.Device.ConnectionConfig = repository.Device.ConnectionConfig with { Port = 445 };
        var candidatePath = "/verification/verify-" + Guid.NewGuid().ToString("N") + ".nc";
        var acquisitionFactory = new Factory(() => new FakeDriver(ProtocolType.SMB)
        {
            // Contract-only failure fixture; this does not bypass production NFS mount identity.
            AcquisitionError = new NfsVerificationFileAcquisitionException(candidatePath, new IOException("fixture identity failure")),
        });
        var acquisitionService = new VerificationTransferService(acquisitionFactory, repository, NullLogger.Instance);
        source.Position = 0;
        request = await RequestAsync(acquisitionService, repository.Device.Id, source);
        result = await acquisitionService.ExecuteAsync(repository.Device.Id, request, source, default);
        Require(!result.HasEvidence && !result.Success && result.CleanupSucceeded == false
            && result.RemotePath == candidatePath && result.Detail.Contains(candidatePath) && result.Detail.Contains("清理未确认"),
            "Created candidate paths must retain an explicit cleanup status even when no lease was returned.");

        var directory = Path.Combine(Path.GetTempPath(), "nfs-host-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            repository.Device.Protocol = ProtocolType.NFS;
            repository.Device.ConnectionConfig = repository.Device.ConnectionConfig with
            { Port = 2049, ExtendedProperties = new() { ["MountPoint"] = directory } };
            var nfsFactory = new Factory(() => new NfsTransferDriver(NullLogger<NfsTransferDriver>.Instance));
            var nfsService = new VerificationTransferService(nfsFactory, repository, NullLogger.Instance);
            var snapshot = await nfsService.GetSnapshotAsync(repository.Device.Id, default);
            Require(snapshot is { Supported: false } && snapshot.Detail.Contains("NFS"),
                "An ordinary directory must explain why no NFS mount evidence exists.");
            source.Position = 0;
            request = await RequestAsync(nfsService, repository.Device.Id, source);
            result = await nfsService.ExecuteAsync(repository.Device.Id, request, source, default);
            Require(!result.Supported && !result.HasEvidence && !result.Success && nfsFactory.Created == 0
                && Directory.GetFileSystemEntries(directory).Length == 0,
                "A local directory must not create a driver/file or real NFS evidence.");
        }
        finally { Directory.Delete(directory); }
    }
}
