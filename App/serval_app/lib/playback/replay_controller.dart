import 'dart:async';

import 'package:flutter/foundation.dart';

import '../data/serval_repository.dart';
import '../models/timeline.dart';
import 'replay_source.dart';
import 'vod_player.dart';

/// Live or replaying, and if replaying, from when.
///
/// Owned by the single-camera screen. It notifies on the things that change the *shape* of the
/// screen — entering or leaving replay, a failure — while the position rides [playhead], because
/// that ticks about four times a second and routing it through `setState` would relayout the
/// transcript panel every 250 ms.
///
/// One [ReplaySource] holds the window and the arithmetic over it. What lives here is the policy
/// that a single camera wants and a wall of them would want differently: how close to now is close
/// enough to hand back to the live view, what a drag means before it settles, and where a tap in a
/// hole should land.
class ReplayController extends ChangeNotifier {
  ReplayController({
    required ServalRepository repository,
    required String cameraId,
    VodPlayer Function()? playerFactory,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now,
       _source = ReplaySource(
         repository: repository,
         cameraId: cameraId,
         playerFactory: playerFactory,
         clock: clock,
       ) {
    _source.position.addListener(_onPosition);
  }

  final ReplaySource _source;

  /// Injectable for the same reason the wall's is: how far a reopen falls behind is measured in
  /// real time, and a test that had to spend it would be measuring its own machine.
  final DateTime Function() _clock;

  /// The bounded window is the whole idea here. A VOD playlist for a 24-hour range is ~21,600
  /// entries, which is slow to fetch and slow to parse before a single frame appears; a playlist
  /// for fifteen minutes is ~225, which is instant. So a seek opens a short window around the
  /// instant asked for, and every drag inside it is a decoder seek with no network at all.
  static const window = ReplaySource.window;
  static const reopenGuard = ReplaySource.reopenGuard;

  /// Seek this close to now and there is nothing to replay — go back to the live view instead,
  /// which is both cheaper and what the person meant.
  static const liveEdge = Duration(seconds: 30);

  /// How still a drag has to go before it is treated as asking to see the frame.
  ///
  /// A sweep across the day never idles this long, so it still costs nothing; a drag that stops to
  /// look pays for one window. Long enough that the natural jitter of holding a mouse still does
  /// not fire it twice, short enough to read as the picture following you rather than lagging.
  static const scrubSettle = Duration(milliseconds: 250);

  static const rates = ReplayRates.values;
  static const maxPlayRate = ReplayRates.maxPlay;
  static const stepPeriod = ReplayRates.stepPeriod;

  /// How often the playhead is republished while crossing a break in the recording. There is no
  /// player to take a position from, so this is the whole clock — smooth enough for the scrubber
  /// line, slow enough not to be a rebuild per frame.
  static const gapTick = Duration(milliseconds: 100);

  VodPlayer? get player => _source.player;

  /// Replaying includes sitting in a break in the recording. There is no player then and nothing
  /// to show, but the playhead is somewhere in the past and the transport still governs it —
  /// falling back to the live view because the recorder was off for a minute would be wrong.
  bool get replaying => _source.isOpen || _inGap;

  /// Crossing a stretch with no recording, playing it out at real time.
  ///
  /// Replay is a claim about wall time, so a break in the recording takes as long to cross as it
  /// took to happen. Stitching the footage either side of it together would put two instants
  /// minutes apart in consecutive frames and call it continuous — and there is no honest playhead
  /// for that. What is on screen instead is the plate saying so, and the scrubber running.
  bool get inGap => _inGap;
  bool _inGap = false;

  /// Where the footage picks up again, or null when nothing follows this break.
  DateTime? _gapUntil;

  /// The playhead's anchor while crossing a break: an instant, the real time it was true, and the
  /// rate. Derived rather than accumulated, for the reason the wall derives its own — a tick that
  /// arrives late, or not at all because the window was in the background, changes nothing.
  DateTime? _gapAnchorAt;
  DateTime? _gapAnchorWall;
  Timer? _gapTimer;

  /// The coverage this controller last saw, which is what says where the breaks are.
  ///
  /// Held rather than passed in because the moment it is needed — playback arriving at the end of
  /// a run — has no gesture behind it to carry one.
  TimelineWindow? _timeline;

  final _playhead = ValueNotifier<DateTime?>(null);

  /// The wall-clock instant on screen, or null while live.
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
  ValueListenable<String?> get failure => _failure;

  /// The pending settle: where the drag has come to rest, and the timer that will open it.
  Timer? _settleTimer;
  DateTime? _settleTarget;

  bool _playing = false;
  int _rate = 1;

  /// A reopen driven by playback is in flight. The wall keeps the same guard per camera, for the
  /// same reason: an open takes two round trips, and the clock that notices it is needed keeps
  /// ticking while it runs.
  bool _reopening = false;

  /// True once replay has started. Meaningless while live, where the picture is the live view's.
  bool get playing => replaying && _playing;
  int get rate => _rate;

  /// Whether the picture is being seeked rather than played, above [maxPlayRate].
  ///
  /// Unlike the wall this needs no clock of its own. The playhead here still comes from the
  /// player's own position, and a step is a seek — so the position moves because the picture did,
  /// which is the same causal order playing has.
  bool get stepping => playing && _rate > maxPlayRate;

  Timer? _stepTimer;

  void _setPlayhead(DateTime? at) {
    _playhead.value = at;
    _playheadSeconds.value = _toSecond(at);
  }

  /// Cancels a settle that has not fired. Anything that decides where playback goes next says
  /// this first, so a timer armed by an earlier pointer sample cannot land on top of it.
  void _cancelSettle() {
    _settleTimer?.cancel();
    _settleTimer = null;
    _settleTarget = null;
  }

  /// Go and play from [at].
  ///
  /// Three outcomes, cheapest first: near the live edge, hand back to the live view; inside the
  /// open playlist, one decoder seek and no request; otherwise a new window.
  Future<void> seekTo(DateTime at, TimelineWindow timeline) async {
    _cancelSettle();
    _timeline = timeline;
    _closeGap();

    // A tap in a hole snaps to the nearest footage. `/vod.m3u8` answers an empty range with a 404,
    // and "nothing was recorded then" must not read on screen as "playback is broken". This is
    // also how a break is skipped rather than sat through: clicking past it lands on the footage
    // the far side, which is the whole reason the break is drawn on the scrubber.
    final target = timeline.snap(at);
    if (target == null) {
      _failure.value = 'Nothing was recorded in this window.';
      notifyListeners();
      return;
    }

    if (_clock().difference(target) < liveEdge) {
      await backToLive();
      return;
    }

    if (_source.covers(target)) {
      _setPlayhead(target);
      await _source.seekWithin(_source.offsetOf(target));
      return;
    }

    await _openWindow(target, timeline);
  }

  /// Mid-drag.
  ///
  /// Three tiers, cheapest first, and the middle one is the whole trick. Inside the open playlist
  /// a drag is a decoder seek and the picture tracks the cursor. Outside it, a new playlist is
  /// wanted — but a drag fires this per pointer sample, and opening a window per sample would put
  /// dozens of requests in flight for one gesture. So the open waits for the drag to hold still:
  /// a sweep across the day still costs nothing, and a pause to look repaints the stage.
  ///
  /// The near-live rule [seekTo] applies is deliberately absent. Dragging *past* now on the way
  /// somewhere else must not yank the screen back to the live view before the finger is up.
  Future<void> scrubTo(DateTime at, TimelineWindow timeline) async {
    _timeline = timeline;
    _closeGap();

    // Snapped for the same reason a tap is, and one step earlier: the line should not come to rest
    // in a hole and then jump out from under the cursor on release.
    final target = timeline.snap(at) ?? at;
    _setPlayhead(target);

    if (_source.covers(target)) {
      _cancelSettle();
      await _source.seekWithin(_source.offsetOf(target));
      return;
    }

    _settleTarget = target;
    _settleTimer?.cancel();
    _settleTimer = Timer(scrubSettle, () {
      final settled = _settleTarget;
      _settleTimer = null;
      _settleTarget = null;
      if (settled != null) unawaited(_openWindow(settled, timeline));
    });
  }

  Future<void> backToLive() async {
    _cancelSettle();
    if (!replaying) return;
    _closeGap();

    _stepTimer?.cancel();
    _stepTimer = null;
    _playing = false;
    _rate = 1;

    _setPlayhead(null);
    _failure.value = null;
    notifyListeners();

    await _source.close();
  }

  /// All three re-anchor the break's clock *before* changing what it is measuring, the same order
  /// the wall anchors in and for the same reason: the time already crossed was crossed at the old
  /// rate and the old play state, and rescaling it retroactively would jump the playhead.
  void play() {
    if (_playing) return;
    _anchorGap();
    _playing = true;
    _applyTransport();
    notifyListeners();
  }

  void pause() {
    if (!_playing) return;
    _anchorGap();
    _playing = false;
    _applyTransport();
    notifyListeners();
  }

  Future<void> setRate(int rate) async {
    if (rate == _rate) return;
    _anchorGap();
    _rate = rate;
    _applyTransport();
    notifyListeners();
  }

  /// Brings the player into line with the transport: the chosen rate while playing, parked while
  /// paused or stepping.
  void _applyTransport() {
    _stepTimer?.cancel();
    _stepTimer = null;

    if (!_source.isOpen) return;

    if (playing && !stepping) {
      unawaited(_source.setRate(_rate.toDouble()));
      unawaited(_source.play());
      return;
    }

    unawaited(_source.pause());
    if (stepping) {
      _stepTimer = Timer.periodic(stepPeriod, (_) => _step());
    }
  }

  /// One step of a parked player: forward by as much wall time as the rate says has passed.
  void _step() {
    final at = _source.instant;
    if (at == null) return;

    final next = at.add(stepPeriod * _rate);
    if (_source.covers(next)) {
      unawaited(_source.seekWithin(_source.offsetOf(next)));
      return;
    }

    // Stepped off the end of the open window. Reopening is the same move playing into the next
    // one makes, and passing no timeline says the same thing: arriving here is proof the footage
    // runs on.
    unawaited(_reopen(next));
  }

  /// Playback has run into the guard at the end of the open window.
  ///
  /// [_openWindow] with the two guards a seek must not have and this must. Nothing is reopened
  /// while an open is still in flight: the position ticks four times a second, and every tick
  /// during a reopen would stack another playlist fetch and another decoder reset behind it. And
  /// nothing is reopened for a window that ends where this one already does — near the live edge
  /// the end is capped at `now`, so the reopen buys the quarter of a second that has passed since
  /// the last tick and lands right back in the guard, which is a picture that blinks forever.
  ///
  /// A playhead that has left the window entirely is a seek, not playback, and neither guard
  /// applies to it.
  ///
  /// The catch-up at the end is what keeps replay's clock going forwards. A reopen is decided at
  /// the instant playback has reached and lands a token, a playlist and a first segment later, so
  /// opening at that instant resumes a second or so behind where the picture already was — the
  /// recording's own burned-in clock runs 53, 54, and then 53 again. Real time passed and replay
  /// is a claim about wall time, so the seek gives that time back.
  Future<void> _reopen(DateTime at) async {
    if (_reopening) return;
    if (_source.holds(at) &&
        !_source.worthReopening(replayWindowEnd(at, now: _clock()))) {
      return;
    }

    _reopening = true;
    final decidedAt = _clock();
    try {
      await _openWindow(at, null, followingPlayback: true);

      // Only for the window this reopen actually committed, and only while playing: a newer open
      // has its own idea of where the playhead is, and a parked player is not losing time.
      if (!_playing || stepping || _source.windowFrom != at) return;

      final resume = at.add(_clock().difference(decidedAt) * _rate);
      if (_source.covers(resume)) {
        await _source.seekWithin(_source.offsetOf(resume));
      }
    } finally {
      _reopening = false;
    }
  }

  Future<void> setMuted(bool muted) => _source.setMuted(muted);

  Future<void> setVolume(double volume) => _source.setVolume(volume);

  /// The camera's playback gain and its gate. See `VodPlayer.setGain` for why the two travel together.
  Future<void> setGain(double db, double? gateRms) =>
      _source.setGain(db, gateRms);

  /// Opens a playlist window around [at].
  ///
  /// [timeline] is what the coverage clip reads; a reopen driven by playback passes none,
  /// because playing *into* the next window is itself proof the footage runs on.
  Future<void> _openWindow(
    DateTime at,
    TimelineWindow? timeline, {
    bool followingPlayback = false,
  }) async {
    final result = await _source.openWindow(
      at,
      replayWindowEnd(at, timeline: timeline, now: _clock()),
      followingPlayback: followingPlayback,
      onWindowSet: (playerCreated) {
        // A window opens playing, which is what `open` does. Anything else is the transport's
        // business, and `_applyTransport` below is what enforces it.
        if (playerCreated) _playing = true;
        _setPlayhead(at);
        _failure.value = null;
        if (playerCreated) notifyListeners();
      },
    );

    switch (result.outcome) {
      // A newer open (or a camera switch) beat this one to it, and has already said what it
      // wanted said.
      case ReplayOpen.superseded:
        return;
      case ReplayOpen.opened:
        _applyTransport();
        return;
      // Stay in replay and say so, rather than snapping back to live — a click that silently
      // returned you to the live view would read as a click that did nothing.
      case ReplayOpen.failed:
        _failure.value = result.message;
        notifyListeners();
    }
  }

  void _onPosition() {
    final at = _source.instant;
    if (at == null) return;

    _setPlayhead(at);

    // Playing towards a break in the recording rather than towards the edge of a playlist. The
    // window was clipped at the end of this run of footage, so there is no next window to reopen
    // into — the next thing that happened here is nothing, and replay says so by showing it.
    if (_gapAt(_source.windowTo) case final gap?) {
      // Not at the reopen guard, which fires thirty seconds early: there is real footage left to
      // play and it must play. Only once the picture has actually reached the end.
      if (!at.isBefore(gap.subtract(const Duration(milliseconds: 500)))) {
        unawaited(_openGap(gap));
      }

      return;
    }

    if (_source.needsReopen) unawaited(_reopen(at));
  }

  /// Hands the screen over to the break starting at [from].
  ///
  /// The player goes, because there is nothing for it to play and a decoder parked on the last
  /// frame of the previous run would leave that frame on screen for the length of the outage —
  /// the one reading most likely to be believed and most likely to be wrong.
  Future<void> _openGap(DateTime from) async {
    if (_inGap) return;

    _inGap = true;
    _gapUntil = _timeline?.nextRunAfter(from);
    _gapAnchorAt = from;
    _gapAnchorWall = _clock();

    _stepTimer?.cancel();
    _stepTimer = null;
    _setPlayhead(from);
    notifyListeners();

    await _source.close();
    if (!_inGap) return;

    _gapTimer ??= Timer.periodic(gapTick, (_) => _tickGap());
  }

  /// Leaves the break, however it is being left: arriving at the next run, a seek, or going live.
  void _closeGap() {
    _gapTimer?.cancel();
    _gapTimer = null;
    _inGap = false;
    _gapUntil = null;
    _gapAnchorAt = null;
    _gapAnchorWall = null;
  }

  /// One tick of the clock while there is no footage. The playhead is derived from the anchor, so
  /// a late tick or a missed one costs nothing.
  void _tickGap() {
    final anchor = _gapAnchorAt;
    final wall = _gapAnchorWall;
    if (anchor == null || wall == null) return;

    final at = _playing
        ? anchor.add(_clock().difference(wall) * _rate)
        : anchor;
    _setPlayhead(at);

    // Nothing was ever recorded after this break, so the playhead is running at the live edge.
    // Handing back to the live view is both what is left to show and what was meant.
    final until = _gapUntil;
    if (until == null) {
      if (_clock().difference(at) < liveEdge) unawaited(backToLive());
      return;
    }

    if (at.isBefore(until)) return;

    // Out the far side. The footage resumes exactly here, so this is a window opened at its own
    // first instant rather than a reopen catching up to one.
    _closeGap();
    notifyListeners();
    unawaited(_openWindow(until, _timeline));
  }

  /// Re-anchors the break's clock, for the same reason the wall re-anchors its own: the time
  /// already crossed was crossed at the old rate, and rescaling it would jump the playhead.
  void _anchorGap() {
    if (!_inGap) return;
    _gapAnchorAt = _playhead.value ?? _gapAnchorAt;
    _gapAnchorWall = _clock();
  }

  /// Where the open window runs into a break in the recording, or null when footage continues.
  ///
  /// [replayWindowEnd] clips a window to the end of the run it starts in, so a window ending
  /// anywhere the timeline has no footage immediately after is a window ending at a break. A
  /// window cut short by the fifteen-minute cap instead ends mid-footage and reopens normally.
  DateTime? _gapAt(DateTime? windowTo) {
    final timeline = _timeline;
    if (windowTo == null || timeline == null) return null;

    // A span's end is inclusive, so coverage has to be asked about the instant *after* it.
    return timeline.covers(windowTo.add(const Duration(seconds: 1)))
        ? null
        : windowTo;
  }

  @override
  void dispose() {
    _cancelSettle();
    _closeGap();
    _stepTimer?.cancel();
    _source.position.removeListener(_onPosition);
    unawaited(_source.dispose());
    _playhead.dispose();
    _playheadSeconds.dispose();
    _failure.dispose();
    super.dispose();
  }
}
