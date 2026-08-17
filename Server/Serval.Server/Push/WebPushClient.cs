using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.Push;

/// <summary>What became of one attempt to deliver a message.</summary>
public enum PushOutcome
{
    /// <summary>The push service took it. Whether the device was awake is not knowable from here.</summary>
    Delivered,

    /// <summary>
    /// The subscription is dead and will never accept anything again — the browser threw it away,
    /// or the user revoked permission. The row should go.
    /// </summary>
    Expired,

    /// <summary>
    /// Something went wrong that may not still be wrong next time. Counted against the
    /// subscription, and enough of them in a row retires it.
    /// </summary>
    Failed,
}

/// <summary>
/// Posts an encrypted message to one push service.
///
/// <para>The only part of Serval that talks to the public internet on its own initiative. It sends
/// ciphertext it cannot itself read back — <see cref="WebPushCrypto"/> encrypts to the browser's
/// key, not to anything this holds — so what crosses that boundary is an opaque blob and a URL the
/// browser chose.</para>
/// </summary>
public sealed class WebPushClient
{
    private readonly HttpClient _http;
    private readonly VapidKeyStore _keys;
    private readonly VapidSigner _signer;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<WebPushClient> _logger;

    public WebPushClient(
        HttpClient http,
        VapidKeyStore keys,
        VapidSigner signer,
        IOptionsMonitor<ServerOptions> options,
        ILogger<WebPushClient> logger)
    {
        _http = http;
        _keys = keys;
        _signer = signer;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Encrypts <paramref name="payload"/> for <paramref name="subscription"/> and delivers it.
    ///
    /// <para>Never throws for a delivery problem. The caller is a background worker walking a list
    /// of devices, and one unreachable phone must not stop the other four from being told.</para>
    /// </summary>
    /// <param name="topic">
    /// Optional collapse key. Two messages queued for an offline device under the same topic
    /// collapse to the most recent, which is why the camera id goes here: a phone that has been in
    /// a pocket for an hour should light up once per camera, not once per alert.
    /// </param>
    public async Task<PushOutcome> SendAsync(
        PushSubscription subscription,
        ReadOnlyMemory<byte> payload,
        string? topic,
        CancellationToken cancellationToken)
    {
        PushOptions push = _options.CurrentValue.Push;

        try
        {
            var endpoint = new Uri(subscription.Endpoint);
            VapidKeyPair keys = await _keys.GetAsync(cancellationToken);
            byte[] body = WebPushCrypto.Encrypt(payload.Span, subscription.P256dh, subscription.Auth);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation(
                "Authorization", _signer.AuthorizationHeader(keys, endpoint, push.ContactUri));
            request.Headers.TryAddWithoutValidation("TTL", push.TtlSeconds.ToString());

            // An alert is the thing the whole product exists to be timely about, so it is worth
            // waking a dozing device for. Push services treat this as a hint, not an instruction.
            request.Headers.TryAddWithoutValidation("Urgency", "high");

            if (SafeTopic(topic) is { } collapse)
            {
                request.Headers.TryAddWithoutValidation("Topic", collapse);
            }

            using HttpResponseMessage response =
                await _http.SendAsync(request, cancellationToken);

            return await ClassifyAsync(response, subscription, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not send a notification to a device of {UserId}.", subscription.UserId);
            return PushOutcome.Failed;
        }
    }

    private async Task<PushOutcome> ClassifyAsync(
        HttpResponseMessage response,
        PushSubscription subscription,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return PushOutcome.Delivered;
        }

        // The two that mean "this endpoint is gone", and the only reason this method reads a status
        // code rather than trusting IsSuccessStatusCode. Everything else may be temporary.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _logger.LogInformation(
                "A device of {UserId} has unsubscribed; dropping it.", subscription.UserId);
            return PushOutcome.Expired;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);

        // 413 is a bug rather than weather: the payload is composed here and is meant to be a few
        // hundred bytes, so it says the composition is wrong rather than that the network is.
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            _logger.LogError(
                "A push service rejected a notification as too large. {Detail}", detail);
        }
        else
        {
            _logger.LogWarning(
                "A push service refused a notification for {UserId} with {Status}. {Detail}",
                subscription.UserId, (int)response.StatusCode, detail);
        }

        return PushOutcome.Failed;
    }

    /// <summary>
    /// A collapse key a push service will accept: base64url characters only, and at most 32 of
    /// them. Camera ids are already close to that, but they are user-chosen and a rejected header
    /// would fail the whole send for the sake of an optimisation.
    /// </summary>
    private static string? SafeTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[32];
        var length = 0;

        foreach (char c in topic)
        {
            if (length == buffer.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                buffer[length++] = c;
            }
        }

        return length == 0 ? null : new string(buffer[..length]);
    }
}
