namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;

internal static partial class CsConnectivityServiceRegressionTests
{
    public static async Task FtpRoundTripIntegrity()
    {
        foreach (var mode in new[] { "healthy", "corrupt", "short", "upload-failure", "cleanup-failure", "corrupt-cleanup-failure", "collision" })
        {
            using var sandbox = new FtpStorageSandbox();
            using var service = Create(sandbox.ConfigurationPath);
            await using var peer = new FtpProbePeer(mode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await service.ProbeFtpClientAsync(ProbeGateway(peer.Port), true, deadline.Token);
            AssertEqual(mode == "healthy", result.Success, $"FTP probe verdict: {mode}: {result.Message}");
            if (mode.StartsWith("corrupt", StringComparison.Ordinal))
            {
                AssertEqual(1, peer.Reads, "Same-length corruption must reach actual read-back");
                AssertEqual(true, result.Message.Contains("下载校验不符"), "Cleanup failure hid content mismatch");
            }
            if (mode == "collision")
            {
                AssertEqual(0, peer.Uploads, "Collision overwrote an existing object");
                AssertEqual(0, peer.Deletes, "Collision deleted an unowned object");
                AssertEqual(0, peer.RemovedDirectories, "Collision removed an unowned directory");
            }
            else
            {
                AssertEqual(1, peer.Deletes, "Failed/successful probe did not attempt owned-file cleanup");
                AssertEqual(1, peer.RemovedDirectories, "Owned directory was not cleaned nonrecursively");
            }
        }
        await FtpProbeCancellationIsPreserved();
    }

    private static async Task FtpProbeCancellationIsPreserved()
    {
        using var sandbox = new FtpStorageSandbox();
        using var service = Create(sandbox.ConfigurationPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var peer = new FtpProbePeer("cancel", cancellation.Cancel);
        try
        {
            await service.ProbeFtpClientAsync(ProbeGateway(peer.Port), true, cancellation.Token);
            throw new InvalidOperationException("FTP probe converted caller cancellation into a verdict");
        }
        catch (OperationCanceledException)
        {
            AssertEqual(1, peer.Reads, "Cancellation did not occur during the real FTP round trip");
        }
    }

    private static CsGateway ProbeGateway(int port) => new()
    {
        Name = "Local FTP integrity fixture", Type = "FTP", Ip = "127.0.0.1", Port = port,
        Username = "fixture", Password = "fixture", RemotePath = "/test-root",
    };
}
