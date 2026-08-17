import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/playback/replay_source.dart';
import 'package:serval_app/playback/vod_player.dart';
import 'package:serval_app/playback/wall_replay_controller.dart';

/// The master clock: what time the wall thinks it is, and how eight players that each disagree
/// with it get pulled back into line.
///
/// The clock is injected and the players are fakes, so none of this waits in real time and none of
/// it needs a decoder — which is the point of both seams.
void main() {
  late DateTime now;
  DateTime clock() => now;

  /// Two cameras. `back` has recorded continuously; `front` has an hour-long hole in the middle,
  /// which is what makes it worth having two.
  late DateTime holeFrom;
  late DateTime holeTo;
  late Map<String, TimelineWindow> timelines;

  /// Well inside footage on both cameras, and far enough from the edges that a window fits.
  late DateTime at;

  late _WallRepository repository;
  late WallReplayController wall;

  /// Every player handed out, in creation order, and which camera asked for it.
  late List<_FakePlayer> players;

  _FakePlayer playerFor(String cameraId) =>
      wall.sourceFor(cameraId)!.player! as _FakePlayer;

  setUp(() {
    now = DateTime(2026, 8, 5, 12);
    final from = now.subtract(const Duration(hours: 12));
    holeFrom = now.subtract(const Duration(hours: 4));
    holeTo = now.subtract(const Duration(hours: 3));
    at = now.subtract(const Duration(hours: 6));

    timelines = {
      'back': TimelineWindow(
        from: from,
        to: now,
        coverage: [
          CoverageSpan(from, now.subtract(const Duration(minutes: 5))),
        ],
      ),
      'front': TimelineWindow(
        from: from,
        to: now,
        coverage: [
          CoverageSpan(from, holeFrom),
          CoverageSpan(holeTo, now.subtract(const Duration(minutes: 5))),
        ],
      ),
    };

    players = [];
    repository = _WallRepository();
    wall = WallReplayController(
      repository: repository,
      clock: clock,
      playerFactory: () {
        final player = _FakePlayer();
        players.add(player);
        return player;
      },
    );
    wall.update(timelines);
  });

  tearDown(() => wall.dispose());

  group('entering replay', () {
    test('opens every camera at the same instant', () async {
      await wall.seekTo(at);

      expect(wall.replaying, isTrue);
      expect(wall.playhead.value, at);

      for (final id in ['back', 'front']) {
        final opens = playerFor(id).opens;
        expect(opens, hasLength(1), reason: id);
        expect(opens.single.at, at, reason: id);
      }
    });

    test('mints one stream token for the whole wall', () async {
      // Eight cameras is otherwise eight round trips before the first frame, for eight copies of
      // the same answer.
      await wall.seekTo(at);

      expect(repository.tokensMinted, 1);
      for (final id in ['back', 'front']) {
        expect(
          playerFor(id).opens.single.playlist.queryParameters['stream_token'],
          'token-1',
          reason: id,
        );
      }
    });

    test('starts playing', () async {
      await wall.seekTo(at);

      expect(wall.playing, isTrue);
      expect(wall.rate, 1);
    });

    test('a camera with no footage there is not opened at all', () async {
      // The Server answers an empty range with a 404. Eight cameras inside a gap must not become
      // eight failed requests every second.
      await wall.seekTo(holeFrom.add(const Duration(minutes: 30)));

      expect(wall.hasFootage('back'), isTrue);
      expect(wall.hasFootage('front'), isFalse);
      expect(wall.sourceFor('front')!.player, isNull);
    });

    test('a gap on one camera is not a failure of the wall', () async {
      await wall.seekTo(holeFrom.add(const Duration(minutes: 30)));

      expect(wall.failure.value, isNull);
      expect(wall.failureFor('front'), isNull);
    });

    test('snapping reads the union, not any one camera', () async {
      // Dead centre of `front`'s hole, which `back` recorded straight through. The wall must stay
      // where it was asked to go rather than snapping out to the far side of a hole that only one
      // of its cameras had.
      final inside = holeFrom.add(const Duration(minutes: 30));
      await wall.seekTo(inside);

      expect(wall.playhead.value, inside);
    });

    test('a window is clipped to the run of footage it starts in', () async {
      // Ten minutes before `front`'s hole, with a fifteen-minute window: its playlist must stop at
      // the hole, while `back` — which recorded through it — gets the full fifteen.
      final before = holeFrom.subtract(const Duration(minutes: 10));
      await wall.seekTo(before);

      final front = playerFor('front').opens.single.playlist.queryParameters;
      expect(DateTime.parse(front['to']!).toLocal(), holeFrom);

      final back = playerFor('back').opens.single.playlist.queryParameters;
      expect(
        DateTime.parse(back['to']!).toLocal(),
        before.add(ReplaySource.window),
      );
    });
  });

  group('the playhead', () {
    test('is derived from the clock and the rate', () async {
      await wall.seekTo(at);

      now = now.add(const Duration(seconds: 10));
      wall.tick();
      expect(wall.playhead.value, at.add(const Duration(seconds: 10)));

      await wall.setRate(4);
      now = now.add(const Duration(seconds: 10));
      wall.tick();

      // Ten seconds at 1x, then ten more at 4x. Re-anchoring on the rate change is what keeps the
      // first ten from being rescaled retroactively.
      expect(wall.playhead.value, at.add(const Duration(seconds: 10 + 40)));
    });

    test('does not move while paused', () async {
      await wall.seekTo(at);
      now = now.add(const Duration(seconds: 5));
      wall.tick();

      wall.pause();
      final stopped = wall.playhead.value;

      now = now.add(const Duration(minutes: 3));
      wall.tick();

      expect(wall.playhead.value, stopped);
      expect(stopped, at.add(const Duration(seconds: 5)));
    });

    test('resumes from where it was paused', () async {
      await wall.seekTo(at);
      wall.pause();
      now = now.add(const Duration(minutes: 3));
      wall.play();

      now = now.add(const Duration(seconds: 7));
      wall.tick();

      expect(wall.playhead.value, at.add(const Duration(seconds: 7)));
    });

    test('survives a tick that never arrived', () async {
      // Derived rather than accumulated, so a backgrounded tab that stops ticking for a minute
      // comes back knowing a minute went by — where a counter advanced per tick would have
      // silently lost it.
      await wall.seekTo(at);

      now = now.add(const Duration(minutes: 1));
      wall.tick();

      expect(wall.playhead.value, at.add(const Duration(minutes: 1)));
    });
  });

  group('drift correction', () {
    /// Wall time played since [at]. Tracked here rather than read off the controller because the
    /// point of these tests is to place a player at a known distance from the playhead, and
    /// deriving that from the thing under test would let both drift together unnoticed.
    var played = Duration.zero;

    /// Enters replay and puts both players exactly on the playhead, so each test only has to
    /// introduce the drift it is about.
    Future<void> settle() async {
      await wall.seekTo(at);
      played = Duration.zero;
      for (final id in ['back', 'front']) {
        playerFor(id)
          ..rates.clear()
          ..seeks.clear();
      }
    }

    /// Moves the clock on far enough for the next drift check to run, and places every player
    /// exactly [behind] where the wall will be. Absolute rather than relative: drift accumulates,
    /// so nudging each player by a delta would leave the previous step's gap still in it.
    void advance(Duration by, {required Duration behind}) {
      now = now.add(by);
      played += by * wall.rate;
      for (final id in ['back', 'front']) {
        playerFor(id).place(played - behind);
      }
      wall.tick();
    }

    test('leaves a tile inside the dead band alone', () async {
      await settle();
      advance(
        const Duration(seconds: 2),
        behind: const Duration(milliseconds: 200),
      );

      // A visible hard seek on every tile every second is far worse to watch than 200 ms of
      // disagreement nobody can see.
      expect(playerFor('back').seeks, isEmpty);
      expect(playerFor('back').rates, isEmpty);
    });

    test('nudges the rate of a tile that has fallen slightly behind', () async {
      await settle();
      advance(
        const Duration(seconds: 2),
        behind: const Duration(milliseconds: 800),
      );

      expect(playerFor('back').seeks, isEmpty);
      expect(playerFor('back').rates.last, closeTo(1.05, 1e-9));
    });

    test('slows a tile that has run slightly ahead', () async {
      await settle();
      advance(
        const Duration(seconds: 2),
        behind: const Duration(milliseconds: -800),
      );

      expect(playerFor('back').rates.last, closeTo(0.95, 1e-9));
    });

    test('nudges around the wall rate, not around 1x', () async {
      await settle();
      await wall.setRate(2);
      playerFor('back').rates.clear();

      advance(
        const Duration(seconds: 2),
        behind: const Duration(milliseconds: 800),
      );

      expect(playerFor('back').rates.last, closeTo(2 * 1.05, 1e-9));
    });

    test('seeks a tile that is far out of step', () async {
      await settle();
      advance(const Duration(seconds: 5), behind: const Duration(seconds: 3));

      expect(playerFor('back').seeks, hasLength(1));
      expect(playerFor('back').rates.last, 1.0);
    });

    test('restores the wall rate once a nudged tile has caught up', () async {
      await settle();
      advance(
        const Duration(seconds: 2),
        behind: const Duration(milliseconds: 800),
      );
      expect(playerFor('back').rates.last, closeTo(1.05, 1e-9));

      advance(const Duration(seconds: 2), behind: Duration.zero);

      expect(playerFor('back').rates.last, 1.0);
    });

    test('checks no more often than once a second', () async {
      await settle();
      advance(const Duration(seconds: 2), behind: Duration.zero);
      playerFor('back')
        ..rates.clear()
        ..seeks.clear();

      // Far enough out of step to be seeked, but only 300 ms since the last check.
      now = now.add(const Duration(milliseconds: 300));
      playerFor('back').place(Duration.zero);
      wall.tick();
      wall.tick();

      expect(playerFor('back').seeks, isEmpty);
      expect(playerFor('back').rates, isEmpty);
    });
  });

  group('stepping above the play rate', () {
    test('parks the players and seeks them instead', () async {
      // hls.js cannot keep a buffer at 8x, and eight tiles failing to is eight stalls.
      await wall.seekTo(at);
      await wall.setRate(8);

      expect(wall.stepping, isTrue);
      expect(playerFor('back').paused, isTrue);

      now = now.add(const Duration(seconds: 2));
      wall.tick();

      expect(playerFor('back').seeks, isNotEmpty);
    });

    test('steps faster than it drift-checks', () async {
      // The step *is* the playback here, so its period is the frame rate you see. At the drift
      // period an 8x skim advanced once a second, which reads as a stall between jumps.
      await wall.seekTo(at);
      await wall.setRate(8);
      playerFor('back').seeks.clear();

      now = now.add(WallReplayController.stepPeriod);
      wall.tick();

      expect(playerFor('back').seeks, hasLength(1));
      expect(
        WallReplayController.stepPeriod,
        lessThan(WallReplayController.driftPeriod),
      );
    });

    test('goes back to playing when the rate comes down', () async {
      await wall.seekTo(at);
      await wall.setRate(8);
      await wall.setRate(2);

      expect(wall.stepping, isFalse);
      expect(playerFor('back').paused, isFalse);
      expect(playerFor('back').rates.last, 2.0);
    });
  });

  group('gaps and reopens', () {
    test('opens a camera that comes back into coverage', () async {
      // Started inside `front`'s hole, so only `back` is playing. Playing on past the far edge of
      // the hole must bring `front` back without a gesture.
      await wall.seekTo(holeTo.subtract(const Duration(minutes: 2)));
      expect(wall.hasFootage('front'), isFalse);

      now = now.add(const Duration(minutes: 3));
      wall.tick();
      await pumpEventQueue();

      expect(wall.hasFootage('front'), isTrue);
      expect(playerFor('front').opens.single.at, isNot(before(holeTo)));
    });

    test('closes a camera that plays into a hole', () async {
      await wall.seekTo(holeFrom.subtract(const Duration(minutes: 2)));
      expect(wall.hasFootage('front'), isTrue);

      now = now.add(const Duration(minutes: 3));
      wall.tick();
      await pumpEventQueue();

      expect(wall.hasFootage('front'), isFalse);
      expect(wall.hasFootage('back'), isTrue);
    });

    test('reopens a camera that plays to the end of its window', () async {
      await wall.seekTo(at);

      // Past the reopen guard: an HLS VOD playlist cannot be appended to, so running off the end
      // would stall rather than continue.
      final on = ReplaySource.window - const Duration(seconds: 20);
      now = now.add(on);
      for (final id in ['back', 'front']) {
        playerFor(id).advance(on);
      }
      wall.tick();
      await pumpEventQueue();

      expect(playerFor('back').opens, hasLength(2));
      expect(playerFor('back').opens.last.at, at.add(on));
    });

    test('does not reopen a window that would end where this one does', () async {
      // `back` has recorded up to five minutes ago and no further, so every window it can open
      // ends at the same instant. Playing into the guard at that end must leave the tile alone:
      // reopening buys nothing, and a drift check every second would reset the decoder every
      // second — eight tiles blinking in step for as long as the wall is parked near the end of
      // its own footage.
      final end = now.subtract(const Duration(minutes: 5));
      await wall.seekTo(end.subtract(const Duration(minutes: 1)));
      expect(playerFor('back').opens, hasLength(1));

      // Into the last thirty seconds of the window, and short of its end — past that there is no
      // footage at all, which is a different question and a plate on the tile.
      for (final step in [
        const Duration(seconds: 40),
        const Duration(seconds: 10),
        const Duration(seconds: 5),
      ]) {
        now = now.add(step);
        wall.tick();
        await pumpEventQueue();
      }

      expect(playerFor('back').opens, hasLength(1));
    });

    test('does not stack opens while one is in flight', () async {
      await wall.seekTo(at);
      repository.holdOpens = true;

      final on = ReplaySource.window - const Duration(seconds: 20);
      now = now.add(on);
      for (final id in ['back', 'front']) {
        playerFor(id).advance(on);
      }

      // Three drift checks a second apart, with the first open still unresolved.
      for (var i = 0; i < 3; i++) {
        wall.tick();
        now = now.add(const Duration(seconds: 1));
      }
      repository.release();
      await pumpEventQueue();

      expect(playerFor('back').opens, hasLength(2));
    });
  });

  group('audio', () {
    test('every tile is muted', () async {
      // Eight cameras is eight overlapping tracks, and the drift nudge is audible as pitch. The
      // single-camera screen is where listening belongs.
      await wall.seekTo(at);

      for (final id in ['back', 'front']) {
        expect(playerFor(id).mutes, isNotEmpty, reason: id);
        expect(playerFor(id).mutes, everyElement(isTrue), reason: id);
      }
    });

    test('and stays muted when its window reopens', () async {
      // The trap this is really pinning: a window is opened afresh every fifteen minutes, so a
      // mute applied once and not re-applied comes back with sound on.
      await wall.seekTo(at);
      playerFor('back').mutes.clear();

      final on = ReplaySource.window - const Duration(seconds: 20);
      now = now.add(on);
      for (final id in ['back', 'front']) {
        playerFor(id).advance(on);
      }
      wall.tick();
      await pumpEventQueue();

      expect(playerFor('back').opens, hasLength(2));
      expect(playerFor('back').mutes, everyElement(isTrue));
      expect(playerFor('back').mutes, isNotEmpty);
    });
  });

  group('leaving replay', () {
    test('closes every player and clears the playhead', () async {
      await wall.seekTo(at);
      final back = playerFor('back');
      final front = playerFor('front');

      await wall.backToLive();

      expect(wall.replaying, isFalse);
      expect(wall.playhead.value, isNull);
      expect(back.disposed, isTrue);
      expect(front.disposed, isTrue);
    });

    test('a tick after leaving does nothing', () async {
      await wall.seekTo(at);
      await wall.backToLive();

      now = now.add(const Duration(minutes: 5));
      wall.tick();

      expect(wall.playhead.value, isNull);
    });
  });

  group('the camera set', () {
    test('a camera added to the wall gains a source', () {
      wall.update({...timelines, 'side': timelines['back']!});

      expect(wall.sourceFor('side'), isNotNull);
    });

    test('a camera removed from the wall loses its player', () async {
      await wall.seekTo(at);
      final back = playerFor('back');

      wall.update({'front': timelines['front']!});
      await pumpEventQueue();

      expect(wall.sourceFor('back'), isNull);
      expect(back.disposed, isTrue);
    });

    test('a wall with no cameras reports nothing rather than throwing', () {
      wall.update({});

      expect(wall.timeline.coverage, isEmpty);
    });
  });
}

Matcher before(DateTime instant) =>
    predicate<DateTime>((at) => at.isBefore(instant), 'before $instant');

/// The design's own repository, with a URL to play from and a count of the tokens minted.
class _WallRepository extends SampleServalRepository {
  int tokensMinted = 0;

  /// Holds `openWindow` mid-flight, so a test can fire drift checks on top of an open that has not
  /// come back yet.
  bool holdOpens = false;
  final _held = <Completer<void>>[];

  void release() {
    holdOpens = false;
    for (final completer in _held) {
      completer.complete();
    }
    _held.clear();
  }

  @override
  Future<String?> mintStreamToken() async => 'token-${++tokensMinted}';

  @override
  Future<Duration> vodStartOffsetFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    if (holdOpens) {
      final completer = Completer<void>();
      _held.add(completer);
      await completer.future;
    }
    return Duration.zero;
  }

  @override
  Uri vodUrlFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) => Uri.parse('http://serval.test/api/cameras/$cameraId/vod.m3u8').replace(
    queryParameters: {
      'from': from.toUtc().toIso8601String(),
      'to': to.toUtc().toIso8601String(),
    },
  );

  @override
  Future<void> ensureReplayDetections(
    String cameraId,
    DateTime from,
    DateTime to,
  ) async {}
}

class _FakePlayer implements VodPlayer {
  final opens = <({Uri playlist, DateTime windowFrom, DateTime at})>[];
  final seeks = <Duration>[];
  final rates = <double>[];

  bool paused = false;
  bool disposed = false;

  /// Every mute ever applied, in order. A list because the question is whether it was re-applied
  /// after a window reopen, not just what it ended at.
  final mutes = <bool>[];

  final _position = ValueNotifier<Duration>(Duration.zero);
  final _duration = ValueNotifier<Duration?>(null);
  final _playing = ValueNotifier<bool>(false);
  final _videoSize = ValueNotifier<Size?>(null);
  final _failure = ValueNotifier<String?>(null);

  @override
  ValueListenable<Duration?> get duration => _duration;

  /// Never called here — a saved clip is opened by the clips screen, which owns its own player.
  @override
  Future<void> openFile(Uri file) async => _position.value = Duration.zero;

  /// Moves the picture on, the way a decoder would between two drift checks.
  void advance(Duration by) => _position.value = _position.value + by;

  /// Puts the picture at a known offset. Not [seekWithin]: that is what the controller does, and a
  /// test arranging its starting position must not show up in the list of seeks it then asserts on.
  void place(Duration offset) => _position.value = offset;

  @override
  Future<void> open(
    Uri playlist, {
    required DateTime windowFrom,
    required DateTime at,
  }) async {
    opens.add((playlist: playlist, windowFrom: windowFrom, at: at));
    _position.value = at.difference(windowFrom);
    paused = false;
  }

  @override
  Future<void> play() async {
    paused = false;
    _playing.value = true;
  }

  @override
  Future<void> pause() async {
    paused = true;
    _playing.value = false;
  }

  @override
  Future<void> seekWithin(Duration offset) async {
    seeks.add(offset);
    _position.value = offset;
  }

  @override
  Future<void> setRate(double value) async => rates.add(value);

  @override
  Future<void> setMuted(bool value) async => mutes.add(value);

  @override
  Future<void> setVolume(double value) async {}

  @override
  Future<void> setGain(double db, double? gateRms) async {}

  @override
  ValueListenable<Duration> get position => _position;

  @override
  ValueListenable<bool> get playing => _playing;

  @override
  ValueListenable<Size?> get videoSize => _videoSize;

  @override
  ValueListenable<String?> get failure => _failure;

  @override
  Widget buildView() => const SizedBox.shrink();

  @override
  Future<void> dispose() async => disposed = true;
}
