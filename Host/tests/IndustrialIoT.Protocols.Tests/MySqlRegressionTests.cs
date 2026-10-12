using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

internal static class MySqlRegressionTests
{
    private static DbContextOptions<IoTDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<IoTDbContext>().UseMySql(connectionString,
            new MySqlServerVersion(Version.Parse(Environment.GetEnvironmentVariable("MYSQL_TEST_SERVER_VERSION")
                ?? "8.4.0"))).Options;

    internal static DbContextOptions<IoTDbContext> CreateIsolatedOptions()
    {
        var connectionString = Environment.GetEnvironmentVariable("MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set MYSQL_TEST_CONNECTION to opt into isolated MySQL database tests.");
        var connection = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = "IOTTRegression_" + Guid.NewGuid().ToString("N")
        };
        return Options(connection.ConnectionString);
    }

    public static Task ModelAsync()
    {
        using var database = new IoTDbContext(Options("Server=127.0.0.1;Database=ModelOnly;User ID=model;"));
        var script = database.Database.GenerateCreateScript();
        TestSupport.Require(script.Contains("utf8mb4") && script.Contains("longtext")
            && !script.Contains("nvarchar"), "Schema must use MySQL Unicode and long text types");
        var timestamps = database.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
                == typeof(DateTimeOffset)).ToArray();
        TestSupport.Require(timestamps.Length >= 6, "Expected persisted timestamps in the model");
        foreach (var property in timestamps)
        {
            TestSupport.Require(property.GetColumnType() == "datetime(6)", "Timestamp precision was not configured");
            var converter = property.GetTypeMapping().Converter!;
            var timestamp = new DateTimeOffset(2026, 9, 28, 14, 30, 15, TimeSpan.FromHours(8));
            var stored = (DateTime)converter.ConvertToProvider(timestamp)!;
            var restored = (DateTimeOffset)converter.ConvertFromProvider(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified))!;
            TestSupport.Require(stored == timestamp.UtcDateTime && restored == timestamp
                && restored.Offset == TimeSpan.Zero, "Timestamp converter did not preserve the UTC instant");
        }
        return Task.CompletedTask;
    }

    public static async Task RoundtripAsync()
    {
        var options = CreateIsolatedOptions();
        await using var database = new IoTDbContext(options);
        await DatabaseBootstrapper.InitializeAsync(database);
        try
        {
            await DatabaseBootstrapper.InitializeAsync(database);
            var timestamp = new DateTimeOffset(2026, 9, 28, 14, 30, 15, TimeSpan.FromHours(8)).AddTicks(123450);
            var device = new Device
            {
                Name = "MySQL \u8bbe\u5907 \U0001F680", Type = DeviceType.Robot, Brand = "HSR", Model = "HSR",
                Protocol = ProtocolType.HuazhongRobot, ConnectionConfig = new() { Host = "127.0.0.1", Port = 502 },
                CreatedAt = timestamp, LastSeenAt = null, Metadata = new() { ["description"] = new string('x', 80000) }
            };
            database.Devices.Add(device);
            var program = new NCProgram
            {
                DeviceId = device.Id, FileName = "main.nc", RemotePath = "/main.nc", StartedAt = timestamp,
                CompletedAt = timestamp.ToOffset(TimeSpan.FromHours(-5)).AddSeconds(1)
            };
            database.NCPrograms.Add(program);
            var record = new RealtimeDataRecord
            {
                DeviceId = device.Id, GroupName = "Main", PayloadJson = "{\"value\":42}",
                CollectedAt = timestamp, StoredAt = timestamp.AddSeconds(2)
            };
            database.RealtimeDataRecords.Add(record);
            await database.SaveChangesAsync();
            await using var check = new IoTDbContext(options);
            var storedDevice = await check.Devices.SingleAsync();
            TestSupport.Require(storedDevice.Name == device.Name && storedDevice.Metadata["description"].Length == 80000
                && storedDevice.ConnectionConfig.Port == 502, "Unicode or JSON storage changed");
            RequireUtc(storedDevice.CreatedAt, timestamp);
            TestSupport.Require(storedDevice.LastSeenAt is null, "Null timestamp was not preserved");
            var storedProgram = await check.NCPrograms.SingleAsync();
            RequireUtc(storedProgram.StartedAt, timestamp);
            RequireUtc(storedProgram.CompletedAt!.Value, program.CompletedAt!.Value);
            var storedRecord = await check.RealtimeDataRecords.SingleAsync(item => item.CollectedAt >= timestamp);
            RequireUtc(storedRecord.CollectedAt, timestamp);
            RequireUtc(storedRecord.StoredAt, record.StoredAt);
            storedDevice.LastSeenAt = timestamp;
            storedProgram.CompletedAt = null;
            await check.SaveChangesAsync();
            check.ChangeTracker.Clear();
            RequireUtc((await check.Devices.SingleAsync()).LastSeenAt!.Value, timestamp);
            TestSupport.Require((await check.NCPrograms.SingleAsync()).CompletedAt is null, "Nullable timestamp update failed");
        }
        finally { await database.Database.EnsureDeletedAsync(); }
    }

    private static void RequireUtc(DateTimeOffset actual, DateTimeOffset expected) =>
        TestSupport.Require(actual == expected && actual.Offset == TimeSpan.Zero,
            "MySQL timestamp roundtrip must preserve microseconds and return UTC");
}
