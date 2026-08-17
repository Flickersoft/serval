import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/audio_levels_socket.dart';
import 'package:serval_app/widgets/audio_level_meter.dart';

/// The meter updates about ten times a second, which is the whole reason it rides a
/// [ValueListenable] instead of `setState`.
///
/// That is easy to regress and impossible to see: a meter wired through the settings form's state
/// still *works*, it just relayouts a dense two-column form ten times a second for as long as the
/// panel is open. So the isolation is pinned rather than left to convention.
void main() {
  AudioLevel reading(double rms) => AudioLevel(
    rms: rms,
    peak: rms * 2,
    speechThreshold: 0.0015,
    soundThreshold: 0.01,
    speechGateOpen: rms > 0.0015,
    soundGateOpen: false,
  );

  testWidgets('a stream of readings does not rebuild anything above the rail', (
    tester,
  ) async {
    final feed = ValueNotifier<AudioLevel?>(null);
    addTearDown(feed.dispose);

    var ancestorBuilds = 0;

    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Builder(
          builder: (context) {
            ancestorBuilds++;
            return SizedBox(
              width: 400,
              child: AudioLevelMeter(level: feed, threshold: 0.0015),
            );
          },
        ),
      ),
    );

    expect(ancestorBuilds, 1);

    for (var i = 1; i <= 10; i++) {
      feed.value = reading(i * 0.001);
      await tester.pump();
    }

    expect(ancestorBuilds, 1, reason: 'the readings must not escape the rail');
  });

  testWidgets('a null feed renders the no-Server state rather than nothing', (
    tester,
  ) async {
    await tester.pumpWidget(
      const Directionality(
        textDirection: TextDirection.ltr,
        child: SizedBox(
          width: 400,
          child: AudioLevelMeter(level: null, threshold: 0.0015),
        ),
      ),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('no live level'), findsOneWidget);
  });

  /// A dropped feed nulls its value rather than freezing on the last reading, because a frozen
  /// bar is indistinguishable from a silent room — the confusion this meter exists to remove.
  testWidgets(
    'a feed that drops goes blank rather than holding its last reading',
    (tester) async {
      final feed = ValueNotifier<AudioLevel?>(reading(0.01));
      addTearDown(feed.dispose);

      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: SizedBox(
            width: 400,
            child: AudioLevelMeter(level: feed, threshold: 0.0015),
          ),
        ),
      );

      expect(find.text('no live level'), findsNothing);

      feed.value = null;
      await tester.pump();

      expect(find.text('no live level'), findsOneWidget);
    },
  );
}
