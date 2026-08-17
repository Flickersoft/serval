// The strings the storage pages print.
//
// Its own test for the same reason `time_labels_test.dart` is: these are pre-rendered figures the
// design is specific about, and `1.8 TB of 4 TB` is a sentence the mockup wrote before there was
// any endpoint to fill it in. Getting the unit convention wrong here would not crash anything — it
// would quietly report a 4 TB pool as 3.6 TB, which reads as 400 GB of missing disk to whoever owns
// it.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/byte_labels.dart';

void main() {
  group('formatBytes', () {
    test('renders the design’s own figures', () {
      expect(formatBytes(1800787030016), '1.8 TB');
      expect(formatBytes(4000787030016), '4 TB');
      expect(formatBytes(412000000000), '412 GB');
    });

    /// Decimal, not binary — what the drive was sold as, and what `df --si` and the NAS agree on.
    /// Pinned so nobody "fixes" it to 2^40 later.
    test('a terabyte is ten to the twelve, not two to the forty', () {
      expect(formatBytes(1000000000000), '1 TB');
      expect(formatBytes(1099511627776), '1.1 TB');
    });

    test('drops the decimal once the figure no longer needs one', () {
      // 1.8 TB to 1.9 TB is 100 GB and worth showing. 412.3 GB to 412.4 GB is noise on a number
      // that moves every minute.
      expect(formatBytes(9900000000), '9.9 GB');
      expect(formatBytes(12300000000), '12 GB');
    });

    test('bytes are never fractional and never scaled away', () {
      expect(formatBytes(0), '0 bytes');
      expect(formatBytes(1), '1 bytes');
      expect(formatBytes(940), '940 bytes');
      expect(formatBytes(999), '999 bytes');
      expect(formatBytes(1000), '1 KB');
      expect(formatBytes(1500), '1.5 KB');
    });

    test('a figure the Server did not measure is a dash, not a zero', () {
      // The distinction the whole payload exists to carry. "0 bytes" is a measurement; this is
      // the absence of one.
      expect(formatBytes(null), kNoFigure);
      expect(formatBytes(-1), kNoFigure);
    });
  });

  group('the derived figures', () {
    test('a write rate carries its unit', () {
      expect(formatBytesPerDay(58857142857), '59 GB/day');
      expect(formatBytesPerDay(null), kNoFigure);
    });

    test('percentages are whole', () {
      expect(formatPercent(41.2), '41%');
      expect(formatPercent(41.6), '42%');
      expect(formatPercent(0), '0%');
      expect(formatPercent(null), kNoFigure);
    });

    test(
      'a span is coarse, because it is only there to give a byte count meaning',
      () {
        expect(formatSpan(const Duration(days: 7)), '7 days');
        expect(formatSpan(const Duration(days: 1)), '1 day');
        expect(formatSpan(const Duration(days: 6, hours: 22)), '6 days');
        expect(formatSpan(const Duration(hours: 14)), '14 hours');
        expect(formatSpan(const Duration(hours: 1)), '1 hour');
        expect(formatSpan(const Duration(minutes: 36)), '36 minutes');
        expect(formatSpan(const Duration(minutes: 1)), '1 minute');
        expect(formatSpan(null), kNoFigure);
        expect(formatSpan(const Duration(days: -1)), kNoFigure);
      },
    );

    test('uptime reads as the design writes it', () {
      expect(
        formatUptime(const Duration(days: 4).inSeconds.toDouble()),
        'up 4 days',
      );
      expect(formatUptime(null), kNoFigure);
    });
  });
}
