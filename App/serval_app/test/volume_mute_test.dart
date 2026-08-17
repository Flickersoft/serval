// The speaker glyph on the volume pill is the mute button, and the track beside it is the whole
// volume — attenuation below the unity mark, amplification above it.
//
// Muting and the level are distinct settings sharing one pill: muting stops the camera's audio
// arriving at all, the level scales what does. So the glyph has to report mute rather than drive
// the level to nothing, and the level has to stay reachable while muted.
import 'package:flutter/material.dart' show MaterialApp;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/playback/playback_volume.dart';
import 'package:serval_app/widgets/volume_control.dart';

void main() {
  /// The track is private, so it is found by the tooltip it wears rather than by type — which is
  /// also the string a reader of the control sees, so a change to one should fail the other.
  const trackTooltip = 'Volume. Past the mark this camera is amplified.';

  /// The pill's own track width, from [VolumeControl]. The detent is measured in pixels, so a test
  /// that aims at the mark has to know how wide the thing it is aiming at is.
  const trackWidth = 170.0;

  /// The pill on its own, with the state a screen would hold for it. `MaterialApp` for the
  /// `Overlay` the tooltip mounts into, and nothing else.
  Widget host({
    required ValueNotifier<double> volume,
    required ValueNotifier<bool> muted,
  }) => MaterialApp(
    home: Center(
      child: ValueListenableBuilder<bool>(
        valueListenable: muted,
        builder: (context, isMuted, _) => VolumeControl(
          volume: volume,
          onChanged: (v) => volume.value = v,
          muted: isMuted,
          onMutedChanged: (v) => muted.value = v,
        ),
      ),
    ),
  );

  /// Taps the track [fromLeft] logical pixels along it.
  Future<void> tapTrack(WidgetTester tester, double fromLeft) async {
    final track = find.byTooltip(trackTooltip);
    await tester.tapAt(tester.getTopLeft(track) + Offset(fromLeft, 11));
    await tester.pumpAndSettle();
  }

  testWidgets('the speaker glyph mutes and unmutes', (tester) async {
    final volume = ValueNotifier(0.8);
    final muted = ValueNotifier(false);
    addTearDown(volume.dispose);
    addTearDown(muted.dispose);

    await tester.pumpWidget(host(volume: volume, muted: muted));

    await tester.tap(find.byTooltip('Mute'));
    await tester.pumpAndSettle();

    expect(muted.value, isTrue);
    // Muting is not the level being turned down — the level is what you go back to.
    expect(volume.value, 0.8);

    await tester.tap(find.byTooltip('Unmute'));
    await tester.pumpAndSettle();

    expect(muted.value, isFalse);
    expect(volume.value, 0.8);
  });

  testWidgets('reaching for the level while muted unmutes', (tester) async {
    final volume = ValueNotifier(0.8);
    final muted = ValueNotifier(true);
    addTearDown(volume.dispose);
    addTearDown(muted.dispose);

    await tester.pumpWidget(host(volume: volume, muted: muted));

    // Anywhere on the track: reaching for the level is itself a statement that you want to hear
    // something, and a slider that silently did nothing would be a dead control.
    await tapTrack(tester, trackWidth / 2);

    expect(muted.value, isFalse);
    expect(volume.value, closeTo(0.5, 0.05));
  });

  testWidgets('the unity mark can be hit exactly', (tester) async {
    final volume = ValueNotifier(0.2);
    final muted = ValueNotifier(false);
    addTearDown(volume.dispose);
    addTearDown(muted.dispose);

    await tester.pumpWidget(host(volume: volume, muted: muted));

    // Aimed three quarters along the visible track, which is where anyone reading the mark aims. The
    // knob rides inside the rail, so the mark is a few pixels short of that — the detent is what
    // covers the difference. Without it this lands a point or two off, and the one marked place on
    // the track is unreachable.
    await tapTrack(tester, unityTravel * trackWidth);

    expect(volume.value, unityTravel);
    expect(find.text('75%'), findsOneWidget);
  });

  testWidgets('the detent does not swallow the rest of the track', (
    tester,
  ) async {
    final volume = ValueNotifier(0.2);
    final muted = ValueNotifier(false);
    addTearDown(volume.dispose);
    addTearDown(muted.dispose);

    await tester.pumpWidget(host(volume: volume, muted: muted));

    // Well clear of the mark. A detent wide enough to catch this would make the bottom of the
    // amplifying range unreachable.
    await tapTrack(tester, unityTravel * trackWidth + 12);

    expect(volume.value, greaterThan(unityTravel));
    expect(volume.value, isNot(unityTravel));
  });

  testWidgets('the amplifying quarter reaches the ceiling', (tester) async {
    final volume = ValueNotifier(unityTravel);
    final muted = ValueNotifier(false);
    addTearDown(volume.dispose);
    addTearDown(muted.dispose);

    await tester.pumpWidget(host(volume: volume, muted: muted));
    expect(find.text('75%'), findsOneWidget);

    // Dragged off the end rather than tapped on it. Every slider's last pixel is fiddly to hit
    // exactly; carrying on past the edge is how anyone actually asks for all of it, and the clamp
    // is what has to turn that into the ceiling rather than into nothing.
    final topLeft = tester.getTopLeft(find.byTooltip(trackTooltip));
    await tester.dragFrom(
      topLeft + const Offset(trackWidth / 2, 11),
      const Offset(trackWidth, 0),
    );
    await tester.pumpAndSettle();

    expect(volume.value, 1);
    // The top of the track is ten times, and the readout still says 100 — the whole point of showing
    // the position rather than the gain.
    expect(find.text('100%'), findsOneWidget);
    expect(playbackFromTravel(volume.value).db, maxBoostDb);
  });
}
