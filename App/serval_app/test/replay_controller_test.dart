import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/playback/replay_controller.dart';
import 'package:serval_app/playback/vod_player.dart';

/// The window arithmetic: which playlist gets opened for which instant, and — the part that
/// actually matters for a scrubber that feels quick — when a gesture costs a request and when it
/// does not.
///
/// No decoder here. [_FakePlayer] stands in for libmpv so this runs anywhere, which is the point
/// of the factory seam on [ReplayController].
void main() {
  // Anchored on the real clock, not a fixed date: the controller caps every window at
  // `DateTime.now()` and hands anything near it back to the live view, so a window in the future
  // would exercise none of the arithmetic under test.
  final now = DateTime.now();
  final from = now.subtract(const Duration(hours: 12));

  /// Footage for eight hours, then an hour-long hole, then footage up to ten minutes ago.
  final holeFrom = now.subtract(const Duration(hours: 4));
  final holeTo = now.subtract(const Duration(hours: 3));
  final window = TimelineWindow(
    from: from,
    to: now,
    coverage: [
      CoverageSpan(from, holeFrom),
      CoverageSpan(holeTo, now.subtract(const Duration(minutes: 10))),
    ],
  );

  /// Well inside the first run, and far enough from its end that a fifteen-minute window fits.
  final at = now.subtract(const Duration(hours: 5));

  late _FakePlayer player;
  late _PlayableRepository repository;
  late ReplayController replay;

  setUp(() {
    player = _FakePlayer();
    repository = _PlayableRepository();
    replay = ReplayController(
      repository: repository,
      cameraId: 'cam1',
      playerFactory: () => player,
    );
  });

  tearDown(() => replay.dispose());

  test(
    'a seek opens a bounded window starting at the instant asked for',
    () async {
      await replay.seekTo(at, window);

      expect(replay.replaying, isTrue);
      expect(player.opens, hasLength(1));
      expect(player.opens.single.at, at);
      expect(player.opens.single.windowFrom, at);

      // The playlist covers fifteen minutes — ~225 segments — not the twelve hours on screen.
      final query = player.opens.single.playlist.queryParameters;
      expect(DateTime.parse(query['from']!).toLocal(), at);
      expect(
        DateTime.parse(query['to']!).toLocal().difference(at),
        ReplayController.window,
      );
    },
  );

  test('a seek inside the open window costs no new playlist', () async {
    await replay.seekTo(at, window);
    await replay.seekTo(at.add(const Duration(minutes: 5)), window);

    expect(
      player.opens,
      hasLength(1),
      reason: 'a five-minute nudge should be a decoder seek',
    );
    expect(player.seeks, [const Duration(minutes: 5)]);
  });

  test('a seek past the open window opens a new one', () async {
    await replay.seekTo(at, window);
    await replay.seekTo(at.add(const Duration(minutes: 30)), window);

    expect(player.opens, hasLength(2));
    expect(player.opens.last.at, at.add(const Duration(minutes: 30)));
  });

  test('a drag that keeps moving opens no playlist at all', () async {
    // The whole reason scrubTo exists. A drag fires per pointer sample; opening a window per
    // sample would put dozens of requests in flight for one gesture. Nothing here holds still for
    // `scrubSettle`, so nothing here costs a request.
    for (var i = 0; i < 20; i++) {
      await replay.scrubTo(from.add(Duration(minutes: 20 * i)), window);
    }

    expect(player.opens, isEmpty);
    expect(replay.playhead.value, isNotNull);
  });

  test('a drag that stops opens the window it stopped in', () async {
    // And the other half of the bargain: a sweep costs nothing, but a pause to look repaints the
    // stage rather than leaving it frozen until the finger comes up.
    final stopped = at.subtract(const Duration(hours: 2));
    for (var i = 0; i < 5; i++) {
      await replay.scrubTo(from.add(Duration(minutes: 20 * i)), window);
    }
    await replay.scrubTo(stopped, window);
    await Future<void>.delayed(ReplayController.scrubSettle * 2);

    expect(player.opens, hasLength(1));
    expect(player.opens.single.at, stopped);
  });

  test('a drag that stops in a hole opens at the nearest footage', () async {
    // Dead centre of the hour-long hole: an exact tie, which goes forward.
    await replay.scrubTo(holeFrom.add(const Duration(minutes: 30)), window);
    await Future<void>.delayed(ReplayController.scrubSettle * 2);

    expect(player.opens.single.at, holeTo);
    expect(replay.playhead.value, holeTo);
  });

  test('a release during a pending settle opens one window, not two', () async {
    final landed = at.subtract(const Duration(hours: 2));
    await replay.scrubTo(landed, window);
    await replay.seekTo(landed, window);
    await Future<void>.delayed(ReplayController.scrubSettle * 2);

    expect(player.opens, hasLength(1));
    expect(player.opens.single.at, landed);
  });

  test('going back to live cancels a settle that has not fired', () async {
    await replay.seekTo(at, window);
    await replay.scrubTo(at.subtract(const Duration(hours: 3)), window);
    await replay.backToLive();
    await Future<void>.delayed(ReplayController.scrubSettle * 2);

    expect(replay.replaying, isFalse);
    expect(player.opens, hasLength(1), reason: 'only the original seek');
  });

  test('a burst of drag samples inside the window collapses', () async {
    // A trackpad delivers these at up to 120 Hz, and handing every one straight to the decoder is
    // re-buffer churn that reads as the picture refusing to track. Not awaited, because that is
    // how the samples actually arrive: on top of a seek still in flight.
    await replay.seekTo(at, window);
    player.seeks.clear();

    for (var i = 1; i <= 10; i++) {
      unawaited(replay.scrubTo(at.add(Duration(seconds: i)), window));
    }
    await Future<void>.delayed(Duration.zero);

    expect(player.seeks.length, lessThan(10));
    expect(
      player.seeks.last,
      const Duration(seconds: 10),
      reason: 'the last sample is the one that must win',
    );
  });

  test('a drag inside an open window does seek, without reopening', () async {
    await replay.seekTo(at, window);
    await replay.scrubTo(at.add(const Duration(minutes: 2)), window);

    expect(player.opens, hasLength(1));
    expect(player.seeks.last, const Duration(minutes: 2));
  });

  test(
    'a tap in a hole snaps to footage rather than opening an empty range',
    () async {
      // /vod.m3u8 answers an empty range with a 404, and that must not read as a broken player.
      // Dead centre of the hole: an exact tie, which goes forward.
      await replay.seekTo(holeFrom.add(const Duration(minutes: 30)), window);

      expect(player.opens.single.at, holeTo);
    },
  );

  test('a window is clipped to the end of the run it starts in', () async {
    // Ten minutes before the hole, with a fifteen-minute window: the playlist must stop at the
    // hole rather than claim five minutes of footage that does not exist.
    await replay.seekTo(holeFrom.subtract(const Duration(minutes: 10)), window);

    final query = player.opens.single.playlist.queryParameters;
    expect(DateTime.parse(query['to']!).toLocal(), holeFrom);
  });

  test(
    'seeking to the live edge goes back to live instead of replaying it',
    () async {
      await replay.seekTo(DateTime.now(), window);

      expect(replay.replaying, isFalse);
      expect(player.opens, isEmpty);
    },
  );

  test('back to live disposes the player and clears the playhead', () async {
    await replay.seekTo(at, window);
    await replay.backToLive();

    expect(replay.replaying, isFalse);
    expect(replay.playhead.value, isNull);
    expect(player.disposed, isTrue);
  });

  test('playing to the end of a window reopens the next one', () async {
    await replay.seekTo(at, window);

    // Past the reopen guard. An HLS VOD playlist cannot be appended to, so running to the end
    // would stall rather than continue.
    player.advance(ReplayController.window - const Duration(seconds: 20));
    await Future<void>.delayed(Duration.zero);

    expect(player.opens, hasLength(2));
    // To the millisecond, not the microsecond: a reopen opens where playback has *got to*, and on
    // a real clock that is a few microseconds past where it was decided. The frame rate here is
    // 15, so a millisecond is already a fifteenth of the smallest visible difference.
    expect(
      player.opens.last.at
          .difference(
            at.add(ReplayController.window - const Duration(seconds: 20)),
          )
          .inMilliseconds,
      0,
    );
  });

  test('a reopen opens on the frame playback reached, not the one it left', () async {
    // The two requests an open makes are a round trip each, and the window being replaced plays
    // throughout them. Opening at the instant the reopen was decided therefore puts the first
    // frame of the new window *behind* the last frame of the old one — the picture stepping back
    // by however long the Server took to answer, which is what is on screen before the catch-up
    // seek can repair it. So the open itself has to carry that time.
    var clock = DateTime.now();
    final catchingUp = ReplayController(
      repository: repository,
      cameraId: 'cam1',
      playerFactory: () => player,
      clock: () => clock,
    );
    addTearDown(catchingUp.dispose);

    repository.onOpen = () =>
        clock = clock.add(const Duration(milliseconds: 450));

    await catchingUp.seekTo(at, window);
    // A seek lands exactly where it was asked to, round trips or not.
    expect(player.opens.single.at, at);

    player.advance(ReplayController.window - const Duration(seconds: 20));
    await Future<void>.delayed(Duration.zero);

    // Measured as a difference rather than an instant: the source reads the clock twice, and the
    // microseconds between those two reads are real elapsed time, not slack in the assertion.
    final decided = at.add(
      ReplayController.window - const Duration(seconds: 20),
    );
    expect(
      player.opens.last.at.difference(decided).inMilliseconds,
      450,
      reason: 'the reopen must open where the old window had played on to',
    );
  });

  test('a reopen resumes where playback got to, not where it started from', () async {
    // A reopen is decided at the instant playback has reached and lands a token, a playlist and a
    // first segment later. Opening at the instant it was decided replays the second that ran
    // while it was in flight — the recording's own burned-in clock counts 53, 54, 53 — so the
    // window opens where it was asked to and then catches up to real time.
    var clock = DateTime.now();
    final catchingUp = ReplayController(
      repository: repository,
      cameraId: 'cam1',
      playerFactory: () => player,
      clock: () => clock,
    );
    addTearDown(catchingUp.dispose);

    // Opening costs real time, and this is the only place in the test that spends any.
    repository.onOpen = () =>
        clock = clock.add(const Duration(milliseconds: 600));

    await catchingUp.seekTo(at, window);
    player.advance(ReplayController.window - const Duration(seconds: 20));
    await Future<void>.delayed(Duration.zero);

    expect(player.opens, hasLength(2));

    // The open already carries the 600 ms it spent, so the catch-up afterwards has nothing left
    // to give back and the two agree on where the picture stands. What matters is that neither
    // leaves it behind the instant the reopen was decided at.
    final decided = at.add(
      ReplayController.window - const Duration(seconds: 20),
    );
    expect(
      player.opens.last.at,
      decided.add(const Duration(milliseconds: 600)),
    );
    expect(catchingUp.playhead.value, player.opens.last.at);
    expect(player.seeks.last, const Duration(milliseconds: 600));
  });

  group('a break in the recording', () {
    // Replay is a claim about wall time, so a stretch nobody recorded takes as long to cross as
    // it took to happen. Stitching the footage either side together would put two instants an
    // hour apart in consecutive frames and call it continuous.
    late ReplayController crossing;
    late DateTime clock;

    setUp(() {
      clock = DateTime.now();
      crossing = ReplayController(
        repository: repository,
        cameraId: 'cam1',
        playerFactory: () => player,
        clock: () => clock,
      );
      addTearDown(crossing.dispose);
    });

    /// Runs the player up to the end of the first run of footage, which ends at [holeFrom].
    Future<void> playToTheHole() async {
      final start = holeFrom.subtract(const Duration(minutes: 2));
      await crossing.seekTo(start, window);
      player.advance(const Duration(minutes: 2));
      await Future<void>.delayed(Duration.zero);
    }

    test('is not played over by opening a window across it', () async {
      await playToTheHole();

      // One open — the seek. Reopening here would build a playlist spanning the hole, and the
      // frames either side of it are an hour apart.
      expect(player.opens, hasLength(1));
      expect(crossing.inGap, isTrue);
    });

    test('drops the picture rather than holding the last frame', () async {
      await playToTheHole();

      // A decoder parked on the last frame before the outage would leave that frame up for the
      // length of it, which is the one reading most likely to be believed and most likely wrong.
      expect(crossing.player, isNull);
      expect(player.disposed, isTrue);
    });

    test('is still replaying, not back at the live view', () async {
      await playToTheHole();

      expect(crossing.replaying, isTrue);
      expect(crossing.playhead.value, isNotNull);
    });

    test('advances the playhead at real time while crossing', () async {
      await playToTheHole();
      final entered = crossing.playhead.value!;

      clock = clock.add(const Duration(minutes: 10));
      await Future<void>.delayed(ReplayController.gapTick * 2);

      expect(
        crossing.playhead.value!.difference(entered).inMinutes,
        10,
        reason: 'ten minutes of outage takes ten minutes to cross at 1x',
      );
    });

    test('crosses it faster at a higher rate', () async {
      await playToTheHole();
      final entered = crossing.playhead.value!;

      await crossing.setRate(4);
      clock = clock.add(const Duration(minutes: 1));
      await Future<void>.delayed(ReplayController.gapTick * 2);

      expect(crossing.playhead.value!.difference(entered).inMinutes, 4);
    });

    test('holds the playhead still while paused', () async {
      await playToTheHole();
      crossing.pause();
      final paused = crossing.playhead.value!;

      clock = clock.add(const Duration(minutes: 5));
      await Future<void>.delayed(ReplayController.gapTick * 2);

      expect(crossing.playhead.value, paused);
    });

    test('opens the far side when the playhead reaches it', () async {
      await playToTheHole();

      clock = clock.add(const Duration(hours: 1, minutes: 5));
      await Future<void>.delayed(ReplayController.gapTick * 2);
      await Future<void>.delayed(Duration.zero);

      expect(crossing.inGap, isFalse);
      expect(player.opens, hasLength(2));
      expect(
        player.opens.last.at,
        holeTo,
        reason: 'the footage resumes exactly where the run does',
      );
    });

    test('a click past it skips it rather than sitting through it', () async {
      await playToTheHole();

      // The break is drawn on the scrubber precisely so it can be clicked past.
      await crossing.seekTo(holeTo.add(const Duration(minutes: 30)), window);

      expect(crossing.inGap, isFalse);
      expect(player.opens.last.at, holeTo.add(const Duration(minutes: 30)));
    });
  });

  test('playing at the live edge does not reopen on every tick', () async {
    // Recording right up to now, which is what makes this the live edge: every window the
    // controller can open ends at `now`, so playing into the guard at the end of one asks for a
    // window that ends a quarter of a second later — and lands straight back in the guard. Left
    // unguarded that is a reopen per position tick for as long as replay runs, each one a stream
    // token, a playlist fetch, and a decoder reset the viewer sees as the picture blinking.
    final live = TimelineWindow(
      from: from,
      to: now,
      coverage: [CoverageSpan(from, now)],
    );

    // Far enough back that the seek replays rather than handing straight to the live view.
    await replay.seekTo(now.subtract(const Duration(seconds: 90)), live);
    expect(player.opens, hasLength(1));

    for (var second = 65; second < 90; second++) {
      player.advance(Duration(seconds: second));
      await Future<void>.delayed(Duration.zero);
    }

    expect(player.opens, hasLength(1));
  });

  test('playing within the window does not reopen', () async {
    await replay.seekTo(at, window);

    player.advance(const Duration(minutes: 5));
    await Future<void>.delayed(Duration.zero);

    expect(player.opens, hasLength(1));
  });

  test('the player is reused across reopens', () async {
    // Building a fresh libmpv instance every fifteen minutes would drop the video surface and
    // flash the stage.
    var built = 0;
    final reusing = ReplayController(
      repository: _PlayableRepository(),
      cameraId: 'cam1',
      playerFactory: () {
        built++;
        return player;
      },
    );
    addTearDown(reusing.dispose);

    await reusing.seekTo(at, window);
    await reusing.seekTo(at.add(const Duration(minutes: 30)), window);

    expect(built, 1);
  });

  test(
    'a window with no footage at all reports it rather than opening nothing',
    () async {
      await replay.seekTo(at, TimelineWindow(from: from, to: now));

      expect(player.opens, isEmpty);
      expect(replay.failure.value, isNotNull);
    },
  );

  test('the mute state survives a reopen', () async {
    await replay.seekTo(at, window);
    await replay.setMuted(true);
    await replay.seekTo(at.add(const Duration(minutes: 30)), window);

    expect(player.muted, isTrue);
  });

  test('the volume is applied to a freshly opened window', () async {
    await replay.setVolume(0.4);
    await replay.seekTo(at, window);

    expect(player.volumes.last, 0.4);
  });

  test('each window opened also loads the boxes to draw over it', () async {
    // The live feed is trimmed and does not reach as far back as the scrubber does, so replay
    // has to fetch its own detections — over the same span as the footage, and at the same
    // moment, so a window never plays with the previous window's boxes on it.
    await replay.seekTo(at, window);

    final asked = repository.detectionWindows.single;
    expect(asked.cameraId, 'cam1');
    expect(asked.from, at);
    expect(asked.to, at.add(ReplayController.window));

    await replay.seekTo(at.add(const Duration(minutes: 30)), window);

    expect(repository.detectionWindows, hasLength(2));
    expect(
      repository.detectionWindows.last.from,
      at.add(const Duration(minutes: 30)),
    );
  });

  group('the playlist starting before the instant asked for', () {
    // Segments are four seconds and a window can be asked for anywhere inside one, so a playlist
    // begins at the boundary at or before it and playback position counts from there. Every
    // instant the controller reports has to take that back off, or the playhead runs up to a whole
    // segment ahead of the picture — which is a curiosity on a clock and a visible error on a
    // detection box drawn over the frame.
    setUp(() => repository.startOffset = const Duration(milliseconds: 3906));

    test('the playhead reports the instant actually on screen', () async {
      await replay.seekTo(at, window);

      // Position 3.906 is the playlist's own boundary plus the offset — the frame for `at`.
      player.advance(const Duration(milliseconds: 3906));
      await Future<void>.delayed(Duration.zero);

      expect(replay.playhead.value, at);
    });

    test('a playhead ten seconds in is ten seconds of footage in', () async {
      await replay.seekTo(at, window);

      player.advance(const Duration(milliseconds: 13906));
      await Future<void>.delayed(Duration.zero);

      expect(replay.playhead.value, at.add(const Duration(seconds: 10)));
    });

    test(
      'the player is told where the playlist starts, not where the instant is',
      () async {
        // Both players seek by `at - windowFrom` to reach the instant asked for, so `windowFrom`
        // has to be the playlist's own media start — the segment boundary — and not the instant
        // itself. Passing the instant for both makes that difference zero and the seek a no-op,
        // and the picture then starts wherever the playlist does: a whole segment early, and
        // visibly backwards from wherever playback had already got to.
        await replay.seekTo(at, window);

        final open = player.opens.single;
        expect(open.at, at);
        expect(
          open.windowFrom,
          at.subtract(const Duration(milliseconds: 3906)),
        );
        expect(
          open.at.difference(open.windowFrom),
          const Duration(milliseconds: 3906),
          reason: 'the offset the player seeks by must be the start offset',
        );
      },
    );

    test('the playhead ignores a position measured against the old source', () async {
      // A reopen tears the media off the element, which reports position zero while it is gone.
      // Against the fresh window that reads as `from - startOffset` — the playhead jumping back
      // by a segment on a picture that has not moved.
      await replay.seekTo(at, window);
      player.advance(const Duration(milliseconds: 3906));
      await Future<void>.delayed(Duration.zero);
      expect(replay.playhead.value, at);

      // Every value the playhead takes, not just the one it settles on: the catch-up seek at the
      // end of a reopen puts it back where it belongs, so a test that reads it afterwards passes
      // straight through the dip it is supposed to be catching.
      final seen = <DateTime>[];
      void record() {
        if (replay.playhead.value case final at?) seen.add(at);
      }

      replay.playhead.addListener(record);
      addTearDown(() => replay.playhead.removeListener(record));

      // The element reporting zero while its media is detached, mid-open.
      repository.onOpen = () => player.advance(Duration.zero);
      player.advance(ReplayController.window - const Duration(seconds: 20));
      await Future<void>.delayed(Duration.zero);

      // Monotonicity, rather than a comparison against an instant computed here. Every previous
      // attempt at this assertion was wrong about which instant to compare with — the start
      // offset, the compensation, the microseconds of a clock read — while the property actually
      // being tested needs none of them: the playhead must never step backwards.
      //
      // A tenth of a second of slack is far below the segment-sized jump this guards against (the
      // real one was 1057 ms) and far above anything a clock read costs.
      expect(seen, isNotEmpty, reason: 'no playhead values seen — vacuous');
      for (var i = 1; i < seen.length; i++) {
        expect(
          seen[i].isBefore(
            seen[i - 1].subtract(const Duration(milliseconds: 100)),
          ),
          isFalse,
          reason: 'playhead went back from ${seen[i - 1]} to ${seen[i]}',
        );
      }
    });

    test('a seek inside the window lands on the frame it asked for', () async {
      await replay.seekTo(at, window);
      await replay.seekTo(at.add(const Duration(minutes: 5)), window);

      expect(
        player.seeks.last,
        const Duration(minutes: 5) + const Duration(milliseconds: 3906),
      );
    });

    test('a drag inside the window seeks by the same correction', () async {
      await replay.seekTo(at, window);
      await replay.scrubTo(at.add(const Duration(minutes: 2)), window);

      expect(
        player.seeks.last,
        const Duration(minutes: 2) + const Duration(milliseconds: 3906),
      );
    });

    test('going back to live forgets it', () async {
      // The next window has its own offset, and carrying this one into it would misplace the
      // playhead by the difference rather than by the whole segment.
      await replay.seekTo(at, window);
      await replay.backToLive();
      repository.startOffset = Duration.zero;
      await replay.seekTo(at, window);

      player.advance(Duration.zero);
      await Future<void>.delayed(Duration.zero);

      expect(replay.playhead.value, at);
    });
  });

  test('a drag inside the open window does not refetch the boxes', () async {
    // The same reason scrubTo does not reopen a playlist: a drag fires per pointer sample, and
    // the boxes for this fifteen minutes are already in hand.
    await replay.seekTo(at, window);
    await replay.scrubTo(at.add(const Duration(minutes: 2)), window);

    expect(repository.detectionWindows, hasLength(1));
  });

  /// The trap the mute line beside it was already here for. A player is built fresh for each
  /// window, so anything not re-applied on open silently reverts the moment you drag past the
  /// window's edge — about every fifteen minutes of continuous replay.
  test('reopening a window re-applies the volume', () async {
    await replay.seekTo(at, window);
    await replay.setVolume(0.25);

    player.volumes.clear();
    await replay.seekTo(at.add(const Duration(minutes: 30)), window);

    expect(player.volumes, contains(0.25));
  });

  test('a volume set before replay starts is not forgotten', () async {
    // Nothing to apply it to yet — the controller has no player until the first seek — so this
    // pins that it is remembered rather than dropped.
    await replay.setVolume(0.6);
    expect(player.volumes, isEmpty);

    await replay.seekTo(at, window);
    expect(player.volumes.last, 0.6);
  });

  /// Caught at the level where it is testable without a widget: muting while live has to reach
  /// the controller, or `_openWindow` re-applies a stale `false` and the camera's audio plays
  /// while the speaker glyph reads muted.
  test('muting while live is remembered when replay starts', () async {
    await replay.setMuted(true);
    expect(replay.replaying, isFalse);

    await replay.seekTo(at, window);

    expect(player.muted, isTrue);
  });

  group('the transport', () {
    test('a fresh window plays', () async {
      await replay.seekTo(at, window);

      expect(replay.playing, isTrue);
      expect(replay.rate, 1);
    });

    test('pause parks the player, play starts it again', () async {
      await replay.seekTo(at, window);

      replay.pause();
      expect(replay.playing, isFalse);
      expect(player.playing.value, isFalse);

      replay.play();
      expect(replay.playing, isTrue);
      expect(player.playing.value, isTrue);
    });

    test('a rate reaches the player', () async {
      await replay.seekTo(at, window);
      player.rates.clear();

      await replay.setRate(2);

      expect(replay.rate, 2);
      expect(player.rates.last, 2.0);
    });

    test('above the play rate the player is parked and stepped', () async {
      // The same bargain the wall strikes: hls.js cannot keep a buffer at 8x, so the picture is
      // seeked instead. Unlike the wall this needs no clock — a step is a seek, so the position
      // moves because the picture did.
      await replay.seekTo(at, window);
      player.seeks.clear();

      await replay.setRate(8);
      expect(replay.stepping, isTrue);
      expect(player.playing.value, isFalse);

      await Future<void>.delayed(ReplayController.stepPeriod * 3);

      expect(player.seeks, isNotEmpty);
    });

    test('stepping steps by as much wall time as the rate claims', () async {
      await replay.seekTo(at, window);
      player.seeks.clear();
      await replay.setRate(8);

      await Future<void>.delayed(ReplayController.stepPeriod * 2);
      final first = player.seeks.first;

      // One step at 8x covers eight step-periods of footage.
      expect(first, ReplayController.stepPeriod * 8);
    });

    test('coming back down from a step resumes playing', () async {
      await replay.seekTo(at, window);
      await replay.setRate(8);
      await replay.setRate(2);

      expect(replay.stepping, isFalse);
      expect(player.playing.value, isTrue);
      expect(player.rates.last, 2.0);
    });

    test('going back to live stops the stepper and resets the rate', () async {
      // A step timer left running against a disposed source is a seek into nothing, every quarter
      // second, for as long as the screen is open.
      await replay.seekTo(at, window);
      await replay.setRate(8);
      await replay.backToLive();

      player.seeks.clear();
      await Future<void>.delayed(ReplayController.stepPeriod * 3);

      expect(replay.rate, 1);
      expect(replay.playing, isFalse);
      expect(player.seeks, isEmpty);
    });
  });
}

/// The design's own repository, with two additions: a URL to play from, and a note of every
/// detection window asked for.
///
/// Everything else the controller reads is unchanged, and building on the sample keeps this test
/// as hermetic as the rest of the suite.
class _PlayableRepository extends SampleServalRepository {
  _PlayableRepository();

  final detectionWindows = <({String cameraId, DateTime from, DateTime to})>[];

  /// What the Server's playlist would say its media starts before the instant asked for.
  Duration startOffset = Duration.zero;

  /// Run as an open begins, for a test that needs one to cost something — a clock to advance,
  /// most of the time. The token is the first of the two requests every open makes.
  void Function()? onOpen;

  @override
  Future<String?> mintStreamToken() async {
    onOpen?.call();
    return super.mintStreamToken();
  }

  @override
  Future<Duration> vodStartOffsetFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async => startOffset;

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
  ) async => detectionWindows.add((cameraId: cameraId, from: from, to: to));
}

class _FakePlayer implements VodPlayer {
  final opens = <({Uri playlist, DateTime windowFrom, DateTime at})>[];
  final files = <Uri>[];
  final seeks = <Duration>[];

  /// Every level ever applied, in order. A list rather than a field because the interesting
  /// question is whether it was re-applied after a window reopen, not just what it ended at.
  final volumes = <double>[];
  final rates = <double>[];

  /// Every gain ever applied, in order, for the same reason [volumes] is a list: the question is
  /// whether the camera's gain survives a window reopen, not what it ended at.
  final gains = <({double db, double? gateRms})>[];

  bool muted = false;
  bool disposed = false;

  final _position = ValueNotifier<Duration>(Duration.zero);
  final _duration = ValueNotifier<Duration?>(null);
  final _playing = ValueNotifier<bool>(false);
  final _videoSize = ValueNotifier<Size?>(null);
  final _failure = ValueNotifier<String?>(null);

  @override
  ValueListenable<Size?> get videoSize => _videoSize;

  @override
  ValueListenable<Duration?> get duration => _duration;

  void advance(Duration to) => _position.value = to;

  @override
  Future<void> open(
    Uri playlist, {
    required DateTime windowFrom,
    required DateTime at,
  }) async {
    opens.add((playlist: playlist, windowFrom: windowFrom, at: at));
    _position.value = Duration.zero;
  }

  /// Recorded but never used by the replay controller — a saved clip is opened by the clips
  /// screen, which owns its own player.
  @override
  Future<void> openFile(Uri file) async {
    files.add(file);
    _position.value = Duration.zero;
  }

  @override
  Future<void> play() async => _playing.value = true;

  @override
  Future<void> pause() async => _playing.value = false;

  @override
  Future<void> seekWithin(Duration offset) async {
    seeks.add(offset);
    _position.value = offset;
  }

  @override
  Future<void> setRate(double value) async => rates.add(value);

  @override
  Future<void> setMuted(bool value) async => muted = value;

  @override
  Future<void> setVolume(double value) async => volumes.add(value);

  @override
  Future<void> setGain(double db, double? gateRms) async =>
      gains.add((db: db, gateRms: gateRms));

  @override
  ValueListenable<Duration> get position => _position;

  @override
  ValueListenable<bool> get playing => _playing;

  @override
  ValueListenable<String?> get failure => _failure;

  @override
  Widget buildView() => const SizedBox.shrink();

  @override
  Future<void> dispose() async => disposed = true;
}
