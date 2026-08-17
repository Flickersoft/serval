// `VitalsHistory.fromJson` against a payload the Server's own serialiser produced.
//
// Same argument `system_stats_test.dart` makes: every other test here builds the model by hand, so
// none of them can catch the Server spelling a key differently from the App. A `fromJson` that
// reads null for a key that does not exist is exactly as quiet as one that works — and a page
// designed to render nulls calmly would ship the rename as "the sparklines stopped drawing", with
// nothing red anywhere.
//
// `fixtures/vitals_history.json` was emitted by `VitalsHistory.From` through the same
// `JsonSerializerDefaults.Web` options ASP.NET serialises with, rather than typed out here. Its
// shape is one description burst — the 11s-on/5s-off duty cycle the GPU investigation found — with
// a three-sample hole in the GPU series where the driver published nothing. Re-capture it against
// a live Server once the route is deployed:
//
//   curl -s localhost:8080/api/system/stats/history -H "Authorization: Bearer $TOKEN" | jq . \
//     > test/fixtures/vitals_history.json
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/vitals_history.dart';

void main() {
  late VitalsHistory history;

  setUpAll(() {
    final json =
        jsonDecode(File('test/fixtures/vitals_history.json').readAsStringSync())
            as Map<String, dynamic>;
    history = VitalsHistory.fromJson(json);
  });

  test('every series arrives, aligned with its time axis', () {
    expect(history.sampledAt, hasLength(40));

    // The alignment is the whole basis for reading index i of each series as one instant.
    expect(history.cpu, hasLength(history.sampledAt.length));
    expect(history.memory, hasLength(history.sampledAt.length));
    expect(history.gpu, hasLength(history.sampledAt.length));

    // Aligned even here, where the key is absent altogether: this fixture predates the accelerator
    // series, and a series shorter than its own time axis would attribute later readings to the
    // wrong instants rather than fail visibly.
    expect(history.accelerator, hasLength(history.sampledAt.length));

    expect(history.windowMinutes, 60);
    expect(history.unavailableReason, isNull);
  });

  test(
    'a server with no accelerator series gets no chart rather than a flat one',
    () {
      // All null, which is what a host with no accelerator sends and what a Server too old to know
      // about the key sends. Both must reach the same place — no sparkline — because a series of
      // zeroes would draw a confident line along the axis for hardware that is not there.
      expect(history.accelerator.whereType<double>(), isEmpty);
      expect(history.seriesOf(history.accelerator), isNull);
    },
  );

  test('an accelerator series draws where the Server sends one', () {
    final withAccelerator = VitalsHistory.fromJson({
      'sampledAt': [
        '2026-08-11T12:00:00Z',
        '2026-08-11T12:00:05Z',
        '2026-08-11T12:00:10Z',
      ],
      'cpu': [30, 31, 33],
      'memory': [54, 54, 55],
      'gpu': [2, 2, 3],
      'accelerator': [61, null, 58],
      'windowMinutes': 60,
    });

    expect(withAccelerator.accelerator, [61, null, 58]);
    expect(withAccelerator.seriesOf(withAccelerator.accelerator), isNotNull);
  });

  test('an unmeasured sample stays null rather than becoming zero', () {
    // The three-sample hole. This is the assertion the whole feature turns on: a zero here would
    // draw a confident line along the axis claiming the GPU was idle, which is a reading nobody
    // took — and it would be indistinguishable from the genuine 3% idle samples either side.
    expect(history.gpu[12], isNull);
    expect(history.gpu[13], isNull);
    expect(history.gpu[14], isNull);

    expect(history.gpu[11], isNotNull);
    expect(history.gpu[15], isNotNull);
  });

  test('the duty cycle survives the round trip', () {
    // Busy and idle samples stay distinguishable — a parse that flattened or reordered the series
    // would still be the right length and still pass the alignment test above.
    expect(history.gpu.whereType<double>().where((v) => v > 90), isNotEmpty);
    expect(history.gpu.whereType<double>().where((v) => v < 10), isNotEmpty);
    expect(
      history.cpu.whereType<double>().reduce((a, b) => a > b ? a : b),
      58.4,
    );
  });

  test('timestamps are read, not inferred from the cadence', () {
    // The Server sends them precisely so a missed tick does not shift every earlier point. Reading
    // them is what makes that guarantee worth anything.
    final gap = history.sampledAt[1].difference(history.sampledAt[0]);
    expect(gap, const Duration(seconds: 5));
    expect(history.sampledAt.first.isBefore(history.sampledAt.last), isTrue);
  });

  group('seriesOf', () {
    test('offers a series that has readings', () {
      expect(history.seriesOf(history.cpu), isNotNull);
      expect(history.seriesOf(history.gpu), isNotNull);
    });

    test('declines a series of nothing but nulls', () {
      // A host with no amdgpu reports GPU unavailable on every sample. The meter should carry the
      // Server's sentence saying so, not an empty chart frame implying the line is merely offscreen.
      expect(history.seriesOf(List<double?>.filled(40, null)), isNull);
    });

    test('declines when the Server says retention is off', () {
      const off = VitalsHistory(
        unavailableReason: 'History retention is switched off.',
      );

      expect(off.seriesOf(const [12, 14]), isNull);
      expect(
        off.isEmpty,
        isFalse,
        reason: 'a stated reason is not the same as "nothing yet"',
      );
    });

    test('an empty history is not an error', () {
      // A server that started a moment ago. Distinguishing this from a disabled one is why
      // unavailableReason exists alongside the arrays.
      const fresh = VitalsHistory();

      expect(fresh.isEmpty, isTrue);
      expect(fresh.unavailableReason, isNull);
    });
  });

  test('a series longer or shorter than its time axis is clamped, not trusted', () {
    // The Server builds these in one pass each and cannot produce a mismatch, but decoding to
    // "aligned or absent" means a future contract slip shows up as missing readings rather than as
    // readings quietly attributed to the wrong instants.
    final skewed = VitalsHistory.fromJson({
      'sampledAt': ['2026-08-03T22:00:00+00:00', '2026-08-03T22:00:05+00:00'],
      'cpu': [1.0, 2.0, 3.0, 4.0],
      'memory': [5.0],
      'gpu': null,
      'windowMinutes': 60,
    });

    expect(skewed.cpu, [1.0, 2.0]);
    expect(skewed.memory, [5.0, null]);
    expect(skewed.gpu, [null, null]);
  });
}
