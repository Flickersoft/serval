import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/ptz.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// A pan/tilt direction, as the velocities the Server's ONVIF endpoint takes.
enum PtzDirection {
  up(pan: 0, tilt: 1),
  down(pan: 0, tilt: -1),
  left(pan: -1, tilt: 0),
  right(pan: 1, tilt: 0);

  const PtzDirection({required this.pan, required this.tilt});

  /// Positive pan is right, positive tilt is up. Clamped Server-side to
  /// [-1, 1].
  final double pan;
  final double tilt;
}

/// The 3x3 pan/tilt pad, sat over the top-right of the video.
///
/// Present only on the single-camera view. The wall has no pan/tilt at all —
/// the design routes an alert into this screen instead, so you only ever drive
/// a camera you are actually watching.
///
/// **Hold to move.** Every ONVIF move carries a 1 s auto-stop timeout, so
/// holding a direction has to re-send `POST /ptz/move` faster than that or the
/// camera halts mid-travel. This widget owns that repeat and reports the press
/// and release; the caller does the HTTP.
class PtzPad extends StatelessWidget {
  const PtzPad({
    super.key,
    this.onMove,
    this.onStop,
    this.onHome,
    this.homeTooltip,
    this.repeatInterval = const Duration(milliseconds: 400),
  });

  /// Called on press and then repeatedly while held.
  final ValueChanged<PtzDirection>? onMove;

  /// Called on release — `POST /ptz/stop`.
  final VoidCallback? onStop;

  /// The centre key. Null when the camera has nothing to recall — no ONVIF home position and no
  /// stored presets — and the pad then draws an empty cell rather than a key that does nothing.
  final VoidCallback? onHome;

  /// What the centre key is, for a camera whose home is a named preset rather than a real ONVIF
  /// home position. Null falls back to the crosshair's own meaning.
  final String? homeTooltip;

  /// Comfortably inside the Server's 1 s ONVIF auto-stop.
  final Duration repeatInterval;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(6),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.bg, 78),
      borderRadius: BorderRadius.circular(9),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        _row([null, PtzDirection.up, null]),
        const SizedBox(height: 3),
        _row([PtzDirection.left, null, PtzDirection.right], home: true),
        const SizedBox(height: 3),
        _row([null, PtzDirection.down, null]),
      ],
    ),
  );

  Widget _row(List<PtzDirection?> cells, {bool home = false}) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      for (var i = 0; i < cells.length; i++) ...[
        if (i > 0) const SizedBox(width: 3),
        if (cells[i] case final direction?)
          _PtzKey(
            direction: direction,
            onMove: onMove,
            onStop: onStop,
            repeatInterval: repeatInterval,
          )
        // `onHome != null` as well as the position: `_HomeKey` paints the accent crosshair
        // whether or not it has a callback, so without this a camera with no home position and no
        // presets draws a key that looks live and does nothing.
        else if (home && i == 1 && onHome != null)
          _HomeKey(onTap: onHome, tooltip: homeTooltip)
        else
          const SizedBox(width: 34, height: 34),
      ],
    ],
  );
}

class _PtzKey extends StatefulWidget {
  const _PtzKey({
    required this.direction,
    required this.repeatInterval,
    this.onMove,
    this.onStop,
  });

  final PtzDirection direction;
  final Duration repeatInterval;
  final ValueChanged<PtzDirection>? onMove;
  final VoidCallback? onStop;

  @override
  State<_PtzKey> createState() => _PtzKeyState();
}

class _PtzKeyState extends State<_PtzKey> {
  Timer? _repeat;
  bool _hovered = false;
  bool _pressed = false;

  void _start() {
    setState(() => _pressed = true);
    widget.onMove?.call(widget.direction);
    _repeat = Timer.periodic(
      widget.repeatInterval,
      (_) => widget.onMove?.call(widget.direction),
    );
  }

  void _end() {
    _repeat?.cancel();
    _repeat = null;
    if (mounted) setState(() => _pressed = false);
    widget.onStop?.call();
  }

  @override
  void dispose() {
    _repeat?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTapDown: (_) => _start(),
      onTapUp: (_) => _end(),
      onTapCancel: _end,
      child: Container(
        width: 34,
        height: 34,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: Nocturne.mix(
            Nocturne.text,
            _pressed
                ? 16
                : _hovered
                ? 11
                : 7,
          ),
          borderRadius: BorderRadius.circular(6),
        ),
        child: PhosphorIcon(_glyph, size: 16, color: Nocturne.text),
      ),
    ),
  );

  PhosphorIconData get _glyph => switch (widget.direction) {
    PtzDirection.up => PhosphorIconsRegular.caretUp,
    PtzDirection.down => PhosphorIconsRegular.caretDown,
    PtzDirection.left => PhosphorIconsRegular.caretLeft,
    PtzDirection.right => PhosphorIconsRegular.caretRight,
  };
}

/// The center key — an accent outline rather than a filled cell, so the pad
/// reads as four directions around one recall.
class _HomeKey extends StatelessWidget {
  const _HomeKey({this.onTap, this.tooltip});

  final VoidCallback? onTap;

  /// Names the position on a camera whose home is a stored preset — the crosshair alone reads as
  /// "centre", which is not what recalling "Gate" does.
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final key = MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          width: 34,
          height: 34,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: Nocturne.mix(Nocturne.accent, 50)),
          ),
          child: const PhosphorIcon(
            PhosphorIconsRegular.crosshairSimple,
            size: 13,
            color: Nocturne.accent400,
          ),
        ),
      ),
    );

    return tooltip == null ? key : Tooltip(message: tooltip!, child: key);
  }
}

/// The zoom column beside the pad: in, a vertical track, out.
///
/// The track is **linear over the lens's travel**, and is not a magnification. ONVIF's generic zoom
/// space is a fraction between the wide and tight ends; nothing in ONVIF publishes the optical
/// range that would turn 0.4 into `2.4x`, and the curve between the two is vendor-specific. This
/// was drawn logarithmically over a hard-coded 1x..8x, which claimed an eightfold lens for every
/// camera and put the knob at a place derived from a factor no camera had reported.
///
/// The readout follows [ZoomPosition.measured]: a percentage of travel where the camera reports
/// its position, and nothing at all where the number is only our own dead reckoning. Drawing the
/// same figure in both cases is the thing that would make it a lie.
class ZoomControl extends StatelessWidget {
  const ZoomControl({super.key, required this.zoom, this.onChanged});

  final ZoomPosition zoom;
  final ValueChanged<double>? onChanged;

  static const _trackHeight = 74.0;
  static const _knobSize = 9.0;

  /// Turns a tap or drag inside the track into a position, 1 at the top so it agrees with the
  /// magnifier glyphs bracketing it.
  ///
  /// The knob's own height comes out of the travel, so dropping the pointer at the very top or
  /// bottom reaches the ends exactly rather than stopping half a knob short.
  void _emit(double localY) {
    final travel = _trackHeight - _knobSize;
    final position = 1 - ((localY - _knobSize / 2) / travel);
    onChanged?.call(position.clamp(0.0, 1.0));
  }

  @override
  Widget build(BuildContext context) {
    final knobTop =
        (1 - zoom.value.clamp(0.0, 1.0)) * (_trackHeight - _knobSize);
    final label = zoom.label;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 8),
      decoration: BoxDecoration(
        color: Nocturne.mix(Nocturne.bg, 78),
        borderRadius: BorderRadius.circular(9),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const PhosphorIcon(
            PhosphorIconsRegular.magnifyingGlassPlus,
            size: 15,
            color: Nocturne.text,
          ),
          const SizedBox(height: 4),
          // The 3px track is far too thin to hit, so the gesture is taken on a box the width of
          // the knob and the drag is tracked rather than only its start — a zoom drag runs the
          // length of the column and would otherwise be lost the moment it left the stripe.
          GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapDown: (details) => _emit(details.localPosition.dy),
            onVerticalDragStart: (details) => _emit(details.localPosition.dy),
            onVerticalDragUpdate: (details) => _emit(details.localPosition.dy),
            child: SizedBox(
              width: _knobSize,
              height: _trackHeight,
              child: Stack(
                alignment: Alignment.topCenter,
                clipBehavior: Clip.none,
                children: [
                  Container(
                    width: 3,
                    height: _trackHeight,
                    decoration: BoxDecoration(
                      color: Nocturne.mix(Nocturne.text, 14),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                  Positioned(
                    top: knobTop,
                    child: Container(
                      width: _knobSize,
                      height: _knobSize,
                      decoration: const BoxDecoration(
                        color: Nocturne.accent,
                        shape: BoxShape.circle,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 4),
          const PhosphorIcon(
            PhosphorIconsRegular.magnifyingGlassMinus,
            size: 15,
            color: Nocturne.text,
          ),
          // A percentage of the lens's travel, and only where the camera reported one. On a camera
          // that reports nothing the row is left out entirely rather than shown empty: the track
          // still says where you put the knob, which is all anybody knows.
          if (label != null) ...[
            const SizedBox(height: 5),
            Text(
              label,
              style: monoStyle(
                fontSize: 9.5,
                color: Nocturne.mix(Nocturne.text, 55),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// The detection box drawn over the video, with its label sat above it.
///
/// Fed by the object detector through `ServalRepository.detectionsFor` over the
/// live view and `detectionsAt` over replay. Which of those is asked is the
/// caller's business; this only knows where to put a rectangle.
///
/// [Serval.alert] unconditionally, and that is exact rather than convenient:
/// both of those sources hand back only alert episodes, so every box that gets
/// here is a claim that someone should look. A car or a sub-threshold person is
/// stored and never reaches this widget.
class DetectionOverlay extends StatelessWidget {
  const DetectionOverlay({
    super.key,
    required this.label,
    required this.rect,
    this.isAlert = true,
    this.isStale = false,
  });

  final String label;

  /// Fractions of the frame, so the box tracks the video at any size.
  final Rect rect;

  /// Orange when true, the Nocturne accent when not.
  ///
  /// Orange means one thing here and it is not "an object": it is a claim that someone should
  /// look. A camera showing every detection draws the rest in the accent, so a car on the drive
  /// and a person at the door stay told apart at a glance rather than becoming the same alarm.
  final bool isAlert;

  /// Dotted when true: the last position known rather than one seen in this frame.
  ///
  /// A second channel, carried by the outline rather than by the colour, so it says nothing about
  /// severity — a dotted alert is still orange. Dashes read as provisional without needing to be
  /// explained, which is what makes them able to say "it was here" where a solid box could only
  /// say "it is here" or say nothing.
  final bool isStale;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final left = rect.left * constraints.maxWidth;
      final top = rect.top * constraints.maxHeight;
      final colour = isAlert ? Serval.alert : Nocturne.accent;
      return Stack(
        children: [
          Positioned(
            left: left,
            top: top,
            width: rect.width * constraints.maxWidth,
            height: rect.height * constraints.maxHeight,
            child: isStale
                ? CustomPaint(painter: _DashedBox(colour))
                : DecoratedBox(
                    decoration: BoxDecoration(
                      border: Border.all(color: colour, width: 1.5),
                      borderRadius: BorderRadius.circular(5),
                    ),
                  ),
          ),
          Positioned(
            left: left,
            top: top - 21,
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
              decoration: BoxDecoration(
                color: colour,
                borderRadius: BorderRadius.circular(3),
              ),
              child: Text(
                label,
                style: monoStyle(
                  fontSize: 10.5,
                  letterSpacing: 0.08 * 10.5,
                  color: Nocturne.bg,
                ),
              ),
            ),
          ),
        ],
      );
    },
  );
}

/// The dotted outline of a box drawn where something was last known to be.
///
/// A painter rather than a `Border`, because Flutter's borders are solid and there is no dash
/// anywhere else in the app to reach for. The shape is the same rounded rectangle the solid box
/// uses, walked with [Path.computeMetrics] and stroked in `_dash`-long pieces — so the two states
/// differ in exactly one property and a box does not appear to move or resize when it goes stale.
class _DashedBox extends CustomPainter {
  const _DashedBox(this.colour);

  final Color colour;

  /// Short dashes with a gap of the same order, which reads as provisional at the sizes these
  /// boxes actually are: a distant subject is a few dozen pixels tall and a coarser pattern would
  /// resolve into two or three marks that no longer look like an outline.
  static const _dash = 4.0;
  static const _gap = 3.0;

  @override
  void paint(Canvas canvas, Size size) {
    final outline = Path()
      ..addRRect(
        RRect.fromRectAndRadius(
          // Inset by half the stroke so the dashes sit where the solid border's 1.5px sits, rather
          // than straddling the edge and reading as a size change.
          Offset.zero & size,
          const Radius.circular(5),
        ).deflate(0.75),
      );

    final paint = Paint()
      ..color = colour
      ..strokeWidth = 1.5
      ..style = PaintingStyle.stroke;

    for (final metric in outline.computeMetrics()) {
      for (var at = 0.0; at < metric.length; at += _dash + _gap) {
        canvas.drawPath(
          metric.extractPath(at, math.min(at + _dash, metric.length)),
          paint,
        );
      }
    }
  }

  @override
  bool shouldRepaint(_DashedBox old) => old.colour != colour;
}
