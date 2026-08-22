using System.Security.Cryptography;
using System.Text;
using Serval.Server.Cameras;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Watches the set of cameras Google has been told about and asks it to re-run SYNC when that set
/// changes.
///
/// <para><b>A signature over the device set, not a hook in camera CRUD.</b> The registry is the
/// source of truth and this reconciles against it on a timer — the arrangement
/// <c>Go2RtcSyncWorker</c> already states in full, and <c>Ai/AiSessionSignature</c> the shape of.
/// It buys three things a hook in <c>CameraEndpoints</c> would not:</para>
/// <list type="bullet">
/// <item>It catches changes no camera route sees — a settings write flipping
/// <c>Serval:WebRtc:Enabled</c>, a configuration restore, somebody editing Mongo directly.</item>
/// <item>It coalesces for free. Renaming three cameras is one call to Google, not three.</item>
/// <item>It makes the bounded-channel question moot. <c>Push/AlertNotifier</c> exists so that no
/// request blocks on an outbound call; a timer worker gets that guarantee more strongly, because
/// there is no request in the path at all. Nothing in <c>Cameras/</c> learns Google exists.</item>
/// </list>
///
/// <para><b>The first tick never fires a sync.</b> On a cold start every signature is new, and a
/// <c>requestSync</c> on every restart is noise Google throttles and nobody wants. The signature is
/// recorded on the first tick and compared from the second.</para>
/// </summary>
public sealed class GoogleHomeSyncWorker : PeriodicWorker
{
    private readonly IServiceScopeFactory _scopes;
    private readonly GoogleHomeGate _gate;
    private readonly HomeGraphClient _homeGraph;
    private readonly ILogger<GoogleHomeSyncWorker> _logger;

    private string? _signature;

    public GoogleHomeSyncWorker(
        IServiceScopeFactory scopes,
        GoogleHomeGate gate,
        HomeGraphClient homeGraph,
        ILogger<GoogleHomeSyncWorker> logger)
        : base(logger)
    {
        _scopes = scopes;
        _gate = gate;
        _homeGraph = homeGraph;
        _logger = logger;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Warning rather than Error: Google being briefly unreachable is routine, and the cost of a
    /// missed sync is a device list that is stale until the next change. The same disposition
    /// <c>Go2RtcSyncWorker</c> takes towards its sidecar.
    /// </summary>
    protected override LogLevel FailureLevel => LogLevel.Warning;

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        // Both cheap and both allowed to change under us, so they are re-read every tick rather
        // than captured: the integration can be switched off, and the key can be absent, without
        // this worker being restarted.
        if (!_gate.IsEffective || !_homeGraph.IsConfigured)
        {
            return;
        }

        using IServiceScope scope = _scopes.CreateScope();
        var cameras = scope.ServiceProvider.GetRequiredService<CameraRepository>();
        var store = scope.ServiceProvider.GetRequiredService<GoogleOAuthStore>();

        GoogleLink? link = await store.GetLinkAsync(stoppingToken);
        if (link is null)
        {
            // Nobody has linked. Forget any signature so the first tick after a link is treated as
            // a cold start rather than as a change — Google runs SYNC itself at link time, and
            // asking it to again immediately would be redundant.
            _signature = null;
            return;
        }

        string signature = Signature(await cameras.ListAsync(stoppingToken));

        if (_signature is null)
        {
            _signature = signature;
            return;
        }

        if (_signature == signature)
        {
            return;
        }

        _signature = signature;

        if (await _homeGraph.RequestSyncAsync(link.AgentUserId, stoppingToken))
        {
            await store.TouchSyncAsync(link.AgentUserId, stoppingToken);
            _logger.LogInformation("Google Home: camera list changed, requestSync sent.");
        }
    }

    /// <summary>
    /// A hash over exactly what SYNC would render — the eligible cameras' ids, names and rooms.
    ///
    /// <para>Deliberately not a hash of the whole camera document: retention days, ONVIF
    /// credentials and detection tuning all change without altering anything Google was told, and
    /// hashing them would spend a call to Google on every edit anyone makes in the App.</para>
    /// </summary>
    internal static string Signature(IEnumerable<Camera> cameras)
    {
        // ASCII unit and record separators. Delimiters that cannot occur in a camera id,
        // name or location, so no two distinct device sets can flatten to the same string —
        // which a printable separator would allow, given a camera may be named "a|b".
        const char Field = '\u001f';
        const char Record = '\u001e';

        var builder = new StringBuilder();

        foreach (SyncDevice device in CameraDeviceMapper.Eligible(cameras)
            .Select(camera => CameraDeviceMapper.ToDevice(camera))
            .OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            builder.Append(device.Id).Append(Field)
                .Append(device.Name.Name).Append(Field)
                .Append(device.RoomHint).Append(Record);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
