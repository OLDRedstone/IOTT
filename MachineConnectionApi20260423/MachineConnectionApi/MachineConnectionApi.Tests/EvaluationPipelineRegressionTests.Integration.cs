using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static partial class EvaluationPipelineRegressionTests
{
    // Exercises the real configuration/attachment controllers, HTTP measurement adapter, runner,
    // JSON persistence, knowledge sync and both report builders. HTTP never leaves this handler.
    private static async Task ConfiguredRunPersistsAndImports()
    {
        var prefix = $"evaluation-integration-{Guid.NewGuid():N}";
        var fileNames = new[] { prefix + "-indicators.json", prefix + "-tasks.json", prefix + "-knowledge.json", prefix + "-import.json" };
        var attachmentDirectory = Path.Combine(Path.GetTempPath(), prefix);
        try
        {
            var indicators = new EvaluationIndicatorStore(fileNames[0]);
            var indicatorApi = new EvaluationIndicatorsController(indicators, attachmentDirectory);
            await using var content = new MemoryStream(new byte[10 * 1024 * 1024]);
            var uploaded = await indicatorApi.Upload(new FormFile(content, 0, content.Length, "file", "集成测试.nc"), CancellationToken.None);
            var file = (EvaluationFile)((OkObjectResult)uploaded.Result!).Value!;
            Assert(file.Id is not null && file.SizeBytes == content.Length, "Attachment API did not publish real metadata");
            Assert(await indicatorApi.Download(file.Id!, CancellationToken.None) is PhysicalFileResult, "Uploaded attachment cannot be downloaded");

            var address = "fixture.address." + new string('x', 300);
            var config = indicators.Get("machine") with
            {
                Indicators = [new()
                {
                    Id = "integration-section", Name = "集成评价", Weight = 100,
                    Children = [new()
                    {
                        Id = "integration-child", Name = "通讯与文件", Weight = 100,
                        Items =
                        [
                            new() { Id = "protocol-item", Name = "协议实测", Weight = 25, MetricId = "industrial-protocol", Protocols = ["ModbusTCP", "Modbus"],
                                Automation = EvaluationAutomationRules.DefaultFor("industrial-protocol")! with
                                { PassRule = new() { Threshold = 100, Unit = "%" }, Test = new() { ReadAddress = address, ReadDataType = "String" } } },
                            new() { Id = "stability-item", Name = "持续通讯", Weight = 25, MetricId = "communication-stability",
                                Automation = EvaluationAutomationRules.DefaultFor("communication-stability")! with
                                { ScoreBands = [new() { Min = 0, Score = 86.66 }], Test = new() { DurationMinutes = 1, SampleIntervalSeconds = 1, ReadAddress = address, ReadDataType = "String" } } },
                            new() { Id = "file-item", Name = "上传速度", Weight = 50, MetricId = "transfer-speed", Files = [file],
                                Automation = EvaluationAutomationRules.DefaultFor("transfer-speed")! with { Test = new() { TargetDirectory = "/integration-only" } } },
                        ],
                    }],
                }],
            };
            var savedConfig = (EvaluationConfig)((OkObjectResult)indicatorApi.Save("machine", config).Result!).Value!;
            var tasks = new IntegrationTaskStore(fileNames[1]);
            var gatewayDevices = new IntegrationDevices();
            var knowledge = new EvaluationKnowledgeStore(indicators, tasks, gatewayDevices, fileNames[2]);
            var httpReads = 0;
            var transferSnapshots = 0;
            using var handler = new RouteHandler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/Devices")
                    return JsonResponse(new[] { new VerifyMeasurementTarget
                    {
                        Id = "fixture-machine", Name = "本轮机床", Host = "192.0.2.11", Port = 502,
                        Model = "fixture-model", Brand = "fixture-system", Protocol = "ModbusTCP",
                        ExtendedProperties = new() { ["DeviceCode"] = "INTEGRATION-001" },
                        Transfer = new() { Protocol = "FTP", Host = "192.0.2.11", Port = 21 },
                    } });
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/program-transfer/fixture-machine/verification/snapshot")
                {
                    transferSnapshots++;
                    return JsonResponse(new { error = "Fixture Host has no transfer snapshot capability" }, HttpStatusCode.NotFound);
                }
                Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/data/fixture-machine/read",
                    "Integration attempted an unapproved HTTP operation, upload, history lookup or other device");
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var tag = payload.RootElement.GetProperty("tags")[0];
                Assert(tag.GetProperty("address").GetString() == address && tag.GetProperty("dataType").GetString() == "String",
                    "Versioned address/type did not reach the real HTTP adapter");
                httpReads++;
                return JsonResponse(new
                {
                    deviceId = "fixture-machine", tags = new[] { new { address, quality = "Good", value = "0" } },
                    evidence = new
                    {
                        version = 1, operation = "application-read", protocol = "ModbusTCP", simulated = false,
                        configurationMatched = true, operationAttempted = true, code = "completed", revision = new string('A', 64),
                        configurationFingerprint = payload.RootElement.GetProperty("verification").GetProperty("configurationFingerprint").GetString(),
                    },
                });
            });
            var clients = new IntegrationClientFactory(handler);
            var adapter = new HttpVerifyMeasurementAdapter(clients, attachmentDirectory);
            Assert((await adapter.ValidateAttachmentAsync(file, CancellationToken.None)).Valid, "Upload and measurement attachment stores disagree");
            var clock = new FakeClock();
            var service = new VerifyAutomationService(clients, DispatchProxy.Create<ICsConnectivityService, RejectCsProxy>(), new EmptyMetricStore(),
                new ConfigurationBuilder().Build(), NullLogger<VerifyAutomationService>.Instance, indicators, adapter, clock);
            var runner = new VerifyTaskRunner(tasks, service, new IntegrationLog(), devices: gatewayDevices);
            var taskApi = new VerifyTasksController(tasks, runner, gatewayDevices);
            var created = (VerifyTaskDto)((OkObjectResult)taskApi.Create(new()
            {
                Id = "ignored-client-id", Name = "端到端离线测试", Type = "performance", DeviceId = "fixture-machine",
                EvaluationCategory = "machine", CreatedAt = DateTimeOffset.UtcNow,
                MetricIds = ["industrial-protocol", "communication-stability"], Options = new() { AllowFileWrites = true },
            }).Result!).Value!;
            var sawProgress = false;
            var editedConfig = false;
            clock.OnDelay = () =>
            {
                var active = tasks.ReadAll().Single();
                var snapshot = JsonSerializer.Deserialize<VerifyRunResponse>(active.CurrentRunJson, WebJson)!;
                Assert(active.LastRunJson == "" && active.ActiveRunId == snapshot.RunId && snapshot.TaskId == created.Id,
                    "Runner did not persist a stable current-run identity without publishing unfinished history");
                Assert(snapshot.Metrics[0].Score == 100 && snapshot.Metrics[1].ExecutionStatus == "running", "Engine progress did not reach persistent task state");
                Assert(knowledge.ListTasks().Count == 0, "An unfinished current run was offered for knowledge import");
                sawProgress |= snapshot.ProgressPercent > 50 && snapshot.ProgressPercent < 100;
                if (editedConfig) return;
                editedConfig = true;
                var latest = indicators.Get("machine");
                var section = latest.Indicators[0];
                var child = section.Children[0];
                indicators.Save(latest with { Indicators = [section with { Children = [child with
                {
                    Items = child.Items.Select(item => item.MetricId == "communication-stability"
                        ? item with { Automation = item.Automation! with { ScoreBands = [new() { Min = 0, Score = 12 }] } } : item).ToList(),
                }] }] });
            };
            var completed = (await runner.RunTaskAsync(created.Id, "手动", CancellationToken.None))!;
            var run = JsonSerializer.Deserialize<VerifyRunResponse>(completed.LastRunJson, WebJson)!;
            Assert(sawProgress && httpReads == 62 && run.TotalScore == 93.33 && run.ProgressPercent == 100, "Integrated measurement/progress/selected average is incorrect");
            Assert(run.EvaluationSnapshot!.Version == savedConfig.Version && run.OptionsSnapshot?.AllowFileWrites == true,
                "Later edits changed the frozen rule/authorization snapshot");
            Assert(completed.CurrentRunJson == "" && completed.ActiveRunId is null && run.Result == "待判定", "Completed snapshot or unconfigured pass rule is incorrect");
            var preview = knowledge.ListTasks().Single();
            Assert(preview.TotalScore == run.TotalScore && preview.PassRate is null,
                "Knowledge preview changed the task average precision or invented a pass rate");
            var draft = knowledge.TaskDraft(created.Id) with { PartName = "离线测试零件", Conclusion = "人工结论", Suggestion = "保留建议" };
            Assert(draft.Scores["stability-item"] == 86.66 && draft.Scores["file-item"] is null, "Knowledge sync replaced real scores or filled an unselected item");
            var saved = knowledge.Create(draft);
            Assert(saved.TotalScore is null && saved.Snapshot.Version == savedConfig.Version, "Knowledge weighted total or historical snapshot is incorrect");
            Assert(saved.Remarks["stability-item"].Length <= 10000 && saved.Remarks["stability-item"].Contains("摘要")
                && saved.Remarks["stability-item"].Contains("最长连续"), "Long measured evidence needs an explicit bounded summary, not a failed import");
            Assert(run.Metrics[1].Evidence.Sum(value => value.Length) > 10000, "Fixture did not cover long real-read evidence");

            foreach (var format in new[] { "xlsx", "pdf" })
            {
                var report = (FileContentResult)taskApi.Export(created.Id, format);
                Assert(report.FileContents.Length > 0, "Finished task report is empty");
                if (format == "pdf") Assert(Encoding.ASCII.GetString(report.FileContents, 0, 5) == "%PDF-", "Task PDF is not a real PDF");
                else Assert(EvaluationSpreadsheetReader.Read(new MemoryStream(report.FileContents)).Any(row => row.Contains("93.33")), "Task Excel lost the selected total");
            }
            var exported = knowledge.ExportSpreadsheet(saved);
            var imported = new EvaluationKnowledgeStore(indicators, tasks, gatewayDevices, fileNames[3]);
            Assert(imported.Import(new MemoryStream(exported)).Success == 1 && imported.List().Single().Scores["stability-item"] == 86.66,
                "Knowledge export/import did not preserve snapshot scores and long evidence summary");
            Assert(Encoding.ASCII.GetString(knowledge.ExportPdf(saved), 0, 5) == "%PDF-", "Knowledge PDF is not a real PDF");

            // A second run must keep the first history while measuring, then publish a new run.
            // A Host without the verification snapshot endpoint must not receive file uploads.
            var previousHistory = completed.LastRunJson;
            var changed = (VerifyTaskDto)((OkObjectResult)taskApi.Update(created.Id, completed with
            { MetricIds = ["communication-stability", "transfer-speed"] }).Result!).Value!;
            clock.OnDelay = () => Assert(tasks.ReadAll().Single().LastRunJson == previousHistory
                && knowledge.TaskDraft(created.Id).SyncRunId == run.RunId, "Rerun overwrote or imported unfinished evidence");
            var rerunTask = (await runner.RunTaskAsync(changed.Id, "手动", CancellationToken.None))!;
            var rerun = JsonSerializer.Deserialize<VerifyRunResponse>(rerunTask.LastRunJson, WebJson)!;
            Assert(rerun.RunId != run.RunId && rerun.Metrics[0].Score == 12 && rerun.TotalScore is null, "Rerun did not use the new rule with nullable selected average");
            Assert(rerun.Metrics[1].ExecutionStatus == "skipped" && rerun.Metrics[1].Score is null
                && rerun.Metrics[1].Evidence.Any(value => value.Contains("真实附件已核对"))
                && rerun.Metrics[1].ScoreReason.Contains("HTTP 404") && transferSnapshots == 1,
                "A Host without a verification snapshot received an upload or produced fake measurement");
            var resynced = knowledge.Create(knowledge.TaskDraft(created.Id) with { PartName = saved.PartName, Conclusion = saved.Conclusion, Suggestion = saved.Suggestion });
            Assert(resynced.Id != saved.Id && resynced.SyncRunId == rerun.RunId && knowledge.List().Count == 2
                && knowledge.Find(saved.Id)!.Scores["stability-item"] == 86.66, "Resync overwrote the original evaluation or provenance");
        }
        finally
        {
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(attachmentDirectory)) Directory.Delete(attachmentDirectory, recursive: true);
        }
    }

    private sealed class IntegrationClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { BaseAddress = new Uri("http://integration-fixture.invalid/") };
    }

    private sealed class IntegrationTaskStore(string fileName) : IVerifyTaskStore
    {
        private readonly JsonFileStore<VerifyTaskDto> _rows = new(fileName);
        public List<VerifyTaskDto> ReadAll() => _rows.ReadAll();
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => _rows.WriteAll(items);
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => _rows.Update(update);
    }

    private sealed class IntegrationDevices : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => [new()
        {
            Id = "fixture-machine", Name = "网关注册名", Type = "CNC", Brand = "fake", Model = "fake", Protocol = "ModbusTCP",
            Status = "Offline", Host = "192.0.2.11", Port = 502, CreatedAt = DateTimeOffset.UtcNow,
        }];
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }

    private sealed class IntegrationLog : ISystemActivityLog
    {
        public List<SystemLogDto> ReadAll() => [];
        public SystemLogDto Append(SystemLogDto entry) => entry;
        public void Write(string type, string action, string detail, string user = "系统", string ip = "-") { }
    }
}
