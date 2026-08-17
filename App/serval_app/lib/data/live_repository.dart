import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../models/activity.dart';
import '../models/alert.dart';
import '../models/camera.dart';
import '../models/clip_selection.dart';
import '../models/config_backup.dart';
import '../models/conversation.dart';
import '../models/saved_clip.dart';
import '../media/media_saver.dart';
import '../media/media_sharer.dart';
import '../models/ptz.dart';
import '../models/push.dart';
import '../models/server_settings.dart';
import '../models/system_stats.dart';
import '../models/vitals_history.dart';
import '../models/timeline.dart';
import '../models/user_preferences.dart';
import '../models/wall_layout.dart';
import '../playback/playback_volume.dart';
import '../push/push_client.dart';
import 'audio_levels_socket.dart';
import 'auth/auth_controller.dart';
import 'auth/auth_models.dart' show Role;
import 'auth/authenticated_client.dart';
import 'auth/user_account.dart';
import 'camera_record.dart';
import 'dashboard_socket.dart';
import 'events_socket.dart';
import 'serval_api.dart';
import 'serval_config.dart';
import 'serval_repository.dart';
import 'telemetry_documents.dart';
import 'time_labels.dart';

/// The Server, behind the interface the screens already read.
///
/// State is cached and pull-shaped because [ServalRepository] is: REST fills it on open, the two
/// sockets keep it current, and `notifyListeners` says the structure changed. Snapshot frames are
/// the exception and go out per camera on [frameNotifier] — at ~1 fps each, notifying the whole
/// tree for a frame would rebuild the wall several times a second to repaint one tile.
///
/// Nothing here throws at the screens. A wall that cannot reach the Server shows its cameras
/// offline and keeps retrying, which is the same posture the Server takes toward a camera it
/// cannot reach.
class LiveServalRepository implements ServalRepository {
  LiveServalRepository({
    required AuthController auth,
    ServalConfig? config,
    ServalApi? api,
  }) : _auth = auth,
       _config = config ?? ServalConfig.fromEnvironment(),
       // Through [AuthenticatedClient], like the one main.dart passes explicitly. A default API
       // built without it would ignore the [AuthController] this constructor was handed: every
       // request goes out with no Authorization header, the Server answers 401, and [_loadRegistry]
       // swallows it, leaving an empty wall and no error anywhere.
       _api =
           api ??
           ServalApi(
             config: config ?? ServalConfig.fromEnvironment(),
             client: AuthenticatedClient(auth: auth),
           ) {
    _dashboard = DashboardSocket(
      config: _config,
      mintTicket: _auth.mintWsTicket,
    );
    _events = EventsSocket(config: _config, mintTicket: _auth.mintWsTicket);
  }

  final AuthController _auth;
  final ServalConfig _config;
  final ServalApi _api;
  final MediaSaver _saver = createMediaSaver();
  final MediaSharer _sharer = const MediaSharer();
  late final DashboardSocket _dashboard;
  late final EventsSocket _events;

  ServalApi get api => _api;
  ServalConfig get config => _config;

  // ------------------------------------------------------------------- state

  final _records = <String, CameraRecord>{};
  var _order = <String>[];

  final _frames = <String, ValueNotifier<Uint8List?>>{};
  final _lastFrameAt = <String, DateTime>{};

  /// Newest first, capped. Keyed by [TelemetryDocument.feedId] so a document redelivered after a
  /// socket gap updates in place rather than appearing twice — the client half of the Server's
  /// idempotent upsert.
  final _feed = <String, TelemetryDocument>{};

  /// How far back the backfill actually reached, per camera, for the cameras where it ran out of
  /// depth before it ran out of window. Absent means that camera's whole window came back.
  ///
  /// See [_historyForCamera] for how one is derived, and [feedHorizon] for what is done with it.
  final _horizons = <String, DateTime>{};

  List<TileLayout>? _savedLayout;

  /// Whether `GET /api/preferences` has come back this session.
  ///
  /// The distinction this draws is the one every write here depends on: *not read yet* and *read,
  /// and empty* produce the same screen — the design's default packing — and must not produce the
  /// same write. Saving from an unread baseline is what turns a preferences request that failed
  /// for a second into an arrangement that is gone for good, because the wall persists on every
  /// accepted move: one tile nudged, and the default that was standing in becomes the stored
  /// truth.
  ///
  /// False until the read succeeds, and it never goes back — the retry in [_watchPreferences] can
  /// only turn it on.
  bool _preferencesKnown = false;

  /// This account's notification preferences, defaults until [_apply] lands the stored ones.
  ///
  /// Defaults mean "notify about everything", which is the right thing to stand in with: a screen
  /// that briefly showed everything muted would be alarming, and [_preferencesKnown] stops anything
  /// being written back from this.
  UserPreferences _notificationPreferences = const UserPreferences();

  bool _connected = false;
  StreamSubscription<void>? _frameSweep;

  /// When we last started listening for frames: [start], the dashboard socket coming up, or the
  /// App returning from the background. Null before [start] and again after [stop], where "we are
  /// not listening" and "we have listened long enough to know" want the same answer — whatever a
  /// camera's silence means, it is not news from here.
  ///
  /// See [_frameStateOf] for what it decides.
  DateTime? _listeningSince;

  /// When the sweep in [start] last ran, so a tick can notice it arrived late. See there.
  DateTime _lastSweepAt = DateTime.now();

  /// The outstanding preferences re-read, or null when there is none — either because the document
  /// is in hand or because it arrived on the first ask.
  Timer? _preferencesRetry;

  /// Set by [dispose], and read across the await in [_watchPreferences]: a request in flight when
  /// the repository goes away would otherwise schedule a timer onto a dead object.
  bool _disposed = false;

  /// What [clockDigest] read on the previous sweep. See the sweep in [start].
  String? _lastClockDigest;
  final _subscriptions = <StreamSubscription<dynamic>>[];

  /// A camera is considered to have dropped out when its last frame is this old. The Server
  /// publishes at `Ingest:SnapshotFps` (1 by default), so this is fifteen missed frames — well
  /// past a hiccup and well short of waiting for a person to notice.
  static const _staleAfter = Duration(seconds: 15);

  /// How long after starting to listen a camera with nothing to show still reads as connecting
  /// rather than offline.
  ///
  /// Taken from [_staleAfter] rather than restated, because the two answer the same question — how
  /// long silence is allowed to last before it counts as absence — measured from two different
  /// starting points. Two constants that happened to agree would be one edit away from a wall that
  /// calls a camera dead *sooner* after a reconnect than it does while running, which is backwards.
  ///
  /// Lengthening it costs a working camera nothing: it flips to online on its first frame, about a
  /// second in. All this buys is how long a genuinely dead one is given the benefit of the doubt.
  static const _listeningWindow = _staleAfter;

  /// Bounds on the preferences re-read, matching [DashboardSocket]'s.
  static const _minPreferencesBackoff = Duration(seconds: 1);
  static const _maxPreferencesBackoff = Duration(seconds: 30);

  /// How far back the activity column and the scrubber read on open.
  ///
  /// The widest window the range button offers, and taken from it rather than restated, because
  /// the column has to reach as far back as the bar can be dragged. Two constants that happened to
  /// agree would be one range-panel edit away from a bar whose longest setting reads short.
  static const _historyWindow = TimelineRange.maxSpan;

  /// Records of each kind, per camera, the backfill asks for over [_historyWindow].
  ///
  /// The same figure the scrubber reads its own window at — see [_timelineMarkLimit] — so the two
  /// run out of depth at the same place rather than one of them going quietly further.
  ///
  /// The Server sorts newest first, so hitting this means the *oldest* records inside the window
  /// are the ones missing. That is a real hole in a bar set wide, and the one thing that must not
  /// happen is it passing unremarked: [feedHorizon] carries how far back the read actually got,
  /// and the column says so at the end of itself rather than trailing off as though the house had
  /// simply been quiet.
  static const _historyLimit = 500;

  /// Cap on the episodes fetched for one replay window. A quarter of an hour of
  /// one camera is tens of episodes even somewhere busy, so this only ever binds
  /// on a camera whose detector is misconfigured — where drawing the first two
  /// hundred boxes is no worse than drawing all of them.
  static const _replayDetectionLimit = 200;

  /// Newer than this and an utterance is still "being said", so it paints as the live caption.
  static const _captionWindow = Duration(seconds: 10);

  /// Newer than this and a camera shows the waveform mark on its tile.
  static const _audioActivityWindow = Duration(seconds: 8);

  /// How stale a vitals sample is allowed to get before the sweep re-reads it.
  ///
  /// Three sweeps rather than one. The Server samples on its own cadence (`Vitals:SampleSeconds`,
  /// 5s by default) and the CPU figure is an average over that window, so asking every 5s would be
  /// three requests to learn the same thing — and a meter that re-renders faster than the number
  /// underneath it moves is a meter pretending to a resolution it has not got.
  static const _statsTtl = Duration(seconds: 15);

  // The two that stay on this machine. Both read as per-user and are not: how loud you want the
  // app, and whether a 376px column fits, are properties of what you are sitting at. Syncing them
  // would let a phone dictate a desktop's volume. The wall layout went to the Server precisely
  // because it is the opposite — an arrangement you make once and want everywhere.
  //
  // The volume's key carries a camera id, because a level is per camera as well as per machine.
  static const _volumeKeyPrefix = 'playback_volume_v2:';
  static const _activityCollapsedKey = 'activity_collapsed_v1';

  /// How long after the last drag the level is written. Long enough that a gesture is one write
  /// rather than fifty, short enough that closing the app straight after a drag still keeps it.
  static const _volumeWriteDelay = Duration(milliseconds: 400);

  /// One notifier per camera asked about, created on demand and kept — see [playbackVolumeFor],
  /// which owes its callers the same instance every time.
  final _playbackVolumes = <String, ValueNotifier<double>>{};
  final _volumeWrites = <String, Timer>{};

  final _activityCollapsed = ValueNotifier<bool>(false);

  /// The answers [activityFor] has given, keyed by what was asked and the [_revision] it was true
  /// at. Insertion-ordered, oldest evicted past [_activityMemoEntries].
  ///
  /// Several entries rather than one because a wall and a single-camera screen ask different
  /// questions of the same repository — different `cameraId`, sometimes a different range — and a
  /// single slot let the two thrash each other into never hitting.
  ///
  /// The filter is not in the key — filtering is the column's own job now, over the pool this
  /// returns — so changing one no longer costs a rebuild of the merge underneath it.
  final _activityMemo =
      <
        ({
          String? cameraId,
          DateTime? at,
          String? range,
          int minute,
          int revision,
          bool all,
        }),
        List<ActivityItem>
      >{};

  /// Enough for a wall and a camera screen to each hold an answer, live and at a playhead, without
  /// the map itself becoming something to think about.
  static const _activityMemoEntries = 4;

  /// One entry per (camera, range) the scrubber has asked for. See [timelineFor].
  final _timelines = <String, _TimelineCache>{};

  /// The edges each range is currently being read at, keyed by [TimelineRange.key]. See
  /// [_anchorFor].
  final _anchors = <String, ({DateTime from, DateTime to, DateTime at})>{};

  /// Bumped whenever anything the derived reads are built from changes: the feed, a fetched
  /// window, a range's anchor, or which detections are drawn at all.
  ///
  /// Both [_marksFor] and [activityFor] walk hundreds of documents to answer a question that has
  /// the same answer until one of those moves, and both are called from `build`. This is the
  /// cheapest thing that can say "still the same" — a counter rather than a hash, because every
  /// mutation already runs through a method that can bump it.
  ///
  /// Deliberately global rather than per camera. A document arriving for one camera invalidates
  /// only that camera's marks, but keeping that precise would mean tracking which caches a write
  /// touched, and the cost of being wrong in the cheap direction is one extra derivation.
  var _revision = 0;

  /// Fired wherever [_revision] is bumped for a reason the feed's readers care about.
  ///
  /// The pair is not an accident: [_revision] says a derived read has gone stale, and this says the
  /// same thing to the widgets built from it. Keeping them together is what stops one being bumped
  /// without the other — a memo invalidated with nobody told is a column that does not update, and
  /// a slice fired with the memo intact is a rebuild that redraws the same rows.
  final _activityChanges = RepositorySlice();

  @override
  Listenable get activityChanges => _activityChanges;

  /// Fired when a timeline window lands. See [ServalRepository.timelineChanges] for why this is not
  /// folded into [_activityChanges].
  final _timelineChanges = RepositorySlice();

  @override
  Listenable get timelineChanges => _timelineChanges;

  final _registryChanges = RepositorySlice();

  @override
  Listenable get registryChanges => _registryChanges;

  final _preferenceChanges = RepositorySlice();

  @override
  Listenable get preferenceChanges => _preferenceChanges;

  final _alertChanges = RepositorySlice();

  @override
  Listenable get alertChanges => _alertChanges;

  /// Broadcast, because the queue screen and an open alert both want it and neither owns it.
  final _alertUpdates = StreamController<Alert>.broadcast();

  @override
  Stream<Alert> get alertUpdates => _alertUpdates.stream;

  final _vitalsChanges = RepositorySlice();

  @override
  Listenable get vitalsChanges => _vitalsChanges;

  final _deviceChanges = RepositorySlice();

  @override
  Listenable get deviceChanges => _deviceChanges;

  /// The detections behind the playlist window each camera is replaying, kept
  /// separately from [_feed] because that one is trimmed and replay reads back
  /// further than it holds. One window per camera: replay plays one at a time.
  final _replayDetections = <String, _ReplayDetections>{};

  /// One entry per camera the single-camera view has opened. See [ptzProbeFor].
  final _ptzProbes = <String, PtzProbe>{};
  final _ptzInFlight = <String>{};

  SystemStats? _stats;
  DateTime? _statsReadAt;
  bool _statsInFlight = false;

  VitalsHistory? _history;
  DateTime? _historyReadAt;
  bool _historyInFlight = false;

  /// What each camera says it is. Never refetched — make, model and serial do not change, and a
  /// firmware upgrade is rarer than an app restart.
  final _deviceInformation = <String, DeviceInformation>{};
  final _deviceInFlight = <String>{};

  /// How long a fetched window stays good before the next read refreshes it. The right edge is
  /// therefore "now, within half a minute", which is as much precision as a 12-hour track can
  /// draw anyway.
  static const _timelineTtl = Duration(seconds: 30);

  /// Marks fetched per telemetry type per window. The Server clamps `limit` at 1000; a very busy
  /// day will truncate, which is acceptable for what is a summary of the day rather than the log.
  static const _timelineMarkLimit = 500;

  // ------------------------------------------------------------------ startup

  /// Reads the registry and the last day of telemetry, then opens both sockets.
  ///
  /// Awaited by `main` before the first frame is built so the wall does not flash empty, but a
  /// failure here is not fatal: the sockets still open and retry, and the next successful
  /// [_loadRegistry] fills the wall in.
  Future<void> start() async {
    // Started rather than awaited: preferences are now a request to the Server, and they do not
    // depend on the registry — `wallLayout()` reconciles the two once both are in. Awaiting it
    // here would put a second round trip end-to-end with the registry's on every cold start, for
    // no ordering that matters.
    final preferences = _loadPreferences();

    // No volume read here: there is one per camera now, and reading every camera's on a cold start
    // would be a storage round trip each for levels nobody has asked to hear yet. They load on the
    // first ask instead — see [_volumeNotifier].
    _activityCollapsed.value = await _loadActivityCollapsed();

    await _loadRegistry();

    // One GET carries every preference, so both are taken off the same response. Two loaders
    // would be two round trips for one document.
    if (await preferences case final saved?) {
      _apply(saved);
    } else {
      // Every other startup read heals itself — the registry on the next successful load, both
      // sockets on their own backoff — and this one is the reason the wall can be the only thing
      // still wrong minutes later. Retrying puts it on the same footing.
      _watchPreferences();
    }

    await _loadHistory();

    // Not awaited: nothing on screen waits on it, and a Server that cannot answer should cost a
    // cold start nothing.
    unawaited(_refreshPushSubscription());

    _subscriptions.add(_dashboard.frames.listen(_onFrame));
    _subscriptions.add(_dashboard.connected.listen(_onConnectedChanged));
    _subscriptions.add(_events.documents.listen(_onDocument));
    _subscriptions.add(_events.alerts.listen(_onAlert));

    // The events socket is a tap, not a queue: anything published while it was down is gone, so
    // a reconnect re-reads the window rather than leaving a hole in the column.
    _subscriptions.add(_events.reconnected.listen((_) => _loadHistory()));

    unawaited(_refreshUnreadAlerts());

    _dashboard.connect();
    _events.connect();

    // Staleness is a function of the clock, not of an event — nothing arrives to tell us a
    // camera has *stopped* sending. Without this tick a dead camera stays "online" until some
    // other change happens to rebuild the wall.
    //
    // Gated on [clockDigest] rather than notifying unconditionally: the tick is here to catch a
    // transition, and the overwhelming majority of ticks have none. Notifying anyway rebuilt the
    // whole signed-in tree twelve times a minute to render the same pixels.
    _lastSweepAt = DateTime.now();
    _startListening();
    _frameSweep = Stream<void>.periodic(const Duration(seconds: 5)).listen((_) {
      // Folded into the sweep rather than given a timer of its own: this already runs on exactly
      // the cadence a vitals refresh wants, and a second periodic would be a second thing to
      // cancel in [dispose].
      unawaited(_refreshStatsIfStale());
      unawaited(_refreshHistoryIfStale());

      // A tick that arrives late is itself evidence the App was away: this stream is throttled to
      // a crawl in a hidden tab and suspended outright in a backgrounded PWA. A gap much wider
      // than the cadence means frames stopped arriving because nobody was listening, not because
      // the cameras stopped.
      //
      // Belt and braces with the lifecycle listener in `main.dart`, and worth having both: this
      // one needs nothing from the platform, so it still fixes the wall on a browser that never
      // reports being hidden. Whichever notices first wins, and [_startListening] is idempotent.
      final now = DateTime.now();
      if (now.difference(_lastSweepAt) > _listeningWindow) _startListening();
      _lastSweepAt = now;

      final digest = clockDigest();
      if (digest == _lastClockDigest) return;
      _lastClockDigest = digest;
      // The registry: what this tick can change is whether a camera reads as online, which is
      // computed from frame staleness and drawn by the tiles and the chrome around them.
      _registryChanges.changed();
    });
  }

  /// The App is back in front of somebody. Start listening again, now.
  ///
  /// Two halves, and neither is optional. The window restarts because every camera's frames are as
  /// old as the time we were away, and none of that is the cameras' fault — without it the wall
  /// paints itself `"<name> is offline"` for the second or two a reconnect takes, which is a claim
  /// about six cameras made on the strength of one socket.
  ///
  /// And both sockets are kicked because a phone that was away for ten minutes is sitting on the
  /// thirty-second backoff cap, which would stretch that window for no reason at all.
  ///
  /// Deliberately *not* a registry re-read. A camera added while the phone was in a pocket will not
  /// appear until something else refreshes it — as true before this method as after — and widening
  /// a resume into a general refresh is a different change.
  ///
  /// Not on [ServalRepository] either, matching [start] and [stop]: `SampleServalRepository` has no
  /// sockets and no clock, and a no-op on the interface would be a member the widget tests have to
  /// answer for. `_RepositoryStarter` already narrows before calling any of the three.
  void resumeLive() {
    _startListening();
    _dashboard.reconnectNow();
    _events.reconnectNow();
  }

  /// Undoes [start]: closes the feeds and forgets everything that belonged to the account that is
  /// signing out, leaving this ready to be started again.
  ///
  /// Signing out and back in without reloading the page is a real path — the rail's sign-out
  /// button puts you on `/login` with the app still running — so every cache must go: a survivor
  /// would hand the next person to sign in the previous one's cameras, activity feed, wall
  /// arrangement and vitals, on a Server that had told this App none of it.
  ///
  /// What is deliberately *not* cleared is the per-machine state — [playbackVolume] and the
  /// activity column's collapsed flag. Those live in `shared_preferences` and belong to the
  /// browser rather than the account, exactly as they do across a page reload.
  void stop() {
    for (final subscription in _subscriptions) {
      subscription.cancel();
    }
    _subscriptions.clear();

    _frameSweep?.cancel();
    _frameSweep = null;
    _preferencesRetry?.cancel();
    _preferencesRetry = null;

    // Not `close()`: that ends the streams for good, which is right when the app is going away and
    // wrong here — [start] subscribes to them again a moment later.
    _dashboard.disconnect();
    _events.disconnect();

    _records.clear();
    _order = [];
    _lastFrameAt.clear();
    _feed.clear();
    _horizons.clear();
    _activityMemo.clear();
    _timelines.clear();
    _anchors.clear();
    _feedMoved();
    _replayDetections.clear();
    _ptzProbes.clear();
    _ptzInFlight.clear();
    _deviceInformation.clear();
    _deviceInFlight.clear();

    _savedLayout = null;
    _preferencesKnown = false;

    _stats = null;
    _statsReadAt = null;
    _statsInFlight = false;
    _history = null;
    _historyReadAt = null;
    _historyInFlight = false;

    _connected = false;
    _lastClockDigest = null;
    _listeningSince = null;

    // Emptied rather than disposed, and the map is kept. A tile mid-unmount may still be listening
    // to one of these, and [_notifierFor] hands out whatever is in the map — so disposing here
    // would leave a live widget holding a dead notifier. [dispose] is where they end; this only
    // takes the last account's picture off the screen.
    for (final notifier in _frames.values) {
      notifier.value = null;
    }

    // Everything at once: this is the account leaving, and there is no part of what was held that
    // is still true. The revision was already bumped above, where the pool was cleared.
    for (final slice in _allSlices) {
      slice.changed();
    }
  }

  Future<void> _loadRegistry() async {
    try {
      final cameras = await _api.listCameras();
      _records
        ..clear()
        ..addEntries([
          for (final camera in cameras) MapEntry(camera.id, camera),
        ]);
      _order = [for (final camera in cameras) camera.id];
      _registryChanges.changed();
    } on Object {
      // Leave whatever was already loaded in place; the sockets keep retrying and the next
      // registry write refreshes this.
    }
  }

  Future<void> _loadHistory() async {
    final to = DateTime.now();
    final from = to.subtract(_historyWindow);

    // Fanned out per camera because the Server has no aggregate feed route. Concurrent rather
    // than sequential: with several cameras this is three requests each, and they do not depend
    // on one another.
    final fetched = await Future.wait([
      for (final id in _order) _historyForCamera(id, from, to),
    ], eagerError: false);

    _horizons.clear();
    for (final (index, result) in fetched.indexed) {
      for (final document in result.documents) {
        _feed[document.feedId] = document;
      }
      if (result.horizon case final horizon?) {
        _horizons[_order[index]] = horizon;
      }
    }

    _trimFeed();
  }

  /// One camera's slice of the backfill, and how far back it actually reached.
  ///
  /// A list that came back exactly [_historyLimit] long is one the Server truncated — it sorts
  /// newest first, so what is missing is everything before that list's oldest record. The camera's
  /// horizon is the *latest* of those per-kind oldest instants, because the feed is only whole back
  /// to the point where every kind still has depth: one truncated stream is enough to make the
  /// merged column incomplete from there on.
  Future<({List<TelemetryDocument> documents, DateTime? horizon})>
  _historyForCamera(String id, DateTime from, DateTime to) async {
    try {
      final results = await Future.wait<List<TelemetryDocument>>([
        _api.scenes(id, from: from, to: to, limit: _historyLimit),
        _api.utterances(id, from: from, to: to, limit: _historyLimit),
        _api.conversationTranscripts(
          id,
          from: from,
          to: to,
          limit: _historyLimit,
        ),
        _api.sounds(id, from: from, to: to, limit: _historyLimit),
        // Backfilled like the rest: without this a detection is only ever visible if it happened
        // to arrive over the socket while this tab was open, so a reload would empty the column of
        // everything the object gate found — and the boxes over the video would go with it, since
        // they read the same feed.
        _api.detections(id, from: from, to: to, limit: _historyLimit),
      ]);

      return (
        documents: [for (final list in results) ...list],
        horizon: horizonFrom(results, limit: _historyLimit),
      );
    } on Object {
      return (documents: const <TelemetryDocument>[], horizon: null);
    }
  }

  /// How far back a set of per-kind reads actually got, or null where none of them was truncated.
  ///
  /// A list exactly [limit] long is one the Server cut off. It sorts newest first, so what is
  /// missing from that kind is everything before the list's own oldest record.
  ///
  /// The answer is the *latest* of those instants rather than the earliest, and that is the whole
  /// subtlety: the merged feed is only whole back to the point where every truncated kind still
  /// has depth. Taking the earliest would claim coverage down to the deepest read while a shallower
  /// one had already run dry above it.
  @visibleForTesting
  static DateTime? horizonFrom(
    Iterable<List<TelemetryDocument>> results, {
    required int limit,
  }) {
    DateTime? horizon;
    for (final list in results) {
      if (list.isEmpty || list.length < limit) continue;

      final oldest = list
          .map((d) => d.when)
          .reduce((a, b) => a.isBefore(b) ? a : b);
      if (horizon == null || oldest.isAfter(horizon)) horizon = oldest;
    }

    return horizon;
  }

  void _onFrame(SnapshotFrame frame) {
    _lastFrameAt[frame.cameraId] = DateTime.now();

    final wasBlank = _frames[frame.cameraId]?.value == null;
    _notifierFor(frame.cameraId).value = frame.jpeg;

    // The first frame flips the camera from connecting to online, which is a structural change
    // the wall's chrome reads. Subsequent frames are the tile's business alone.
    if (wasBlank) _registryChanges.changed();
  }

  void _onConnectedChanged(bool connected) {
    // The guard is load-bearing, not tidiness: [DashboardSocket] adds `true` on *every message*, so
    // without it the arming below would run once per camera per second.
    if (_connected == connected) return;
    _connected = connected;

    // Losing the socket is not notified, and that is deliberate. It says nothing new about any one
    // camera — the window opened by the last connect simply runs out, and by the time it does we
    // really have tried and failed, which is what the wall should then say.
    //
    // Getting it back is the news: every camera's silence is explained again. The clock on that
    // explanation restarts *here* rather than only at the resume, so a reconnect that takes its
    // time cannot burn the window before any frame could have flowed.
    if (connected) _startListening();
  }

  /// Restart the window during which a camera with nothing to show reads as connecting rather than
  /// offline.
  ///
  /// Every caller means the same thing: what just changed is *our* ability to hear, not the
  /// cameras. Frame staleness cannot tell those apart, and painting the whole wall offline for the
  /// second a reconnect takes is what happens when it tries.
  void _startListening() {
    _listeningSince = DateTime.now();

    // Now, rather than at the next sweep. The window opening is a transition the tiles draw, and
    // up to five seconds of the wall still reading offline is most of the flash this exists to
    // remove.
    _lastClockDigest = clockDigest();
    _registryChanges.changed();
  }

  bool get _withinListeningWindow {
    final since = _listeningSince;
    return since != null && DateTime.now().difference(since) < _listeningWindow;
  }

  void _onDocument(TelemetryDocument document) {
    // A detection is the one record delivered twice under the same id — open, then closed — and
    // the two now travel in separate lanes on the Server, so the close can arrive ahead of a
    // heartbeat published before it. Last-write-wins would let that straggler reopen a finished
    // episode and pin its box to the picture, which is the failure the lanes were split to end.
    // An end never becomes un-ended, so the closed record simply stands.
    if (document is DetectionDocument && document.isOngoing) {
      final held = _feed[document.feedId];
      if (held is DetectionDocument && !held.isOngoing) return;
    }

    // The slice and nothing wider. This is the hot path — a detection episode reaches here twice a
    // second per camera for as long as the thing is in view — and it was the whole-repository
    // notification that made a phone with movement on three cameras rebuild every open screen six
    // times a second.
    _feed[document.feedId] = document;
    _trimFeed();
  }

  /// Bounds the pool the column and the overlays read: drops what no window can reach any more,
  /// and nothing else.
  ///
  /// **By age alone.** [TimelineRange.maxSpan] is the widest window the range button offers, so a
  /// document older than it cannot appear on any bar or in any column and is held for nobody.
  /// Everything newer is kept, which is the property the whole feature rests on: what the bar
  /// covers, the column lists.
  ///
  /// **No document cap, per kind or otherwise.** Anything counted in documents can bite before the
  /// window does: detections outnumber every other record by an order of magnitude, so a cap over
  /// the merged pool evicts every utterance within the hour and leaves a column showing a few
  /// minutes of speech under a bar claiming a day, with nothing on screen saying so. The pool is
  /// bounded by what the Server publishes in a day — the honest bound, and the one to watch if a
  /// very busy site ever makes this expensive.
  /// Bumps [_revision] on the way through, which is why both feed writes call it immediately after
  /// writing: this is the one place every change to the pool passes through, so the derived reads
  /// built on top of it cannot go stale without something here having run.
  void _trimFeed() {
    final oldest = DateTime.now().subtract(TimelineRange.maxSpan);
    _feed.removeWhere((_, document) => document.when.isBefore(oldest));
    _feedMoved();
  }

  /// The pool changed: stale the derived reads, and wake what is drawn from them.
  ///
  /// The two together, always, and through here rather than by hand — see [_activityChanges] for
  /// what each half is and what going wrong looks like when only one of them runs.
  ///
  /// The one deliberate exception is the anchor bump in [_anchorFor], which stales its memo without
  /// firing this. That runs inside a read, from `build`, and notifying there would be a rebuild
  /// scheduled during the build that asked.
  void _feedMoved() {
    _revision++;
    _activityChanges.changed();
  }

  ValueNotifier<Uint8List?> _notifierFor(String cameraId) =>
      _frames.putIfAbsent(cameraId, () => ValueNotifier<Uint8List?>(null));

  /// Closes the feeds and everything holding a listener.
  ///
  /// No longer an override: this stopped being a [ChangeNotifier] when the one notification it
  /// raised was split into [RepositorySlice]s, so the slices are disposed here alongside the rest
  /// rather than by a superclass.
  void dispose() {
    _disposed = true;
    _frameSweep?.cancel();
    _preferencesRetry?.cancel();

    // Flush rather than drop: a level set in the last fraction of a second before the app closes
    // is exactly the one the debounce would otherwise lose, and it would look like the setting
    // simply does not stick.
    for (final pending in _volumeWrites.entries) {
      if (!pending.value.isActive) continue;
      pending.value.cancel();
      unawaited(_writeVolume(pending.key));
    }

    for (final subscription in _subscriptions) {
      subscription.cancel();
    }
    _dashboard.close();
    _events.close();
    for (final notifier in _frames.values) {
      notifier.dispose();
    }
    for (final notifier in _playbackVolumes.values) {
      notifier.dispose();
    }
    _activityCollapsed.dispose();
    unawaited(_alertUpdates.close());
    for (final slice in _allSlices) {
      slice.dispose();
    }
    _api.close();
  }

  /// Every slice this repository owns, for [dispose] and for [_reset] to fire in one pass.
  ///
  /// Listed once so that adding a slice and forgetting to dispose it is not a thing that can
  /// happen quietly.
  late final List<RepositorySlice> _allSlices = [
    _activityChanges,
    _timelineChanges,
    _registryChanges,
    _preferenceChanges,
    _alertChanges,
    _vitalsChanges,
    _deviceChanges,
  ];

  // ------------------------------------------------------------------ cameras

  @override
  bool get connected => _connected;

  @override
  bool get canStreamLive => true;

  @override
  List<CameraRecord> cameraRecords() => [
    for (final id in _order) ?_records[id],
  ];

  @override
  CameraRecord? cameraRecordById(String id) => _records[id];

  @override
  List<Camera> cameras() => [
    for (final id in _order)
      if (_records[id] case final record?) _viewOf(record),
  ];

  @override
  Camera? cameraById(String id) {
    final record = _records[id];
    return record == null ? null : _viewOf(record);
  }

  /// The registry record as the wall and the single-camera view want it.
  ///
  /// Most of this is a rename; the interesting part is the three flags the Server has no field
  /// for, each derived from the one signal that does exist rather than left permanently false.
  Camera _viewOf(CameraRecord record) {
    final fresh = _hasFreshFrame(record.id);

    // Once, and read three times below. Not a getter call per field: this is a clock reading, and
    // three of them taken microseconds apart could in principle disagree.
    final state = record.enabled
        ? _frameStateOf(record.id)
        : CameraConnection.offline;

    return Camera(
      id: record.id,
      name: record.name,
      enabled: record.enabled,
      twoWayAudio: record.twoWayAudio,
      recordAudio: record.recordAudio,
      playbackGainDb: record.playbackGainDb,
      playbackGateRms: record.playbackGateRms,
      aiVision: record.aiVision,
      aiAudio: record.aiAudio,
      ptzConfigured: record.ptzConfigured,

      // No status field exists. A camera that is switched off is not "offline" — it is off on
      // purpose — so `enabled` decides first and the frame clock only speaks for the rest.
      //
      // (Off still draws the offline tile, which is a separate wrong: "Kitchen is offline" for a
      // camera nobody asked to be running. Left alone here rather than folded in, because it is a
      // wording decision and not this derivation's business.)
      connection: state,

      // No per-camera recording state is published either, so it is inferred from two things. A
      // fresh frame says the camera's ingest is alive; the `record` role says that ingest writes
      // segments. Both are needed. Freshness alone would put a REC dot on a camera set to keep
      // nothing — which looks identical to a working recorder and is the one mistake nothing
      // downstream could catch, since the absence of footage is what that camera is *for*.
      isRecording: record.enabled && fresh && record.records,

      records: record.records,

      hasAudioActivity: _heardSpeechRecently(record.id),

      needsAttention: _alertedRecently(record.id),

      // STUB: the camera model carries no resolution, and the snapshot cannot stand in for one —
      // `Ingest:SnapshotMaxMegapixels` fits it to a pixel budget, so decoding a frame would report
      // the thumbnail's size, not the camera's.
      resolutionLabel: null,

      // A disabled camera keeps its stripes — `state` is offline for it, but it is not *missing*,
      // and the flat fill is the mark of an absent feed rather than of a camera nobody asked to
      // run. `StageFallback` draws this too.
      placeholder: record.enabled && state == CameraConnection.offline
          ? TilePlaceholder.offline
          : TilePlaceholder.forCameraId(record.id),
    );
  }

  /// What the frame clock alone says about a camera, before `enabled` is taken into account.
  ///
  /// Three answers rather than two, and the middle one is the whole point. Silence has two
  /// unrelated causes — that camera has stopped sending, or nobody here has been listening long
  /// enough to have heard it yet — and freshness alone cannot tell them apart. A wall coming back
  /// from the background has stale frames for every camera at once, and "we have not heard from any
  /// of them since we started listening again" is not the same claim as "each of them has stopped".
  ///
  /// Keyed by id rather than taking a [CameraRecord], because [clockDigest] walks [_order] and the
  /// test seam never fills [_records].
  CameraConnection _frameStateOf(String cameraId) {
    if (_hasFreshFrame(cameraId)) return CameraConnection.online;

    // Never a frame at all — the cold-start posture, and deliberately not bounded by the window.
    // This says we have never successfully heard from this camera, which no amount of elapsed
    // listening changes into a measurement.
    if (!_lastFrameAt.containsKey(cameraId)) return CameraConnection.connecting;

    return _withinListeningWindow
        ? CameraConnection.connecting
        : CameraConnection.offline;
  }

  bool _hasFreshFrame(String cameraId) {
    final last = _lastFrameAt[cameraId];
    return last != null && DateTime.now().difference(last) < _staleAfter;
  }

  bool _heardSpeechRecently(String cameraId) {
    final now = DateTime.now();
    for (final document in _feed.values) {
      if (document is UtteranceDocument &&
          document.cameraId == cameraId &&
          now.difference(document.when) < _audioActivityWindow) {
        return true;
      }
    }
    return false;
  }

  /// Whether this camera has raised an alert lately — the orange tile dot and the "Someone's
  /// here" pill.
  ///
  /// An alert-labelled sound is the only telemetry that can say so. It decays on the same window
  /// as audio activity rather than latching, because nothing in the app acknowledges an alert; a
  /// latched one would stay lit until the process restarted.
  bool _alertedRecently(String cameraId) {
    final now = DateTime.now();
    for (final document in _feed.values) {
      if (document is SoundDocument &&
          document.isAlert &&
          document.cameraId == cameraId &&
          now.difference(document.when) < _audioActivityWindow) {
        return true;
      }
    }
    return false;
  }

  /// Everything here that changes with the clock rather than with an event.
  ///
  /// What makes the sweep in [start] conditional. Nothing arrives to say a camera has stopped
  /// sending or a caption aged out, so those are re-read on a timer — but the tick is not itself
  /// news, and notifying on every one rebuilds the whole tree twelve times a minute to render the
  /// same pixels. Notify only when this string moves.
  ///
  /// Four groups, all load-bearing:
  ///
  ///  * per camera, [_frameStateOf] itself — the state the tiles read, rather than the freshness
  ///    and listening window it was derived from, which is what makes this gate correct by
  ///    construction: every clock-driven transition [_viewOf] can make moves exactly one character.
  ///    It is also narrower than carrying the window as a term of its own would be, since that
  ///    would wake the sweep the moment the window closed even on a wall where every camera is
  ///    fresh. Add the two eight-second telemetry windows behind `hasAudioActivity` and
  ///    `needsAttention`;
  ///  * whether [liveCaptionFor] is currently painting, which decays on its own ten-second window
  ///    and has nothing else to clear it;
  ///  * **the minute.** [ActivityItem.timeLabel] is a pre-rendered string rather than a timestamp
  ///    (see `time_labels.dart` for why), so "2 min ago" stays whatever it was until something
  ///    re-renders the feed. A minute bucket is the coarsest thing that keeps it honest, and the
  ///    reason this digest cannot be built from the camera flags alone.
  ///
  ///  * **the vitals reading, quantised** — the alert kinds outstanding, each percentage bucketed
  ///    to 5% and each byte figure to the gigabyte. Raw, a processor figure moving from 41.2% to
  ///    41.4% would wake the sweep on every tick and undo the whole gate. Bucketing rebuilds the
  ///    tree when the *reading* moves, not when the sample does.
  ///
  /// `enabled` is deliberately absent, along with everything else on [CameraRecord]: a registry
  /// write notifies on its own path, and re-deriving it here would only widen what the sweep can
  /// wake for.
  @visibleForTesting
  String clockDigest() {
    final now = DateTime.now();
    final digest = StringBuffer()
      ..write(now.millisecondsSinceEpoch ~/ Duration.millisecondsPerMinute);

    for (final id in _order) {
      digest
        ..write('|')
        ..write(_frameStateOf(id).index)
        ..write(_heardSpeechRecently(id) ? '1' : '0')
        ..write(_alertedRecently(id) ? '1' : '0')
        ..write(liveCaptionFor(id) == null ? '0' : '1');
    }

    digest
      ..write('|')
      ..write(_statsDigest());

    return digest.toString();
  }

  /// The vitals half of [clockDigest]. See there for why every figure is bucketed.
  String _statsDigest() {
    final stats = _stats;
    if (stats == null) return '-';

    String percent(double? value) =>
        value == null ? '-' : '${(value / 5).round()}';
    String gigabytes(int? value) =>
        value == null ? '-' : '${value ~/ 1000000000}';

    return [
      for (final alert in stats.alerts) alert.kind.wireName,
      percent(stats.cpu.containerPercent),
      percent(stats.cpu.hostPercent),
      percent(stats.memory.percent),
      percent(stats.gpu.busyPercent),
      gigabytes(stats.disk.freeBytes),
      gigabytes(stats.disk.mediaBytes),
      // Not bucketed: it changes once per sweep at most, and the per-camera list is what the
      // settings page draws its bars from.
      '${stats.disk.scannedAt?.millisecondsSinceEpoch ?? '-'}',
    ].join(',');
  }

  /// Puts the registry order, the frame clock and the feed into a known state, for the tests that
  /// read them — [clockDigest] and the detection overlay.
  ///
  /// The real ingest paths cannot express what those tests need: `_onFrame` stamps
  /// `DateTime.now()`, so there is no way through it to describe a camera whose last frame is
  /// twenty seconds old short of waiting twenty seconds, and `_order` is only ever filled by
  /// [_loadRegistry], which [start] reaches only after opening both sockets. Hence a seam rather
  /// than a fake `ServalApi`.
  ///
  /// Still not a general-purpose setter: nothing here notifies, and it reaches only the
  /// fields it names.
  @visibleForTesting
  void seedForTest({
    required List<String> order,
    Map<String, DateTime> lastFrameAt = const {},
    List<TelemetryDocument> documents = const [],
    SystemStats? stats,
    Map<String, DateTime> horizons = const {},

    /// Defaults to null — the window closed, which is what a repository that was constructed but
    /// never started already is, and why every test written before the window existed still reads
    /// the same.
    DateTime? listeningSince,
  }) {
    _order = List.of(order);
    _listeningSince = listeningSince;
    _lastFrameAt
      ..clear()
      ..addAll(lastFrameAt);
    _feed
      ..clear()
      ..addEntries([for (final d in documents) MapEntry(d.feedId, d)]);
    _stats = stats;
    _horizons
      ..clear()
      ..addAll(horizons);
    // This seam writes the feed behind the methods that would normally announce it, so it has to
    // say so itself or a test seeding twice would read the first seed's answer out of the memos.
    _feedMoved();
  }

  /// Runs the feed's retention pass over whatever [seedForTest] put there.
  ///
  /// Separate from that seam rather than folded into it, because the two are opposites: the seed
  /// describes the state a trim is supposed to act on, and a seed that trimmed itself could not
  /// describe one.
  @visibleForTesting
  void trimFeedForTest() => _trimFeed();

  /// Delivers one document as the events socket would.
  ///
  /// Separate from [seedForTest], which writes the feed behind this: the ordering guard in
  /// [_onDocument] is a rule about what a second delivery under the same id may do to the first,
  /// so it can only be shown by arrival.
  @visibleForTesting
  void receiveForTest(TelemetryDocument document) => _onDocument(document);

  @override
  Uint8List? snapshotFor(String cameraId) => _frames[cameraId]?.value;

  /// Null — the stage carries the live WebRTC view, so the wall clock is the right clock. This
  /// starts returning a position once playback exists.
  @override
  DateTime? pictureTakenAt(String cameraId) => null;

  @override
  ValueListenable<Uint8List?> frameNotifier(String cameraId) =>
      _notifierFor(cameraId);

  /// The oldest instant the feed can speak for, or null when the backfill covered its whole
  /// window.
  ///
  /// Scoped like [activityFor]: one camera answers with its own read's depth, and the whole house
  /// answers with the *latest* of them — the merged column is only whole back to the point where
  /// every camera in it still has something to say.
  @override
  DateTime? feedHorizon({String? cameraId}) {
    if (cameraId != null) return _horizons[cameraId];
    if (_horizons.isEmpty) return null;

    return _horizons.values.reduce((a, b) => a.isAfter(b) ? a : b);
  }

  // ------------------------------------------------------------------ activity

  /// The edges a feed read is scoped to, from the scrubber's window and the playhead together.
  ///
  /// Either may be null and both only ever narrow: no range is everything held, and no playhead is
  /// a screen running live. Where both name a top edge the earlier one wins, which during replay
  /// is always the playhead — the range is the track it is moving along.
  ///
  /// [asOf] is quantised to the second on the way through, for the reason [activityFor]'s memo
  /// gives: the playhead ticks ten times a second and nothing in a feed whose finest label is
  /// "now" changes between two of them.
  ({DateTime? from, DateTime? to}) _readWindow(
    TimelineRange? range,
    DateTime? asOf,
  ) {
    final at = asOf == null ? null : _toSecond(asOf);
    if (range == null) return (from: null, to: at);

    // The scrubber's own edges, not a fresh reading of the clock. Computing them here again would
    // put the column a few seconds ahead of the track beside it — listing events the track has no
    // room to draw, and dropping ones at the left edge it is still drawing. See [_anchorFor].
    //
    // `create: false`: a column can be built before any track has fetched, and a feed read is not
    // a reason to fix the edges every later reader will be held to.
    final anchor = _anchorFor(range, create: false);

    return (
      from: anchor.from,
      to: at == null || anchor.to.isBefore(at) ? anchor.to : at,
    );
  }

  /// The live feed, plus whatever the scrubber has already fetched for a window overlapping the one
  /// being read.
  ///
  /// The caches hold what [_feed] does not: a chosen day, or the window around a row older than the
  /// backfill reaches. Deduped by feedId, so a document in both counts once.
  ///
  /// With no range the overlap is against the playhead alone, which is the containment test this
  /// made before ranges existed — a feed scoped to nothing has no window to overlap.
  Map<String, TelemetryDocument> _poolFor(DateTime? from, DateTime? to) {
    final pool = <String, TelemetryDocument>{..._feed};

    final foldFrom = from ?? to;
    final foldTo = to ?? from;
    if (foldFrom == null || foldTo == null) return pool;

    for (final cache in _timelines.values) {
      if (cache.to.isBefore(foldFrom) || cache.from.isAfter(foldTo)) continue;
      pool.addAll(cache.documents);
    }

    return pool;
  }

  @override
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  }) {
    // Quantised to the second, and memoised on it. The playhead ticks ten times a second and the
    // column is a Column rather than a lazy list, so without this a replaying wall would rebuild
    // every row it is drawing ten times a second to show the same thing — and sub-second precision
    // means nothing to a feed whose finest label is "now".
    //
    // Memoised while live as well, which the playhead alone could not make safe: a live read has no
    // `at` to key on, and its answer changes the moment a document arrives. [_revision] is what
    // closes that — in the key, so an arriving document misses rather than being served a stale
    // list, and out of it nothing changes but the clock.
    final at = asOf == null ? null : _toSecond(asOf);
    final (:from, :to) = _readWindow(range, asOf);

    // The wall clock, always — even at a playhead. What [asOf] decides is *whether* a row is
    // listed, never what it is dated. Dating against the playhead read "now" over footage from
    // last Tuesday, which is a claim about the present made over a recording of the past: the row
    // has to keep saying when the thing actually happened.
    //
    // In the key to the minute, which is the finest these labels change at, so a paused playhead
    // still ages "2 min ago" into "3 min ago" rather than freezing it.
    final now = DateTime.now();
    final key = (
      cameraId: cameraId,
      at: at,
      range: range?.key,
      minute: now.millisecondsSinceEpoch ~/ 60000,
      revision: _revision,
      // In the key because it changes the answer without changing a document: two screens can be
      // asking at the same revision and want different rows back.
      all: includeAllDetections,
    );
    if (_activityMemo[key] case final hit?) return hit;

    final pool = _poolFor(from, to);

    final documents =
        pool.values
            .where((d) => cameraId == null || d.cameraId == cameraId)
            // The whole point: nothing that has not happened yet on screen. A feed running ahead
            // of the picture is a feed telling you what is about to happen.
            .where((d) => to == null || !d.when.isAfter(to))
            // And nothing from before the window the scrubber is showing, so the column and the
            // track beside it describe the same slice of the day.
            .where((d) => from == null || !d.when.isBefore(from))
            .toList()
          ..sort((a, b) => b.when.compareTo(a.when));

    // Built from `documents` rather than from `_feed`, and before the loop below rather than
    // inside it. Both matter: `documents` is already scoped to this camera and clamped by `asOf`,
    // so a voice from after the playhead cannot give a row a bubble — and the utterances a settled
    // conversation is counted from are present here, dropped only at item-build time.
    final conversations = _ConversationIndex.of(documents);

    final items = <ActivityItem>[];
    for (final document in documents) {
      // Superseded by the transcript.
      if (document is DiarizationDocument) continue;

      // Utterances and conversation transcripts describe the same audio — the transcript is the
      // better *later* view, not a correction — so a column rendering both shows the same words
      // twice. Where a conversation has settled into a transcript, its raw utterances drop out.
      if (document is UtteranceDocument &&
          document.conversationId != null &&
          conversations.settled.contains(document.conversationId)) {
        continue;
      }

      // The same gate the box and the scrubber tick pass through — see [_drawn]. A parked car is
      // stored and queryable and is not a claim that anyone should look, and a column reporting
      // every one of them buries the rows that are.
      if (document is DetectionDocument &&
          !_drawn(document, includeAll: includeAllDetections)) {
        continue;
      }

      if (_itemOf(document, now, conversations) case final item?) {
        items.add(item);
      }
    }

    // Oldest out first. Keys carry [_revision], so a busy feed mints a new one per document and the
    // map would otherwise grow without bound.
    _activityMemo[key] = items;
    while (_activityMemo.length > _activityMemoEntries) {
      _activityMemo.remove(_activityMemo.keys.first);
    }

    return items;
  }

  static DateTime _toSecond(DateTime at) => DateTime.fromMillisecondsSinceEpoch(
    at.millisecondsSinceEpoch - at.millisecond,
    isUtc: at.isUtc,
  );

  /// A detection as a row.
  ///
  /// Reads as presence rather than as a measurement — "Person at the front door"
  /// is what happened; the confidence and frame count are how we know, and belong
  /// in the detail view rather than the running list. The tense follows
  /// [DetectionDocument.isOngoing], which is the one thing about a detection an
  /// operator glancing at the column actually needs.
  /// One row is one object, so the subject is always singular. Three people in
  /// shot is three rows, each with its own start and its own duration, which is
  /// what makes "still there" mean something about a particular person rather
  /// than about whether anybody at all is left.
  String _detectionText(DetectionDocument document) {
    final subject = _capitalise(document.label);

    return document.isOngoing ? '$subject, still there' : subject;
  }

  static String _capitalise(String label) =>
      label.isEmpty ? label : label[0].toUpperCase() + label.substring(1);

  ActivityItem? _itemOf(
    TelemetryDocument document,
    DateTime now,
    _ConversationIndex conversations,
  ) {
    final name = _records[document.cameraId]?.name ?? document.cameraId;

    final (String text, bool speech) = switch (document) {
      SceneDocument(:final description) => (description, false),
      UtteranceDocument(:final transcript) => ('“$transcript”', true),
      ConversationTranscriptDocument(:final text) => ('“$text”', true),
      DiarizationDocument() => ('', false),
      // The bare label rather than the full AudioSet phrase: "Vehicle horn" reads as a row,
      // "Vehicle horn, car horn, honking" reads as a database dump.
      SoundDocument(:final shortLabel) => ('$shortLabel heard', false),
      DetectionDocument() => (_detectionText(document), false),
    };

    if (text.trim().isEmpty || text == '“”') return null;

    return ActivityItem(
      id: document.feedId,
      kind: document.kind,
      cameraId: document.cameraId,
      cameraName: name,
      at: document.when,
      speaker: _speakerOf(document),
      speakerNumber: conversations.bubbleFor(document),

      // A settled conversation gets no row-level face: there is no whole-conversation feeling, and
      // averaging its turns' readings into one would be an invention. Its faces ride the turns.
      emotion: document is UtteranceDocument
          ? ActivityEmotion.fromWire(document.emotion)
          : null,
      turns: document is ConversationTranscriptDocument
          ? _turnsOf(document)
          : const [],
      timeLabel: activityTimeLabel(document.when, now: now, heard: speech),
      text: text,
      icon: _iconOf(document),
      label: _labelOf(document),
      isSpeech: speech,
      // Sound records are the only telemetry carrying severity, and the operator decides which
      // labels earn it — see SoundOptions.AlertLabels.
      isAlert:
          (document is SoundDocument && document.isAlert) ||
          (document is DetectionDocument && document.isAlert),
      isRecent: isRecent(document.when, now: now),
    );
  }

  /// A settled conversation's turns, drawn inside its one row.
  ///
  /// The row stays one row — that decision is unchanged — but the turns are now visible in it, so
  /// the attribution you were watching while people spoke does not vanish the moment they stop.
  ///
  /// Numbered off `speaker_count` rather than off the turns' own distinct count, because the
  /// heading's "2 speakers" reads that same field: taking both from one source is what makes it
  /// impossible for the heading and the bubbles to contradict each other on screen.
  ///
  /// Each turn is quoted individually, so a settled row is typeset like a live one.
  List<ActivityTurn> _turnsOf(ConversationTranscriptDocument transcript) {
    final numbered = transcript.speakerCount > 1;

    return [
      for (final turn in transcript.turns)
        if (turn.text.trim().isNotEmpty)
          ActivityTurn(
            text: '“${turn.text}”',
            speakerNumber: numbered ? turn.speaker + 1 : null,
            emotion: ActivityEmotion.fromWire(turn.emotion),
          ),
    ];
  }

  /// What the single-camera panel puts above the row — where the voice was, or how many there
  /// were. Never *which*.
  ///
  /// Never the wire's own `speaker_0`: that is an index into one conversation, meaningless across
  /// conversations and meaningless to a reader. Identity rides in the bubble on the quote, which
  /// frees this slot for the question that is true of every utterance.
  String? _speakerOf(TelemetryDocument document) => switch (document) {
    UtteranceDocument() => 'At the camera',

    // How many voices, never which — the bubbles answer that. A one-voice conversation reads the
    // same as a lone utterance, because that is what it is.
    ConversationTranscriptDocument(:final speakerCount) =>
      speakerCount > 1 ? '$speakerCount speakers' : 'At the camera',
    _ => null,
  };

  /// The class the model named, for the column's *What was seen or heard* facet.
  ///
  /// Only the two records that carry one. A sound's whole AudioSet phrase rather than its short
  /// form, so "Gunshot, gunfire" is one filter — the row reads the short label, but folding two
  /// phrases that share a first synonym into one chip would filter for more than it says.
  String? _labelOf(TelemetryDocument document) => switch (document) {
    DetectionDocument(:final label) => label,
    SoundDocument(:final label) => label,
    _ => null,
  };

  /// Which glyph leads the row.
  ///
  /// The design picks one per event — a person, a car, a cat, a dropped connection — and no
  /// telemetry field names a glyph directly. Speech rows are unambiguous from the record type.
  /// A sound carries a real classification, so its glyph is derived from that. A scene has only
  /// the prose, so that is read for the subjects the design draws, falling back to the generic
  /// scene glyph rather than guessing.
  ActivityIcon _iconOf(TelemetryDocument document) => switch (document) {
    SoundDocument(:final label) => _soundIcon(label),
    SceneDocument(:final description) => _sceneIcon(description),
    // A detection names its subject outright, so unlike a scene there is nothing
    // to infer — the model already said what it is.
    DetectionDocument(:final label) => _detectionIcon(label),
    _ => ActivityIcon.speech,
  };

  /// The detector's class straight to a glyph. Exact matches rather than the
  /// substring reading a scene needs: these are a fixed vocabulary from the
  /// model's label file, not English prose, so a mapping that misses should fall
  /// back to the generic glyph rather than guess from a shared substring.
  ActivityIcon _detectionIcon(String label) => switch (label) {
    'person' => ActivityIcon.person,
    'car' || 'truck' || 'bus' || 'motorcycle' || 'bicycle' => ActivityIcon.car,
    'cat' => ActivityIcon.cat,
    'dog' => ActivityIcon.dog,
    _ => ActivityIcon.scene,
  };

  ActivityIcon _sceneIcon(String description) {
    final text = description.toLowerCase();
    bool mentions(List<String> words) => words.any(text.contains);

    if (mentions([
      'person',
      'people',
      'someone',
      'man ',
      'woman ',
      'courier',
      'delivery',
    ])) {
      return ActivityIcon.person;
    }
    if (mentions(['car', 'vehicle', 'truck', 'suv', 'van ', 'driving'])) {
      return ActivityIcon.car;
    }
    if (mentions(['cat', 'dog', 'fox'])) return ActivityIcon.cat;
    if (mentions(['garage'])) return ActivityIcon.garage;
    return ActivityIcon.scene;
  }

  /// Groups a raw AudioSet label into one of the design's glyphs.
  ///
  /// Client-side on purpose: the Server stores the model's own label verbatim precisely so this
  /// grouping can be rewritten — or replaced with a real taxonomy — without a schema version.
  /// Substring matching is sound here rather than sloppy: AudioSet display names are hierarchical
  /// English phrases, so "Bark" and "Bow-wow" really do sit under the same subject. The list is
  /// kept short and the fallback generic, because a wrong glyph is worse than a plain one.
  ActivityIcon _soundIcon(String label) {
    final text = label.toLowerCase();
    bool mentions(List<String> words) => words.any(text.contains);

    // Alarming things first: several also match a later group ("Car alarm" contains "car"), and
    // the severity reading is the one that matters.
    if (mentions([
      'alarm',
      'siren',
      'gunshot',
      'glass',
      'shatter',
      'scream',
      'smoke detector',
      'explosion',
    ])) {
      return ActivityIcon.alarm;
    }
    if (mentions(['dog', 'bark', 'bow-wow', 'howl', 'growl', 'whimper'])) {
      return ActivityIcon.dog;
    }
    if (mentions(['cat', 'meow', 'purr', 'bird', 'animal'])) {
      return ActivityIcon.cat;
    }
    if (mentions([
      'vehicle',
      'car',
      'engine',
      'horn',
      'truck',
      'motorcycle',
      'bus',
    ])) {
      return ActivityIcon.car;
    }
    if (mentions(['door', 'slam', 'garage', 'knock'])) {
      return ActivityIcon.garage;
    }
    if (mentions(['speech', 'conversation', 'singing', 'shout', 'chatter'])) {
      return ActivityIcon.speech;
    }
    return ActivityIcon.scene;
  }

  // ------------------------------------------------------------------ summary

  @override
  SceneSummary? summaryFor(
    String cameraId, {
    DateTime? asOf,
    TimelineRange? range,
  }) {
    // The same window and the same pool the feed reads, from the same two helpers — a summary
    // taken from outside the window the panel is scoped to would be describing a moment none of
    // the rows under it are allowed to mention.
    final (:from, :to) = _readWindow(range, asOf);
    final pool = _poolFor(from, to);

    SceneDocument? newest;
    for (final document in pool.values) {
      if (document is! SceneDocument || document.cameraId != cameraId) continue;
      if (to != null && document.when.isAfter(to)) continue;
      if (from != null && document.when.isBefore(from)) continue;
      if (newest == null || document.when.isAfter(newest.when)) {
        newest = document;
      }
    }

    if (newest == null || newest.description.trim().isEmpty) return null;

    return SceneSummary(text: newest.description);
  }

  @override
  String? liveCaptionFor(String cameraId) {
    final now = DateTime.now();
    UtteranceDocument? newest;

    for (final document in _feed.values) {
      if (document is! UtteranceDocument || document.cameraId != cameraId) {
        continue;
      }
      if (now.difference(document.when) >= _captionWindow) continue;
      if (newest == null || document.when.isAfter(newest.when)) {
        newest = document;
      }
    }

    return newest == null || newest.transcript.trim().isEmpty
        ? null
        : '“${newest.transcript}”';
  }

  /// Whether the UI draws this episode at all.
  ///
  /// The detector stores every class it knows, but only an alert is a claim that
  /// someone should look — so a car, a truck, or a person who never cleared the
  /// alert confidence bar is kept and queryable and drawn nowhere: no box on the
  /// video, no tick on the scrubber, and nothing on its activity band either.
  ///
  /// One predicate for all three, so the box and the tick can never disagree.
  ///
  /// [includeAll] is the deliberate exception, and it arrives as an argument
  /// from the screen doing the asking rather than as state held here — see
  /// [ServalRepository.detectionsFor]. An episode admitted by it draws in the
  /// accent rather than in orange and marks the scrubber as activity rather
  /// than as an alert: it widens what is shown, never what is claimed to
  /// matter.
  static bool _drawn(DetectionDocument episode, {required bool includeAll}) =>
      episode.isAlert || includeAll;

  /// Boxes for whatever is in front of this camera *now*.
  ///
  /// Only episodes still open. A closed one is a record of something that has
  /// gone, and the live overlay is a claim about the present — painting a
  /// finished episode over the live view would be a lie about what is there.
  /// Replay asks [detectionsAt] instead, which is a claim about an instant.
  ///
  /// **Still open is not the same as still there, and the difference is the bug
  /// this bounds.** An episode closes by a `ended_at` arriving over the socket,
  /// and that close is a single message: the Server's broadcast queue drops the
  /// oldest event when a slow reader backs it up, and the oldest event is
  /// exactly the close, since the position heartbeats filling the queue are all
  /// newer than it. One dropped close left an episode drawing a box for the
  /// rest of the session — every one that went that way accumulating on the
  /// picture, and only ever visible with the overlay widened, because an alert
  /// episode is short and a parked car is not.
  ///
  /// So the right edge is [DetectionDocument.coversUntil], the same bound
  /// [DetectionDocument.overlaysAt] applies on the replay side and for the same
  /// reason. An object that really is there is re-sent every detection frame,
  /// which carries its bound forward continuously; one whose close was lost
  /// stops drawing a grace period after its last real evidence.
  @override
  List<DetectionBox> detectionsFor(
    String cameraId, {
    bool includeAllDetections = false,
  }) {
    final now = DateTime.now();

    return [
      for (final document in _feed.values)
        if (document is DetectionDocument &&
            document.cameraId == cameraId &&
            document.isOngoing &&
            !now.isAfter(document.coversUntil) &&
            _drawn(document, includeAll: includeAllDetections))
          ...document.overlays,
    ];
  }

  /// Boxes for whatever was in front of this camera at [when].
  ///
  /// Reads the episode's track rather than its peak box, so a box sits where the
  /// object was at that instant instead of where it happened to look clearest —
  /// see [DetectionDocument.overlaysAt].
  ///
  /// Reads the replay cache first and the feed second. The feed is capped and
  /// trimmed, so scrubbing back far enough finds the episodes evicted; the cache
  /// is what [ensureReplayDetections] fills for the window being played.
  @override
  List<DetectionBox> detectionsAt(
    String cameraId,
    DateTime when, {
    bool includeAllDetections = false,
  }) {
    final cached = _replayDetections[cameraId];
    final Iterable<DetectionDocument> episodes =
        cached != null &&
            !when.isBefore(cached.from) &&
            !when.isAfter(cached.to)
        ? cached.episodes
        : _feed.values.whereType<DetectionDocument>().where(
            (document) => document.cameraId == cameraId,
          );

    return [
      for (final episode in episodes.where(
        (e) => _drawn(e, includeAll: includeAllDetections),
      ))
        ...episode.overlaysAt(when),
    ];
  }

  /// Loads the detections overlapping a stretch of recording, for replay to draw
  /// over it.
  ///
  /// Called once per playlist window rather than per frame: fifteen minutes is
  /// both what the player opens at a time and a sane amount of telemetry to hold,
  /// and a scrub that stays inside the loaded span costs nothing. A window
  /// already covered is not re-fetched.
  @override
  Future<void> ensureReplayDetections(
    String cameraId,
    DateTime from,
    DateTime to,
  ) async {
    final cached = _replayDetections[cameraId];
    if (cached != null &&
        !from.isBefore(cached.from) &&
        !to.isAfter(cached.to)) {
      return;
    }

    try {
      final episodes = await _api.detections(
        cameraId,
        from: from,
        to: to,
        limit: _replayDetectionLimit,
      );
      _replayDetections[cameraId] = _ReplayDetections(from, to, episodes);
      _activityChanges.changed();
    } on Object {
      // Leave whatever was cached in place. A window that failed to load is a
      // stage without boxes, which replay degrades to cleanly.
    }
  }

  // ----------------------------------------------------------------- timeline

  /// The scrubber's window: coverage from `/coverage`, marks from telemetry.
  ///
  /// Self-priming and synchronous, which is what keeps the interface's contract literal — this
  /// returns the cache and kicks a fetch behind it when the entry is missing or stale. Two rules
  /// make that safe to call from `build`:
  ///
  ///  * [notifyListeners] is only ever called from the async continuation, never from inside this
  ///    method. Notifying during a build is a framework error.
  ///  * a TTL plus an in-flight flag means repeated rebuilds do not become repeated requests.
  ///
  /// A `FutureProvider.family` would fit the shape, and is deliberately not used: the first rule is
  /// already enforced by the framework, there are three read sites in the whole app, and converting
  /// them would cost the synchronous pull-shaped property [ServalRepository] depends on. Worth
  /// revisiting only if a fourth self-priming read appears, or if this and [ptzProbeFor] /
  /// [deviceInformationFor] ever stop agreeing on how they behave.
  ///
  /// Coverage is read from `/coverage` rather than `/recordings` because the latter is one row
  /// per four-second segment — measured against the live Server, two hours is 1798 rows and
  /// 208 KB — which is an expensive way to draw a solid bar.
  @override
  TimelineWindow timelineFor(
    String cameraId,
    TimelineRange range, {
    bool includeAllDetections = false,
  }) {
    final key = '$cameraId/${range.key}';
    final cached = _timelines[key];

    if (cached == null) {
      // Nothing yet, and nothing to draw a window against. Anchor on the range's own edges so
      // the track has a shape, and say it is loading so it does not read as "nothing was ever
      // recorded".
      //
      // `create: true` — this is a track about to fetch, so these are the edges every later reader
      // should be held to. Asking without creating would leave the column free to compute its own
      // a moment later, which is the disagreement the anchor exists to prevent.
      final (:from, :to) = _anchorFor(range, create: true);
      unawaited(_loadTimeline(cameraId, range));
      return TimelineWindow(from: from, to: to, loading: true);
    }

    // Only a live range goes stale. A chosen period's edges are fixed, so refetching it would ask
    // the Server the same question every half minute and get the same answer — the marks that do
    // arrive meanwhile ride the events socket, and `_marksFor` drops any that fall outside.
    if (range.live &&
        !cached.inFlight &&
        DateTime.now().difference(cached.fetchedAt) > _timelineTtl) {
      unawaited(_loadTimeline(cameraId, range));
    }

    // Rebuilt only when something it is built from moved. The same object otherwise, which the
    // scrubber compares by identity to skip re-deriving its layers — see [_TimelineCache.window].
    final held = cached.window;
    if (held != null &&
        cached.revision == _revision &&
        cached.all == includeAllDetections) {
      return held;
    }

    // The cache's own edges rather than the range's current anchor: these are the edges the marks
    // were fetched for, and a window claiming edges its contents do not cover would silently drop
    // the marks at whichever end had moved.
    final window = TimelineWindow(
      from: cached.from,
      to: cached.to,
      coverage: cached.coverage,
      marks: _marksFor(cameraId, cached, includeAll: includeAllDetections),
      loading: cached.loading,
    );

    cached
      ..window = window
      ..revision = _revision
      ..all = includeAllDetections;

    return window;
  }

  /// The fetched marks, plus anything the events socket has delivered since.
  ///
  /// The union is what makes the scrubber live: an utterance arriving on `/api/events` appears at
  /// once — the socket already notifies — while the depth of the window comes from the fetch.
  /// Deduped by [TelemetryDocument.feedId], the same key the feed itself is stored under, so a
  /// document present in both counts once.
  ///
  /// Both halves are filtered here rather than on the way in: filtering the fetched half before
  /// caching it makes the toggle lie in both directions — marks pulled while the overlay was wide
  /// survive narrowing it, and widening it adds no historical marks until the entry expires,
  /// which for a chosen period is never.
  List<TimelineMark> _marksFor(
    String cameraId,
    _TimelineCache cached, {
    required bool includeAll,
  }) {
    bool drawn(TelemetryDocument document) =>
        document is! DetectionDocument ||
        _drawn(document, includeAll: includeAll);

    final marks = <String, TimelineMark>{
      for (final document in cached.documents.values)
        if (drawn(document)) document.feedId: _markOf(document),
    };

    for (final document in _feed.values) {
      if (document.cameraId != cameraId) continue;
      if (document.when.isBefore(cached.from) ||
          document.when.isAfter(cached.to)) {
        continue;
      }
      if (!drawn(document)) continue;
      marks[document.feedId] = _markOf(document);
    }

    return marks.values.toList()..sort((a, b) => a.at.compareTo(b.at));
  }

  /// Sounds and detections are the two records carrying severity, so they are the only ones that
  /// can mark the track as an alert. A scene's trigger and motion score describe what caused a
  /// description, not whether it mattered.
  ///
  /// A detection reaching here is an alert unless the screen asked for the wide overlay, so the
  /// detection arm of the severity test has two live sides and both are reachable. Marking a
  /// non-alert episode as an alert because the operator asked to see it would put orange on the
  /// scrubber for a car — the toggle widens what is drawn, never what is claimed to matter.
  ///
  /// A detection's `ran` is its real duration rather than a point, which is the one mark on this
  /// track with genuine width: an episode that is still open is drawn up to now.
  TimelineMark _markOf(TelemetryDocument document) {
    final seconds = switch (document) {
      SceneDocument(:final frameSpanSeconds) => frameSpanSeconds,
      UtteranceDocument(:final durationSeconds) => durationSeconds,
      SoundDocument(:final durationSeconds) => durationSeconds,
      DetectionDocument(:final duration) => duration.inMilliseconds / 1000,
      _ => 0.0,
    };

    return TimelineMark(
      at: document.when,
      ran: Duration(milliseconds: (seconds * 1000).round()),
      kind:
          (document is SoundDocument && document.isAlert) ||
              (document is DetectionDocument && document.isAlert)
          ? TimelineMarkKind.alert
          : TimelineMarkKind.activity,
      of: activityKindOf(document.kind),
    );
  }

  /// Coverage, or nothing. An empty band reads as "no footage here", which is wrong but silent;
  /// losing the marks with it would be wrong and loud, and the marks are the part that has a
  /// source on every Server this app has ever talked to.
  Future<List<CoverageSpan>> _coverageOrNone(
    String cameraId,
    DateTime from,
    DateTime to,
  ) async {
    try {
      return await _api.coverage(cameraId, from: from, to: to);
    } on Object {
      return const [];
    }
  }

  /// The edges every read of [range] is answered at, for as long as they stay good.
  ///
  /// Anchored once and shared rather than recomputed per read, for two reasons. A `to` that
  /// tracked the wall clock would slide every mark leftwards between rebuilds, so the track would
  /// never sit still. And a second reader computing its own edges a moment later — the activity
  /// column does, through [_readWindow] — would scope itself to a window the track beside it is
  /// not drawing, so the two would disagree about which slice of the day they describe.
  ///
  /// Shared across cameras as well as across readers, which is what lets a wall's merged track
  /// position every camera's marks against one window: see [TimelineWindow.union].
  ///
  /// Refreshed on the same [_timelineTtl] the fetch is, so the right edge is "now, within half a
  /// minute". A chosen period's edges are its own and never move, so its anchor is computed once
  /// and then kept forever.
  ///
  /// [create] is false for a reader that is only asking. A feed built before any track has fetched
  /// still needs edges, but it is not a reason to fix the ones every later reader is held to.
  ({DateTime from, DateTime to}) _anchorFor(
    TimelineRange range, {
    required bool create,
  }) {
    final now = DateTime.now();
    final held = _anchors[range.key];
    if (held != null &&
        (!range.live || now.difference(held.at) <= _timelineTtl)) {
      return (from: held.from, to: held.to);
    }

    final to = range.endAt(now);
    // Asked of the range rather than measured back from [to], which is the same instant for a
    // preset and for a chosen period and is not for a window still growing into its width.
    final from = range.startAt(now);
    if (create) {
      _anchors[range.key] = (from: from, to: to, at: now);
      _revision++;
    }

    return (from: from, to: to);
  }

  Future<void> _loadTimeline(String cameraId, TimelineRange range) async {
    final key = '$cameraId/${range.key}';
    final cache = _timelines.putIfAbsent(key, _TimelineCache.new);
    if (cache.inFlight) return;
    cache.inFlight = true;

    final (:from, :to) = _anchorFor(range, create: true);

    // Stamped before the request rather than only after it, so a window read while this is still
    // in flight reports the edges it is being fetched for. Left at their defaults a loading cache
    // describes a zero-length window at the epoch, which the track survives — it draws nothing
    // while loading — but which puts it in open disagreement with the column beside it, reading
    // the same range off the same anchor.
    if (cache.loading) {
      cache
        ..from = from
        ..to = to
        ..revision = -1;
    }

    try {
      final results = await Future.wait<Object>([
        // Coverage is caught separately: it is the one read here that a Server predating this
        // feature does not answer, and a 404 for the footage bar should not also cost the marks,
        // which come from routes that have always existed.
        _coverageOrNone(cameraId, from, to),
        _api.scenes(cameraId, from: from, to: to, limit: _timelineMarkLimit),
        _api.utterances(
          cameraId,
          from: from,
          to: to,
          limit: _timelineMarkLimit,
        ),
        // Fetched here as well as in the feed: without it only sounds that arrived live would
        // ever mark the scrubber, so scrolling back would silently lose every alert.
        _api.sounds(cameraId, from: from, to: to, limit: _timelineMarkLimit),
        _api.detections(
          cameraId,
          from: from,
          to: to,
          limit: _timelineMarkLimit,
        ),
      ]);

      cache
        ..from = from
        ..to = to
        ..fetchedAt = DateTime.now()
        ..loading = false
        ..coverage = results[0] as List<CoverageSpan>
        ..documents = {
          for (final document in <TelemetryDocument>[
            ...results[1] as List<SceneDocument>,
            ...results[2] as List<UtteranceDocument>,
            ...results[3] as List<SoundDocument>,
            // Unfiltered: `_marksFor` decides what is drawn, so the same cache entry can answer a
            // narrow reader and a wide one without either refetching.
            ...results[4] as List<DetectionDocument>,
          ])
            document.feedId: document,
        };

      // Both: the window brought documents with it, and the coverage it also brought is what the
      // seek-on-open and the clip save have been waiting for.
      _feedMoved();
      _timelineChanges.changed();
    } on Object {
      // Leave the previous window in place — the same posture as _loadRegistry. A failed refresh
      // should not blank a scrubber that was drawing fine a moment ago.
      cache.fetchedAt = DateTime.now();
    } finally {
      cache.inFlight = false;
    }
  }

  @override
  Uri? vodUrlFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) => _api.vodUrl(cameraId, from: from, to: to);

  @override
  Future<Duration> vodStartOffsetFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    try {
      return await _api.vodStartOffset(cameraId, from: from, to: to);
    } on Object {
      // Zero is what the window would have used before it was asked, so a failure here costs the
      // sub-segment correction rather than the replay.
      return Duration.zero;
    }
  }

  @override
  Future<String?> mintStreamToken() => _auth.mintStreamToken();

  /// A fresh feed each time, owned by the caller.
  ///
  /// Not cached and not held here on purpose: subscribing is what makes the Server start
  /// measuring this camera, so the feed's lifetime has to be the panel's rather than the
  /// repository's. One kept alive here would keep a camera measured for the life of the app.
  @override
  AudioLevelFeed? watchAudioLevels(String cameraId) => AudioLevelFeed(
    config: _config,
    cameraId: cameraId,
    mintTicket: _auth.mintWsTicket,
  );

  // ------------------------------------------------------------------- layout

  /// The wall's arrangement.
  ///
  /// A saved layout is honoured only for the cameras that still exist, and any camera it does
  /// not mention is appended — so adding a camera on the Server makes it appear on the wall
  /// rather than being invisible until the layout is reset.
  ///
  /// The rules themselves live in [WallGrid], because the wall screen runs them again on its own
  /// working copy while you drag; the repository is not the only caller.
  @override
  List<TileLayout> wallLayout() =>
      WallGrid.reconcile(_savedLayout ?? const <TileLayout>[], _order);

  /// This account's own state, from `GET /api/preferences`.
  ///
  /// Null on any failure, which the wall answers by packing the design's default — the same thing
  /// it does for somebody who has never arranged one — and which the overlay answers by drawing
  /// alerts only. There is no local cache behind this and deliberately none: a `shared_preferences`
  /// copy would only be a second answer to go stale against the first.
  ///
  /// Standing in for the arrangement is safe; *writing* from it is not, which is what
  /// [_preferencesKnown] separates. The App being served by the Server it is asking does not make
  /// the Server reachable when it asks: a browser holding a cached bundle reaches this screen while
  /// the Server is still starting, and a Server with models to load can be a good half-minute from
  /// its first accepted connection.
  Future<UserPreferences?> _loadPreferences() async {
    try {
      return await _api.preferences();
    } on Object {
      return null;
    }
  }

  /// Takes a fetched document, and with it the right to write one back.
  ///
  /// [notifyListeners] here rather than at the call sites, and it is not decoration: this lands
  /// *after* [_loadRegistry] has already notified, so by the time the arrangement is in hand the
  /// wall has drawn a default pack and been told nothing since. A quiet assignment left that
  /// standing for the whole session — the wall showing defaults over a saved layout on every
  /// interactive sign-in, with only a browser reload putting it right.
  void _apply(UserPreferences saved) {
    _savedLayout = saved.wallLayout;
    _notificationPreferences = saved;
    _preferencesKnown = true;
    // The revision as well as the slice: the saved arrangement is one of the things the derived
    // reads are keyed against, so a memo answered before this landed is answering for a wall that
    // has since been rearranged.
    _revision++;
    _preferenceChanges.changed();
  }

  /// Re-reads preferences until they arrive, then lets the wall settle onto them.
  ///
  /// Capped exponential backoff, the same shape and the same bounds as [DashboardSocket]'s: this
  /// is the same situation — a Server that will be there shortly — and a second retry cadence in
  /// the same app would only be a second thing to reason about.
  ///
  /// Arriving late is the whole point, so what lets this reach a wall that is already on screen is
  /// [_apply]'s notification and the wall's willingness to re-seed from it — see `_reconcile` in
  /// `wall_screen.dart`. Neither is optional: the camera set has not changed by the time this
  /// lands, so a wall that only re-seeded on cameras would never see it.
  void _watchPreferences() {
    if (_preferencesKnown || _preferencesRetry != null) return;

    var backoff = _minPreferencesBackoff;

    void schedule() {
      _preferencesRetry = Timer(backoff, () async {
        backoff = backoff * 2 > _maxPreferencesBackoff
            ? _maxPreferencesBackoff
            : backoff * 2;

        if (await _loadPreferences() case final saved?) {
          _preferencesRetry = null;
          _apply(saved);
          return;
        }

        // Disposal races the request: a timer scheduled here would outlive the repository.
        if (_disposed) return;
        schedule();
      });
    }

    schedule();
  }

  /// Saves the arrangement, showing it immediately and storing it behind.
  ///
  /// The local assignment and [notifyListeners] come first so the wall settles the moment the
  /// button is pressed rather than after a round trip — the same ordering, and the same reason, as
  /// [setPlaybackVolume]. A rejected write therefore leaves the screen showing an arrangement the
  /// Server does not have, which is recoverable by pressing save again and is a better failure
  /// than a wall that jumps back under your hands.
  @override
  bool get preferencesKnown => _preferencesKnown;

  @override
  Future<void> saveWallLayout(List<TileLayout> layout) async {
    // Refused rather than queued while the stored arrangement is unread. What would be sent here
    // is a default pack standing in for an arrangement nobody has seen yet, and the Server has no
    // way to tell the two apart — see [_preferencesKnown]. The wall keeps the rearrange control
    // shut for the same reason, so reaching this is a bug rather than a race.
    if (!_preferencesKnown) return;

    _savedLayout = layout;
    _preferenceChanges.changed();

    await _api.saveWallLayout(layout);
  }

  // ------------------------------------------------------------- notifications

  /// Tells the Server about this browser's subscription again, every launch.
  ///
  /// Three things this repairs, none of which announce themselves:
  ///
  ///  * A browser may **reissue or discard** a subscription whenever it likes and never tells the
  ///    page. Re-reading what it currently holds and sending that is how the Server's record stops
  ///    being a URL nobody answers on. The row is keyed by a hash of the endpoint, so sending back
  ///    the unchanged one costs a write and changes nothing.
  ///  * The deployment's **VAPID key can change** — a database restored without one, a fresh
  ///    install pointed at old browsers. A subscription is bound to the key it was made with, so
  ///    every message to it is then rejected with nothing said to anyone. Comparing the key we
  ///    subscribed with against the one the Server now offers turns permanent silence into one
  ///    re-subscribe.
  ///  * Somebody signing in as a **different account** on a browser that is already subscribed
  ///    would otherwise keep notifying the first account on this machine.
  ///
  /// Silent on every failure. This is bookkeeping nobody asked for; the notifications screen is
  /// where problems are meant to be diagnosed, and it says far more than a toast could.
  Future<void> _refreshPushSubscription() async {
    if (!PushClient.isSupported) return;

    try {
      final existing = await PushClient.current();
      if (existing == null) return;

      final config = await _api.pushConfig();

      if (PushClient.subscribedKey != config.vapidPublicKey) {
        // Bound to a key this Server no longer signs with. Re-subscribing is the only repair, and
        // it needs no permission prompt — the browser has already granted it.
        if (await PushClient.subscribe(config.vapidPublicKey)
            case final renewed?) {
          await _api.registerPushDevice(
            endpoint: renewed.endpoint,
            p256dh: renewed.p256dh,
            auth: renewed.auth,
          );
        }
        return;
      }

      await _api.registerPushDevice(
        endpoint: existing.endpoint,
        p256dh: existing.p256dh,
        auth: existing.auth,
      );
    } on Object {
      // Deliberately swallowed. See the note above.
    }
  }

  @override
  UserPreferences get notificationPreferences => _notificationPreferences;

  @override
  Future<void> saveNotificationPreferences({
    bool? enabled,
    List<CameraNotificationRule>? rules,
  }) async {
    // Refused while unread, for the reason [saveWallLayout] gives: what is on screen would be the
    // defaults standing in for preferences nobody has read, and writing those would store the
    // stand-in — here, silently muting cameras somebody had deliberately left on.
    if (!_preferencesKnown) return;

    // Written back from the Server's answer rather than assumed, unlike the wall. This is not a
    // drag being followed at pointer rate: it is one switch, the round trip is imperceptible, and
    // the Server's copy is the one that decides what actually gets sent.
    _apply(
      await _api.saveNotificationPreferences(enabled: enabled, rules: rules),
    );
  }

  @override
  Future<PushConfig> pushConfig() => _api.pushConfig();

  @override
  Future<List<PushDevice>> pushDevices() => _api.pushDevices();

  @override
  Future<void> registerPushDevice({
    required String endpoint,
    required String p256dh,
    required String auth,
  }) => _api.registerPushDevice(endpoint: endpoint, p256dh: p256dh, auth: auth);

  @override
  Future<void> unregisterPushDevice(String deviceId) =>
      _api.unregisterPushDevice(deviceId);

  @override
  Future<PushTestResult> sendTestNotification() => _api.sendTestNotification();

  // ------------------------------------------------------------------- volume

  @override
  ValueListenable<double> playbackVolumeFor(String cameraId) =>
      _volumeNotifier(cameraId);

  @override
  void setPlaybackVolume(String cameraId, double travel) {
    // The notifier first and unconditionally, so the knob and the audio follow the finger with no
    // latency. The write is the slow part and nothing is waiting on it — same ordering as
    // saveWallLayout, for the same reason.
    _volumeNotifier(cameraId).value = travel.clamp(0.0, 1.0);

    _volumeWrites[cameraId]?.cancel();
    _volumeWrites[cameraId] = Timer(
      _volumeWriteDelay,
      () => _writeVolume(cameraId),
    );
  }

  /// This camera's notifier, made on first ask and kept.
  ///
  /// Opens at the camera's own seed rather than at silence or at unity, because the first paint
  /// happens before storage answers and a knob that jumps once the read lands looks like a control
  /// changing its mind. The stored position then overwrites it if there is one.
  ValueNotifier<double> _volumeNotifier(String cameraId) {
    final existing = _playbackVolumes[cameraId];
    if (existing != null) return existing;

    final notifier = ValueNotifier<double>(_seedTravel(cameraId));
    _playbackVolumes[cameraId] = notifier;

    unawaited(
      _loadVolume(cameraId).then((stored) {
        // A drag that beat the read wins: the level under the finger is the one that was asked for
        // most recently, and overwriting it with what was on disk would undo it mid-gesture.
        if (_disposed || stored == null) return;
        if (_volumeWrites[cameraId]?.isActive ?? false) return;
        notifier.value = stored;
      }),
    );

    return notifier;
  }

  /// Where a camera's knob sits before this client has moved it: whatever gain the camera carries.
  ///
  /// Unity when the registry has nothing to say, which covers both an uncalibrated camera and an id
  /// asked about before the registry landed.
  double _seedTravel(String cameraId) =>
      travelFor(volume: 1, db: _records[cameraId]?.playbackGainDb ?? 0);

  Future<void> _writeVolume(String cameraId) async {
    final notifier = _playbackVolumes[cameraId];
    if (notifier == null) return;
    await (await SharedPreferences.getInstance()).setDouble(
      '$_volumeKeyPrefix$cameraId',
      notifier.value,
    );
  }

  Future<double?> _loadVolume(String cameraId) async {
    try {
      final stored = (await SharedPreferences.getInstance()).getDouble(
        '$_volumeKeyPrefix$cameraId',
      );
      return stored?.clamp(0.0, 1.0);
    } on Object {
      // Browser storage nobody controls: an unreadable level falls back to the seed, never throws.
      return null;
    }
  }

  // ------------------------------------------------------- activity panel

  @override
  ValueListenable<bool> get activityPanelCollapsed => _activityCollapsed;

  @override
  void setActivityPanelCollapsed(bool collapsed) {
    // The notifier first, so both screens re-lay-out on the click rather than on the write —
    // same ordering, and the same reason, as [setPlaybackVolume] and [saveWallLayout].
    _activityCollapsed.value = collapsed;
    unawaited(_writeActivityCollapsed(collapsed));
  }

  Future<void> _writeActivityCollapsed(bool collapsed) async {
    await (await SharedPreferences.getInstance()).setBool(
      _activityCollapsedKey,
      collapsed,
    );
  }

  Future<bool> _loadActivityCollapsed() async {
    try {
      return (await SharedPreferences.getInstance()).getBool(
            _activityCollapsedKey,
          ) ??
          false;
    } on Object {
      // Same as the two loaders above: a preference is worth losing, not crashing over.
      return false;
    }
  }

  // --------------------------------------------------------------------- ptz

  /// What this camera's PTZ can do, cached and self-priming — the same shape [timelineFor] uses,
  /// and safe to call from `build` for the same two reasons: [notifyListeners] only ever fires
  /// from the async continuation, and an in-flight flag stops repeated rebuilds becoming repeated
  /// requests.
  ///
  /// A camera with no ONVIF endpoint is answered without a request at all: there is nothing to
  /// ask, and the answer cannot change until its settings do.
  @override
  PtzProbe ptzProbeFor(String cameraId) {
    final record = _records[cameraId];
    if (record != null && !record.ptzConfigured) {
      return const PtzNotConfigured();
    }

    final cached = _ptzProbes[cameraId];
    if (cached != null) return cached;

    unawaited(_loadPtzProbe(cameraId));
    return const PtzProbing();
  }

  Future<void> _loadPtzProbe(String cameraId) async {
    if (!_ptzInFlight.add(cameraId)) return;

    try {
      _ptzProbes[cameraId] = await _api.ptzCapabilities(cameraId);
    } on ServalApiException catch (e) {
      // 400 is "no ONVIF endpoint", which is a configuration fact rather than a failure; anything
      // else is the camera or its ONVIF service, and the operator wants the Server's own words.
      _ptzProbes[cameraId] = e.statusCode == 400
          ? const PtzNotConfigured()
          : PtzUnknown(e.message);
    } on Object catch (e) {
      _ptzProbes[cameraId] = PtzUnknown('$e');
    } finally {
      _ptzInFlight.remove(cameraId);
      _deviceChanges.changed();
    }
  }

  // ------------------------------------------------------------------ vitals

  /// The Server's own figures, cached and self-priming — the same shape [ptzProbeFor] uses, and
  /// safe to call from `build` for the same two reasons: [notifyListeners] only ever fires from
  /// the async continuation, and [_statsInFlight] stops repeated rebuilds becoming repeated
  /// requests.
  @override
  SystemStats? systemStats() {
    if (_statsReadAt == null) unawaited(_refreshStats());
    return _stats;
  }

  /// Called from the sweep. Does nothing until something has asked for the figures at all —
  /// [_statsReadAt] stays null until the first [systemStats] call — so a session that never opens
  /// the server page or draws the wall banner never polls for this.
  Future<void> _refreshStatsIfStale() async {
    final readAt = _statsReadAt;
    if (readAt == null) return;
    if (DateTime.now().difference(readAt) < _statsTtl) return;

    await _refreshStats();
  }

  /// The sparkline series, cached and self-priming on the same terms as [systemStats].
  ///
  /// Its own read rather than a field on the stats refresh: the wall calls [systemStats] for its
  /// disk banner every sweep and has no sparkline to draw, so pairing them would put an hour of
  /// samples on every wall poll. Only the server page calls this, so only the server page pays.
  @override
  VitalsHistory? vitalsHistory() {
    if (_historyReadAt == null) unawaited(_refreshHistory());
    return _history;
  }

  /// Called from the sweep, and — like [_refreshStatsIfStale] — does nothing until something has
  /// asked for the series at all.
  Future<void> _refreshHistoryIfStale() async {
    final readAt = _historyReadAt;
    if (readAt == null) return;
    if (DateTime.now().difference(readAt) < _statsTtl) return;

    await _refreshHistory();
  }

  Future<void> _refreshHistory() async {
    if (_historyInFlight) return;
    _historyInFlight = true;

    // Stamped before the request, for the reason _refreshStats gives.
    _historyReadAt = DateTime.now();

    try {
      _history = await _api.vitalsHistory();
    } on Object {
      // Leave the previous series in place, as the stats refresh does: a momentary failure should
      // not blank a chart that was drawing correctly a moment ago.
    } finally {
      _historyInFlight = false;
      _vitalsChanges.changed();
    }
  }

  Future<void> _refreshStats() async {
    if (_statsInFlight) return;
    _statsInFlight = true;

    // Stamped before the request rather than after, so a Server that is slow or unreachable does
    // not turn the sweep into a request per tick.
    _statsReadAt = DateTime.now();

    try {
      _stats = await _api.systemStats();
    } on Object {
      // Leave the previous sample in place. A momentary failure should not blank a page that was
      // reading correctly a moment ago, and the sweep will ask again.
    } finally {
      _statsInFlight = false;
      _vitalsChanges.changed();
    }
  }

  /// Make, model and firmware. Self-priming like [ptzProbeFor]; null while unread, and for a
  /// camera with no ONVIF endpoint to ask.
  @override
  DeviceInformation? deviceInformationFor(String cameraId) {
    final cached = _deviceInformation[cameraId];
    if (cached != null) return cached;

    final record = _records[cameraId];
    if (record != null && !record.ptzConfigured) return null;

    unawaited(_loadDeviceInformation(cameraId));
    return null;
  }

  Future<void> _loadDeviceInformation(String cameraId) async {
    if (!_deviceInFlight.add(cameraId)) return;

    try {
      _deviceInformation[cameraId] = await _api.deviceInformation(cameraId);
      _deviceChanges.changed();
    } on Object {
      // Left absent rather than recorded as a failure: this fills in a subtitle, and a camera that
      // would not answer simply has one line less. The next open retries.
    } finally {
      _deviceInFlight.remove(cameraId);
    }
  }

  // ----------------------------------------------------------------- commands

  @override
  Future<void> ptzMove(
    String cameraId, {
    double pan = 0,
    double tilt = 0,
    double zoom = 0,
  }) => _api.ptzMove(cameraId, pan: pan, tilt: tilt, zoom: zoom);

  @override
  Future<void> ptzStop(String cameraId) => _api.ptzStop(cameraId);

  @override
  Future<void> ptzZoomTo(String cameraId, double position) =>
      _api.ptzZoomTo(cameraId, position);

  /// Null on any failure as well as on a camera that does not report a position, because the two
  /// land the caller in the same place: dead reckoning, with the slider saying so. A 502 from an
  /// unreachable camera is not worth a different slider than a camera that stays quiet.
  @override
  Future<ZoomPosition?> ptzZoomPosition(String cameraId) async {
    try {
      return await _api.ptzStatus(cameraId);
    } catch (_) {
      return null;
    }
  }

  @override
  Future<void> ptzHome(String cameraId) => _api.ptzHome(cameraId);

  @override
  Future<void> ptzPreset(String cameraId, String preset) =>
      _api.ptzPreset(cameraId, preset);

  @override
  bool get canSaveMedia => true;

  @override
  Future<SavedMedia> saveSnapshot(String cameraId) async {
    final stamp = _fileStamp(DateTime.now());
    final download = await _api.openMedia(
      _api.snapshotUrl(cameraId),
      fallbackName: '$cameraId-$stamp.jpg',
    );

    return _saver.save(
      fileName: download.fileName,
      mimeType: 'image/jpeg',
      stream: download.stream,
    );
  }

  @override
  Future<SavedMedia> saveClip(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    void Function(int bytes)? onBytes,
  }) async {
    final download = await _api.openMedia(
      _api.clipUrl(cameraId, from: from, to: to),
      fallbackName: '$cameraId-${_fileStamp(from)}.mp4',
    );

    final saved = await _saver.save(
      fileName: download.fileName,
      mimeType: 'video/mp4',
      stream: download.stream,
      onBytes: onBytes,
    );

    // Reported rather than inferred from the file's length: the Server measured what it actually
    // wrote, and a clip short because the recording restarted is a different thing from one short
    // because the camera was off.
    return SavedMedia(
      fileName: saved.fileName,
      location: saved.location,
      bytes: saved.bytes,
      truncatedTo: download.truncated ? download.covered : null,
    );
  }

  // ------------------------------------------------------------- saved clips

  @override
  Future<List<RecordedSegment>> segmentsFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    try {
      return await _api.recordedSegments(cameraId, from: from, to: to);
    } on Object {
      // An empty list reads as "nothing here to trim", which is what the trimmer would show anyway
      // — and is better than a mode that refuses to open because one request failed.
      return const [];
    }
  }

  @override
  Future<List<SavedClip>> savedClips({String? query, String? cameraId}) =>
      _api.listClips(query: query, cameraId: cameraId);

  @override
  Future<SavedClipDetail> savedClip(String id) => _api.getClip(id);

  @override
  Future<SavedClip> keepClip({
    required String cameraId,
    required DateTime from,
    required DateTime to,
    required String name,
  }) =>
      _api.saveClipToServer(cameraId: cameraId, from: from, to: to, name: name);

  @override
  Future<ClipProgress> clipProgress(String id) => _api.clipProgress(id);

  @override
  Future<void> renameClip(String id, String name) => _api.renameClip(id, name);

  @override
  Future<void> deleteClip(String id) => _api.deleteClip(id);

  @override
  Future<Uri?> savedClipUrl(String id) async =>
      _api.savedClipUrl(id, streamToken: await _auth.mintStreamToken());

  @override
  Future<Map<String, Uri>> clipPosterUrls(List<String> ids) async {
    if (ids.isEmpty) return const {};

    final token = await _auth.mintStreamToken();
    return {
      for (final id in ids) id: _api.clipPosterUrl(id, streamToken: token),
    };
  }

  // --------------------------------------------------------------------- alerts

  int _unreadAlerts = 0;

  /// How many alerts nobody has opened. What the rail's dot is drawn from.
  ///
  /// Kept here rather than fetched by whoever draws it, because the rail is under every wide
  /// screen and a count it re-read on every rebuild would be a request per frame. Seeded when the
  /// feeds start, corrected by every list read — those carry the true figure — and moved by the
  /// live feed in between, so a doorbell lights the rail without anything having asked.
  @override
  int get unreadAlerts => _unreadAlerts;

  void _setUnreadAlerts(int value) {
    if (value == _unreadAlerts) return;
    _unreadAlerts = value;
    _alertChanges.changed();
  }

  Future<void> _refreshUnreadAlerts() async {
    try {
      _setUnreadAlerts((await _api.listAlerts()).unread);
    } on Object {
      // A rail without its dot is a worse rail, not a broken one.
    }
  }

  /// An alert arrived, or the one already in the queue got its clip.
  ///
  /// Only the first of those is new, and [AlertClipState.waiting] is what tells them apart: a raise
  /// is always pending, because the clip is cut some sixteen seconds later, and a republish never
  /// is. Counting both left the rail one too high for every alert nobody had opened yet, until the
  /// next list read corrected it.
  void _onAlert(Alert alert) {
    if (alert.clipState.waiting && !alert.read && !alert.dismissed) {
      _setUnreadAlerts(_unreadAlerts + 1);
    }

    // No slice of its own. [_setUnreadAlerts] has already raised the only state this holds, and
    // firing one as well was a whole-tree rebuild that redrew a count it had just redrawn. The
    // stream is the narrow wire instead: the queue is fetched by the screen that lists it, and what
    // that screen and an open alert need from a republish is which alert settled.
    _alertUpdates.add(alert);
  }

  @override
  Future<AlertQueue> alerts({String? cameraId, DateTime? before}) async {
    final page = await _api.listAlerts(cameraId: cameraId, before: before);

    // The list is the authority: it counts the whole queue rather than a page of it, so this is
    // where a drift the live feed introduced gets corrected. Only an unnarrowed read can say
    // anything about the total.
    if (cameraId == null || cameraId.isEmpty) {
      _setUnreadAlerts(page.unread);
    }

    return page;
  }

  @override
  Future<Alert> alert(String id) => _api.getAlert(id);

  @override
  Future<void> markAlertRead(String id) => _api.markAlertRead(id);

  @override
  Future<void> dismissAlert(String id) => _api.dismissAlert(id);

  @override
  Future<int> dismissAllAlerts({String? cameraId}) =>
      _api.dismissAllAlerts(cameraId: cameraId);

  @override
  Future<Uri?> alertClipUrl(String id) async =>
      _api.alertClipUrl(id, streamToken: await _auth.mintStreamToken());

  @override
  Future<Map<String, Uri>> alertPosterUrls(List<Alert> alerts) async {
    if (alerts.isEmpty) return const {};

    final token = await _auth.mintStreamToken();
    return {
      for (final alert in alerts)
        alert.id: _api.alertPosterUrl(
          alert.id,
          streamToken: token,
          state: alert.clipState,
        ),
    };
  }

  @override
  Future<SavedMedia> downloadSavedClip(
    String id, {
    void Function(int bytes)? onBytes,
  }) async {
    // The bearer header rather than a stream token: this goes through the authenticated client
    // like any other request, and unlike a <video> element it can set one.
    final download = await _api.openMedia(
      _api.savedClipUrl(id),
      fallbackName: 'clip-$id.mp4',
    );

    return _saver.save(
      fileName: download.fileName,
      mimeType: 'video/mp4',
      stream: download.stream,
      onBytes: onBytes,
    );
  }

  @override
  bool get canShare => _sharer.canShare;

  @override
  Future<void> shareSavedClip(String id) async {
    final download = await _api.openMedia(
      _api.savedClipUrl(id),
      fallbackName: 'clip-$id.mp4',
    );

    await _sharer.share(
      fileName: download.fileName,
      mimeType: 'video/mp4',
      stream: download.stream,
    );
  }

  @override
  Future<SavedMedia> saveConfigBackup() async {
    final download = await _api.openMedia(
      _api.configBackupUrl(),
      fallbackName: 'serval-config-${_fileStamp(DateTime.now())}.json',
    );

    return _saver.save(
      fileName: download.fileName,
      mimeType: 'application/json',
      stream: download.stream,
    );
  }

  @override
  Future<ConfigRestoreResult> restoreConfigBackup(List<int> bytes) async {
    final result = await _api.restoreConfig(bytes);

    // A restore rewrites the registry underneath the cached copy. Reloaded here rather than left to
    // the caller, for the same reason createCamera patches its own map: this class owns that cache,
    // and a screen that forgot to ask would leave the wall showing cameras as they were before.
    await _loadRegistry();
    _registryChanges.changed();

    return result;
  }

  /// `20260802-140530` — the same stamp the Server puts in `Content-Disposition`, so a Server that
  /// does not send one still produces the same name.
  static String _fileStamp(DateTime at) {
    String two(int v) => v.toString().padLeft(2, '0');
    return '${at.year}${two(at.month)}${two(at.day)}'
        '-${two(at.hour)}${two(at.minute)}${two(at.second)}';
  }

  @override
  Future<String> openWebRtc(String cameraId, String offer) =>
      _api.exchangeSdp(cameraId, offer);

  @override
  Future<void> createCamera(CameraRecord camera) async {
    final created = await _api.createCamera(camera);
    _records[created.id] = created;
    if (!_order.contains(created.id)) _order = [..._order, created.id];
    _registryChanges.changed();
  }

  @override
  Future<void> updateCamera(CameraRecord camera) async {
    final updated = await _api.updateCamera(camera);
    _records[updated.id] = updated;
    _registryChanges.changed();
  }

  @override
  Future<void> deleteCamera(String id) async {
    await _api.deleteCamera(id);
    _records.remove(id);
    _order = [
      for (final existing in _order)
        if (existing != id) existing,
    ];
    _frames.remove(id)?.dispose();
    _lastFrameAt.remove(id);
    _registryChanges.changed();
  }

  @override
  Future<List<UserAccount>> listUsers() => _api.listUsers();

  @override
  Future<UserAccount> createUser({
    required String username,
    required String displayName,
    required String password,
    required Role role,
  }) => _api.createUser(
    username: username,
    displayName: displayName,
    password: password,
    role: role,
  );

  @override
  Future<UserAccount> updateUser(
    String username, {
    Role? role,
    String? password,
    bool signOutAllSessions = false,
  }) => _api.updateUser(
    username,
    role: role,
    password: password,
    signOutAllSessions: signOutAllSessions,
  );

  @override
  Future<void> signOutUser(String username) => _api.signOutUser(username);

  @override
  Future<void> changeOwnPassword({
    required String currentPassword,
    required String newPassword,
    bool signOutAllSessions = false,
  }) => _api.changeOwnPassword(
    currentPassword: currentPassword,
    newPassword: newPassword,
    signOutAllSessions: signOutAllSessions,
  );

  @override
  Future<void> deleteUser(String username) => _api.deleteUser(username);

  @override
  Future<ServerSettings> settings() => _api.settings();

  @override
  Future<ServerSettings> updateSettings(Map<String, Object?> changes) =>
      _api.updateSettings(changes);
}

/// What one pass over the feed can say about the conversations in it.
///
/// Two questions, both of which need every document before any row can be built: which
/// conversations have settled into a transcript, and how many voices each one turned out to have.
///
/// **Emotion is deliberately not its business.** That arrives on the wire, resolved Server-side,
/// because this side cannot do the join: an utterance's timestamp is when the VAD *emitted* it —
/// after the speech plus the trailing silence it waited through — so lining utterances up against
/// turn times needs the VAD's own minimum-silence setting, which never leaves the module. An
/// alignment attempted here would have the span both backwards and offset, and would fail
/// silently. See `ConversationReprocessor.SpanOf`.
class _ConversationIndex {
  _ConversationIndex._(this.settled, this._voices);

  factory _ConversationIndex.of(List<TelemetryDocument> documents) {
    final settled = <String>{};
    final voices = <String, Set<int>>{};

    for (final document in documents) {
      switch (document) {
        case ConversationTranscriptDocument(:final conversationId):
          settled.add(conversationId);

        case UtteranceDocument(:final conversationId?, :final speaker):
          if (_speakerIndex(speaker) case final index?) {
            (voices[conversationId] ??= <int>{}).add(index);
          }

        default:
          break;
      }
    }

    return _ConversationIndex._(settled, voices);
  }

  /// Conversations that have a transcript, whose raw utterances therefore drop out of the feed.
  final Set<String> settled;

  /// Distinct *parsed* speaker indices per conversation.
  ///
  /// Parsed rather than raw, and that is the point: a label the bubble could not draw must not be
  /// allowed to push the count past one, or the row would promise a second voice and then show a
  /// single bubble.
  final Map<String, Set<int>> _voices;

  /// The number to draw on this row, or null for no bubble at all.
  ///
  /// Only for a live utterance — a settled conversation's numbers ride its turns. Null whenever
  /// there was nobody to distinguish this voice from: one voice in the conversation, no
  /// conversation to scope a count to, or a label this side cannot read.
  ///
  /// Retroactive by construction. A conversation that has only been heard from once has one voice,
  /// so its rows draw nothing; when a second arrives, the earlier rows gain bubbles too. Nothing
  /// extra is needed to repaint — the arriving document already notifies, and it *is* the second
  /// voice.
  int? bubbleFor(TelemetryDocument document) {
    if (document is! UtteranceDocument) return null;

    final conversationId = document.conversationId;
    if (conversationId == null) return null;

    final heard = _voices[conversationId];
    if (heard == null || heard.length < 2) return null;

    return switch (_speakerIndex(document.speaker)) {
      final index? => index + 1,
      _ => null,
    };
  }

  /// Reads `speaker_0`, `speaker_1`, … — the literal shape `SpeakerLabeller` publishes.
  ///
  /// Strict on purpose. Matching a trailing digit instead is tempting and wrong: anything already
  /// 1-based, like the `Speaker 1` an older fixture carried, would parse as 1 and draw ② for the
  /// first voice. A shape the contract does not define draws nothing rather than a guess.
  static int? _speakerIndex(String? label) => label == null
      ? null
      : int.tryParse(_label.firstMatch(label)?.group(1) ?? '');

  static final _label = RegExp(r'^speaker_(\d+)$');
}

/// One fetched scrubber window, per camera and range.
///
/// Mutable and private: it is a cache slot, not a value the screens see. [TimelineWindow] is what
/// crosses the interface, rebuilt from this on every read so the marks can be unioned with
/// whatever the events socket has delivered since the fetch.
class _TimelineCache {
  DateTime fetchedAt = DateTime.fromMillisecondsSinceEpoch(0);
  DateTime from = DateTime.fromMillisecondsSinceEpoch(0);
  DateTime to = DateTime.fromMillisecondsSinceEpoch(0);

  /// True until the first successful fetch. After that a failed refresh leaves the last good
  /// window drawing rather than reverting to a loading track.
  bool loading = true;
  bool inFlight = false;

  List<CoverageSpan> coverage = const [];

  /// The documents the marks are drawn from, keyed by [TelemetryDocument.feedId] so the union with
  /// the live feed dedupes.
  ///
  /// **Whole documents, not marks.** The activity column has to be answerable at the playhead as
  /// well as at now, and the live feed cannot do it — scrubbing back a couple of hours already runs
  /// off the end of what it holds. These were fetched anyway to draw the track, so serving the
  /// column from them costs no request, and it is still one document per document in memory.
  Map<String, TelemetryDocument> documents = const {};

  /// The last window built from this cache, and the repository revision it was built at.
  ///
  /// The scrubber reads through `build`, so without this every rebuild rescans the whole live feed
  /// and re-sorts. Holding the built [TimelineWindow] rather than only its marks is what lets the
  /// widget memoise on identity: two rebuilds with nothing changed in between get the same object
  /// back, so the track can skip re-deriving its layers as well.
  TimelineWindow? window;
  int revision = -1;

  /// The overlay width [window] was built at. Alongside [revision] rather than folded into it,
  /// because it changes the marks without any document having moved.
  bool all = false;
}

/// The detections covering one replay window, and the span they were fetched for.
///
/// The span is held alongside them because it is what says whether a playhead can
/// be answered from this at all — an episode list on its own cannot distinguish
/// "nothing was detected then" from "that stretch was never loaded".
class _ReplayDetections {
  const _ReplayDetections(this.from, this.to, this.episodes);

  final DateTime from;
  final DateTime to;
  final List<DetectionDocument> episodes;
}
