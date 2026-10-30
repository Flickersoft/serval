// Where the dashes of a broken outline fall.
//
// `dashedRRectPath` lays a rounded rectangle's dashes out from its geometry rather than by walking
// the rectangle's `PathMetric`, and the claim that makes that safe is that the two agree: a dash
// begins and ends at the same points on the outline either way. The metric is the reference here
// for exactly that reason — it is asked where a distance along the outline lands, which is the
// question `extractPath` was answering one dash at a time.
//
// Both sides are measured magnified. A metric measures a curve by flattening it to within half a
// pixel, which on a 6px corner is two chords and a quarter of a pixel short — an error the size of
// the thing being checked. At [_scale] the same half pixel is a sixty-fourth of one.
import 'dart:typed_data';
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/widgets/dashed_border.dart';

const _scale = 32.0;

/// One metric per contour of [path], each measured at [_scale].
List<PathMetric> _measured(Path path) => path
    .transform(
      Float64List(16)
        ..[0] = _scale
        ..[5] = _scale
        ..[10] = 1
        ..[15] = 1,
    )
    .computeMetrics()
    .toList();

Offset _at(PathMetric magnified, double distance) =>
    magnified.getTangentForOffset(distance * _scale)!.position / _scale;

double _lengthOf(PathMetric magnified) => magnified.length / _scale;

void _expectDashesOn(
  Rect rect,
  double radius, {
  required double dash,
  required double gap,
}) {
  final outline = _measured(
    Path()..addRRect(RRect.fromRectAndRadius(rect, Radius.circular(radius))),
  ).single;
  final length = _lengthOf(outline);

  final dashes = _measured(dashedRRectPath(rect, radius, dash: dash, gap: gap));

  // One contour per dash, however many sides and corners it crosses.
  expect(dashes.length, (length / (dash + gap)).ceil());

  for (final (index, drawn) in dashes.indexed) {
    final start = index * (dash + gap);
    final end = (start + dash).clamp(0.0, length);
    final reason = 'dash $index of ${dashes.length} on $rect r=$radius';

    expect(
      (_at(drawn, 0) - _at(outline, start)).distance,
      lessThan(0.05),
      reason: '$reason starts in the wrong place',
    );
    expect(
      (_at(drawn, _lengthOf(drawn)) - _at(outline, end)).distance,
      lessThan(0.05),
      reason: '$reason ends in the wrong place',
    );
    expect(
      _lengthOf(drawn),
      closeTo(end - start, 0.05),
      reason: '$reason is the wrong length',
    );
  }
}

void main() {
  test('an offline tile gets the dashes its outline measures to', () {
    _expectDashesOn(const Rect.fromLTWH(0, 0, 640, 360), 8, dash: 5, gap: 4);
  });

  test('a chip gets them too, where a corner is longer than a dash', () {
    _expectDashesOn(const Rect.fromLTWH(0, 0, 86, 24), 6, dash: 4, gap: 3);
  });

  test('a detection box inset by half its stroke', () {
    _expectDashesOn(
      const Rect.fromLTWH(0.75, 0.75, 38.5, 61.5),
      4.25,
      dash: 4,
      gap: 3,
    );
  });

  test('a pill, whose ends are all corner', () {
    _expectDashesOn(const Rect.fromLTWH(0, 0, 120, 20), 10, dash: 4, gap: 3);
  });

  test('a radius the rectangle cannot hold is fitted to it', () {
    _expectDashesOn(const Rect.fromLTWH(0, 0, 40, 16), 30, dash: 4, gap: 3);
  });

  test('square corners', () {
    _expectDashesOn(const Rect.fromLTWH(10, 20, 200, 100), 0, dash: 5, gap: 4);
  });

  test('nothing to outline draws nothing', () {
    expect(
      dashedRRectPath(Rect.zero, 8, dash: 4, gap: 3).computeMetrics(),
      isEmpty,
    );
  });
}
