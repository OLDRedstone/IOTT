using System.Net;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

internal static partial class EvaluationPipelineRegressionTests
{
    private static async Task HttpMeasurementContract()
    {
        var paths = new List<string>();
        var bodies = new List<string>();
        var quality = "Good";
        var responseDevice = "fixture-machine";
        var responseError = "";
        var readStatus = HttpStatusCode.OK;
        var hasPoint = true;
        var hasValue = true;
        var simulated = false;
        var operationAttempted = true;
        var includeEvidence = true;
        var evidenceCode = "completed";
        var actualProtocol = "ModbusTCP";
        var revision = new string('A', 64);
        using var handler = new RouteHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/api/collection-config/fixture-machine")
            {
                Assert(request.Method == HttpMethod.Get, "Point discovery changed device configuration");
                return JsonResponse(hasPoint ? new object[]
                {
                    new { deviceId = "fixture-machine", groups = new[] { new { tags = new[] { new { address = "configured:12", dataType = "UInt16" } } } } },
                } : Array.Empty<object>());
            }
            Assert(path == "/api/data/fixture-machine/read" && request.Method == HttpMethod.Post, "Adapter used a handshake, history, or unexpected device operation");
            var body = await request.Content!.ReadAsStringAsync(ct);
            bodies.Add(body);
            using var json = JsonDocument.Parse(body);
            var address = json.RootElement.GetProperty("tags")[0].GetProperty("address").GetString();
            var verification = json.RootElement.GetProperty("verification");
            var fingerprint = verification.GetProperty("configurationFingerprint").GetString();
            Assert(fingerprint is { Length: 64 }, "Read did not send a server-verifiable configuration constraint");
            object? evidence = includeEvidence ? new
            {
                version = 1, operation = "application-read", protocol = actualProtocol, simulated,
                configurationMatched = evidenceCode != "configuration-changed", operationAttempted,
                configurationFingerprint = fingerprint, revision, code = evidenceCode,
            } : null;
            if (readStatus != HttpStatusCode.OK) return JsonResponse(new { deviceId = responseDevice, error = responseError, evidence }, readStatus);
            return JsonResponse(new
            {
                deviceId = responseDevice, evidence,
                tags = new[] { new { address, dataType = "UInt16", quality, value = hasValue ? (int?)42 : null, errorMessage = responseError } },
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://measurement-fixture.invalid/") };
        var adapter = new HttpVerifyMeasurementAdapter(new ClientFactory(client));
        var target = Target();
        var observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(observation is { Supported: true, HasEvidence: true, Success: true }, "Actual good application result was not recognized");
        Assert(paths.SequenceEqual(["/api/data/fixture-machine/read"]) && bodies.Single().Contains("rule:7") && bodies.Single().Contains("String"), "Rule address/type did not take priority over device configuration");
        observation = await adapter.ReadAsync(target, "", "String", 3000, CancellationToken.None);
        Assert(observation.Success && bodies.Last().Contains("configured:12") && bodies.Last().Contains("UInt16"), "Fallback did not use the existing device address AND type");
        await adapter.ReadAsync(target, "", "String", 3000, CancellationToken.None);
        Assert(paths.Count(x => x.Contains("collection-config")) == 1, "A run did not hold its configured fallback point stable");
        await adapter.ReadAsync(Target(), "", "String", 3000, CancellationToken.None);
        Assert(paths.Count(x => x.Contains("collection-config")) == 2, "Fallback point configuration leaked across run target snapshots");
        hasPoint = false;
        var readCount = bodies.Count;
        observation = await adapter.ReadAsync(Target(), "", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence && bodies.Count == readCount, "Adapter guessed a point when neither rule nor device had one");

        quality = "Bad";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(observation is { Supported: true, HasEvidence: true, Success: false }, "An actual bad application result became missing evidence");
        responseError = "driver does not support tag read operations";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Unsupported protocol was counted as a measured zero");
        responseError = "";
        quality = "Good";
        hasValue = false;
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "An empty Good response fabricated a successful read");
        hasValue = true;
        responseDevice = "different-device";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "A response from a different device was accepted");
        responseDevice = "fixture-machine";
        readStatus = HttpStatusCode.BadGateway;
        responseError = "Read failed: fixture protocol read timed out";
        evidenceCode = "operation-failed";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(observation.HasEvidence && !observation.Success, "Explicit Host application failure was lost");
        operationAttempted = false;
        evidenceCode = "initialization-failed";
        // Exactly the same legacy text also describes SDK/connect initialization failures.
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Host initialization failure became a measured read failure");
        includeEvidence = false;
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Legacy Read failed text was used as proof the callback ran");
        responseError = "unrelated proxy failure";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Generic gateway failure was falsely attributed to the machine");
        readStatus = HttpStatusCode.OK;
        responseError = "";
        operationAttempted = true;
        evidenceCode = "completed";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Unverified legacy Good data was scored");
        includeEvidence = true;
        simulated = true;
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Good simulated data was scored as a real machine read");
        simulated = false;
        actualProtocol = "Simulator";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Simulator protocol masqueraded as a real driver");
        actualProtocol = "OpcUa";
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "A changed actual protocol was attributed to the frozen protocol");
        actualProtocol = "ModbusTCP";
        revision = new string('B', 64);
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "A changed Host secret/configuration revision was accepted in the same run");
        var latchedCount = handler.Count;
        revision = new string('A', 64);
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence && handler.Count == latchedCount, "Changed target resumed after reverting configuration");
        target = Target();
        evidenceCode = "configuration-changed";
        readStatus = HttpStatusCode.Conflict;
        observation = await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Host configuration conflict became a machine failure score");
        latchedCount = handler.Count;
        await adapter.ReadAsync(target, "rule:7", "String", 3000, CancellationToken.None);
        Assert(handler.Count == latchedCount, "Configuration mismatch did not stop later reads in the run");
        var httpCount = handler.Count;
        foreach (var excluded in new[] { "FTP", "Simulator", "Simulated", "Simulation" })
        {
            observation = await adapter.ReadAsync(Target(protocol: excluded), "anything", "String", 3000, CancellationToken.None);
            Assert(!observation.HasEvidence && handler.Count == httpCount, "File-only or simulated protocol reached the application read API");
        }

        var directoryProtocol = "FTP";
        var directorySimulated = false;
        var directoryCode = "completed";
        using var directoryHandler = new RouteHandler(async (request, ct) =>
        {
            Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/data/fixture-machine/verification-directory",
                "Directory verification used an unbound capability/files GET or performed a write");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var fingerprint = json.RootElement.GetProperty("verification").GetProperty("configurationFingerprint").GetString();
            Assert(json.RootElement.GetProperty("directory").GetString() is "/test folder" or "/Programs" or "/", "Directory was ignored");
            return JsonResponse(new { deviceId = "fixture-machine", files = Array.Empty<object>(), evidence = new
            {
                version = 1, operation = "directory-read", protocol = directoryProtocol, simulated = directorySimulated,
                configurationMatched = directoryCode != "configuration-changed", operationAttempted = true,
                configurationFingerprint = fingerprint, revision = new string('C', 64), code = directoryCode,
            } });
        });
        using var directoryClient = new HttpClient(directoryHandler) { BaseAddress = new Uri("http://measurement-fixture.invalid/") };
        var directoryAdapter = new HttpVerifyMeasurementAdapter(new ClientFactory(directoryClient));
        VerifyMeasurementTarget DirectoryTarget(string protocol) => new() { Id = "fixture-machine", Protocol = protocol };
        var directoryTarget = DirectoryTarget("FTP");
        observation = await directoryAdapter.ReadTransferDirectoryAsync(directoryTarget, "/test folder", 3000, CancellationToken.None);
        Assert(observation.Success && observation.HasEvidence && observation.Protocol == "FTP" && directoryHandler.Count == 1,
            "Frozen directory evidence was not accepted");
        observation = await directoryAdapter.ReadTransferDirectoryAsync(directoryTarget, "/../unsafe", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence && directoryHandler.Count == 1, "Unsafe directory reached the Host");
        directorySimulated = true;
        observation = await directoryAdapter.ReadTransferDirectoryAsync(directoryTarget, "/", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Simulated directory was scored");
        directorySimulated = false;
        directoryCode = "configuration-changed";
        observation = await directoryAdapter.ReadTransferDirectoryAsync(directoryTarget, "/", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "Changed transfer target was scored");
        var directoryCount = directoryHandler.Count;
        await directoryAdapter.ReadTransferDirectoryAsync(directoryTarget, "/", 3000, CancellationToken.None);
        Assert(directoryHandler.Count == directoryCount, "Changed transfer target was not latched");
        directoryCode = "completed";
        directoryProtocol = "FOCAS";
        observation = await directoryAdapter.ReadTransferDirectoryAsync(DirectoryTarget("FOCAS"), "/", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence && directoryHandler.Count == directoryCount, "Static FOCAS root became evidence");
        observation = await directoryAdapter.ReadTransferDirectoryAsync(DirectoryTarget("FOCAS"), "/Programs", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "FOCAS empty/error-collapsed directory was scored");
        directoryProtocol = "SMB";
        observation = await directoryAdapter.ReadTransferDirectoryAsync(DirectoryTarget("SMB"), "/", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence, "SMB empty/error-collapsed directory was scored");
        directoryCount = directoryHandler.Count;
        directoryProtocol = "NFS";
        observation = await directoryAdapter.ReadTransferDirectoryAsync(DirectoryTarget("NFS"), "/", 3000, CancellationToken.None);
        Assert(!observation.HasEvidence && !observation.Success && directoryHandler.Count == directoryCount
            && observation.Detail.Contains("缓存") && observation.Detail.Contains("回读"),
            "Potentially cached NFS directory responses were accepted or lacked the round-trip guidance");

        ProtocolAliasContract();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await adapter.ReadAsync(target, "rule:7", "String", 3000, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && handler.Count == httpCount, "Caller cancellation became a fake failure or contacted a device");
    }

    private static void ProtocolAliasContract()
    {
        string Normalize(string protocol) => EvaluationAutomationRules.NormalizeProtocol(protocol);
        var families = new Dictionary<string, string[]>
        {
            ["GSK"] = ["GSK", "GskWebServer", "GSK-WebServer", "Gskrm"],
            ["Modbus"] = ["Modbus", "ModbusTCP", "Modbus RTU"],
            ["OPC UA"] = ["OPC UA", "OpcUa", "OPC/OPC UA"],
            ["Profibus"] = ["Profibus", "PROFIBUS-DP"],
            ["S7"] = ["S7", "SiemensS7", "S7Comm"],
            ["NC-Link"] = ["NC-Link", "NCLink", "NCLinkApi"],
            ["MTConnect"] = ["MTConnect"], ["FOCAS"] = ["FOCAS", "FANUC", "FanucFocas"],
            ["EtherNet/IP"] = ["EtherNet/IP", "EthernetIP"], ["Profinet"] = ["Profinet"],
            ["MQTT"] = ["MQTT"], ["FTP"] = ["FTP"], ["NFS"] = ["NFS"], ["SMB"] = ["SMB"],
        };
        foreach (var (label, aliases) in families)
            Assert(aliases.Select(Normalize).Distinct().SequenceEqual([label]), $"Protocol aliases did not deduplicate to {label}");
        Assert(families.Keys.Select(Normalize).Distinct().Count() == 14, "Different editor families were conflated");
        foreach (var pair in new[] { ("OPC", "OPC UA"), ("MQTT", "NC-Link"), ("GskrmFileTransfer", "GSK"),
                     ("FTP", "FTPS"), ("FTP", "SFTP"), ("FOCAS", "FanucRobot"), ("NC-Link", "HncSdk"), ("S7", "Profinet") })
            Assert(Normalize(pair.Item1) != Normalize(pair.Item2), "Unequal protocol families were merged");
    }

    private static async Task AttachmentAndWriteSafety()
    {
        var directory = Path.Combine(Path.GetTempPath(), "evaluation-attachment-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var handler = new RouteHandler((_, _) => throw new InvalidOperationException("No live upload is permitted in this fixture"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://measurement-fixture.invalid/") };
        var adapter = new HttpVerifyMeasurementAdapter(new ClientFactory(client), directory);
        try
        {
            var file = FixtureFile(0.1);
            var binary = Path.Combine(directory, file.Id + ".bin");
            var metadata = Path.Combine(directory, file.Id + ".json");
            await using (var stream = new FileStream(binary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(file.SizeBytes!.Value);
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(file, WebJson));
            Assert((await adapter.ValidateAttachmentAsync(file, CancellationToken.None)).Valid, "Real local attachment metadata and size were not accepted");
            Assert(!(await adapter.ValidateAttachmentAsync(file with { Name = "../escape.nc" }, CancellationToken.None)).Valid, "Attachment path traversal passed validation");
            Assert(!(await adapter.ValidateAttachmentAsync(file with { Id = "../../escape" }, CancellationToken.None)).Valid, "Untrusted attachment ID passed validation");
            Assert(!(await adapter.ValidateAttachmentAsync(file with { SizeBytes = file.SizeBytes + 1 }, CancellationToken.None)).Valid, "Declared and actual attachment sizes were not compared");
            Assert(!(await adapter.ValidateAttachmentAsync(new() { Name = "default-only.nc", SizeBytes = 104858 }, CancellationToken.None)).Valid, "Default metadata was accepted as a real attachment");
            await using (var stream = new FileStream(binary, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(12);
            Assert(!(await adapter.ValidateAttachmentAsync(file, CancellationToken.None)).Valid, "Corrupted stored attachment length was not detected");
            File.Delete(binary);
            Assert(!(await adapter.ValidateAttachmentAsync(file, CancellationToken.None)).Valid, "Missing binary was replaced by metadata/history");

            var focas = new VerifyMeasurementTarget { Id = "fixture-focas", Protocol = "FOCAS" };
            var transfer = await adapter.TransferAsync(focas, file, "/", $"verify-{Guid.NewGuid():N}.nc", _ => { }, CancellationToken.None);
            Assert(!transfer.HasEvidence && handler.Count == 0, "Missing FOCAS attachment reached a remote write or produced evidence");
            transfer = await adapter.TransferAsync(Target(), file, "/", $"verify-{Guid.NewGuid():N}.nc", _ => { }, CancellationToken.None);
            Assert(!transfer.HasEvidence && handler.Count == 0, "Missing attachment reached the Host or was replaced by historical evidence");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
