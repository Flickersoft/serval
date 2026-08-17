import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/time_labels.dart';
import 'package:serval_app/models/timeline.dart';

/// The scrubber's tick labels, derived rather than written down.
///
/// The first two cases are the point of this file: against the design's own capture time, the
/// derived labels come out *identical* to the strings the scaffold had hard-coded. That is what
/// says the rounding rule is the design's rule and not one invented to replace it.
void main() {
  final anchor =
      SampleServalRepository.capturedAt; // Tue 30 Jul 2024, 4:18:07 pm

  List<String> labelsOver(Duration span) => [
    for (final (_, label) in timelineTicks(anchor.subtract(span), anchor))
      label,
  ];

  test('the 12 h grid reads as the design writes it', () {
    expect(labelsOver(TimelineRange.halfDay.duration), [
      '6 am',
      '9 am',
      '12 pm',
      '3 pm',
    ]);
  });

  test('the 24 h grid reads as the design writes it', () {
    expect(labelsOver(TimelineRange.day.duration), [
      '6 pm',
      '12 am',
      '6 am',
      '12 pm',
    ]);
  });

  test('the 1 h grid steps by a quarter hour, on the quarter hour', () {
    // The scaffold's strings here were 3:20/3:35/3:50/4:05 — the same fifteen-minute step, but
    // measured off an unrounded anchor. Rounded is the one that makes two ranges comparable.
    expect(labelsOver(TimelineRange.hour.duration), [
      '3:30 pm',
      '3:45 pm',
      '4:00 pm',
      '4:15 pm',
    ]);
  });

  test('ticks land on their labels', () {
    for (final (at, label) in timelineTicks(
      anchor.subtract(const Duration(hours: 12)),
      anchor,
    )) {
      expect(hourLabel(at), label);
      expect(at.minute, 0);
      expect(at.hour % 3, 0, reason: 'a 12 h window steps by three hours');
    }
  });

  test('both edges are excluded', () {
    // The left one would be clipped by the track's own inset; the right one is where the scrubber
    // writes "now".
    final ticks = timelineTicks(
      DateTime(2026, 7, 24, 6),
      DateTime(2026, 7, 24, 18),
    );
    expect(ticks.first.$1, DateTime(2026, 7, 24, 9));
    expect(ticks.last.$1, DateTime(2026, 7, 24, 15));
  });

  test('midnight and noon are 12 am and 12 pm', () {
    expect(hourLabel(DateTime(2026, 7, 24)), '12 am');
    expect(hourLabel(DateTime(2026, 7, 24, 12)), '12 pm');
  });

  test(
    'a window shorter than one step yields nothing rather than crashing',
    () {
      final at = DateTime(2026, 7, 24, 10, 1);
      expect(timelineTicks(at, at.add(const Duration(seconds: 30))), isEmpty);
      expect(timelineTicks(at, at), isEmpty);
      expect(timelineTicks(at, at.subtract(const Duration(hours: 1))), isEmpty);
    },
  );

  test('a narrow window steps in minutes rather than going bare', () {
    // The shape a row in the feed opens on, and it spends its first half hour under the
    // quarter-hour rung — which over five minutes is one label or none.
    final at = DateTime(2026, 7, 24, 10, 1);

    expect(timelineTicks(at, at.add(const Duration(minutes: 4))), [
      (DateTime(2026, 7, 24, 10, 2), '10:02 am'),
      (DateTime(2026, 7, 24, 10, 3), '10:03 am'),
      (DateTime(2026, 7, 24, 10, 4), '10:04 am'),
    ]);

    expect(timelineTicks(at, at.add(const Duration(minutes: 18))), [
      (DateTime(2026, 7, 24, 10, 5), '10:05 am'),
      (DateTime(2026, 7, 24, 10, 10), '10:10 am'),
      (DateTime(2026, 7, 24, 10, 15), '10:15 am'),
    ]);
  });
}
