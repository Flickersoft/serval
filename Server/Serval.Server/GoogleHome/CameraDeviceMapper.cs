using Serval.Server.Cameras;
using Serval.Server.Ingest;
using Serval.Server.Snapshots;
using Serval.Server.Vitals;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Turns Serval's camera registry into the devices Google is told about, and answers whether one
/// is online.
///
/// <para>Pure over its inputs — a camera list and a snapshot lookup — so the payload shapes can be
/// tested without a host, a database or a running ingest pipeline.</para>
/// </summary>
public static class CameraDeviceMapper
{
    /// <summary>
    /// How stale a snapshot may be before the camera counts as offline.
    ///
    /// <para>Deliberately the same fifteen seconds the App uses (<c>_staleAfter</c> in
    /// <c>live_repository.dart</c>), because it answers the same question from the same evidence.
    /// Two constants that happened to agree would be one edit away from Google and the App
    /// disagreeing about whether a camera is up.</para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The cameras Google is offered: <b>exactly</b> the set the go2rtc sync worker registers.
    ///
    /// <para>It calls <see cref="Go2RtcSyncWorker.IsWebRtcEligible"/> rather than restating the
    /// conditions, because a second copy would drift and the drift is not survivable — a camera
    /// Google lists but go2rtc has no stream for produces a stream request that succeeds and then
    /// never connects, and the only place that failure appears is a spinning display in somebody's
    /// kitchen.</para>
    /// </summary>
    public static IEnumerable<Camera> Eligible(IEnumerable<Camera> cameras) =>
        cameras.Where(Go2RtcSyncWorker.IsWebRtcEligible);

    /// <summary>
    /// Whether this camera can be played over HLS as well as WebRTC — which is to say, whether it
    /// is being recorded.
    ///
    /// <para>The HLS routes serve the segments the recorder writes, so a camera with recording
    /// switched off, or with no stream carrying the record role, has nothing for them to serve.
    /// Saying so per camera is the honest answer: the alternative is advertising a protocol that
    /// resolves to a 404 on the device, which reads to the viewer as a broken camera rather than
    /// one that cannot be cast.</para>
    /// </summary>
    public static bool CanPlayHls(Camera camera) =>
        camera.Recording && camera.RecordStream is not null;

    /// <summary>
    /// The protocols offered for one camera, <b>in preference order</b> — Google's attribute is
    /// documented as ordered, and WebRTC is what we want every surface that can take it to use: it
    /// is sub-second, it keeps the media on the LAN, and it costs no disk. HLS follows as the
    /// fallback for a Cast receiver, which cannot speak WebRTC at all.
    /// </summary>
    public static IReadOnlyList<string> ProtocolsFor(Camera camera) =>
        CanPlayHls(camera)
            ? [SmartHome.WebRtcProtocol, SmartHome.HlsProtocol]
            : [SmartHome.WebRtcProtocol];

    /// <summary>
    /// One camera, as SYNC describes it.
    ///
    /// <para><paramref name="willReportState"/> is passed in rather than fixed because it is a
    /// promise, and whether we can keep it depends on deployment: Report State needs the HomeGraph
    /// service-account key, which most deployments will not have. Claiming it without one would
    /// leave HomeGraph waiting for reports that never come.</para>
    /// </summary>
    public static SyncDevice ToDevice(
        Camera camera, bool willReportState = false, bool switchable = false) => new(
        Id: camera.Id,
        Type: SmartHome.CameraType,
        // OnOff is what gives the Home app something to switch, and it governs whether Google is
        // offered a stream and nothing else — see GoogleCameraSwitch for why that boundary is drawn
        // where it is.
        //
        // Declared only where the switch can be protected. Google requires a pinNeeded challenge
        // for OnOff on a camera, and offering the trait with no PIN configured would mean anyone
        // within earshot of an Assistant could disable a security camera by asking. Off is the
        // safe direction to fail in: no trait, no switch, cameras stay available.
        Traits: switchable
            ? [SmartHome.CameraStreamTrait, SmartHome.OnOffTrait]
            : [SmartHome.CameraStreamTrait],
        Name: new SyncDeviceName(
            string.IsNullOrWhiteSpace(camera.Name) ? camera.Id : camera.Name),

        // A camera has no *trait* state — CameraStream declares no attributes that change — but
        // `online` is reportable, and Google expects it reported. See GoogleHomeStateWorker for
        // what goes unmeasured when this is false: HomeGraph believes nothing about the device,
        // which is invisible in daily use and disqualifying at certification.
        WillReportState: willReportState,

        Attributes: new CameraStreamAttributes(
            SupportedProtocols: ProtocolsFor(camera),

            // False, and it has to be: with HLS offered, the media is fetched by the Cast Web
            // Receiver on the device rather than by Google's cloud, and the generic receiver cannot
            // send an Authorization header at all — Google's own note is that auth tokens work only
            // with a custom receiver. So the ticket travels in the URL instead.
            //
            // This costs WebRTC nothing. Its signaling URL has always carried the ticket as well as
            // returning it as cameraStreamAuthToken, and the handler accepts either, because
            // Google's trait reference and its signaling reference disagree about which form
            // reaches every receiver.
            NeedAuthToken: false),

        DeviceInfo: new SyncDeviceInfo(
            Manufacturer: "Serval",
            Model: "Serval camera",
            SwVersion: ServerVersion.Current.Version),

        // Omitted when blank rather than sent empty — see SyncDevice.
        RoomHint: string.IsNullOrWhiteSpace(camera.Location) ? null : camera.Location.Trim());

    /// <summary>
    /// Whether Google should be told the camera is up, derived from snapshot staleness because the
    /// server publishes no status field of its own — the same evidence the App's wall reads.
    ///
    /// <para><b>A camera that has never produced a snapshot is reported online, and that is not an
    /// oversight.</b> Snapshots come from the record/detect ffmpeg pipeline, so a camera carrying
    /// only a <c>live</c> role has none and still has a perfectly good WebRTC view; calling it
    /// offline would stop Google from even attempting the stream. The App's own table makes the
    /// same call by a different name — it says <em>connecting</em>, never <em>offline</em>, for a
    /// camera it has never measured — and Google has no third value to say it with. Being wrong
    /// this way costs one failed connection attempt that reports itself; being wrong the other way
    /// makes a working camera unreachable with nothing to explain it.</para>
    /// </summary>
    public static bool IsOnline(Snapshot? latest, DateTimeOffset now) =>
        latest is null || now - latest.CapturedAt <= StaleAfter;
}
