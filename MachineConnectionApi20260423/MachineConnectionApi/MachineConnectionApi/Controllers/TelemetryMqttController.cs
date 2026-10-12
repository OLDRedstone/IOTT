using IndustrialIoT.Messaging;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MachineConnectionApi.Controllers;

[ApiController]
[Route("api/telemetry/mqtt")]
public sealed class TelemetryMqttController(
    MqttTelemetryPublisher publisher, IHttpClientFactory clients, IConfiguration configuration) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var gateway = publisher.GetStatus();
        MqttForwardingStatus? host = null;
        string? hostError = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var path = configuration["IndustrialIoT:CollectionPath"] ?? "api/collection";
            host = await clients.CreateClient("IndustrialIoT")
                .GetFromJsonAsync<MqttForwardingStatus>($"{path.TrimEnd('/')}/mqtt/status", timeout.Token);
            if (host is null) hostError = "Host 返回了空的 MQTT 状态";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            hostError = "Host MQTT 状态暂不可用，请检查 Host 服务连接";
        }
        return Ok(new { gateway, host, hostError });
    }
}
