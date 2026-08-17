import 'package:flutter/foundation.dart';

import '../models/activity.dart';
import '../models/alert.dart';
import '../models/camera.dart';
import '../models/clip_selection.dart';
import '../models/config_backup.dart';
import '../models/conversation.dart';
import '../media/media_saver.dart';
import '../models/ptz.dart';
import '../models/push.dart';
import '../models/saved_clip.dart';
import '../models/server_settings.dart';
import '../models/system_stats.dart';
import '../models/user_preferences.dart';
import '../models/vitals_history.dart';
import '../models/timeline.dart';
import 'audio_levels_socket.dart';
import 'auth/auth_models.dart' show Role;
import 'auth/user_account.dart';
import 'camera_record.dart';

/// Everything the screens read, and the few things they do.
///
/// The screens depend on this interface only, and the property is worth keeping: everything below
/// stays synchronous and pull-shaped, and a live implementation caches rather than returning
/// futures.
///
/// Three shapes here follow the Server rather than the sample data:
///
///  * [activityFor] returns one merged list, because the Server has no aggregate feed route. The
///    live implementation fans out `/scenes`, `/utterances` and `/conversation-transcripts` per
///    camera over the window, merges by time, and then keeps the list current from the single
///    `/api/events` socket. Scoping it to one camera is therefore a filter over what is already
///    in memory, not a second read.
///  * [detectionsFor] and [detectionsAt] are per-camera and split by what they claim — one is
///    about now, the other about an instant in the recording.
///  * [snapshotFor] returns bytes rather than a URL, because the frames arrive pushed down one
///    socket for the whole wall — there is no per-tile request to point an `Image.network` at.
///
/// A live implementation pushes, and **what you listen to is a slice**. There is deliberately no
/// whole-repository notification: one wire carrying both a camera rename and a detection
/// heartbeat arriving twice a second per camera would rebuild every listening screen at the rate
/// of the fastest thing in the house.
///
/// The slices below are that wire split by what actually moved. Each names what changed rather than
/// who cares, and the pairing to watch is [activityChanges] against [timelineChanges] — they look
/// alike and behave nothing alike.
///
/// Snapshot frames were the first thing taken off the shared wire, long before the rest and for
/// exactly this reason: at ~1 fps per camera they would rebuild the whole wall to repaint one tile,
/// so they ride [frameNotifier] and rebuild only the tile they belong to.
///
/// The screens reach this through `providers.dart`, which wraps the fast slices in providers so a
/// `Consumer` can watch one; the rare ones are handed to a `ListenableBuilder` directly. The
/// `ValueListenable`s below stay as they are: a provider per camera frame would be a container
/// entry per tile delivering bytes that have identity equality anyway, and `ValueListenableBuilder`
/// and `Consumer` are the same one widget.
abstract interface class ServalRepository {
  /// Notifies when the activity pool changes, and at no other time.
  ///
  /// The fast one, and the reason the slices exist: a detection episode publishes a position
  /// heartbeat once per detection frame — `DetectFps`, 2/second — for as long as the thing is in
  /// view, per camera. Everything that reads [activityFor], [feedHorizon], [detectionsAt] or the
  /// timeline's marks wants those; nothing else does, and nothing else should be woken by them.
  Listenable get activityChanges;

  /// Notifies when a timeline window finishes loading — its coverage and its first marks.
  ///
  /// Split from [activityChanges] because the two look alike and behave nothing alike. Marks move
  /// with the feed, twice a second; **coverage arrives once**, from a fetch [timelineFor] starts
  /// behind its first answer. Anything that must wait for the recording to be known — seeking to
  /// the instant a screen was opened at, saving a clip — is waiting for this and would wait forever
  /// on a signal that only ever carried heartbeats.
  ///
  /// Rare enough to be watched from the top of a screen: once per window, and again per
  /// half-minute TTL on a live range.
  Listenable get timelineChanges;

  /// Notifies when the set of cameras changes, or what is known about one does.
  ///
  /// The registry behind [cameras], [cameraById], [cameraRecords] and [wallLayout]: the sign-in
  /// load, a camera created, edited or deleted, a configuration restored, and the staleness sweep
  /// that flips a camera between online and offline. Config edits and a five-second tick — rare
  /// enough to be watched from the top of a screen, and the route does exactly that.
  Listenable get registryChanges;

  /// Notifies when the account's saved preferences land or are written.
  ///
  /// What [preferencesKnown] and [notificationPreferences] answer, and the saved arrangement behind
  /// [wallLayout]. Once per sign-in, then once per save.
  Listenable get preferenceChanges;

  /// Notifies when the unread alert count moves.
  ///
  /// Two badges read it and nothing else does, which is the whole argument for this being its own
  /// wire: a doorbell should light a dot on the rail, not rebuild the wall behind it.
  Listenable get alertChanges;

  /// Notifies when [systemStats] or [vitalsHistory] is refreshed — the five-second sweep, and the
  /// reads the server screen asks for.
  Listenable get vitalsChanges;

  /// Notifies when a lazily-read per-camera fact lands: a PTZ probe, or a camera's device
  /// information.
  ///
  /// Both are fetched once on first use and then held, so this fires a handful of times per camera
  /// per session.
  Listenable get deviceChanges;

  /// Every registered camera, in wall order.
  List<Camera> cameras();

  /// The full Server-side record for each camera — streams, roles, ONVIF fields, the lot.
  ///
  /// The settings screen edits these; the wall and the single-camera view use [cameras] instead,
  /// which is the rendering-shaped view of the same thing.
  List<CameraRecord> cameraRecords();

  CameraRecord? cameraRecordById(String id);

  /// The wall's saved arrangement, reconciled against the cameras that currently exist.
  ///
  /// Per account and Server-side, from `GET /api/preferences`. An implementation that has not read
  /// that yet answers with the design's default pack, which is indistinguishable from the answer
  /// for somebody who has never arranged one — [preferencesKnown] is what tells the two apart, and
  /// nothing may be written back on the strength of this alone.
  List<TileLayout> wallLayout();

  Camera? cameraById(String id);

  /// The latest wall frame for a camera, or null before the first one arrives.
  Uint8List? snapshotFor(String cameraId);

  /// Notifies when *this camera's* frame changes, so a tile can rebuild without the wall doing
  /// so. Never null — a camera with no frames yet simply never fires.
  ValueListenable<Uint8List?> frameNotifier(String cameraId);

  /// Whether the wall socket is currently up. Distinguishes "the Server is unreachable" from
  /// "this one camera stopped sending", which are identical from frame staleness alone.
  bool get connected;

  /// Whether there is a Server to negotiate a WebRTC session with at all.
  ///
  /// False for the sample repository, and the single-camera view then paints the design's
  /// placeholder rather than a live view — which also keeps the widget tests and the goldens
  /// from reaching for a native WebRTC plugin that is not loaded under `flutter test`.
  bool get canStreamLive;

  /// The "What's happening" feed, newest first — merged across every camera, or scoped to one.
  ///
  /// Unfiltered beyond [cameraId], [asOf] and [range]. What the column shows is
  /// `ActivityFilter.apply` over this, and what its facet panel counts is `ActivityFacets.of` over
  /// the same list — so the screen needs the whole pool, not the part that survives the filter.
  /// Filtering here would mean answering twice to draw once.
  ///
  /// [cameraId] is what the single-camera panel reads. It is a filter rather than a separate
  /// route because the merge has already happened in memory and every telemetry document carries
  /// its `camera_id`; asking the Server again per camera would be a request to re-fetch what is
  /// already here.
  ///
  /// [range] is the scrubber's own window, and scoping to it makes the column and the track beside
  /// it describe the same slice of the day. Null means everything held. The whole [TimelineRange]
  /// rather than a bare start instant, because a chosen period needs its *top* edge clamped too.
  ///
  /// [asOf] clamps the feed to an instant: nothing that happened after it, null meaning now. It
  /// composes with [range] rather than replacing it — the earlier of the two is the top edge. This
  /// is what makes the column agree with the picture during replay, instead of spoiling footage you
  /// have not watched yet.
  ///
  /// Both clamp *what is listed* and nothing else. Rows keep being dated against the wall clock:
  /// what a reader needs from a replayed row is when the thing actually happened.
  /// [includeAllDetections] widens the rows from the alerting episodes to every stored one. Off
  /// by default, and the default is what every caller that is not the camera screen passes — see
  /// [detectionsFor] for what the flag is and why it belongs to a screen rather than to an
  /// account.
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  });

  /// The oldest instant [activityFor] can speak for, or null when it can speak for its whole
  /// window.
  ///
  /// Non-null means the backfill ran out of depth before it ran out of window: the Server returns
  /// the newest records first and caps how many it will return at once, so what is missing is
  /// always the *oldest* part of the window. A range dragged past this instant covers time the
  /// column has nothing to say about — not because the house was quiet, but because the read
  /// stopped there.
  ///
  /// Exposed rather than swallowed because of what the column otherwise does with it: it trails
  /// off mid-afternoon and reads as an evening when nothing happened. The rule that what the bar
  /// covers the column lists cannot always be kept — a busy enough day will outrun any single read
  /// — so where it cannot be kept, it has to be said.
  ///
  /// Scoped like [activityFor]: null [cameraId] is the merged feed, which is only whole back to
  /// the shallowest of its cameras.
  DateTime? feedHorizon({String? cameraId});

  /// Serval's read on what is happening on this camera.
  ///
  /// [asOf] and [range] clamp it the way they clamp [activityFor], and for the same reason: this
  /// is the newest scene description, so during replay an unclamped one narrates footage further
  /// on than the picture — the spoiler is shorter than a feed of them but no less of one. And a
  /// summary taken from outside the window the panel is scoped to would be describing a moment
  /// none of the rows under it can mention.
  SceneSummary? summaryFor(
    String cameraId, {
    DateTime? asOf,
    TimelineRange? range,
  });

  /// Boxes to paint over the live video: what is in front of this camera now.
  /// Empty when nothing is detected.
  ///
  /// [includeAllDetections] draws every stored episode rather than only the alerting ones. Off is
  /// the ordinary view and the default. On is a troubleshooting instrument: it answers "what is
  /// the detector actually seeing here", which is otherwise a question only the database can be
  /// asked — a sub-threshold detection reaches the activity feed in words while being drawn
  /// nowhere, and that gap is what it closes.
  ///
  /// **A parameter rather than repository state, which is the whole of how it stays contained.**
  /// It belongs to the screen you are looking at: the camera screen passes its own switch, every
  /// other caller passes nothing and gets the alerts-only view unconditionally. Held in the
  /// repository it was one screen's switch silently rewriting another's — the wall has no such
  /// control and drew whatever the camera screen had last been left on.
  ///
  /// An episode admitted this way stays `isAlert: false`, so it draws in the accent rather than in
  /// orange. It widens what is *shown* and touches nothing about what is stored or alerted on.
  List<DetectionBox> detectionsFor(
    String cameraId, {
    bool includeAllDetections = false,
  });

  /// Boxes to paint over replayed video: what was in front of this camera at [when].
  ///
  /// Separate from [detectionsFor] because they answer different questions, not because one is
  /// the other with a clock attached. Live has one answer and holds it until the object leaves;
  /// replay has a different answer every second of an episode, read from the track the Server
  /// records as the object moves.
  ///
  /// Synchronous and pull-shaped like the rest of this interface, so it can be read from `build`.
  /// [ensureReplayDetections] is what puts the answers within reach.
  List<DetectionBox> detectionsAt(
    String cameraId,
    DateTime when, {
    bool includeAllDetections = false,
  });

  /// Loads the detections over a stretch of recording, so [detectionsAt] can answer inside it.
  ///
  /// Called when replay opens a playlist window rather than per frame. The live feed is trimmed
  /// and does not reach back as far as the scrubber does, so without this a playhead dragged into
  /// yesterday would find nothing to draw and be unable to tell that from nothing having happened.
  Future<void> ensureReplayDetections(
    String cameraId,
    DateTime from,
    DateTime to,
  );

  /// What the scrubber draws over the given range: where footage exists, and where something
  /// happened.
  ///
  /// Synchronous and pull-shaped like everything else here, so a live implementation returns what
  /// it has cached and fills the rest in behind, notifying when it lands. The first read after a
  /// camera is opened therefore comes back with the window already set and `loading` true, rather
  /// than with a track that claims nothing was ever recorded.
  /// [includeAllDetections] widens the marks the same way it widens the boxes — see
  /// [detectionsFor]. A mark admitted by it stays `activity` rather than becoming an alert: the
  /// flag changes what is drawn, never what is claimed to matter.
  TimelineWindow timelineFor(
    String cameraId,
    TimelineRange range, {
    bool includeAllDetections = false,
  });

  /// What this camera's pan/tilt/zoom can actually do, as the camera itself reports it.
  ///
  /// Synchronous and self-priming like [timelineFor]: returns what is cached and kicks the probe
  /// behind it, notifying when it lands. Deliberately not part of the registry read — discovery is
  /// a live SOAP round trip to the camera, and one unplugged camera would otherwise cost every
  /// launch an ONVIF timeout.
  ///
  /// The four states are distinct on purpose; see [PtzProbe].
  PtzProbe ptzProbeFor(String cameraId);

  /// What the camera says it is — make, model, firmware. Null until it has been read, and for a
  /// camera with no ONVIF endpoint to ask.
  ///
  /// Self-priming on the same terms as [ptzProbeFor], and for the same reason.
  DeviceInformation? deviceInformationFor(String cameraId);

  /// What the Server says about itself: processor and GPU load, memory against its limit, and how
  /// much of the media volume is left.
  ///
  /// Null before the first read lands, and on any repository with no Server behind it. Synchronous
  /// and self-priming like [ptzProbeFor] — returns what is cached and refreshes behind, notifying
  /// when a new sample arrives.
  ///
  /// Every figure *inside* it is nullable too, and the screens must render that difference rather
  /// than collapse it: a host whose GPU driver publishes no usage number says so, and a meter
  /// painted at 0% for it would be a measurement the Server never made.
  SystemStats? systemStats();

  /// Roughly an hour of past samples for the three vitals meters, or null before the first read
  /// lands and on any repository with no Server behind it.
  ///
  /// Self-priming like [systemStats], but deliberately *separate* from it: the dashboard wall
  /// calls [systemStats] for its disk banner and has no use for history, so folding the two
  /// together would make every wall poll carry an hour of samples for a page that is closed.
  ///
  /// The series inside are `List<double?>` and a null must stay a null — see [VitalsHistory].
  VitalsHistory? vitalsHistory();

  /// The VOD playlist for a past window, or null where there is no Server to play from.
  ///
  /// Null is the capability test as much as the URL — the same job [canStreamLive] does for
  /// WebRTC. The sample repository returns null and the stage paints the design's placeholder, so
  /// `flutter test` and the goldens never construct a player and never reach for libmpv.
  Uri? vodUrlFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  });

  /// How far into that playlist the instant it was asked for actually sits.
  ///
  /// Playback position is measured from the segment boundary the playlist starts on, not from the
  /// instant requested, and the two are up to one segment apart. Subtracting this is what turns a
  /// position back into the wall-clock time of the picture on screen — without it the playhead
  /// reads up to four seconds ahead of the frame, which is a curiosity on a clock and a visible
  /// error on anything drawn over the video.
  ///
  /// Zero where there is no Server to ask, which is also the right answer for a player that never
  /// opens.
  Future<Duration> vodStartOffsetFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  });

  /// A short-lived token scoping access to GET-only media routes, for whoever is about to open a
  /// [vodUrlFor] URL. The player fetches `vod.m3u8` and every `.m4s` under it directly — not
  /// through [ServalApi] — so it cannot carry an `Authorization` header the way every other read
  /// here does; this is appended as `?stream_token=` instead. Null where there is no Server (or
  /// minting failed), the same capability test [vodUrlFor] itself is.
  Future<String?> mintStreamToken();

  /// The caption currently painted over the video, if the camera is hearing speech. Null when
  /// nothing is being said.
  String? liveCaptionFor(String cameraId);

  /// A live input-level feed for one camera, or null where there is no Server to open one
  /// against — the same capability test [vodUrlFor] and [canStreamLive] perform.
  ///
  /// Returns a **fresh feed the caller owns and must close**, deliberately not a cached one. The
  /// Server measures a camera's level only while somebody is subscribed, so a feed left open keeps
  /// an RMS pass and a ten-per-second publish running for a panel nobody is looking at. Caching it
  /// here would make that the default rather than a mistake.
  AudioLevelFeed? watchAudioLevels(String cameraId);

  /// When the picture currently on the single-camera stage was taken.
  ///
  /// **Null means "this is live"** and the clock pill shows the wall clock, which is then the
  /// truth. It is a method rather than a bare `DateTime.now()` at the use site because it stops
  /// being the truth the moment you can drag back — a pill reading "now" over footage from an
  /// hour ago is worse than no pill.
  ///
  /// Replay does exactly that, but the position it feeds the pill comes from the single-camera
  /// screen's playback controller rather than from here: it ticks about four times a second, and
  /// notification on this interface means *the structure changed*. That is the same argument the
  /// header makes for snapshot frames riding [frameNotifier]. So this stays the answer for "the
  /// picture on the stage is not live" in every other sense, and returns null while it is.
  DateTime? pictureTakenAt(String cameraId);

  // ------------------------------------------------------------------ commands

  /// Whether this account's stored preferences have been read yet.
  ///
  /// False while the arrangement on screen is the design's default *standing in for* one nobody
  /// has read, which looks exactly like the default belonging to an account that never arranged a
  /// wall. Screens gate on this before offering to change anything held against the account —
  /// [saveWallLayout] writes a whole value the Server takes as stated, so writing one from a
  /// stand-in stores the stand-in.
  ///
  /// True for any implementation with no Server behind it, which has nothing to fail to read.
  bool get preferencesKnown;

  /// The wall's arrangement, stored against this account rather than this browser, which is the
  /// reason it followed you here from `shared_preferences`.
  ///
  /// Called for every accepted move and resize; there is no separate save step. Refused while
  /// [preferencesKnown] is false.
  Future<void> saveWallLayout(List<TileLayout> layout);

  /// This account's notification preferences, as last read from the Server.
  ///
  /// Whether they interrupt this person at all, and which cameras may about what. Subject to the
  /// same caution as [wallLayout]: an implementation that has not read them yet answers with the
  /// defaults, which look identical to an account that has never changed any — [preferencesKnown]
  /// is what separates the two, and the notifications screen gates its controls on it.
  UserPreferences get notificationPreferences;

  /// Saves either half of those, leaving the other alone.
  ///
  /// Sends only what it is given, because the Server merges: a build that predates a preference
  /// must not erase it. Within a rule, a null class list means *everything this camera alerts on*
  /// and an empty one means nothing — see [CameraNotificationRule].
  Future<void> saveNotificationPreferences({
    bool? enabled,
    List<CameraNotificationRule>? rules,
  });

  // ------------------------------------------------------------------ push
  //
  // Registering *this browser* to receive alerts while nobody is looking at the screen. Delivery
  // only: which alerts reach this person is [notificationPreferences] above, because that belongs
  // to the account and this belongs to one of their browsers.

  /// The deployment's VAPID public key and whether notifications are switched on at all.
  Future<PushConfig> pushConfig();

  /// Every browser this account has registered.
  Future<List<PushDevice>> pushDevices();

  /// Registers this browser, or refreshes what the Server holds for it.
  Future<void> registerPushDevice({
    required String endpoint,
    required String p256dh,
    required String auth,
  });

  /// Stops the Server notifying one browser, named by the id the device list or a registration
  /// returned.
  Future<void> unregisterPushDevice(String deviceId);

  /// Sends a test notification to every device this account has registered.
  Future<PushTestResult> sendTestNotification();

  /// This camera's volume, as a slider position, 0..1 — not the volume a player takes.
  /// `playbackFromTravel` splits it into that and the gain behind it.
  ///
  /// Per camera, and stored on this machine. Both halves are deliberate. Per camera because how loud
  /// a camera needs to be is a fact about its microphone and where it points, so a level found once
  /// should not have to be found again on the way back. On this machine because how loud you want to
  /// listen is a property of what you are sitting at, and syncing it would let a phone dictate a
  /// desktop's volume.
  ///
  /// Seeded from [CameraRecord.playbackGainDb] until this client has a position of its own, so a
  /// camera somebody has already calibrated is not silent on every new browser.
  ///
  /// A [ValueListenable] rather than a getter plus `notifyListeners`, because a drag reports on
  /// every pointer sample and routing that through this interface's notification would relayout
  /// the entire single-camera screen for the length of the gesture. The same argument
  /// [frameNotifier] makes.
  ///
  /// Stable per id: the same call twice hands back the same notifier, because a listener attached to
  /// a replacement would never hear the drag.
  ValueListenable<double> playbackVolumeFor(String cameraId);

  /// Sets it, and persists it behind a debounce.
  ///
  /// `void` rather than `Future<void>` deliberately: this is called once per pointer sample, and
  /// a preferences write per sample is exactly what the debounce exists to avoid. Contrast
  /// [saveWallLayout], which is driven by a button press and can afford to be awaited.
  void setPlaybackVolume(String cameraId, double travel);

  /// Whether the "What's happening" sidebar is collapsed to its rail.
  ///
  /// One flag for both the wall and the single-camera view: collapsing it is a statement about
  /// wanting the room for video, and having to make that statement twice — once per screen —
  /// would be the app forgetting what you just told it. Persisted client-side; the Server has no
  /// opinion on how you arrange the window, the same as [wallLayout].
  ValueListenable<bool> get activityPanelCollapsed;

  /// Sets it, and persists it. `void` for symmetry with [setPlaybackVolume], though this one is a
  /// button press and writes straight away rather than behind a debounce.
  void setActivityPanelCollapsed(bool collapsed);

  /// Start a continuous pan/tilt/zoom, velocities −1..1.
  ///
  /// The camera auto-stops about a second after each command, so a held control has to re-send
  /// inside that window — `PtzPad` already does, on its own repeat.
  Future<void> ptzMove(String cameraId, {double pan, double tilt, double zoom});

  Future<void> ptzStop(String cameraId);

  /// Drive zoom to a position, 0..1 of the lens's travel.
  ///
  /// Only for cameras whose probe reports `absoluteZoom`. The rest are driven with [ptzMove] and
  /// resynced with [ptzZoomPosition] — see [ZoomPosition] for why the two are not the same control.
  Future<void> ptzZoomTo(String cameraId, double position);

  /// Where the camera says its lens is, or null when it does not report a zoom position.
  ///
  /// A live ONVIF round trip: asked on opening a camera and again once a drag settles, never on a
  /// timer. Null is the answer that matters — it means dead reckoning is all there is, and the
  /// slider must stop claiming to be a reading.
  Future<ZoomPosition?> ptzZoomPosition(String cameraId);

  /// Recall a stored ONVIF position, by the token the camera gave it.
  Future<void> ptzPreset(String cameraId, String preset);

  /// Recall the camera's stored home position, for cameras that report one.
  Future<void> ptzHome(String cameraId);

  /// Exchange an SDP offer for the answer, proxied to go2rtc. Media never transits the Server.
  Future<String> openWebRtc(String cameraId, String offer);

  /// Whether there is a Server to fetch media from at all.
  ///
  /// False for the sample repository, so `flutter test` never opens a socket and the two save
  /// buttons report that there is nothing behind them — the same job [canStreamLive] does for
  /// WebRTC and [vodUrlFor] does for replay.
  bool get canSaveMedia;

  /// Save the camera's latest still to the device.
  ///
  /// **Live only.** `snapshot.jpg` is the newest frame and no route extracts a still at a past
  /// instant, so while replaying this would silently save a different moment than the one on
  /// screen — invisible in the resulting file, which is the worst kind of wrong.
  Future<SavedMedia> saveSnapshot(String cameraId);

  /// Save a standalone MP4 of a window to the device.
  ///
  /// [onBytes] ticks as the export arrives where the platform can tell. There is no percentage
  /// anywhere: the Server pipes the body straight out of ffmpeg with no `Content-Length`.
  Future<SavedMedia> saveClip(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    void Function(int bytes)? onBytes,
  });

  // ------------------------------------------------------------- saved clips
  //
  // The one part of Serval that does not roll off. A clip is an independent copy — its own video,
  // its own poster, and the telemetry of its window frozen with it — so all of this reads from the
  // Server rather than from the live caches everything else here uses.

  /// The segment boundaries a trim handle may land on, for one window.
  ///
  /// Empty where nothing was recorded, which the trimmer reads as "there is nothing here to trim"
  /// rather than as a failure.
  Future<List<RecordedSegment>> segmentsFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  });

  /// Every saved clip, newest first, optionally narrowed.
  ///
  /// [query] matches names and what was said inside a clip; [cameraId] narrows to one camera.
  Future<List<SavedClip>> savedClips({String? query, String? cameraId});

  /// One clip with the speech frozen alongside it.
  Future<SavedClipDetail> savedClip(String id);

  /// Ask the Server to keep a range.
  ///
  /// Returns as soon as the range is accepted, not when the file exists — a half-hour clip is
  /// gigabytes and tens of seconds of ffmpeg. Follow it with [clipProgress].
  Future<SavedClip> keepClip({
    required String cameraId,
    required DateTime from,
    required DateTime to,
    required String name,
  });

  /// How far a save has got. Bytes written, because there is no total until it is finished.
  Future<ClipProgress> clipProgress(String id);

  Future<void> renameClip(String id, String name);

  Future<void> deleteClip(String id);

  /// The clip's video, ready for a player or a download. Null where there is no Server.
  Future<Uri?> savedClipUrl(String id);

  /// Poster frames for a page of clips, keyed by clip id.
  ///
  /// A page at a time rather than one call per card, because each one needs a stream token — the
  /// image is fetched by the browser rather than by the authenticated client, so it cannot carry a
  /// header. Minting one per thumbnail would be fourteen round trips to draw one screen.
  ///
  /// Missing ids simply have no poster, which the card draws as its camera's stripe.
  Future<Map<String, Uri>> clipPosterUrls(List<String> ids);

  // --------------------------------------------------------------------- alerts

  /// How many alerts nobody has opened.
  ///
  /// A property rather than a call, because the rail is under every wide screen and draws its dot
  /// from this on every rebuild. It moves with [notifyListeners] like everything else here.
  int get unreadAlerts;

  /// The alert queue, newest first, with how much of it is new.
  ///
  /// Every camera unless [cameraId] narrows it — an alert queue you have to already know which
  /// camera to open is not being told about anything.
  Future<AlertQueue> alerts({String? cameraId, DateTime? before});

  /// One alert, including a dismissed one. A notification tapped after somebody else cleared the
  /// queue still has to land somewhere.
  Future<Alert> alert(String id);

  /// Marks an alert seen. Not the same as dismissing it: the row stays in the queue and only stops
  /// counting as new.
  Future<void> markAlertRead(String id);

  Future<void> dismissAlert(String id);

  /// Clears the queue, or one camera's part of it. Returns how many went.
  Future<int> dismissAllAlerts({String? cameraId});

  /// The alert's preview clip, ready for a player. Null where there is no Server.
  Future<Uri?> alertClipUrl(String id);

  /// The frames alerts fired on, for a page of rows, keyed by alert id.
  ///
  /// A page at a time for the reason [clipPosterUrls] is: each needs a stream token, because the
  /// image is fetched by the browser rather than by the authenticated client.
  ///
  /// Takes the alerts rather than their ids because the URL carries each one's clip state — the
  /// picture behind it is replaced when the clip settles, and an unchanging URL would go on showing
  /// the one the browser cached. See [ServalApi.alertPosterUrl].
  Future<Map<String, Uri>> alertPosterUrls(List<Alert> alerts);

  /// Every alert the Server publishes: a new one, and the same one again once its preview settles.
  ///
  /// The second of those is what a screen showing an alert is waiting for. An alert is raised the
  /// moment it fires and its clip is cut some sixteen seconds later, so anything opened from a
  /// notification arrives at one with no poster and no player — and this is what says when that has
  /// stopped being true.
  ///
  /// A [Stream] rather than a [RepositorySlice] because what a listener needs is *which* alert moved
  /// and what it moved to, and a [Listenable] carries neither. The repository does not hold the
  /// queue either; the screen that lists it does.
  Stream<Alert> get alertUpdates;

  /// Save an already-kept clip's video to the device.
  Future<SavedMedia> downloadSavedClip(
    String id, {
    void Function(int bytes)? onBytes,
  });

  /// Hand an already-kept clip's video to the platform's share sheet.
  ///
  /// Only where [canShare] — there is no share sheet on a Linux desktop, and offering one that
  /// silently does nothing is worse than not offering it.
  Future<void> shareSavedClip(String id);

  /// Whether this platform can share a file at all.
  bool get canShare;

  /// Download this Server's whole configuration — cameras, settings, accounts and preferences —
  /// as one JSON file.
  ///
  /// **The file carries secrets in plain text**: every camera's ONVIF password, any
  /// `user:password` inside a stream URL, and every account's password hash. That is deliberate,
  /// so a restore restores the ability to connect and to sign in rather than a list of names — but
  /// it means the caller is expected to have said so and been agreed with before calling this.
  ///
  /// Admin-only, and gated on [canSaveMedia] like the other two saves.
  Future<SavedMedia> saveConfigBackup();

  /// Merge a downloaded configuration back into this Server.
  ///
  /// **Merges, and never deletes.** Everything the file names is created or overwritten whole;
  /// anything this Server has that the file does not name is left as it is. Overwriting is not
  /// field-level — a camera in the file replaces the one here entirely, rotated password included.
  ///
  /// Best-effort: the result names what was refused and why, and everything else still landed.
  Future<ConfigRestoreResult> restoreConfigBackup(List<int> bytes);

  /// Register a camera and start ingesting it.
  Future<void> createCamera(CameraRecord camera);

  /// Replace a camera's settings. The whole record is sent — the Server does not merge — so
  /// [camera] must be a complete record, not a patch.
  Future<void> updateCamera(CameraRecord camera);

  /// Unregister a camera and stop its stream. Recorded media on disk is left alone.
  Future<void> deleteCamera(String id);

  // -------------------------------------------------------------------- users
  //
  // Unlike the camera methods above, these are plain passthroughs rather than a cached,
  // push-kept-fresh view: there is no socket feed for accounts, and the Users & access screen
  // re-fetches after every mutation rather than patching a local copy.

  /// Every account on this Server.
  Future<List<UserAccount>> listUsers();

  /// Create an account.
  Future<UserAccount> createUser({
    required String username,
    required String displayName,
    required String password,
    required Role role,
  });

  /// Change an existing account's role and/or password.
  ///
  /// [signOutAllSessions] ends every session that account holds, which a password change does not
  /// do on its own — the old sessions keep working for up to 30 days otherwise.
  Future<UserAccount> updateUser(
    String username, {
    Role? role,
    String? password,
    bool signOutAllSessions,
  });

  /// End every session for one account, leaving its password alone.
  Future<void> signOutUser(String username);

  /// Change the signed-in account's own password. The Server checks [currentPassword] and rejects
  /// the change if it is wrong, so holding a session is not on its own enough to replace it.
  Future<void> changeOwnPassword({
    required String currentPassword,
    required String newPassword,
    bool signOutAllSessions,
  });

  /// Delete an account. The Server refuses to delete the last remaining Admin.
  Future<void> deleteUser(String username);

  // ----------------------------------------------------------------- settings
  //
  // Passthroughs, like the account methods above and for the same reason: there is no push feed
  // for settings, and the write returns the whole new state, so the screen has nothing to patch.

  /// Every setting that can be changed here, with its value and where that came from.
  Future<ServerSettings> settings();

  /// Apply settings changes. A null value resets that setting to the deployment's own.
  ///
  /// Returns the catalogue as it now stands, so the screen never has to work out for itself what a
  /// change did to the reset values or to the restart banner.
  Future<ServerSettings> updateSettings(Map<String, Object?> changes);
}

/// One slice of what a repository holds, as a [Listenable] of its own.
///
/// A bare [ChangeNotifier] with the notification made callable from outside, because the thing that
/// knows the slice moved is the repository around it rather than the slice itself. Held privately
/// and handed out through the interface as a plain [Listenable], so nobody but its owner can fire
/// it.
class RepositorySlice extends ChangeNotifier {
  /// Tell whoever is watching this slice that it moved.
  void changed() => notifyListeners();
}

/// A [Listenable] that never fires, for a slice that cannot move.
///
/// What [SampleServalRepository] answers with: its fixtures are `const` and nothing arrives to
/// change them, so a listener on any of its slices would wait forever. Const-constructible, which a
/// [ChangeNotifier] is not, and that is what lets the sample repository stay `const` too.
class UnchangingSlice implements Listenable {
  const UnchangingSlice();

  @override
  void addListener(VoidCallback listener) {}

  @override
  void removeListener(VoidCallback listener) {}
}
