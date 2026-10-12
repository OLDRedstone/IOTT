namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

internal static class EvaluationPreparationRegressionTests
{
    private static List<EvaluationItem> Items(EvaluationConfig config) => config.Indicators
        .SelectMany(section => section.Children).SelectMany(child => child.Items).ToList();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAll()
    {
        var fileName = $"evaluation-preparation-{Guid.NewGuid():N}.json";
        var storePath = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
        var directory = Path.Combine(Path.GetTempPath(), "iott-samples-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EvaluationIndicatorStore(fileName);
            var original = store.Get("machine");
            var changed = original with { Indicators = original.Indicators.Select(section => section with
            {
                Children = section.Children.Select(child => child with
                {
                    Items = child.Items.Select(item => item.MetricId switch
                    {
                        "file-integrity" => item with { Automation = item.Automation! with
                        { PassRule = new() { Threshold = 90, Unit = "%" } } },
                        "transfer-speed" => item with { Automation = item.Automation! with
                        { PassRule = new() { Threshold = 5, Unit = "MB/s" } } },
                        "max-connections" => item with { Automation = item.Automation! with
                        { Test = item.Automation.Test with { MaxConnections = 4 } } },
                        _ => item,
                    }).ToList(),
                }).ToList(),
            }).ToList() };
            var saved = store.Save(changed);
            var snapshot = JsonSerializer.Serialize(saved);
            var controller = new EvaluationIndicatorsController(store, directory);
            var result = await controller.PrepareDefaults("machine", new(saved.Version), default);
            var prepared = (EvaluationConfig)((OkObjectResult)result.Result!).Value!;
            var items = Items(prepared);
            Check(prepared.Version == saved.Version + 1, "Preparation did not use versioned save");
            Check(items.Where(item => item.Automation is not null).All(item => item.Automation!.PassRule is not null),
                "Missing acceptance lines remain after preparation");
            Check(items.Single(item => item.MetricId == "file-integrity").Automation!.PassRule!.Threshold == 90
                && items.Single(item => item.MetricId == "transfer-speed").Automation!.PassRule!.Threshold == 5,
                "Custom acceptance lines were overwritten");
            Check(items.Single(item => item.MetricId == "max-connections").Automation!.Test.MaxConnections == 100,
                "Legacy configuration limit did not change to 100");
            Check(items.Single(item => item.MetricId == "transfer-protocol").Protocols.SequenceEqual(["FTP", "NFS", "SMB"]),
                "Prototype transfer standards were not replaced by supported round-trip protocols");
            foreach (var item in items)
            foreach (var size in EvaluationSamplePreparation.RequiredBytes(item.MetricId ?? ""))
            {
                var file = item.Files.Single(value => value.SizeBytes == size);
                Check(Guid.TryParseExact(file.Id, "N", out _) && file.Name.EndsWith(".txt"), "Sample is only prototype metadata");
                var download = (PhysicalFileResult)await controller.Download(file.Id!, default);
                Check(new FileInfo(download.FileName).Length == size, "Sample bytes differ from the required size");
                await using var stream = File.OpenRead(download.FileName);
                var prefix = new byte[24];
                await stream.ReadExactlyAsync(prefix);
                Check(System.Text.Encoding.ASCII.GetString(prefix).StartsWith("IOTT verification sample"), "Sample content was not generated");
            }
            Check(Directory.GetFiles(directory, "*.bin").Length == 5, "Samples were not shared between metrics");
            Check(items.Where(item => EvaluationSamplePreparation.RequiredBytes(item.MetricId ?? "").Length > 0)
                .SelectMany(item => item.Files).All(file => !string.IsNullOrWhiteSpace(file.Id)),
                "Unuploaded prototype filenames remain alongside prepared samples");
            Check(store.Get("machining").Version == 1 && JsonSerializer.Serialize(saved) == snapshot,
                "Preparation mutated another category or an existing snapshot");
            var previousFiles = Directory.GetFiles(directory).OrderBy(path => path).ToArray();
            var stale = await controller.PrepareDefaults("machine", new(saved.Version), default);
            Check(stale.Result is ConflictObjectResult && previousFiles.SequenceEqual(Directory.GetFiles(directory).OrderBy(path => path)),
                "Stale configuration created files or overwrote data");
            var repeated = await controller.PrepareDefaults("machine", new(prepared.Version), default);
            Check(repeated.Result is OkObjectResult && previousFiles.SequenceEqual(Directory.GetFiles(directory).OrderBy(path => path)),
                "Repeated preparation regenerated unchanged sample files");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await controller.PrepareDefaults("machine", new(store.Get("machine").Version), canceled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            if (File.Exists(storePath)) File.Delete(storePath);
            if (Directory.Exists(directory))
            {
                Check(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
                    "Sample cleanup escaped the temporary directory");
                Directory.Delete(directory, true);
            }
        }
    }
}
