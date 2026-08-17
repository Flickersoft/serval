using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Serval.CameraModule;

/// <summary>
/// Delivers telemetry to the Serval server over HTTP, POSTing each batch as a JSON array to
/// <c>{ServerUrl}/api/cameras/{CameraId}/telemetry</c>. It only returns successfully once the
/// server accepts the batch (2xx) — any other outcome throws, so the outbox keeps the records
/// unsynced and retries. That is the whole point of the sink contract: at-least-once delivery
/// with the durable outbox behind it.
/// </summary>
public sealed class HttpTelemetrySink : ITelemetrySink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly ILogger<HttpTelemetrySink> _logger;

    public HttpTelemetrySink(
        IHttpClientFactory httpClientFactory,
        IOptions<CameraModuleOptions> options,
        ILogger<HttpTelemetrySink> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        OutputOptions output = options.Value.Output;
        if (string.IsNullOrWhiteSpace(output.ServerUrl) || string.IsNullOrWhiteSpace(output.CameraId))
        {
            throw new InvalidOperationException(
                "HttpTelemetrySink requires Output:ServerUrl and Output:CameraId to be set.");
        }

        _endpoint = new Uri(new Uri(output.ServerUrl, UriKind.Absolute),
            $"/api/cameras/{output.CameraId}/telemetry");
        _apiKey = output.ApiKey;
    }

    public async Task DeliverAsync(IReadOnlyList<string> jsonRecords, CancellationToken cancellationToken)
    {
        // The records arrive already serialized, so the batch is just their concatenation. This is
        // why the sink takes JSON rather than typed records: handing a list of IOutputRecord to
        // System.Text.Json would emit only the interface's members and every record would collapse
        // to nothing but its "type" field.
        string body = "[" + string.Join(",", jsonRecords) + "]";

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add("X-Api-Key", _apiKey);
        }

        HttpClient client = _httpClientFactory.CreateClient(nameof(HttpTelemetrySink));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Delivered {Count} record(s) to {Endpoint}", jsonRecords.Count, _endpoint);
    }
}
