import 'dart:async';

import 'package:flutter/foundation.dart';

import '../data/serval_repository.dart';
import '../models/timeline.dart';
import 'replay_source.dart';
import 'vod_player.dart';

/// The whole wall replaying together, against one clock.
///
/// The single-camera [ReplayController] takes its position from its player, which is right when
/// there is one: the picture is the truth and the playhead follows it. That does not generalise.
/// Eight players each reporting their own position have eight opinions about what time it is, and
/// following any of them makes the other seven wrong.
///
/// So the playhead here is derived from the wall clock — an instant, the real time it was true,
/// and a rate — and every player is a slave that gets nudged back towards it. Deriving rather than
/// accumulating matters: a tick that arrives late, or not at all because the tab was in the
/// background, changes nothing, where a counter advanced per tick would have quietly lost the time
/// it was not running for.
class WallReplayController extends ChangeNotifier {
  WallReplayController({
    required this.repository,
    this._playerFactory,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final ServalRepository repository;
  final VodPlayer Function()? _playerFactory;

  /// Injectable so the drift arithmetic is testable without waiting in real time.
  final DateTime Function() _clock;

  static const rates = ReplayRates.values;
  static const maxPlayRate = ReplayRates.maxPlay;

  /// Drift wider than this is seeked away.
  static const driftSeek = Duration(milliseconds: 1500);

  /// Drift wider than this is closed by a rate nudge, narrower is left alone.
  ///
  /// The dead band is the point. A visible hard seek on every tile every few seconds is far worse
  /// to watch than a few hundred milliseconds of disagreement nobody can see.
  static const driftNudge = Duration(milliseconds: 300);

  /// How far off real time a nudged player runs, as a fraction. Slow enough not to be audible or
  /// visible, fast enough to close [driftSeek] worth of gap in a few seconds.
  static const nudge = 0.05;

  static const driftPeriod = Duration(seconds: 1);

  /// Faster than [driftPeriod] because it is doing a different job: a drift check corrects a
  /// player that is otherwise running, where a step is the playback itself.
  static const stepPeriod = ReplayRates.stepPeriod;

  /// How often the playhead is republished. Smooth enough for the scrubber line, slow enough that
  /// it is not a per-frame rebuild of eight tiles.
  static const tickPeriod = Duration(milliseconds: 100);

  final _sources = <String, ReplaySource>{};
  var _timelines = <String, TimelineWindow>{};

  /// What each camera is called, for its bar on the scrubber.
  var _labels = <String, String>{};

  final _failures = <String, String?>{};

  /// Cameras with an open request in flight, so a drift tick every second cannot stack opens on
  /// top of each other while the first is still resolving.
  final _opening = <String>{};

  final _playhead = ValueNotifier<DateTime?>(null);

  /// The wall-clock instant every tile is trying to show, or null while live.
  ValueListenable<DateTime?> get playhead => _playhead;

  final _playheadSeconds = ValueNotifier<DateTime?>(null);

  /// The same instant as [playhead], truncated to the second.
  ///
  /// The feed reads this rather than [playhead] because it only ever changes what it shows on a
  /// second boundary, and a `ValueNotifier` suppresses a set that does not change the value — so
  /// this notifies once a second where the playhead notifies ten times, and the activity column
  /// is not rebuilt nine times for nothing.
  ValueListenable<DateTime?> get playheadSeconds => _playheadSeconds;

  static DateTime? _toSecond(DateTime? at) => at == null
      ? null
      : DateTime.fromMillisecondsSinceEpoch(
          at.millisecondsSinceEpoch - at.millisecond,
          isUtc: at.isUtc,
        );

  final _failure = ValueNotifier<String?>(null);

  /// Set only when *no* camera could play. One camera with a gap is a plate on one tile, not a
  /// failure of the wall.
  ValueListenable<String?> get failure => _failure;

  /// The instant the playhead was last anchored at, and the real time it was true.
  DateTime? _anchorAt;
  DateTime? _anchorWall;

  bool _playing = false;
  int _rate = 1;

  Timer? _ticker;
  DateTime? _lastDrift;

  bool get replaying => _anchorAt != null;
  bool get playing => _playing;
  int get rate => _rate;

  /// Whether the players are being seeked rather than played. True above [maxPlayRate].
  bool get stepping => _playing && _rate > maxPlayRate;

  ReplaySource? sourceFor(String cameraId) => _sources[cameraId];

  /// Whether this camera has footage on screen at the playhead. False during a gap, which is the
  /// tile's cue to draw a plate rather than a black rectangle.
  bool hasFootage(String cameraId) => _sources[cameraId]?.isOpen ?? false;

  String? failureFor(String cameraId) => _failures[cameraId];

  /// Everything the scrubber above the wall draws.
  ///
  /// Merged in [update] rather than here. The union sorts every camera's coverage and marks
  /// together, and this is read from `build`, from [seekTo] and from [scrubTo] — the last of which
  /// runs once per pointer sample of a drag. Since [_timelines] only changes in [update], doing the
  /// work there costs one merge per repository notification instead of one per read.
  TimelineWindow get timeline => _merged;
  var _merged = TimelineWindow.union(const []);

  /// The same windows unmerged, one per camera, in the order they were handed to [update] — which
  /// is the wall's own order, so a bar sits under the tiles it belongs to.
  ///
  /// Read from here rather than from the repository a second time so the bars and the merge they
  /// snap against cannot be built from two different reads and disagree about the same second.
  ///
  /// Built in [update] alongside [timeline], and for the same reason: the wall's scrubber rebuilds
  /// on every hover and drag sample, and a getter that allocated a lane per camera each time would
  /// hand the widget a new object per frame — which also defeats the scrubber's own memo, since it
  /// compares windows by identity.
  List<TimelineLane> get lanes => _lanes;
  var _lanes = const <TimelineLane>[];

  /// The cameras on the wall, what each has recorded, and what each is called — refreshed as the
  /// screen rebuilds.
  ///
  /// The key set *is* the camera set: a camera that appears here gains a source, one that
  /// disappears loses it. `timelineFor` is synchronous and self-priming, so a screen can call this
  /// every build and the coverage fills in behind.
  void update(
    Map<String, TimelineWindow> timelines, {
    Map<String, String> labels = const {},
  }) {
    _timelines = Map.of(timelines);
    _labels = Map.of(labels);
    _merged = TimelineWindow.union(_timelines.values);
    _lanes = [
      for (final entry in _timelines.entries)
        TimelineLane(
          label: _labels[entry.key] ?? entry.key,
          window: entry.value,
        ),
    ];

    for (final id in timelines.keys) {
      _sources.putIfAbsent(
        id,
        () => ReplaySource(
          repository: repository,
          cameraId: id,
          playerFactory: _playerFactory,
          clock: _clock,
          // Silent, and not as a default a control could undo. Eight cameras is eight overlapping
          // tracks with nothing to say which tile a sound came from; the drift correction runs a
          // lagging tile 5% fast, which is invisible as motion and plainly audible as pitch; and
          // above 4x the rate makes speech unintelligible where it does not drop it outright.
          // Listening is a question you ask of one camera, which is what the single-camera screen
          // is for.
          muted: true,
        ),
      );
    }

    final gone = _sources.keys
        .where((id) => !timelines.containsKey(id))
        .toList();
    for (final id in gone) {
      final source = _sources.remove(id);
      _failures.remove(id);
      unawaited(source?.dispose());
    }

    if (gone.isNotEmpty) notifyListeners();
  }

  /// Go to [at] and play from there, entering replay if the wall is live.
  Future<void> seekTo(DateTime at) async {
    final target = timeline.snap(at);
    if (target == null) {
      _failure.value = 'Nothing was recorded in this window.';
      notifyListeners();
      return;
    }

    final entering = !replaying;
    if (entering) _playing = true;

    _anchor(target);
    _setPlayhead(target);
    _start();
    if (entering) notifyListeners();

    await _openAll(target);
  }

  /// Mid-drag: move the playhead now, open playlists when it settles.
  ///
  /// Unlike the single-camera controller there is no settle timer here. A wall drag is answered by
  /// the drift tick, which is already running at [driftPeriod] and already knows how to open a
  /// window for a camera whose playhead has left its own — so a sweep across the day costs at most
  /// one round of opens per second, and no second mechanism is needed to say so.
  void scrubTo(DateTime at) {
    final target = timeline.snap(at) ?? at;
    _anchor(target);
    _setPlayhead(target);
  }

  /// Both of these anchor *before* the flag flips, and it matters in both directions. [_derived]
  /// reads [_playing] to decide whether time has passed, so anchoring after the flip asks the
  /// wrong question: a resume would count the whole pause as played, and a pause would drop
  /// whatever ran since the last tick.
  void play() {
    if (_playing) return;
    _anchor();
    _playing = true;
    _applyTransport();
    notifyListeners();
  }

  void pause() {
    if (!_playing) return;
    _anchor();
    _playing = false;
    _applyTransport();
    notifyListeners();
  }

  /// Re-anchored rather than applied outright: the elapsed time so far was lived at the old rate,
  /// and rescaling it retroactively would jump the playhead.
  Future<void> setRate(int rate) async {
    if (rate == _rate) return;
    _anchor();
    _rate = rate;
    _applyTransport();
    notifyListeners();
  }

  Future<void> backToLive() async {
    if (!replaying) return;

    _ticker?.cancel();
    _ticker = null;
    _anchorAt = null;
    _anchorWall = null;
    _lastDrift = null;
    _playing = false;
    _rate = 1;
    _setPlayhead(null);
    _failure.value = null;
    _failures.clear();
    _opening.clear();
    notifyListeners();

    await Future.wait([for (final source in _sources.values) source.close()]);
  }

  /// One step of the clock. Public so the drift arithmetic can be tested without waiting.
  void tick() {
    final at = _derived;
    if (at == null) return;

    _setPlayhead(at);

    final now = _clock();
    final period = stepping ? stepPeriod : driftPeriod;
    if (_lastDrift == null || now.difference(_lastDrift!) >= period) {
      _lastDrift = now;
      _correctDrift(at);
    }
  }

  /// Where the playhead is, derived from the anchor rather than accumulated.
  DateTime? get _derived {
    final at = _anchorAt;
    if (at == null) return null;
    if (!_playing) return at;
    return at.add(_clock().difference(_anchorWall!) * _rate);
  }

  void _setPlayhead(DateTime? at) {
    _playhead.value = at;
    _playheadSeconds.value = _toSecond(at);
  }

  void _anchor([DateTime? at]) {
    _anchorAt = at ?? _derived ?? _anchorAt;
    _anchorWall = _clock();
  }

  void _start() {
    _lastDrift = null;
    _ticker ??= Timer.periodic(tickPeriod, (_) => tick());
  }

  /// Brings every open player into line with the transport: the wall's rate while playing, paused
  /// while paused or stepping.
  void _applyTransport() {
    for (final source in _sources.values) {
      if (!source.isOpen) continue;
      if (_playing && !stepping) {
        unawaited(source.setRate(_rate.toDouble()));
        unawaited(source.play());
      } else {
        unawaited(source.pause());
      }
    }
  }

  /// Opens every camera at [at], on one token.
  ///
  /// One `mintStreamToken` for the wall rather than one per camera: eight cameras is eight round
  /// trips before the first frame, for eight copies of the same answer.
  Future<void> _openAll(DateTime at) async {
    final token = await repository.mintStreamToken();

    await Future.wait([
      for (final id in _sources.keys) _openOne(id, at, streamToken: token),
    ]);

    // Only when nobody could play. A wall where one camera has a gap is a wall that is working.
    final anyOpen = _sources.values.any((source) => source.isOpen);
    _failure.value = anyOpen || _sources.isEmpty
        ? null
        : 'There is no recording to play here.';
    notifyListeners();
  }

  Future<void> _openOne(String id, DateTime at, {String? streamToken}) async {
    final source = _sources[id];
    if (source == null || _opening.contains(id)) return;

    // Nothing recorded here for this camera. Closed rather than opened against a range the Server
    // answers with a 404 — the tile draws a plate, and eight cameras in a gap do not become eight
    // failed requests a second.
    if (!(_timelines[id]?.covers(at) ?? false)) {
      if (source.isOpen) {
        await source.close();
        notifyListeners();
      }
      _failures[id] = null;
      return;
    }

    _opening.add(id);
    try {
      final wasOpen = source.isOpen;
      final result = await source.openWindow(
        at,
        replayWindowEnd(at, timeline: _timelines[id], now: _clock()),
        streamToken: streamToken,
      );

      switch (result.outcome) {
        case ReplayOpen.superseded:
          return;
        case ReplayOpen.failed:
          _failures[id] = result.message;
        case ReplayOpen.opened:
          _failures[id] = null;
          await source.setRate(_rate.toDouble());
          if (!_playing || stepping) await source.pause();
      }

      if (!wasOpen) notifyListeners();
    } finally {
      _opening.remove(id);
    }
  }

  /// Pulls every tile back towards the master clock.
  ///
  /// Three outcomes per camera, and which one applies is the whole design: outside its open
  /// window, open the next one; far out of step, seek; slightly out of step, nudge the rate and
  /// let it catch up over the next second or two.
  void _correctDrift(DateTime at) {
    for (final entry in _sources.entries) {
      final source = entry.value;

      // Either it never opened, or the playhead has run past the end of the window it had. Both
      // want the same thing, and `covers` already folds in the reopen guard.
      if (!source.isOpen || !source.covers(at)) {
        // Unless the playhead is still inside that window and the next one would end where it
        // already does. That is the tile playing out its own window rather than the wall being
        // dragged somewhere else, and at the live edge — where the end is capped at `now` — a
        // drift tick every second would reset every tile's decoder for the second of footage that
        // had appeared since, which is eight pictures blinking in step.
        final next = replayWindowEnd(
          at,
          timeline: _timelines[entry.key],
          now: _clock(),
        );
        if (!source.holds(at) || source.worthReopening(next)) {
          unawaited(_openOne(entry.key, at));
        }

        continue;
      }

      final actual = source.instant;
      if (actual == null) continue;

      // Stepping does not converge — the players are parked, so every tick is a fresh seek.
      if (stepping) {
        unawaited(source.seekWithin(source.offsetOf(at)));
        continue;
      }

      final drift = at.difference(actual);
      final magnitude = drift.abs();

      if (magnitude > driftSeek) {
        unawaited(source.seekWithin(source.offsetOf(at)));
        unawaited(source.setRate(_rate.toDouble()));
      } else if (magnitude > driftNudge) {
        // Positive drift means the tile is behind the wall, so it runs fast until it is not.
        unawaited(
          source.setRate(_rate * (drift.isNegative ? 1 - nudge : 1 + nudge)),
        );
      } else if (source.rate != _rate) {
        unawaited(source.setRate(_rate.toDouble()));
      }
    }
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _ticker = null;
    for (final source in _sources.values) {
      unawaited(source.dispose());
    }
    _sources.clear();
    _playhead.dispose();
    _playheadSeconds.dispose();
    _failure.dispose();
    super.dispose();
  }
}
