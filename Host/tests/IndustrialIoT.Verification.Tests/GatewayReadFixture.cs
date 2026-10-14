using System.Net;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests
{
    // Runs the actual Gateway read-contract regression source without building the Gateway/Host
    // projects. Attachment/transfer tests are intentionally NOT invoked in this read-only harness.
    internal static partial class EvaluationPipelineRegressionTests
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        public static Task RunReadContractOnly() => HttpMeasurementContract();
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static VerifyMeasurementTarget Target(string id = "fixture-machine", string host = "192.0.2.11", string protocol = "ModbusTCP") =>
            new() { Id = id, Name = id, Host = host, Port = 502, Protocol = protocol, Transfer = new() { Protocol = "FTP", Host = host, Port = 21 } };
        private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body, WebJson), Encoding.UTF8, "application/json") };
        private static EvaluationFile FixtureFile(double mib) => new()
        { Id = Guid.NewGuid().ToString("N"), Name = "fixture.nc", SizeBytes = (long)Math.Round(mib * 1024 * 1024) };
        private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }
        private sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
        {
            public int Count { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                return route(request, cancellationToken);
            }
        }
    }
}

namespace MachineConnectionApi.Services
{
    public sealed partial class HttpVerifyMeasurementAdapter
    {
        // Only the transfer-owned partial is substituted. Any accidental transfer is a hard failure,
        // never a fake successful measurement. The production read/directory implementation is linked.
        private Task<VerifyTransferObservation> TransferCoreAsync(VerifyMeasurementTarget target, EvaluationFile file,
            string directory, string remoteName, Action<double> reportFraction, CancellationToken ct) =>
            throw new InvalidOperationException("Transfers are forbidden in the read-only verification fixture.");
    }
}
