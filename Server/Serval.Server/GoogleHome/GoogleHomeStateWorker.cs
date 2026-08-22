using Serval.Server.Cameras;
using Serval.Server.Snapshots;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Keeps HomeGraph's idea of which cameras are up in step with ours, by reporting
/// <c>online</c> whenever it changes.
///
/// <para><b>Why this is needed when QUERY already answers the same question.</b> QUERY is pull:
/// Google asks when it wants to show something. Report State is push, and it is what HomeGraph
/// actually stores. A device that never reports is one HomeGraph believes nothing about — which is
/// invisible day to day, because QUERY covers the cases a user sees, and then decisive at
/// certification: Google's Test Suite checks a device is online <em>before</em> testing it and
/// reads that from HomeGraph, so an integration that never reports cannot be tested at all.</para>
///
/// <para><b>Separate from <see cref="GoogleHomeSyncWorker"/>, whose first tick is deliberately
/// silent.</b> That one skips the first pass because every signature is new on a cold start and a
/// <c>requestSync</c> per restart is noise. This one must do the opposite: the first tick is the
/// most important report it will ever send, because until it lands HomeGraph holds nothing at all.
/// Two workers rather than one because that difference is the whole of their behaviour.</para>
///
/// <para>Reporting is skipped entirely without a HomeGraph key, which is the ordinary deployment —
/// and SYNC says <c>willReportState: false</c> in that case, so nothing is promised that is not
/// delivered.</para>
/// </summary>
public sealed class GoogleHomeStateWorker : PeriodicWorker
{
    /// <summary>
    /// Half <see cref="CameraDeviceMapper.StaleAfter"/>, so a camera that drops off is reported
    /// within about a snapshot's grace of the moment we would say it was offline. Faster would
    /// spend calls on a flapping camera; slower would let Google answer "online" for a camera that
    /// has been dark for the better part of a minute.
    /// </summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly GoogleHomeGate _gate;
    private readonly HomeGraphClient _homeGraph;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly TimeProvider _time;
    private readonly ILogger<GoogleHomeStateWorker> _logger;

    /// <summary>What HomeGraph was last told, so an unchanged tick costs nothing.</summary>
    private Dictionary<string, CameraState> _reported = new(StringComparer.Ordinal);

    public GoogleHomeStateWorker(
        IServiceScopeFactory scopes,
        GoogleHomeGate gate,
        HomeGraphClient homeGraph,
        SnapshotBroadcaster snapshots,
        TimeProvider time,
        ILogger<GoogleHomeStateWorker> logger)
        : base(logger)
    {
        _scopes = scopes;
        _gate = gate;
        _homeGraph = homeGraph;
        _snapshots = snapshots;
        _time = time;
        _logger = logger;
    }

    protected override TimeSpan Interval => Cadence;

    /// <summary>Google being briefly unreachable is routine; the next change re-sends.</summary>
    protected override LogLevel FailureLevel => LogLevel.Warning;

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        // Re-read every tick rather than captured: the integration can be switched off and the key
        // can appear or vanish without this worker being restarted.
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
            // Nobody is linked, so there is nobody to report to. Forgetting what was reported means
            // the first tick after a link sends the full set, which is what a fresh HomeGraph needs.
            _reported.Clear();
            return;
        }

        var switches = scope.ServiceProvider.GetRequiredService<GoogleCameraSwitchStore>();

        Dictionary<string, CameraState> states = States(
            await cameras.ListAsync(stoppingToken),
            _snapshots.Latest,
            await switches.OffAsync(stoppingToken),
            _time.GetUtcNow());

        if (!Changed(_reported, states))
        {
            return;
        }

        if (await _homeGraph.ReportStateAsync(link.AgentUserId, states, stoppingToken))
        {
            // Recorded only on success, so a refused report is retried on the next tick rather than
            // being remembered as sent — the failure mode that leaves HomeGraph permanently wrong.
            _reported = states;

            _logger.LogInformation(
                "Google Home: reported {Online} of {Total} cameras online, {Off} switched off.",
                states.Count(pair => pair.Value.Online),
                states.Count,
                states.Count(pair => !pair.Value.On));
        }
    }

    /// <summary>
    /// Whether each camera Google knows about is up, by the same rule QUERY answers with — so the
    /// pushed state and the pulled state can never disagree, which is a contradiction Google's own
    /// Test Suite checks for.
    /// </summary>
    internal static Dictionary<string, CameraState> States(
        IEnumerable<Camera> cameras,
        Func<string, Snapshot?> latest,
        IReadOnlySet<string> switchedOff,
        DateTimeOffset now) =>
        CameraDeviceMapper.Eligible(cameras)
            .ToDictionary(
                camera => camera.Id,
                camera => new CameraState(
                    Online: CameraDeviceMapper.IsOnline(latest(camera.Id), now),
                    On: !switchedOff.Contains(camera.Id)),
                StringComparer.Ordinal);

    /// <summary>
    /// Whether anything worth a call to Google moved — a camera's state flipping, but equally one
    /// appearing or disappearing, since a device added to the set has never been reported at all.
    /// </summary>
    internal static bool Changed(
        IReadOnlyDictionary<string, CameraState> reported,
        IReadOnlyDictionary<string, CameraState> current) =>
        reported.Count != current.Count
        || current.Any(pair => !reported.TryGetValue(pair.Key, out CameraState was) || was != pair.Value);
}
