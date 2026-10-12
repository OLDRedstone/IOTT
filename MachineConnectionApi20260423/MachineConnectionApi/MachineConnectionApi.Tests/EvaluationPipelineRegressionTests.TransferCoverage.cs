namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;

internal static partial class EvaluationPipelineRegressionTests
{
    private static async Task TransferCoverageRoundTrips()
    {
        using var fixture = new PipelineFixture();
        fixture.Change("transfer-protocol", item => item with { Protocols = ["FTP", "SMB"], Files = [FixtureFile(0.1)] });
        fixture.Targets.Add(new() { Id = "smb", Protocol = "SMB", Host = "192.0.2.50", Port = 445 });
        var options = new VerifyRunOptions { AllowFileWrites = true, TransferDeviceIds = ["fixture-machine", "fixture-2", "smb"] };
        var result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Measurement == 2 && fixture.Adapter.Transfers.Count == 2
            && fixture.Adapter.Directories.Count == 0, "Unique protocol coverage did not require round trips");
        options.TransferDeviceIds = [];
        Assert(result.OptionsSnapshot?.TransferDeviceIds?.Count == 3, "Transfer target history snapshot changed");
        var before = fixture.Adapter.Transfers.Count;
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null && fixture.Adapter.Transfers.Count == before,
            "Unselected SMB target was automatically written or credited");
        options.TransferDeviceIds = ["fixture-machine", "smb"];
        options.AllowFileWrites = false;
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null && fixture.Adapter.Transfers.Count == before,
            "Directory success or absent write permission enabled transfer coverage");
        options.AllowFileWrites = true;
        fixture.Change("transfer-protocol", item => item with { Files = [] });
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null && fixture.Adapter.Transfers.Count == before, "Missing attachment caused writes");
        fixture.Change("transfer-protocol", item => item with { Files = [FixtureFile(0.1)] });
        fixture.Adapter.Validate = _ => new(false, "missing binary");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null && fixture.Adapter.Transfers.Count == before, "Missing binary caused writes");
        fixture.Adapter.Validate = _ => new(true, "fixture binary");
        foreach (var invalid in new[] { new[] { "missing" }, new[] { "smb", "smb" }, new[] { " smb" } })
        {
            options.TransferDeviceIds = invalid;
            result = await fixture.Run(["transfer-protocol"], options);
            Assert(result.Metrics.Single().Score is null && fixture.Adapter.Transfers.Count == before, "Invalid target caused writes");
        }
        options.TransferDeviceIds = ["fixture-machine", "fixture-2", "smb"];
        fixture.Adapter.TargetTransfer = (target, file) => target.Id == "fixture-machine"
            ? new(true, true, false, false, 0, 0, "actual upload failed")
            : new(true, true, true, true, file.SizeBytes!.Value, 1, "actual hash readback matched");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Measurement == 2 && fixture.Adapter.Transfers.Count == before + 3,
            "A second successful target for a failed protocol was ignored or counted twice");
        fixture.Adapter.TargetTransfer = (_, _) => new(true, true, false, false, 0, 0, "actual transfer failed");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Measurement == 0 && result.Metrics.Single().Score == 0,
            "Real transfer failures were confused with unsupported operations");
        fixture.Adapter.TargetTransfer = (_, _) => new(false, false, false, false, 0, 0, "round trip unsupported");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Measurement is null && result.Metrics.Single().Score is null,
            "Unsupported round trip was scored zero");
        fixture.Adapter.TargetTransfer = (_, file) => new(true, true, true, true, file.SizeBytes!.Value - 1, 1, "short upload");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null, "A truncated success was counted as protocol coverage");
        fixture.Adapter.TargetTransfer = (_, file) => new(true, true, true, false, file.SizeBytes!.Value, 1, "unverified download");
        result = await fixture.Run(["transfer-protocol"], options);
        Assert(result.Metrics.Single().Score is null, "Missing readback hash was counted as protocol coverage");
    }
}
