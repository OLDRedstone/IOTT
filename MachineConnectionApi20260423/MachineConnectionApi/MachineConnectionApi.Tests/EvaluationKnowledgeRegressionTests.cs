using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MachineConnectionApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

internal static class EvaluationKnowledgeRegressionTests
{
    public static void RunAll()
    {
        using var fixture = new Fixture();
        ScoresVersionsAndSnapshots(fixture);
        SpreadsheetRoundTrip(fixture);
        BinaryImportAndController(fixture);
        ExportSummariesAndLargeSnapshot(fixture);
        TaskEvidenceAndDeduplication(fixture);
        MalformedSpreadsheetIsRejected();
        PdfContainsAllPages();
        PdfTablesWrapAndRepeatHeaders();
        EvaluationSpreadsheetRegressionTests.RunAll();
    }

    private static void ScoresVersionsAndSnapshots(Fixture fixture)
    {
        var original = fixture.Record();
        var first = original.Snapshot.Indicators[0].Children[0].Items[0];
        original.Scores[first.Id] = 0;
        var saved = fixture.Store.Create(original with { TotalScore = 999 });
        Check(saved.TotalScore is >= 0 and < 100 && saved.Scores[first.Id] == 0, "zero scores or recomputation failed");
        var nullable = fixture.Record();
        nullable.Scores[first.Id] = null;
        Check(fixture.Store.Create(nullable).TotalScore is null, "incomplete scoring must remain null");
        var zeroWeight = fixture.Record();
        var items = zeroWeight.Snapshot.Indicators[0].Children[0].Items;
        zeroWeight.Weights[first.Id] = 0;
        zeroWeight.Weights[items[1].Id] = items[1].Weight + first.Weight;
        zeroWeight.Scores[first.Id] = null;
        Check(fixture.Store.Create(zeroWeight).TotalScore == 100, "zero weight must remain zero");
        var rounded = fixture.Record();
        rounded.Weights[first.Id] = first.Weight - 0.01;
        Check(fixture.Store.Create(rounded).TotalScore == 100, "rounding tolerance must match editor weighting");
        Expect<ArgumentException>(() => fixture.Store.Create(fixture.Record() with { Snapshot = null! }));
        var changedConfig = fixture.Indicators.Get("machine");
        changedConfig.Indicators[0] = changedConfig.Indicators[0] with { Name = "updated-name" };
        fixture.Indicators.Save(changedConfig);
        var forged = saved with { Snapshot = changedConfig, Conclusion = "updated" };
        var updated = fixture.Store.Update(saved.Id, forged)!;
        Check(updated.Version == 2 && updated.Snapshot.Indicators[0].Name != "updated-name", "historical snapshot changed");
        Expect<KnowledgeConflictException>(() => fixture.Store.Update(saved.Id, saved));
        var bad = fixture.Record();
        bad.CategoryWeights[bad.Snapshot.Indicators[0].Id] = 99;
        var count = fixture.Store.List().Count;
        Expect<ArgumentException>(() => fixture.Store.Create(bad));
        Check(fixture.Store.List().Count == count, "validation persisted an invalid record");
        Expect<ArgumentException>(() => fixture.Store.Create(fixture.Record() with { TestDate = "2026-99-99" }));
    }

    private static void SpreadsheetRoundTrip(Fixture fixture)
    {
        var record = fixture.Record();
        var item = record.Snapshot.Indicators[0].Children[0].Items[0];
        record.Scores[item.Id] = 0;
        record.TestResults[item.Id] = "0";
        record.Remarks[item.Id] = "中文备注，真实结果";
        record = fixture.Store.Create(record);
        using var exported = new MemoryStream(fixture.Store.ExportSpreadsheet(record));
        var result = fixture.Store.Import(exported);
        Check(result.Success == 1 && result.Failed == 0, "spreadsheet import failed");
        var imported = fixture.Store.List().First(row => row.DataSource == "import");
        Check(imported.Scores[item.Id] == 0 && imported.TestResults[item.Id] == "0"
            && imported.Remarks[item.Id] == record.Remarks[item.Id], "spreadsheet lost scores or evidence");
        Check(imported.Snapshot.Version == record.Snapshot.Version, "spreadsheet lost snapshot");
        using var template = new MemoryStream(fixture.Store.Template());
        var templateRows = EvaluationSpreadsheetReader.Read(template);
        var header = templateRows[0].Cast<object?>().ToList();
        var valid = templateRows[1].Cast<object?>().ToList();
        foreach (var (column, value) in new[] { (0, "test"), (1, "no"), (2, "model"), (3, "control"), (4, "part"), (7, "2026-09-28") })
            valid[column] = value;
        var invalid = valid.ToList();
        invalid[7] = "invalid-date";
        using var batch = new MemoryStream(ExcelBuilder.Build("records", [header, valid, invalid]));
        var partial = fixture.Store.Import(batch);
        Check(partial.Total == 2 && partial.Success == 1 && partial.Failed == 1 && partial.Errors[0].StartsWith("第 3 行"), "partial import result invalid");
        var serialOne = valid.ToList();
        serialOne[1] = "excel-date-one";
        serialOne[7] = 1;
        var leapDay = valid.ToList();
        leapDay[7] = 60;
        var notFinite = valid.ToList();
        notFinite[7] = "NaN";
        var badScore = valid.ToList();
        var scoreColumn = templateRows[0].FindIndex(name => name.StartsWith("评分:", StringComparison.Ordinal));
        badScore[scoreColumn] = "Infinity";
        using var dates = new MemoryStream(ExcelBuilder.Build("dates", [header, serialOne, leapDay, notFinite, badScore]));
        var dateResult = fixture.Store.Import(dates);
        Check(dateResult is { Total: 4, Success: 1, Failed: 3 }
            && dateResult.Errors[0].StartsWith("第 3 行") && dateResult.Errors[2].StartsWith("第 5 行"),
            "invalid Excel dates or non-finite scores must produce per-row feedback");
        Check(fixture.Store.List().First(row => row.MachineNo == "excel-date-one").TestDate == "1900-01-01", "Excel 1900 date epoch is incorrect");
    }

    private static void BinaryImportAndController(Fixture fixture)
    {
        var controller = new EvaluationKnowledgeController(fixture.Store);
        using var binary = new MemoryStream(EvaluationBiffFixture.Bytes());
        var file = new FormFile(binary, 0, binary.Length, "file", "真实二进制.XLS");
        var response = controller.Import(file) as OkObjectResult;
        var result = response?.Value as KnowledgeImportResult;
        Check(result is { Total: 2, Success: 1, Failed: 1 } && result.Errors[0].StartsWith("第 4 行"),
            "BIFF partial import must keep physical row errors");
        var imported = fixture.Store.List().First(row => row.MachineNo == "BIFF-001");
        Check(imported.Scores["i_1"] == 0 && imported.Scores["i_2"] is null && imported.TotalScore is null,
            "BIFF import fabricated a missing score or discarded zero");
        Check(imported.TestDate == "2026-09-28" && imported.TestResults["i_1"] == "0"
            && imported.Remarks["i_1"] == "中文备注，二进制真实结果", "BIFF import lost dates or evidence");
        using var fake = new MemoryStream(Encoding.UTF8.GetBytes("not an Excel workbook"));
        Check(controller.Import(new FormFile(fake, 0, fake.Length, "file", "fake.xls")) is BadRequestObjectResult,
            "renaming arbitrary bytes must not produce a successful XLS import");
        foreach (var category in new[] { "machine", "machining" })
        {
            var template = (FileContentResult)controller.Template(category);
            using var stream = new MemoryStream(template.FileContents);
            var rows = EvaluationSpreadsheetReader.Read(stream);
            Check(rows[1][9] == category, "template category query was ignored");
        }
        Check(controller.Template("unknown") is BadRequestObjectResult, "invalid template category accepted");
    }

    private static void ExportSummariesAndLargeSnapshot(Fixture fixture)
    {
        var snapshot = new EvaluationConfig
        {
            Indicators =
            [
                new EvaluationSection { Id = "section-a", Name = "分类甲", Weight = 40, Children =
                [
                    new EvaluationSubcategory { Id = "child-a1", Name = "二级甲一", Weight = 25, Items =
                    [new EvaluationItem { Id = "zero", Name = "零分项目", Weight = 25 }, new EvaluationItem { Id = "full", Name = "满分项目", Weight = 75 }] },
                    new EvaluationSubcategory { Id = "child-a2", Name = "二级甲二", Weight = 75, Items =
                    [new EvaluationItem { Id = "other", Name = "其他项目", Weight = 100 }] },
                ] },
                new EvaluationSection { Id = "section-b", Name = "分类乙", Weight = 60, Children =
                [new EvaluationSubcategory { Id = "child-b", Name = "二级乙", Weight = 100, Items =
                    [new EvaluationItem { Id = "half", Name = "半分项目", Weight = 100 }] }] },
            ],
        };
        var record = fixture.Store.Create(fixture.Record() with { Snapshot = snapshot,
            Scores = new() { ["zero"] = 0, ["full"] = 100, ["other"] = 100, ["half"] = 50 } });
        var summary = SpreadsheetValues(fixture.Store.ExportSpreadsheet(record));
        Check(record.TotalScore == 67.5 && summary["综合得分"] == "67.5", "weighted total changed");
        Check(summary["分类得分:section-a:分类甲"] == "93.75" && summary["分类加权贡献:section-a:分类甲"] == "37.5"
            && summary["二级小计:child-a1:二级甲一"] == "75" && summary["二级分类内贡献:child-a1:二级甲一"] == "18.75"
            && summary["二级总分贡献:child-a1:二级甲一"] == "7.5", "Excel omitted or miscomputed hierarchical summary");
        Check(summary["评分:zero:零分项目"] == "0" && summary["评分口径"].Contains("简单平均"), "zero score or scoring basis missing");
        var pdf = Encoding.ASCII.GetString(fixture.Store.ExportPdf(record));
        Check(PdfStrings(pdf).Contains("分类得分与总分贡献") && PdfStrings(pdf).Contains("二级小计与加权贡献"),
            "PDF summary tables missing");

        record.Scores["full"] = null;
        var incomplete = fixture.Store.Create(record);
        var incompleteValues = SpreadsheetValues(fixture.Store.ExportSpreadsheet(incomplete));
        Check(incomplete.TotalScore is null && incompleteValues["分类得分:section-a:分类甲"] == ""
            && incompleteValues["分类加权贡献:section-a:分类甲"] == "", "unrated summaries must remain blank, not zero");
        using (var stream = new MemoryStream(fixture.Store.ExportSpreadsheet(incomplete)))
            Check(fixture.Store.Import(stream) is { Success: 1, Failed: 0 }, "summary columns broke reimport");
        var zeroWeighted = fixture.Store.Create(record with { CategoryWeights = new() { ["section-a"] = 0, ["section-b"] = 100 } });
        var zeroWeightedValues = SpreadsheetValues(fixture.Store.ExportSpreadsheet(zeroWeighted));
        Check(zeroWeighted.TotalScore == 50 && zeroWeightedValues["分类加权贡献:section-a:分类甲"] == "0", "zero-weight contribution is not zero");

        var longDescription = string.Concat(Enumerable.Repeat("中文快照 带空格 ", 1500));
        snapshot.Indicators[0].Children[0].Items[0] = snapshot.Indicators[0].Children[0].Items[0] with { Desc = longDescription };
        var large = fixture.Store.Create(record with { Snapshot = snapshot, MachineNo = "large-snapshot" });
        var largeBytes = fixture.Store.ExportSpreadsheet(large);
        var largeValues = SpreadsheetValues(largeBytes);
        Check(largeValues.ContainsKey("指标快照JSON分段:2"), "large snapshots must be split within Excel cell limits");
        using (var stream = new MemoryStream(largeBytes))
            Check(fixture.Store.Import(stream) is { Success: 1, Failed: 0 }, "large snapshot no longer roundtrips");
        var reimported = fixture.Store.List().First(row => row.MachineNo == "large-snapshot" && row.DataSource == "import");
        Check(reimported.Snapshot.Indicators[0].Children[0].Items[0].Desc == longDescription, "snapshot continuation lost text or whitespace");
    }

    private static Dictionary<string, string> SpreadsheetValues(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var rows = EvaluationSpreadsheetReader.Read(stream);
        return rows[0].Select((header, index) => (header, value: index < rows[1].Count ? rows[1][index] : ""))
            .ToDictionary(pair => pair.header, pair => pair.value);
    }

    private static void TaskEvidenceAndDeduplication(Fixture fixture)
    {
        var config = fixture.Indicators.Get("machine");
        var metric = config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items)
            .First(item => item.MetricId is not null);
        var run = new VerifyRunResponse
        {
            RunId = "run-1", TaskId = "task-1", Status = "completed", CompletedAt = "2026-09-28T09:00:00+08:00",
            EvaluationSnapshot = config,
            MachineSnapshot = new EvaluationMachineSnapshot
            {
                Id = "historical-device", Name = "历史机床", DeviceCode = "history-code",
                Model = "history-model", ControlSystem = "history-control",
            },
            Metrics = [new VerifyMetricResult { MetricId = metric.MetricId!, Value = "0", Detail = "measured detail", Evidence = ["real evidence"] }],
        };
        fixture.Tasks.Rows =
        [
            new VerifyTaskDto { Id = "task-1", Name = "task", Type = "machine", DeviceId = "edited-device",
                CreatedAt = DateTimeOffset.UtcNow, LastRunJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)) },
            new VerifyTaskDto { Id = "task-2", Name = "unexecuted", Type = "machine", CreatedAt = DateTimeOffset.UtcNow },
        ];
        var tasks = fixture.Store.ListTasks();
        Check(tasks.Count == 1 && tasks[0].MachineName == "历史机床", "sync listed fake tasks or edited device");
        var draft = fixture.Store.TaskDraft("task-1");
        Check(draft.Scores.Values.All(score => score is null), "sync fabricated evaluation scores");
        Check(draft.TestResults[metric.Id] == "0" && draft.Remarks[metric.Id].Contains("real evidence"), "sync lost raw evidence");
        Check(draft.SyncRunId == "run-1" && draft.MachineNo == "history-code", "sync provenance missing");
        var count = fixture.Store.List().Count;
        fixture.Store.TaskDraft("task-1");
        Check(fixture.Store.List().Count == count, "draft was persisted");
        var complete = draft with { PartName = "part" };
        fixture.Store.Create(complete);
        Expect<KnowledgeConflictException>(() => fixture.Store.Create(complete));
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task-2"));
        Expect<ArgumentException>(() => fixture.Store.TaskDraft("task-1", "machining"));
        run.EvaluationSnapshot = fixture.Indicators.Get("machining");
        run.RunId = "run-machining";
        fixture.Tasks.Rows[0] = fixture.Tasks.Rows[0] with
        { LastRunJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var machining = fixture.Store.TaskDraft("task-1");
        Check(machining.Category == "machining" && machining.Scores.Count == 35, "draft lost recorded category");
        Check(fixture.Store.ListTasks()[0].Category == "machining", "task category was not retained");
    }

    private static void MalformedSpreadsheetIsRejected()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("xl/workbook.xml").Open());
            writer.Write("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///never-read'>]><a>&x;</a>");
        }
        buffer.Position = 0;
        Expect<System.Xml.XmlException>(() => EvaluationSpreadsheetReader.Read(buffer));
        using var oversized = new MemoryStream();
        using (var archive = new ZipArchive(oversized, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("large").Open();
            stream.Write(new byte[21 * 1024 * 1024]);
        }
        oversized.Position = 0;
        Expect<ArgumentException>(() => EvaluationSpreadsheetReader.Read(oversized));
    }

    private static void PdfContainsAllPages()
    {
        var bytes = EvaluationPdfBuilder.Build(Enumerable.Range(0, 120).Select(index => $"评价报告第 {index} 行"));
        var text = Encoding.ASCII.GetString(bytes);
        Check(text.StartsWith("%PDF-1.4") && text.Contains("/Count 3") && text.Contains("/ToUnicode"),
            "PDF pagination or Unicode mapping missing");
        Check(text.Contains(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("评价报告第 119 行"))), "PDF truncated report");
    }

    private static void PdfTablesWrapAndRepeatHeaders()
    {
        var body = string.Concat(Enumerable.Repeat("中文长文本验证跨页不能截断", 500));
        var table = new EvaluationPdfTable("跨页明细表", ["项目", "证据"],
            [new[] { "首个项目", body + "最后证据" }, new[] { "末个项目", "保留零分 0 和未评分" }], [1, 3]);
        var previousCulture = CultureInfo.CurrentCulture;
        byte[] bytes;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            bytes = EvaluationPdfBuilder.BuildReport("统一报告标题", ["介绍"], [table], ["结尾保留"]);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        var pdf = Encoding.ASCII.GetString(bytes);
        var pageCount = int.Parse(Regex.Match(pdf, @"/Count (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var strings = PdfStrings(pdf);
        Check(pageCount > 3 && strings.Count(text => text == "统一报告标题") == pageCount,
            "report title must repeat on every table page");
        Check(strings.Count(text => text == "跨页明细表") > 1 && strings.Count(text => text == "证据") > 1,
            "continuation pages need table title and column headers");
        // All source characters must survive wrapping and page breaks, not merely the last row marker.
        Check(strings.Sum(text => text.Count(character => character == '中')) == body.Count(character => character == '中')
            && string.Concat(strings).Contains("最后证据") && strings.Contains("结尾保留") && strings.Contains("末个项目"),
            "PDF table rows or closing text were truncated");
        Check(pdf.Contains(" re S") && pdf.Contains("/FontFile2") && pdf.Contains("/ToUnicode"), "PDF table grid or embedded Unicode font missing");
        var widths = Regex.Matches(Regex.Match(pdf, @"/W \[((?:\d+ \[\d+\] *)+)\]").Groups[1].Value, @"(\d+) \[(\d+)\]")
            .ToDictionary(match => (char)int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match => int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        Check(widths.Count > 0, "PDF must declare the embedded font's actual character advances");
        foreach (Match match in Regex.Matches(pdf, @"BT /F1 ([0-9.]+) Tf 1 0 0 1 ([0-9.]+) ([0-9.]+) Tm <([A-F0-9]*)> Tj"))
        {
            var size = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var x = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var y = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var text = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(match.Groups[4].Value));
            Check(x >= 50 && x + text.Sum(character => widths[character]) * size / 1000 <= 545.001 && y is >= 40 and <= 794,
                "PDF text escaped printable page bounds");
        }
        Expect<ArgumentException>(() => EvaluationPdfBuilder.BuildReport("invalid", [],
            [new EvaluationPdfTable("table", ["one", "two"], [new[] { "missing" }])]));
        Expect<ArgumentException>(() => EvaluationPdfBuilder.BuildReport("invalid", [],
            [new EvaluationPdfTable("table", ["one", "two"], [], [double.NaN, 1])]));
    }

    private static List<string> PdfStrings(string pdf) => Regex.Matches(pdf, @"<([A-F0-9]*)> Tj")
        .Select(match => Encoding.BigEndianUnicode.GetString(Convert.FromHexString(match.Groups[1].Value))).ToList();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _indicatorFile = $"evaluation-test-indicators-{Guid.NewGuid():N}.json";
        private readonly string _recordFile = $"evaluation-test-records-{Guid.NewGuid():N}.json";
        public EvaluationIndicatorStore Indicators { get; }
        public TaskStore Tasks { get; } = new();
        public EvaluationKnowledgeStore Store { get; }

        public Fixture()
        {
            Indicators = new(_indicatorFile);
            Store = new(Indicators, Tasks, new DeviceStoreFake(), _recordFile);
        }

        public KnowledgeRecord Record()
        {
            var config = Indicators.Get("machine");
            return new KnowledgeRecord
            {
                MachineName = "测试机床", MachineNo = "machine-code", MachineModel = "model", ControlSystem = "control",
                PartName = "part", TestDate = "2026-09-28", Snapshot = config,
                Scores = config.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items)
                    .ToDictionary(item => item.Id, _ => (double?)100),
            };
        }

        public void Dispose()
        {
            foreach (var fileName in new[] { _indicatorFile, _recordFile })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private sealed class TaskStore : IVerifyTaskStore
    {
        public List<VerifyTaskDto> Rows { get; set; } = [];
        public List<VerifyTaskDto> ReadAll() => Rows;
        public void WriteAll(IEnumerable<VerifyTaskDto> items) => Rows = items.ToList();
        public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => update(Rows);
    }

    private sealed class DeviceStoreFake : IDeviceStore
    {
        public List<MachineDeviceDto> ReadAll() => [];
        public void WriteAll(IEnumerable<MachineDeviceDto> items) => throw new NotSupportedException();
        public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) => throw new NotSupportedException();
    }
}
