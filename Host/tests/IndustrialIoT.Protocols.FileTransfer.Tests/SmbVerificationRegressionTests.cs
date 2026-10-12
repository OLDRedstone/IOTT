namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using System.Security.Cryptography;
using IndustrialIoT.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SmbVerificationRegressionTests
{
    public static async Task RunAll()
    {
        await Regression.Run("SMB verification fixture preserves short writes and real read-back bytes", () => RoundTrip(false));
        await Regression.Run("SMB verification fixture exposes equal-length corruption for Host hashing", () => RoundTrip(true));
        await Regression.Run("SMB verification collision never writes or deletes existing file", Collision);
        await Regression.Run("SMB verification cancellation leaves its handle available for bounded cleanup", Cancellation);
        await Regression.Run("SMB rejects a non-445 endpoint before creating a client", NonDefaultPort);
    }

    private static async Task RoundTrip(bool corrupt)
    {
        await using var fixture = new SmbRegressionTests.Fixture();
        fixture.Probe.MaxWrite = 13;
        var bytes = Enumerable.Range(0, 70000).Select(value => (byte)value).ToArray();
        var lease = await fixture.Driver.CreateVerificationFileAsync("/verification", Name(), default);
        using var source = new MemoryStream(bytes);
        Regression.Require(await lease.UploadAsync(source, bytes.Length, default) == bytes.Length, "Upload lost bytes.");
        fixture.Probe.CorruptReadBack = corrupt;
        using var destination = new MemoryStream();
        await lease.DownloadAsync(destination, default);
        Regression.Require(destination.Length == bytes.Length, "Read-back length changed.");
        var same = SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(destination.ToArray()));
        Regression.Require(same != corrupt, "Read-back must expose actual remote content, not source bytes.");
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await lease.CleanupAsync(cleanup.Token);
        Regression.Require(fixture.Probe.DeleteCalls == 1 && fixture.Probe.CloseCalls == 1
            && !fixture.Probe.VerificationFileExists, "Owned file was not deleted through its handle.");
    }

    private static async Task Collision()
    {
        await using var fixture = new SmbRegressionTests.Fixture();
        fixture.Probe.VerificationFileExists = true;
        fixture.Probe.Contents.Write([7, 8, 9]);
        try
        {
            await fixture.Driver.CreateVerificationFileAsync("/", Name(), default);
            throw new InvalidOperationException("Existing verification path was accepted.");
        }
        catch (IOException) { }
        Regression.Require(fixture.Probe.VerificationFileExists && fixture.Probe.DeleteCalls == 0
            && fixture.Probe.Contents.ToArray().SequenceEqual(new byte[] { 7, 8, 9 }), "Collision changed existing data.");
    }

    private static async Task Cancellation()
    {
        await using var fixture = new SmbRegressionTests.Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.Probe.AfterWrite = cancel.Cancel;
        var lease = await fixture.Driver.CreateVerificationFileAsync("/", Name(), default);
        using var source = new MemoryStream(new byte[70000]);
        try
        {
            await lease.UploadAsync(source, source.Length, cancel.Token);
            throw new InvalidOperationException("Cancelled upload completed.");
        }
        catch (OperationCanceledException) { }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await lease.CleanupAsync(cleanup.Token);
        Regression.Require(fixture.Probe.DeleteCalls == 1 && !fixture.Probe.VerificationFileExists,
            "Cancellation did not clean the acquired file.");
    }

    private static async Task NonDefaultPort()
    {
        await using var driver = new SmbTransferDriver(NullLogger<SmbTransferDriver>.Instance);
        var result = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture.invalid", Port = 1445 });
        Regression.Require(!result.Success && result.ErrorMessage?.Contains("445") == true
            && result.ErrorMessage.Contains("1445"), "Unsupported endpoint was connected or its error hid the requested port.");
        var field = typeof(SmbTransferDriver).GetField("_smbClient",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Regression.Require(field is not null && field.GetValue(driver) is null, "Non-445 endpoint created a network client.");
    }

    private static string Name() => "verify-" + Guid.NewGuid().ToString("N") + ".nc";
}
