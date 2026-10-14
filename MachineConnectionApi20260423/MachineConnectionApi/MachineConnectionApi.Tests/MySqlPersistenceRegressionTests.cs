using MachineConnectionApi.Controllers;
using MachineConnectionApi.Data;
using MachineConnectionApi.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MachineConnectionApi.Tests;

internal static class MySqlPersistenceRegressionTests
{
    public static async Task RunAll()
    {
        var connection = Environment.GetEnvironmentVariable("MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "--mysql requires MYSQL_TEST_CONNECTION with permission to create and drop an isolated test database.");

        var connectionBuilder = new MySqlConnectionStringBuilder(connection)
        {
            Database = $"iott_gateway_test_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<MCConfigurationDbContext>()
            .UseMySql(connectionBuilder.ConnectionString, new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;
        await using var database = new MCConfigurationDbContext(options);
        try
        {
            Require(await database.Database.EnsureCreatedAsync(), "The isolated MySQL schema must be created.");
            Require(!await database.Database.EnsureCreatedAsync(), "Schema initialization must be repeatable.");
            await VerifyPointLifecycle(database);
            await VerifyUniqueIndex(database);
        }
        finally
        {
            await database.Database.EnsureDeletedAsync();
        }
    }

    private static async Task VerifyPointLifecycle(MCConfigurationDbContext database)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var controller = new DatacollectionController(database, null!, null!, null!, null!,
            loggerFactory.CreateLogger<DatacollectionController>());
        var deviceId = new string('设', 64);
        var firstPath = new string('路', 499) + "甲";
        var secondPath = new string('路', 499) + "乙";
        var request = new DatacollectionSyncRequest(deviceId,
            [new("温度🌡", firstPath, "Double", 250), new("压力", secondPath, "Float", 500)],
            [firstPath, secondPath]);
        var savedResult = await controller.Sync(request, CancellationToken.None);
        Require(savedResult is OkObjectResult,
            $"Saving Chinese points with maximum-length device/path values failed: {(savedResult as ObjectResult)?.Value}");
        database.ChangeTracker.Clear();
        var original = await database.Datacollections.AsNoTracking().SingleAsync(point => point.Path == firstPath);
        Require(original.Id > 0 && original.Name == "温度🌡" && original.CollectionFrequency == 250,
            "Generated identity, utf8mb4 text, and frequency must round-trip.");
        Require(original.Datetime.HasValue && original.Datetime.Value.TimeOfDay == TimeSpan.Zero,
            "The saved date must round-trip without a time component.");
        Require((await controller.GetPaths(deviceId, CancellationToken.None)).Value?.Count == 2,
            "Distinct long paths sharing their first 499 characters must both be queryable.");

        var update = new DatacollectionSyncRequest(deviceId,
            [new("更新温度", firstPath, "Int32", 1000)], [firstPath, secondPath]);
        Require(await controller.Sync(update, CancellationToken.None) is OkObjectResult,
            "Updating a point and deselecting another must succeed in one transaction.");
        database.ChangeTracker.Clear();
        var updated = await database.Datacollections.AsNoTracking().SingleAsync();
        Require(updated.Id == original.Id && updated.Name == "更新温度" && updated.Datatype == "Int32" &&
            updated.CollectionFrequency == 1000 && updated.Protocol == "IndustrialIoT",
            "Updating must preserve identity and persist the edited fields while removing deselected points.");
        var list = (await controller.List(deviceId, CancellationToken.None)).Value;
        Require(list?.Count == 1 && list[0].Path == firstPath,
            "The point listing must return the saved long path through MySQL query translation.");
    }

    private static async Task VerifyUniqueIndex(MCConfigurationDbContext database)
    {
        database.ChangeTracker.Clear();
        var saved = await database.Datacollections.AsNoTracking().SingleAsync();
        database.Datacollections.Add(new Datacollection
        {
            DeviceId = "other-device", Path = saved.Path, Name = "共享路径", Datatype = "Double"
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        Require(await database.Datacollections.CountAsync() == 2,
            "The same path must be allowed for different devices.");
        database.Datacollections.Add(new Datacollection
        {
            DeviceId = saved.DeviceId, Path = saved.Path, Name = "重复路径", Datatype = "Double"
        });
        try
        {
            await database.SaveChangesAsync();
            throw new InvalidOperationException("The unique device_id + path index did not reject a duplicate.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            database.ChangeTracker.Clear();
        }
        Require(await database.Datacollections.CountAsync() == 2,
            "A duplicate insert must leave existing points intact.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
