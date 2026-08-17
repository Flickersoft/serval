import 'dart:math' show pi;

import 'package:flutter/widgets.dart';

import '../models/camera.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';

/// Paints a camera's stand-in image: diagonal two-tone stripes under a soft
/// radial bloom, with the camera's name and resolution centered in mono.
///
/// This is what a tile shows before its first frame arrives. When the wiring
/// pass connects `WS /api/dashboard`, only the *body* of a tile changes — this
/// stays as the loading and reconnecting state, which is why each camera gets
/// its own stripe and bloom rather than one shared grey.
class TilePlaceholderView extends StatelessWidget {
  const TilePlaceholderView({super.key, required this.placeholder});

  final TilePlaceholder placeholder;

  @override
  Widget build(BuildContext context) =>
      CustomPaint(painter: _StripePainter(placeholder), size: Size.infinite);
}

/// The single-camera view's stage when there is no live picture on it.
///
/// The same stripes as a wall tile, under the design's centered mono label. It is used in two
/// places for one reason: with no Server to signal to there is nothing to negotiate, and with a
/// Server that cannot start the stream there is nothing to show — and in both cases the design's
/// answer is this rather than a black rectangle.
class LivePlaceholderView extends StatelessWidget {
  const LivePlaceholderView({
    super.key,
    required this.camera,
    this.label,
    this.detail,
  });

  final Camera camera;

  /// Overrides the design's `NAME · LIVE · RES`, for the cases that have something more useful
  /// to say — connecting, or why it failed.
  final String? label;

  final String? detail;

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: [
      TilePlaceholderView(placeholder: camera.placeholder),
      // The bare label is laid out exactly as the design has it — no wrapping Column — so the
      // common case lands on the same pixels the mock does. The Column only appears when there
      // is a second line to stack under it.
      Center(child: detail == null ? _label : _stacked),
    ],
  );

  Widget get _label => Text(
    label ??
        '${camera.name.toUpperCase()} · LIVE'
            '${camera.resolutionLabel == null ? '' : ' · ${camera.resolutionLabel}'}',
    style: monoStyle(
      fontSize: 11,
      letterSpacing: 0.16 * 11,
      color: Nocturne.mix(Nocturne.text, 28),
    ),
  );

  Widget get _stacked => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _label,
      const SizedBox(height: 8),
      ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Text(
          detail!,
          textAlign: TextAlign.center,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12.5,
            height: 1.4,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      ),
    ],
  );
}

class _StripePainter extends CustomPainter {
  const _StripePainter(this.placeholder);

  final TilePlaceholder placeholder;

  /// The design's `repeating-linear-gradient(118deg, …)` — 12px bands, so a
  /// 24px period.
  static const _bandWidth = 12.0;
  static const _angleDegrees = 118.0;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    canvas.save();
    canvas.clipRect(rect);

    canvas.drawRect(rect, Paint()..color = placeholder.stripeDark);

    // Rotate about the center and paint bands wide enough to cover the tile at
    // any angle — the diagonal of the rect is the worst case in both axes.
    final diagonal = size.width + size.height;
    canvas
      ..translate(size.width / 2, size.height / 2)
      ..rotate((_angleDegrees - 90) * pi / 180);

    final light = Paint()..color = placeholder.stripeLight;
    for (var x = -diagonal; x < diagonal; x += _bandWidth * 2) {
      canvas.drawRect(
        Rect.fromLTWH(x, -diagonal, _bandWidth, diagonal * 2),
        light,
      );
    }
    canvas.restore();

    // The bloom: the camera's own hue at the top-middle, falling to near-black
    // at the edges, which is what keeps a wall of placeholders from reading as
    // one flat field.
    canvas.drawRect(
      rect,
      Paint()
        ..shader = RadialGradient(
          center: const Alignment(0, -0.3),
          radius: 1.2,
          colors: [placeholder.bloom, const Color(0x80000000)],
        ).createShader(rect),
    );
  }

  @override
  bool shouldRepaint(_StripePainter oldDelegate) =>
      oldDelegate.placeholder != placeholder;
}
