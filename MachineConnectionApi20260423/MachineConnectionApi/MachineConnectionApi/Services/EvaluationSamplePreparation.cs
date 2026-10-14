namespace MachineConnectionApi.Services;

using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

public sealed class EvaluationSamplePreparation(EvaluationIndicatorStore store, string attachmentDirectory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EvaluationConfig> PrepareAsync(string category, int version, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var config = store.Get(category);
            if (config.Version != version) throw new EvaluationConfigConflictException();
            var sections = new List<EvaluationSection>();
            foreach (var section in config.Indicators)
            {
                var children = new List<EvaluationSubcategory>();
                foreach (var child in section.Children)
                {
                    var items = new List<EvaluationItem>();
                    foreach (var item in child.Items) items.Add(await PrepareItemAsync(item, ct));
                    children.Add(child with { Items = items });
                }
                sections.Add(section with { Children = children });
            }
            ct.ThrowIfCancellationRequested();
            return store.Save(config with { Indicators = sections });
        }
        finally { Gate.Release(); }
    }

    private async Task<EvaluationItem> PrepareItemAsync(EvaluationItem item, CancellationToken ct)
    {
        if (item.Automation is not { } rule || item.MetricId is not { } metricId) return item;
        var prototypeNames = new[] { "test_0.1MB.nc", "test_1MB.nc", "test_10MB.nc", "test_20MB.nc",
            "speed_test_10M.nc", "max_test_1M.nc", "max_test_10M.nc", "max_test_200M.nc" };
        var files = item.Files.Where(file => !string.IsNullOrWhiteSpace(file.Id)
            || !prototypeNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var size in RequiredBytes(metricId))
        {
            var matches = files.Where(file => file.SizeBytes == size && IsAvailable(file)).ToList();
            var sample = matches.FirstOrDefault() ?? await CreateSampleAsync(size, ct);
            files.RemoveAll(file => file.SizeBytes == size);
            files.Add(sample);
        }
        return item with
        {
            Automation = rule with
            {
                PassRule = rule.PassRule ?? EvaluationAutomationRules.DefaultCriterionFor(metricId),
                Test = metricId == "max-connections" && rule.Test.MaxConnections == 4
                    ? rule.Test with { MaxConnections = 100 } : rule.Test,
            },
            Files = files,
            Protocols = metricId == "transfer-protocol" && item.Protocols.Count == 2
                && item.Protocols.Contains("FTP") && item.Protocols.Contains("OPC UA")
                    ? ["FTP", "NFS", "SMB"] : item.Protocols,
        };
    }

    public static long[] RequiredBytes(string metricId) => metricId switch
    {
        "transfer-protocol" => [104858],
        "file-integrity" => [104858, 1048576, 10485760, 20971520],
        "transfer-speed" => [10485760],
        "file-size" => [1048576, 10485760, 209715200],
        _ => [],
    };

    private bool IsAvailable(EvaluationFile file)
    {
        if (!Guid.TryParseExact(file.Id, "N", out var id)) return false;
        var path = Path.Combine(attachmentDirectory, id.ToString("N") + ".bin");
        var metadata = Path.Combine(attachmentDirectory, id.ToString("N") + ".json");
        if (!File.Exists(path) || !File.Exists(metadata) || new FileInfo(path).Length != file.SizeBytes) return false;
        try
        {
            var saved = JsonSerializer.Deserialize<EvaluationFile>(File.ReadAllText(metadata), Json);
            return saved is not null && saved.Id == file.Id && saved.Name == file.Name && saved.SizeBytes == file.SizeBytes;
        }
        catch (JsonException) { return false; }
    }

    private async Task<EvaluationFile> CreateSampleAsync(long size, CancellationToken ct)
    {
        Directory.CreateDirectory(attachmentDirectory);
        var name = $"verification-sample-{size}-bytes.txt";
        foreach (var metadataPath in Directory.EnumerateFiles(attachmentDirectory, "*.json"))
        {
            EvaluationFile? existing;
            try { existing = JsonSerializer.Deserialize<EvaluationFile>(await File.ReadAllTextAsync(metadataPath, ct), Json); }
            catch (JsonException) { continue; }
            if (existing is not null && existing.Name == name && existing.SizeBytes == size && IsAvailable(existing)) return existing;
        }
        var id = Guid.NewGuid().ToString("N");
        var binaryPath = Path.Combine(attachmentDirectory, id + ".bin");
        var metadataFile = Path.Combine(attachmentDirectory, id + ".json");
        var sample = new EvaluationFile { Id = id, Name = name, SizeBytes = size, Size = $"{size / 1048576d:0.###} MiB" };
        var completed = false;
        try
        {
            var pattern = Encoding.ASCII.GetBytes("IOTT verification sample; text data only.\r\n");
            var buffer = new byte[65536];
            for (var index = 0; index < buffer.Length; index++) buffer[index] = pattern[index % pattern.Length];
            await using (var stream = new FileStream(binaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (long written = 0; written < size; written += buffer.Length)
                    await stream.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - written)), ct);
            }
            await File.WriteAllTextAsync(metadataFile, JsonSerializer.Serialize(sample, Json), ct);
            completed = true;
            return sample;
        }
        finally
        {
            if (!completed)
            {
                if (File.Exists(binaryPath)) File.Delete(binaryPath);
                if (File.Exists(metadataFile)) File.Delete(metadataFile);
            }
        }
    }
}
