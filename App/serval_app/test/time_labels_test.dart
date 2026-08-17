import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/time_labels.dart';

/// The strings the activity column and the transcript carry.
///
/// They are pre-rendered in the models rather than formatted at the use site, because the design
/// writes the same moment differently depending on context — "now" on a scene, "heard just now"
/// on speech. Every case here is one the design actually shows.
void main() {
  // A Tuesday afternoon, so the weekday and meridiem cases are both exercised.
  final now = DateTime(2026, 7, 28, 16, 20, 0);

  group('activity labels', () {
    test('the last minute is “now”', () {
      expect(
        activityTimeLabel(now.subtract(const Duration(seconds: 20)), now: now),
        'now',
      );
    });

    test('speech in the last minute says it was heard', () {
      expect(
        activityTimeLabel(
          now.subtract(const Duration(seconds: 20)),
          now: now,
          heard: true,
        ),
        'heard just now',
      );
    });

    test('the next few minutes count up', () {
      expect(
        activityTimeLabel(now.subtract(const Duration(minutes: 1)), now: now),
        '1 min ago',
      );
      expect(
        activityTimeLabel(now.subtract(const Duration(minutes: 4)), now: now),
        '4 min ago',
      );
    });

    test('past that it is a clock time, as the design’s own capture shows', () {
      // The mock is stamped 4:18 pm and labels a six-minute-old event "4:12 pm" — so the switch
      // is minutes, not an hour, and "37 min ago" is a string this never produces.
      expect(
        activityTimeLabel(DateTime(2026, 7, 28, 16, 14), now: now),
        '4:14 pm',
      );
      expect(
        activityTimeLabel(DateTime(2026, 7, 28, 9, 5), now: now),
        '9:05 am',
      );
    });

    test('yesterday says so', () {
      expect(
        activityTimeLabel(DateTime(2026, 7, 27, 18, 40), now: now),
        '6:40 pm yesterday',
      );
    });

    test('yesterday is a calendar day, not 24 hours', () {
      // 23:50 the previous evening is under 24h ago but is not "today", and calling it 1010 min
      // ago would be useless. The ladder has to break on midnight.
      expect(
        activityTimeLabel(DateTime(2026, 7, 27, 23, 50), now: now),
        '11:50 pm yesterday',
      );
    });

    test('older than that carries a date', () {
      expect(
        activityTimeLabel(DateTime(2026, 7, 25, 18, 40), now: now),
        '6:40 pm · 25 Jul',
      );
    });
  });

  group('clocks', () {
    test('noon and midnight read as 12, not 0', () {
      expect(clockLabel(DateTime(2026, 7, 28, 12, 5)), '12:05 pm');
      expect(clockLabel(DateTime(2026, 7, 28, 0, 5)), '12:05 am');
    });

    test('the transcript carries seconds, since turns are close together', () {
      expect(preciseClockLabel(DateTime(2026, 7, 28, 16, 18, 7)), '4:18:07 pm');
    });

    test('the video stamp is the design’s, weekday and all', () {
      expect(
        stampLabel(DateTime(2024, 7, 30, 16, 18, 7)),
        'Tue 30 Jul · 4:18:07 pm',
      );
    });
  });

  group('a clip’s length', () {
    test('is written as a player writes it, seconds and all', () {
      // Not `spanLabel`, which is minute-granular and would render every clip worth keeping as
      // "0 min" — the two measure different things and both are needed.
      expect(clipLengthLabel(const Duration(seconds: 55)), '0:55');
      expect(clipLengthLabel(const Duration(minutes: 2, seconds: 10)), '2:10');
      expect(clipLengthLabel(const Duration(minutes: 3, seconds: 41)), '3:41');
      expect(
        clipLengthLabel(const Duration(hours: 1, minutes: 2, seconds: 10)),
        '1:02:10',
      );
    });

    test('reads as a duration inside a sentence', () {
      // "Save these 0:55…" reads as a timestamp; a colon is right beside a progress bar and wrong
      // in prose.
      expect(clipSpokenLabel(const Duration(seconds: 55)), '55 s');
      expect(clipSpokenLabel(const Duration(seconds: 4)), '4 s');
      expect(clipSpokenLabel(const Duration(minutes: 2)), '2 min');
      expect(
        clipSpokenLabel(const Duration(minutes: 2, seconds: 10)),
        '2 min 10 s',
      );
      expect(clipSpokenLabel(const Duration(hours: 1)), '1 h');
      expect(
        clipSpokenLabel(const Duration(hours: 1, minutes: 5)),
        '1 h 5 min',
      );
    });
  });

  group('which bucket a saved clip goes in', () {
    // Fixed rather than `DateTime.now()`, because the whole function is about the distance between
    // two instants and a test that supplied only one of them would be testing today.
    final today = DateTime(2026, 8, 9, 14, 0);

    test('the last week is named by how long ago', () {
      expect(clipGroupLabel(today, today), 'Today');
      expect(
        clipGroupLabel(today.subtract(const Duration(days: 1)), today),
        'Yesterday',
      );
      expect(
        clipGroupLabel(today.subtract(const Duration(days: 3)), today),
        'This week',
      );
      expect(
        clipGroupLabel(today.subtract(const Duration(days: 6)), today),
        'This week',
      );
    });

    test('past the week it is named by month', () {
      // Coarser than a heading per day on purpose: a library goes back years, and a day-by-day
      // grouping would be mostly headings.
      expect(clipGroupLabel(DateTime(2026, 8, 1), today), 'Earlier in August');
      expect(clipGroupLabel(DateTime(2026, 6, 12), today), 'June');
    });

    test('a different year carries the year', () {
      // "Earlier in July" in September would read as this year's July with no year on it.
      expect(clipGroupLabel(DateTime(2025, 8, 20), today), 'August 2025');
      expect(clipGroupLabel(DateTime(2024, 12, 3), today), 'December 2024');
    });

    test('the day is what counts, not the hour', () {
      // Grouped by when the thing happened, and a clip from this morning is still Today at 2pm.
      expect(clipGroupLabel(DateTime(2026, 8, 9, 0, 30), today), 'Today');
      expect(clipGroupLabel(DateTime(2026, 8, 8, 23, 30), today), 'Yesterday');
    });
  });

  group('the “Right now” section', () {
    test('holds exactly the rows whose label is still relative', () {
      // One rule, not two: everything under *Right now* reads "now" or "N min ago", everything
      // below carries a clock. A split that could drift from the labels would eventually put a
      // row saying "4:12 pm" under a heading saying it just happened.
      for (final minutes in [0, 1, 4, 6, 30]) {
        final at = now.subtract(Duration(minutes: minutes));
        final label = activityTimeLabel(at, now: now);
        expect(
          isRecent(at, now: now),
          label == 'now' || label.endsWith('min ago'),
          reason: 'a row labelled “$label” should agree with its section',
        );
      }
    });
  });
}
