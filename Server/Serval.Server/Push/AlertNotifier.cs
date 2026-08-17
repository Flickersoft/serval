using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Serval.Server.Alerts;
using Serval.Server.Auth;
using Serval.Server.Configuration;
using Serval.Server.Preferences;

namespace Serval.Server.Push;

/// <summary>
/// Turns a raised alert into a notification on everybody's phone who asked for it.
///
/// <para><b>One push per alert row, and the row's own words.</b> The queue's premise is that every
/// row was worth interrupting somebody for, so this sends exactly the rows and adds no judgement of
/// its own. <see cref="Alert.Title"/> is composed once, server-side, and reused verbatim here —
/// which is why a notification and the queue entry it belongs to can never disagree about what
/// happened.</para>
///
/// <para><b>It hangs off the raise, not the event bus.</b> <see cref="AlertService"/> hands work
/// here only after the insert actually created a row, so the once-a-frame duplicate path costs
/// nothing and each alert notifies exactly once. Subscribing to the broadcaster instead would see
/// every alert twice — once on raise and again when the clip settles — and would need to remember
/// which was which.</para>
///
/// <para><b>Nothing here may make the caller wait.</b> The caller is the detection loop. This takes
/// a slot in a bounded channel and returns; a full channel drops the notification and says so,
/// because a camera must never stall behind somebody's push service.</para>
///
/// <para><b>The one judgement it does make is about rate.</b> <see cref="NotificationCooldown"/>
/// holds back a notification about something this person was just told about. That is not a second
/// opinion on the row — the row is already written and already on screen — but on whether a phone
/// needs to buzz about it twice. It is a private field rather than an injected service, and the
/// reason is on that class.</para>
/// </summary>
public sealed class AlertNotifier : BackgroundService
{
    /// <summary>
    /// How many alerts may be waiting to go out. Deep enough for every camera to alert at once
    /// several times over, and bounded because the alternative to dropping under a sustained flood
    /// is growing until the process dies — and the queue on screen is the durable record either way.
    /// </summary>
    private const int QueueCapacity = 256;

    private readonly Channel<PendingNotification> _queue =
        Channel.CreateBounded<PendingNotification>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly NotificationCooldown _cooldown = new();

    private readonly PushSubscriptionRepository _subscriptions;
    private readonly UserPreferencesRepository _preferences;
    private readonly WebPushClient _client;
    private readonly TokenService _tokens;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<AlertNotifier> _logger;

    public AlertNotifier(
        PushSubscriptionRepository subscriptions,
        UserPreferencesRepository preferences,
        WebPushClient client,
        TokenService tokens,
        IOptionsMonitor<ServerOptions> options,
        ILogger<AlertNotifier> logger)
    {
        _subscriptions = subscriptions;
        _preferences = preferences;
        _client = client;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Queues an alert for delivery. Returns immediately, always.
    ///
    /// Takes the composed <see cref="AlertResponse"/> rather than an id because the caller has just
    /// built one — re-reading the alert to learn what the caller already knows would be a database
    /// round trip per alert for nothing.
    /// </summary>
    public void Enqueue(AlertResponse alert)
    {
        if (!_options.CurrentValue.Push.Enabled)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(new PendingNotification(alert)))
        {
            _logger.LogWarning(
                "Dropped a notification for alert {AlertId}: more than {Capacity} are already "
                + "waiting to be sent.", alert.Id, QueueCapacity);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (PendingNotification item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await NotifyAsync(item.Alert, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One alert's delivery failing must not end the worker; the next alert is still
                // worth trying, and the row is on screen regardless.
                _logger.LogError(
                    ex, "Could not send notifications for alert {AlertId}.", item.Alert.Id);
            }
        }
    }

    private async Task NotifyAsync(AlertResponse alert, CancellationToken cancellationToken)
    {
        List<PushSubscription> subscriptions = await _subscriptions.ListAllAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        AlertKind kind = string.Equals(alert.Kind, "sound", StringComparison.OrdinalIgnoreCase)
            ? AlertKind.Sound
            : AlertKind.Object;

        // Grouped by account because the rules are the account's, and one person's five devices
        // ask the same question five times otherwise.
        foreach (IGrouping<string, PushSubscription> byUser in
            subscriptions.GroupBy(s => s.UserId, StringComparer.Ordinal))
        {
            UserPreferences preferences = await _preferences.GetAsync(byUser.Key, cancellationToken);

            if (!preferences.WantsNotifiedOf(alert.CameraId, kind, alert.Label))
            {
                continue;
            }

            // After the rule, because the window's value comes from the rule and because a label
            // this person has muted must not spend their cooldown slot. Before Compose, which mints
            // a stream token — a credential issued for a notification nobody will receive.
            //
            // Dated from the alert rather than from the wall clock, so the window means "two alerts
            // a minute apart" rather than "two deliveries a minute apart". The two are the same
            // until the channel backs up behind a slow push service, and the first is the one
            // somebody would recognise as the gap they asked for.
            if (!_cooldown.Allows(
                byUser.Key,
                alert.CameraId,
                kind,
                alert.Label,
                preferences.CooldownSecondsFor(
                    alert.CameraId, _options.CurrentValue.Push.CooldownSeconds),
                alert.At))
            {
                _logger.LogDebug(
                    "Held a notification to {UserId} for {Title}: they were told about it within "
                    + "the cooldown. Alert {AlertId} is in the queue either way.",
                    byUser.Key, alert.Title, alert.Id);

                continue;
            }

            byte[] payload = Compose(alert, byUser.Key);

            foreach (PushSubscription subscription in byUser)
            {
                await DeliverAsync(subscription, payload, alert, cancellationToken);
            }
        }
    }

    private async Task DeliverAsync(
        PushSubscription subscription,
        byte[] payload,
        AlertResponse alert,
        CancellationToken cancellationToken)
    {
        PushOutcome outcome =
            await _client.SendAsync(subscription, payload, alert.CameraId, cancellationToken);

        switch (outcome)
        {
            case PushOutcome.Delivered:
                await _subscriptions.RecordSuccessAsync(subscription.Id, cancellationToken);
                break;

            case PushOutcome.Expired:
                await _subscriptions.DeleteAsync(
                    subscription.Id, userId: null, cancellationToken);
                break;

            case PushOutcome.Failed:
                int failures =
                    await _subscriptions.RecordFailureAsync(subscription.Id, cancellationToken);

                if (failures >= _options.CurrentValue.Push.MaxFailures)
                {
                    _logger.LogInformation(
                        "Retiring a device of {UserId} after {Failures} failures in a row.",
                        subscription.UserId, failures);

                    await _subscriptions.DeleteAsync(
                        subscription.Id, userId: null, cancellationToken);
                }

                break;
        }
    }

    /// <summary>
    /// The message body, encrypted per subscription but composed once per account — the stream
    /// token in it is the account's, and every device of theirs may use the same one.
    /// </summary>
    private byte[] Compose(AlertResponse alert, string userId)
    {
        // The picture is the camera's live snapshot rather than the alert's own poster, and the
        // difference matters. The poster is cut from footage that does not exist yet: it 404s until
        // the clip settles, some seconds after the alert. The snapshot is served from memory and is
        // there now, within a second or two of the frame that fired — so the notification arrives
        // with a picture instead of a broken one.
        //
        // Browsers fetch a notification's image themselves, with no Authorization header available
        // to them, which is what the stream token in the URL is for. It rides inside the encrypted
        // payload, so the push service relaying this never sees it.
        var (streamToken, _) = _tokens.CreateStreamToken(userId, Role.Viewer);

        return JsonSerializer.SerializeToUtf8Bytes(new NotificationPayload(
            Id: alert.Id,
            CameraId: alert.CameraId,
            Title: alert.Title,
            Body: alert.At.ToLocalTime().ToString("HH:mm:ss"),
            Image: $"/api/cameras/{Uri.EscapeDataString(alert.CameraId)}/snapshot.jpg"
                + $"?stream_token={Uri.EscapeDataString(streamToken)}&at={alert.At.ToUnixTimeSeconds()}",
            Url: $"/alerts/{Uri.EscapeDataString(alert.Id)}",
            At: alert.At));
    }

    private sealed record PendingNotification(AlertResponse Alert);
}

/// <summary>
/// What the service worker receives. Deliberately small — a push payload has a hard ceiling of a few
/// kilobytes once encrypted, and everything here except the title is a pointer to something the App
/// can fetch properly once somebody taps it.
/// </summary>
internal sealed record NotificationPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("at")] DateTimeOffset At,

    /// <summary>
    /// Whether to update the card without interrupting anybody — the service worker's
    /// <c>renotify</c> and <c>silent</c>, inverted.
    ///
    /// <para><b>Nothing sets this yet, and it ships anyway.</b> A registered service worker updates
    /// on its own schedule, so a browser can run last week's <c>sw.js</c> against today's server
    /// indefinitely. The client half of a contract like this has to land before the producer does,
    /// or the first release that sets it is silently loud for everybody who has not reloaded. An
    /// absent flag reads as false, which is exactly today's behaviour.</para>
    ///
    /// <para>What it is for: the alert republished when its clip settles, which today goes nowhere
    /// because a second buzz for a picture nobody is still looking at costs more than it is worth.
    /// Quiet, it costs nothing.</para>
    /// </summary>
    [property: JsonPropertyName("quiet")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Quiet = false);
