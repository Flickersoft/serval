import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/playback/playback_volume.dart';

/// One control, several backends, and no compiler to catch a mix-up.
///
/// Getting a *volume* mapping wrong does not crash — it produces a slider that reaches full volume a
/// tenth of the way along, or one that is silent until the far end. That reads as a broken control
/// rather than as a bug, which is why the mapping lives in one place and is pinned here.
///
/// Getting the *split* wrong is worse, because the backends disagree on what is even reachable: a
/// `<video>` element clamps itself at unity, libwebrtc at ten, and only libmpv's filter chain is
/// unbounded. A mistake there is a control that works on one platform and silently under-delivers on
/// another.
void main() {
  group('the slider’s split', () {
    test('unity is exactly at the mark, and adds nothing', () {
      final level = playbackFromTravel(unityTravel);
      expect(level.volume, 1);
      expect(level.db, 0);
    });

    test('silence at the bottom and the ceiling at the top', () {
      expect(playbackFromTravel(0).volume, 0);
      expect(playbackFromTravel(0).db, 0);
      expect(playbackFromTravel(1).volume, 1);
      expect(playbackFromTravel(1).db, maxBoostDb);
    });

    test('below unity nothing is lifted, so no filter chain is built', () {
      // The gate and the limiter only exist to make amplification usable. Below unity there is
      // nothing to guard, and a gate here would take quiet content away for nothing.
      for (final travel in [0.0, 0.2, 0.5, 0.74]) {
        expect(playbackFromTravel(travel).db, 0);
        expect(mpvAudioFilter(playbackFromTravel(travel).db, 0.001), '');
      }
    });

    test('above unity the player sits at unity and the lift rides behind', () {
      // Anything less than 1 here would attenuate the signal the gain is about to lift, and the
      // level would be applied twice.
      for (final travel in [0.8, 0.9, 1.0]) {
        expect(playbackFromTravel(travel).volume, 1);
        expect(playbackFromTravel(travel).db, greaterThan(0));
      }
    });

    test('the effective gain never goes backwards across the seam', () {
      var previous = -1.0;
      for (var step = 0; step <= 100; step++) {
        final level = playbackFromTravel(step / 100);
        final effective = level.volume * boostFactor(level.db);
        expect(effective, greaterThanOrEqualTo(previous));
        previous = effective;
      }
    });

    test('the curve below unity is perceptual, not raw amplitude', () {
      // A linear amplitude track does almost nothing audible for its first two thirds. Squaring is
      // what makes the middle of the track sound like the middle.
      expect(playbackFromTravel(unityTravel / 2).volume, closeTo(0.25, 0.001));
      expect(playbackFromTravel(unityTravel).volume, 1);
    });

    test('out of range clamps rather than passing through', () {
      expect(playbackFromTravel(-1).volume, 0);
      expect(playbackFromTravel(2).db, maxBoostDb);
    });
  });

  group('travelFor', () {
    test('round-trips every position the slider can be dragged to', () {
      for (var step = 0; step <= 100; step++) {
        final travel = step / 100;
        final level = playbackFromTravel(travel);
        expect(
          travelFor(volume: level.volume, db: level.db),
          closeTo(travel, 0.0001),
          reason: 'travel $travel did not survive the round trip',
        );
      }
    });

    test('an uncalibrated camera opens at unity', () {
      expect(travelFor(volume: 1, db: 0), unityTravel);
    });

    test('a camera carrying a lift opens above unity', () {
      // What seeds the knob from the camera's starting volume: a camera somebody has already
      // calibrated must not arrive silent on a new browser.
      expect(travelFor(volume: 1, db: 12), greaterThan(unityTravel));
      expect(travelFor(volume: 1, db: maxBoostDb), 1);
    });
  });

  group('volumeLabel', () {
    test('is the knob’s position on its track, 0 to 100', () {
      expect(volumeLabel(0), '0%');
      expect(volumeLabel(0.5), '50%');
      expect(volumeLabel(unityTravel), '75%');
      expect(volumeLabel(1), '100%');
    });

    test('never shows a number above 100', () {
      // The top of the track is ten times, and saying so would be four digits of arithmetic nobody
      // asked for. Amplifying is what the colour past the mark is for.
      for (var step = 0; step <= 1000; step++) {
        final shown = int.parse(volumeLabel(step / 1000).replaceAll('%', ''));
        expect(shown, inInclusiveRange(0, 100));
      }
    });

    test('never goes backwards as the knob moves right', () {
      var previous = -1;
      for (var step = 0; step <= 1000; step++) {
        final shown = int.parse(volumeLabel(step / 1000).replaceAll('%', ''));
        expect(shown, greaterThanOrEqualTo(previous));
        previous = shown;
      }
    });

    test('reports the position, not the squared amplitude', () {
      // The curve is a feel correction. Surfacing it would have the middle of the track read "25%"
      // and look mislabelled.
      expect(playbackFromTravel(0.5).volume, closeTo(0.444, 0.001));
      expect(volumeLabel(0.5), '50%');
    });

    test('never grows past the width the readout is sized for', () {
      for (var step = 0; step <= 100; step++) {
        expect(volumeLabel(step / 100).length, lessThanOrEqualTo(4));
      }
    });
  });

  group('mpv', () {
    test('0 is silence and 1 is unity', () {
      expect(mpvVolume(0), 0);
      expect(mpvVolume(1), 100);
    });

    test(
      'the middle of the app range is the middle of libmpv’s',
      () => expect(mpvVolume(0.5), 50),
    );

    test('out of range clamps rather than passing through', () {
      // libmpv accepts above 100 as software amplification; nothing here asks for it, and a
      // value arriving out of range means a caller is confused rather than ambitious. Boost goes
      // through the filter chain instead — see the mpvAudioFilter group.
      expect(mpvVolume(1.5), 100);
      expect(mpvVolume(-1), 0);
    });
  });

  group('html video', () {
    test('is already the app’s range', () {
      expect(htmlVideoVolume(0), 0);
      expect(htmlVideoVolume(0.5), 0.5);
      expect(htmlVideoVolume(1), 1);
    });

    test('out of range clamps', () {
      expect(htmlVideoVolume(2), 1);
      expect(htmlVideoVolume(-0.5), 0);
    });
  });

  group('webrtc', () {
    /// Clamped to 0..1 rather than scaled to libwebrtc's 0..10, so the same number is the same
    /// loudness on Linux and in a browser. Scaling would make the native build ten times louder
    /// than the web one at every position.
    test('is unity at 1, not a tenth of the way up', () {
      expect(webRtcVolume(1), 1);
      expect(webRtcVolume(0.5), 0.5);
      expect(webRtcVolume(0), 0);
    });

    test(
      'the plain mapping still refuses above-unity boost',
      () => expect(webRtcVolume(5), 1),
    );
  });

  test('every backend agrees on silence', () {
    expect(mpvVolume(0), 0);
    expect(htmlVideoVolume(0), 0);
    expect(webRtcVolume(0), 0);
  });

  group('boostFactor', () {
    test('0 dB changes nothing', () => expect(boostFactor(0), 1));

    test('6 dB is a doubling, which is what makes the stops readable', () {
      expect(boostFactor(6), closeTo(2, 0.01));
      expect(boostFactor(12), closeTo(4, 0.02));
    });

    test('the ceiling is ten times', () {
      expect(boostFactor(maxBoostDb), closeTo(10, 0.01));
    });

    test('past the ceiling it stops rather than growing', () {
      expect(boostFactor(100), boostFactor(maxBoostDb));
    });

    test('a negative gain is not attenuation', () {
      // Listening more quietly is the volume control's job. Reading a negative dB as attenuation
      // here would give two controls the same effect with no way to see which one silenced a camera.
      expect(boostFactor(-12), 1);
    });
  });

  group('the starting-volume stops', () {
    test('start at unity and end at the top of the track', () {
      expect(startingVolumeStops.first, unityTravel * 100);
      expect(startingVolumeStops.last, 100);
    });

    test('never state a position the control does not have', () {
      // The whole point of the stops: this is the one other place a level is set, and it has to quote
      // the same 0..100 the pill does.
      for (final stop in startingVolumeStops) {
        expect(stop, inInclusiveRange(0, 100));
      }
    });

    test('each is a distinct lift, and round in dB as well', () {
      final lifts = startingVolumeStops
          .map((stop) => playbackFromTravel(stop / 100).db)
          .toList();
      expect(lifts.toSet(), hasLength(lifts.length));
      for (final lift in lifts) {
        expect(lift, closeTo(lift.roundToDouble(), 0.0001));
      }
    });

    test('are in order, so the menu reads as a scale', () {
      final sorted = [...startingVolumeStops]..sort();
      expect(startingVolumeStops, sorted);
    });
  });

  group('native live boost', () {
    test('unity at 0 dB is the level it always was', () {
      expect(nativeWebRtcBoostedVolume(1, 0), 1);
      expect(nativeWebRtcBoostedVolume(0.5, 0), 0.5);
    });

    test('scales into libwebrtc’s real 0..10 range', () {
      expect(nativeWebRtcBoostedVolume(1, 6), closeTo(2, 0.01));
      expect(nativeWebRtcBoostedVolume(0.5, 6), closeTo(1, 0.01));
    });

    test('stops at the ceiling, which is where libwebrtc stops', () {
      expect(nativeWebRtcBoostedVolume(1, maxBoostDb), closeTo(10, 0.01));
      // Asked for more than the control can offer, it must land on the cap rather than overflowing
      // the range: a value libwebrtc will not take is a level that does not get applied at all.
      expect(nativeWebRtcBoostedVolume(1, 100), closeTo(10, 0.01));
    });

    test('reaches everything the control can ask for', () {
      // The reason there is no per-platform cap to warn about. If the control's ceiling ever rose
      // above this path's, a level set on the desktop live view would silently arrive quieter than
      // the same number does everywhere else.
      expect(nativeWebRtcBoostedVolume(1, maxBoostDb), lessThanOrEqualTo(10));
      expect(boostFactor(maxBoostDb), lessThanOrEqualTo(10));
    });
  });

  group('mpvAudioFilter', () {
    test('an unboosted camera gets no filter chain at all', () {
      // The empty string clears `af`. Anything else would put a filter in the path of a camera
      // nobody asked to change.
      expect(mpvAudioFilter(0, null), '');
      expect(mpvAudioFilter(0, 0.001), '');
    });

    test('a boost without a gate is gain and limiter only', () {
      final chain = mpvAudioFilter(18, null);
      expect(chain, contains('volume=18.0dB'));
      expect(chain, contains('alimiter'));
      expect(chain, isNot(contains('agate')));
    });

    test('the limiter has ffmpeg’s auto-level switched off', () {
      // Measured, not guessed: with `alimiter`'s default auto-level on, a signal driven 10 dB past
      // the ceiling comes out at 0.0 dBFS — the normalisation puts back exactly what the limiting
      // took off, so the limiter does nothing at all. With this it is held at -3.1 dBFS.
      expect(
        mpvAudioFilter(40, null),
        contains('alimiter=limit=0.7:level=disabled'),
      );
    });

    test('the limiter is always behind the gain, never in front of it', () {
      // Order is the whole point of the chain: limiting before the gain would catch nothing, since
      // it is the gain that pushes a transient past the ceiling.
      final chain = mpvAudioFilter(30, 0.0006);
      expect(chain.indexOf('volume='), lessThan(chain.indexOf('alimiter')));
    });

    test('the gate is always in front of the gain, never behind it', () {
      // And the gate has to be first, or it would be measuring a level the gain has already lifted
      // — so its threshold would mean something different at every gain setting.
      final chain = mpvAudioFilter(30, 0.0006);
      expect(chain.indexOf('agate'), lessThan(chain.indexOf('volume=')));
    });

    test('the gate carries the shared envelope constants', () {
      final chain = mpvAudioFilter(24, 0.0006);
      expect(chain, contains('attack=$gateAttackMs'));
      expect(chain, contains('release=$gateReleaseMs'));
      // Stated rather than left to ffmpeg's default, so the stored threshold keeps meaning an RMS.
      expect(chain, contains('detection=rms'));
    });

    test('a small threshold is written as a decimal ffmpeg can read', () {
      // `toString` would give `6e-7` here, which ffmpeg reads as a filter-syntax error rather than
      // a number — and the whole chain would be rejected, taking the boost with it.
      final chain = mpvAudioFilter(12, 0.0000006);
      expect(chain, isNot(contains('e-')));
      expect(chain, contains('agate=threshold=0.00000060'));
    });

    test('a gain past the ceiling is clamped in the filter too', () {
      expect(mpvAudioFilter(100, null), contains('volume=${maxBoostDb}dB'));
    });

    test('a nonsense threshold is treated as no gate rather than as zero', () {
      expect(mpvAudioFilter(12, 0), isNot(contains('agate')));
      expect(mpvAudioFilter(12, -1), isNot(contains('agate')));
    });
  });

  group('the gate floor', () {
    test('is quiet but not silent', () {
      // Full mute would be no quieter to any listener and would make the gate's own opening and
      // closing an audible event.
      expect(gateFloor, greaterThan(0));
      expect(gateFloor, lessThan(0.01));
    });

    test('is deep enough to survive the largest boost', () {
      // A noise floor around -69 dBFS, attenuated by the gate and then lifted by the whole range,
      // has to come out inaudible — otherwise the gate is not doing the job it was added for.
      const noiseFloor = 0.000355; // -69 dBFS
      final gated = noiseFloor * gateFloor * boostFactor(maxBoostDb);
      expect(gated, lessThan(0.0001)); // below -80 dBFS
    });
  });
}
