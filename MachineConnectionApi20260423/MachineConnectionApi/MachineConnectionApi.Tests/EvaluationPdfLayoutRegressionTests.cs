using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace MachineConnectionApi.Tests;

internal static class EvaluationPdfLayoutRegressionTests
{
    public static void RunAll(string? outputDirectory = null)
    {
        var body = string.Concat(Enumerable.Repeat("采样序列 EnglishWord_0123456789 中文 threshold>=99.95%; ", 150))
            + new string('X', 400) + "跨页证据结束";
        var samples = Samples(body);
        samples["mixed-pagination"] = EvaluationPdfBuilder.BuildReport("中文与 English 混排验证", ["0 分与未评分独立保存"],
            [new("跨页明细", ["项目", "证据"], [new[] { "长行", body }, new[] { "末行", "0 / 未评分" }], [1, 3])]);
        foreach (var (name, bytes) in samples)
        {
            CheckGeometry(bytes);
            var strings = Strings(Encoding.ASCII.GetString(bytes));
            Check(strings.Sum(value => value.Count(character => character == '序')) == 150, name + " lost source characters");
            Check(string.Concat(strings).Contains("跨页证据结束"), name + " lost final evidence");
            if (name == "mixed-pagination")
                Check(strings.Count(value => value == "长行") > 1, "Continuation lost the row identifier");
            if (name == "knowledge-report")
                Check(strings.Contains("完整内容") && string.Concat(strings).Contains("详见后附证据 1")
                    && string.Concat(strings).Contains("详见后附证据 2") && strings.Count(value => value == "多行结果末尾") == 1,
                    "Long evidence must be referenced in the summary and printed once in a wide table");
            if (outputDirectory is null) continue;
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, name + ".pdf"), bytes);
        }
    }

    private static Dictionary<string, byte[]> Samples(string body)
    {
        var items = Enumerable.Range(1, 7).Select(index => new EvaluationItem
        {
            Id = "item-" + index, MetricId = "metric-" + index, Name = "数据传输稳定性 / CNC-" + index, Weight = 100d / 7,
            Automation = new() { Unit = "%", PassRule = new() { Threshold = 99, Unit = "%" },
                ScoreBands = [new() { Min = 99, Score = 100 }, new() { Min = 90, Score = 80 }] },
        }).ToList();
        var snapshot = new EvaluationConfig { Version = 12, Indicators = [new() { Id = "section", Name = "互联互通", Weight = 100,
            Children = [new() { Id = "child", Name = "通信与实测评价", Weight = 100, Items = items }] }] };
        var run = new VerifyRunResponse
        {
            TaskId = "pdf-layout", TaskName = "机床联网验收 / CNC-2026", RunId = "layout-sample-run", Status = "completed",
            Result = "需人工复核", StartedAt = "2026-09-29T09:00:00+08:00", CompletedAt = "2026-09-29T09:35:00+08:00",
            Detail = "样本结论：保留原始证据与评分依据。", EvaluationSnapshot = snapshot, OptionsSnapshot = new(),
            MachineSnapshot = new() { Name = "五轴加工中心", DeviceCode = "CNC-001", Host = "192.0.2.10", Port = 8193, Protocol = "FANUC" },
            Metrics = items.Select((item, index) => new VerifyMetricResult
            {
                MetricId = item.MetricId!, Code = "M-" + (index + 1), Name = item.Name, Measurement = 99.5, Unit = "%",
                ExecutionStatus = "completed", Result = "pass", Score = index == 0 ? 0 : index == 1 ? null : 100,
                MeasurementSource = "machine-adapter / verified-read", ScoreReason = "实测值与本轮配置档位比较，0 分保留，缺失评分不补值。",
                Detail = "连接成功 199 次 / 200 次；返回耗时 12.50 ms。", Evidence = [index == 0 ? body : "traceId=cnc-2026; result=OK"],
            }).ToList(),
        };
        var tasks = new TaskStore { Rows = [new() { Id = run.TaskId, Name = run.TaskName, Type = "machine", CreatedAt = DateTimeOffset.UtcNow,
            LastRunJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }] };
        var taskPdf = ((FileContentResult)new VerifyTasksController(tasks, null!).Export(run.TaskId, "pdf")).FileContents;
        var record = new KnowledgeRecord
        {
            MachineName = "五轴加工中心", MachineNo = "CNC-001", MachineModel = "VMC-850", ControlSystem = "FANUC 0i-MF",
            PartName = "精密壳体", PartFeature = "多面加工", TestLocation = "生产车间 A", Tester = "测试工程师",
            TestDate = "2026-09-29", Version = 3, Snapshot = snapshot,
            Scores = items.Select((item, index) => (item.Id, Score: index == 0 ? (double?)0 : index == 1 ? null : 100))
                .ToDictionary(value => value.Id, value => value.Score),
            TestResults = items.ToDictionary(item => item.Id, _ => "成功率 99.5%；199/200 samples；耗时 12.50 ms"),
            Remarks = items.Select((item, index) => (item.Id, Text: index == 0 ? body : "证据归档 traceId=cnc-2026"))
                .ToDictionary(value => value.Id, value => value.Text),
            Conclusion = "待补齐未评分项目后复核。", Suggestion = "保持原始记录、版本和权重口径一致。",
        };
        record.TestResults[items[2].Id] = string.Join("\n", Enumerable.Range(1, 9).Select(index => $"读取 {index} - 12.50 ms"))
            + "\n多行结果末尾";
        var knowledgePdf = new EvaluationKnowledgeStore(null!, tasks, null!, "pdf-layout-unused.json").ExportPdf(record);
        return new() { ["task-report"] = taskPdf, ["knowledge-report"] = knowledgePdf };
    }

    private static void CheckGeometry(byte[] bytes)
    {
        var pdf = Encoding.ASCII.GetString(bytes);
        var widthEntries = Regex.Match(pdf, @"/W \[((?:\d+ \[\d+\] *)+)\]").Groups[1].Value;
        var widths = Regex.Matches(widthEntries, @"(\d+) \[(\d+)\]")
            .ToDictionary(match => (char)int.Parse(match.Groups[1].Value), match => int.Parse(match.Groups[2].Value));
        Check(widths.Count > 0, "Missing embedded font widths");
        var pages = Regex.Matches(pdf, @"/Type /Page /Parent.*?/Contents (\d+) 0 R");
        Check(pages.Count > 1, "Sample must exercise pagination");
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var objectStart = pdf.IndexOf("\n" + pages[pageIndex].Groups[1].Value + " 0 obj\n", StringComparison.Ordinal);
            var streamStart = pdf.IndexOf("stream\n", objectStart, StringComparison.Ordinal) + 7;
            var streamEnd = pdf.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            var content = pdf[streamStart..streamEnd];
            var cells = Regex.Matches(content, @"([0-9.]+) ([0-9.]+) ([0-9.]+) ([0-9.]+) re S")
                .Select(match => Enumerable.Range(1, 4).Select(index => Number(match.Groups[index].Value)).ToArray()).ToList();
            foreach (Match match in Regex.Matches(content, @"BT /F1 ([0-9.]+) Tf 1 0 0 1 ([0-9.]+) ([0-9.]+) Tm <([A-F0-9]*)> Tj"))
            {
                var size = Number(match.Groups[1].Value);
                var left = Number(match.Groups[2].Value);
                var bottom = Number(match.Groups[3].Value);
                var value = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(match.Groups[4].Value));
                var right = left + value.Sum(character => widths[character]) * size / 1000;
                Check(left >= 50 && right <= 545.001 && bottom >= 40 && bottom <= 794, "Text outside page");
                if (size != 9 || bottom == 40) continue;
                Check(cells.Any(cell => Math.Abs(left - cell[0] - 5) < 0.002 && bottom >= cell[1] + 4
                    && bottom + size <= cell[1] + cell[3] - 4 && right <= cell[0] + cell[2] - 4.999), "Text outside table cell");
            }
            Check(Strings(content).Contains($"{pageIndex + 1} / {pages.Count}"), "Wrong page number");
            Check(cells.Count > 0 && content.Contains("0.9 g"), "Missing repeated table header");
        }
    }

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static List<string> Strings(string pdf) => Regex.Matches(pdf, @"<([A-F0-9]*)> Tj")
        .Select(match => Encoding.BigEndianUnicode.GetString(Convert.FromHexString(match.Groups[1].Value))).ToList();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TaskStore : IVerifyTaskStore
    {
        public List<VerifyTaskDto> Rows { get; init; } = [];
        public List<VerifyTaskDto> ReadAll() => Rows;
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => throw new NotSupportedException();
    }
}
