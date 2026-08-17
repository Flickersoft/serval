/// Drawing and editing mask polygons over a camera's still.
///
/// Everything here works in **fractions of the picture**, 0..1, and converts to pixels only at the
/// paint boundary. That is not a convention, it is the storage format — a mask survives the camera
/// being replaced with a sharper one because nothing about it is measured in pixels.
///
/// The interaction is the design's, and the one rule nobody guesses is called out on screen:
/// clicking places points, and *closing is clicking the first point*. It grows a ring and says so
/// when the pointer is near it, because a shape you cannot finish is worse than one you cannot
/// start.
library;

import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/widgets.dart';

import '../data/camera_record.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'frame_size.dart';
import 'mask_preview.dart';
import 'pill.dart';

/// Which tool the pointer is holding.
enum MaskTool {
  /// Clicks place vertices.
  draw('Draw a mask'),

  /// Drags move a vertex, or an edge's midpoint to add one.
  select('Select & move');

  const MaskTool(this.label);

  final String label;
}

/// How close to a point the pointer has to be, as a fraction of the picture, before it counts as
/// on it. A fraction rather than pixels so the target is the same size on any window — 2.2% of a
/// 900px-wide frame is about 20px, which is a comfortable click target.
const double kMaskHitRadius = 0.022;

/// How close to an edge the pointer has to be before that edge is considered clamped to it.
const double kMaskSnapRadius = 0.015;

/// The still, the committed masks, and whatever is being drawn on top of them.
class MaskCanvas extends StatefulWidget {
  const MaskCanvas({
    super.key,
    required this.frame,
    required this.masks,
    required this.draft,
    required this.selected,
    required this.hidden,
    required this.tool,
    required this.snapToEdges,
    required this.onDraftChanged,
    required this.onCommitDraft,
    required this.onMaskChanged,
    required this.onSelect,
  });

  final Uint8List? frame;

  /// The masks already drawn, in the order the inspector lists them.
  final List<DetectionMaskSettings> masks;

  /// The polygon being drawn: a flat `x1, y1, …` list of the points placed so far, still open.
  final List<double> draft;

  /// Which committed mask the inspector is showing, or null.
  final int? selected;

  /// Masks the eye has been taken off. Editor state only — never persisted, because a mask has no
  /// enabled flag and inventing one would mean inventing what the Server does with it.
  final Set<int> hidden;

  final MaskTool tool;
  final bool snapToEdges;

  final ValueChanged<List<double>> onDraftChanged;

  /// The draft closed into a finished polygon.
  final VoidCallback onCommitDraft;

  /// A committed mask whose points moved.
  final void Function(int index, List<double> points) onMaskChanged;

  final ValueChanged<int> onSelect;

  @override
  State<MaskCanvas> createState() => _MaskCanvasState();
}

class _MaskCanvasState extends State<MaskCanvas>
    with FrameSizeReader<MaskCanvas> {
  @override
  void initState() {
    super.initState();
    readFrameSize(widget.frame);
  }

  @override
  void didUpdateWidget(MaskCanvas old) {
    super.didUpdateWidget(old);
    if (!identical(old.frame, widget.frame)) readFrameSize(widget.frame);
  }

  /// Where the pointer is, in fractions, or null when it is off the picture. What draws the
  /// rubber band and what decides whether the first point is offering to close.
  Offset? _cursor;

  /// The vertex being dragged: which mask (null for the draft) and which point.
  ({int? mask, int point})? _dragging;

  /// The picture's rectangle inside this box, which is what fractions are fractions of.
  ///
  /// The image is drawn `contain`ed, so unless the box happens to share the still's aspect ratio
  /// there are bars down two sides — and a fraction measured against the box rather than the
  /// picture lands a mask somewhere it was never drawn.
  Rect _picture = Rect.zero;

  Offset _toFraction(Offset local) {
    if (_picture.isEmpty) return Offset.zero;
    return Offset(
      ((local.dx - _picture.left) / _picture.width).clamp(0.0, 1.0),
      ((local.dy - _picture.top) / _picture.height).clamp(0.0, 1.0),
    );
  }

  Offset _toPixels(Offset fraction) => Offset(
    _picture.left + (fraction.dx * _picture.width),
    _picture.top + (fraction.dy * _picture.height),
  );

  /// Clamped to the frame's edge when it is nearly there, so a mask meant to run off the picture
  /// actually does rather than leaving a sliver the detector still watches.
  Offset _snapped(Offset fraction) {
    if (!widget.snapToEdges) return fraction;

    double snap(double value) {
      if (value < kMaskSnapRadius) return 0;
      if (value > 1 - kMaskSnapRadius) return 1;
      return value;
    }

    return Offset(snap(fraction.dx), snap(fraction.dy));
  }

  /// Whether a point closes the shape: near the first vertex, with at least three placed.
  ///
  /// Three, because two cannot enclose anything and offering to close a shape the Server will
  /// refuse is worse than not offering.
  ///
  /// Takes the point rather than reading [_cursor], and that matters: a tap carries no hover, so
  /// deciding this from the cursor would make a polygon impossible to finish with a finger. The
  /// cursor drives the ring and the rubber band — what is drawn — and never what a click does.
  bool _closes(Offset point) =>
      widget.draft.length >= 6 &&
      _near(point, Offset(widget.draft[0], widget.draft[1]));

  /// True when the shape is offering to close under the pointer, which is what draws the ring.
  bool get _closing {
    final cursor = _cursor;
    return cursor != null && _closes(cursor);
  }

  bool _near(Offset a, Offset b) => (a - b).distance < kMaskHitRadius;

  void _onTapDown(TapDownDetails details) {
    // Unsnapped for the proximity test: snapping happens against the frame's edges, and a first
    // point sitting on one would otherwise pull every later click towards it.
    final raw = _toFraction(details.localPosition);
    final point = _snapped(raw);

    if (widget.tool == MaskTool.select) {
      _selectAt(point);
      return;
    }

    if (_closes(raw)) {
      widget.onCommitDraft();
      return;
    }

    widget.onDraftChanged([...widget.draft, point.dx, point.dy]);
  }

  /// Picks whichever committed mask contains the point, topmost first — the inspector lists them
  /// in the same order, so the one drawn last is the one clicked.
  void _selectAt(Offset point) {
    for (var i = widget.masks.length - 1; i >= 0; i--) {
      if (widget.hidden.contains(i)) continue;
      final path = maskPath(widget.masks[i], const Size(1, 1));
      if (path != null && path.contains(Offset(point.dx, point.dy))) {
        widget.onSelect(i);
        return;
      }
    }
  }

  void _onPanStart(DragStartDetails details) {
    if (widget.tool != MaskTool.select) return;
    final point = _toFraction(details.localPosition);

    // A vertex of the selected mask first: it is the one whose handles are drawn, so it is the one
    // the pointer was aiming at.
    final selected = widget.selected;
    if (selected != null && selected < widget.masks.length) {
      final points = widget.masks[selected].points;
      for (var i = 0; i + 1 < points.length; i += 2) {
        if (_near(point, Offset(points[i], points[i + 1]))) {
          setState(() => _dragging = (mask: selected, point: i ~/ 2));
          return;
        }
      }

      // Then an edge's midpoint, which inserts a vertex there and drags the new one.
      final inserted = _insertAtMidpoint(points, point);
      if (inserted != null) {
        widget.onMaskChanged(selected, inserted.points);
        setState(() => _dragging = (mask: selected, point: inserted.index));
        return;
      }
    }
  }

  /// The points with a new vertex spliced into whichever edge's midpoint was grabbed, or null when
  /// none was.
  ({List<double> points, int index})? _insertAtMidpoint(
    List<double> points,
    Offset at,
  ) {
    final count = points.length ~/ 2;
    for (var i = 0; i < count; i++) {
      final j = (i + 1) % count;
      final a = Offset(points[i * 2], points[(i * 2) + 1]);
      final b = Offset(points[j * 2], points[(j * 2) + 1]);
      final middle = Offset((a.dx + b.dx) / 2, (a.dy + b.dy) / 2);

      if (_near(at, middle)) {
        final next = [...points];
        next.insertAll((i + 1) * 2, [middle.dx, middle.dy]);
        return (points: next, index: i + 1);
      }
    }
    return null;
  }

  void _onPanUpdate(DragUpdateDetails details) {
    final dragging = _dragging;
    if (dragging == null) return;

    final point = _snapped(_toFraction(details.localPosition));
    final mask = dragging.mask;
    if (mask == null || mask >= widget.masks.length) return;

    final points = [...widget.masks[mask].points];
    points[dragging.point * 2] = point.dx;
    points[(dragging.point * 2) + 1] = point.dy;
    widget.onMaskChanged(mask, points);
  }

  void _onPanEnd(DragEndDetails details) => setState(() => _dragging = null);

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final box = Size(constraints.maxWidth, constraints.maxHeight);
      _picture = _pictureRect(box);

      return MouseRegion(
        cursor: widget.tool == MaskTool.draw
            ? SystemMouseCursors.precise
            : SystemMouseCursors.click,
        onHover: (event) =>
            setState(() => _cursor = _toFraction(event.localPosition)),
        onExit: (_) => setState(() => _cursor = null),
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTapDown: _onTapDown,
          onPanStart: _onPanStart,
          onPanUpdate: _onPanUpdate,
          onPanEnd: _onPanEnd,
          child: Stack(
            fit: StackFit.expand,
            children: [
              if (widget.frame case final bytes?)
                Image.memory(bytes, fit: BoxFit.contain, gaplessPlayback: true)
              else
                const _NoStill(),
              CustomPaint(
                painter: _MaskCanvasPainter(
                  picture: _picture,
                  masks: widget.masks,
                  hidden: widget.hidden,
                  selected: widget.selected,
                  draft: widget.draft,
                  cursor: widget.tool == MaskTool.draw ? _cursor : null,
                  closing: _closing,
                  showHandles: widget.tool == MaskTool.select,
                ),
              ),
              if (_closing) _closeHint,
            ],
          ),
        ),
      );
    },
  );

  /// "Click the first point" is the one rule nobody guesses, so it is said at the moment it
  /// becomes true rather than in a legend somewhere.
  Widget get _closeHint {
    final at = _toPixels(Offset(widget.draft[0], widget.draft[1]));

    return Positioned(
      left: at.dx + 18,
      top: math.max(0, at.dy - 46),
      child: IgnorePointer(
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
          decoration: BoxDecoration(
            color: Serval.tile.withValues(alpha: 0.92),
            borderRadius: BorderRadius.circular(7),
            border: Border.all(color: Nocturne.mix(Nocturne.accent, 55)),
          ),
          child: Text(
            'Click to close this mask',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: Nocturne.accent300,
            ),
          ),
        ),
      ),
    );
  }

  /// Where the `contain`ed still actually sits inside [box].
  ///
  /// Measured from the frame's own decoded dimensions rather than assumed, because a 4:3 camera in
  /// a 16:9 box is letterboxed by an eighth of the width down each side — and every mask drawn
  /// against the box rather than the picture lands there instead. Falls back to the design's 16:9
  /// only for the moment before the first decode returns.
  Rect _pictureRect(Size box) =>
      pictureRectIn(box, fallbackAspect: Serval.pictureAspect);
}

class _NoStill extends StatelessWidget {
  const _NoStill();

  @override
  Widget build(BuildContext context) => Center(
    child: Text(
      'Waiting for a picture from this camera…',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 13,
        color: Nocturne.mix(Nocturne.text, 40),
      ),
    ),
  );
}

class _MaskCanvasPainter extends CustomPainter {
  const _MaskCanvasPainter({
    required this.picture,
    required this.masks,
    required this.hidden,
    required this.selected,
    required this.draft,
    required this.cursor,
    required this.closing,
    required this.showHandles,
  });

  final Rect picture;
  final List<DetectionMaskSettings> masks;
  final Set<int> hidden;
  final int? selected;
  final List<double> draft;
  final Offset? cursor;
  final bool closing;
  final bool showHandles;

  Offset _at(double x, double y) => Offset(
    picture.left + (x * picture.width),
    picture.top + (y * picture.height),
  );

  @override
  void paint(Canvas canvas, Size size) {
    _paintCommitted(canvas);
    _paintDraft(canvas);
  }

  void _paintCommitted(Canvas canvas) {
    for (var i = 0; i < masks.length; i++) {
      if (hidden.contains(i)) continue;

      final path = maskPath(masks[i], picture.size);
      if (path == null) continue;
      final shifted = path.shift(picture.topLeft);
      final isSelected = i == selected;

      canvas.drawPath(
        shifted,
        Paint()
          ..style = PaintingStyle.fill
          ..color = Serval.alert.withValues(alpha: isSelected ? 0.26 : 0.18),
      );
      canvas.drawPath(
        shifted,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = isSelected ? 3 : 2.5
          ..strokeJoin = StrokeJoin.round
          ..color = Serval.alert,
      );

      // Handles only on the mask being edited, and only with the tool that moves them: a frame of
      // dots on every polygon reads as clutter rather than as an affordance.
      if (isSelected && showHandles) _paintHandles(canvas, masks[i].points);
    }
  }

  void _paintHandles(Canvas canvas, List<double> points) {
    for (var i = 0; i + 1 < points.length; i += 2) {
      final at = _at(points[i], points[i + 1]);
      canvas
        ..drawCircle(at, 7, Paint()..color = Serval.tile)
        ..drawCircle(
          at,
          7,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 3
            ..color = Serval.alertText,
        );
    }

    // The midpoints, smaller and hollow: drag one and it becomes a vertex.
    final count = points.length ~/ 2;
    for (var i = 0; i < count; i++) {
      final j = (i + 1) % count;
      final a = _at(points[i * 2], points[(i * 2) + 1]);
      final b = _at(points[j * 2], points[(j * 2) + 1]);

      canvas.drawCircle(
        Offset((a.dx + b.dx) / 2, (a.dy + b.dy) / 2),
        3.5,
        Paint()..color = Serval.alertText.withValues(alpha: 0.6),
      );
    }
  }

  void _paintDraft(Canvas canvas) {
    if (draft.length < 2) return;

    final vertices = [
      for (var i = 0; i + 1 < draft.length; i += 2) _at(draft[i], draft[i + 1]),
    ];

    final line = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 3
      ..strokeJoin = StrokeJoin.round
      ..color = Nocturne.accent300;

    if (vertices.length > 1) {
      final open = Path()..moveTo(vertices.first.dx, vertices.first.dy);
      for (final vertex in vertices.skip(1)) {
        open.lineTo(vertex.dx, vertex.dy);
      }

      // A translucent preview of what the shape will enclose, so a polygon can be judged before it
      // is closed rather than after.
      canvas
        ..drawPath(
          Path.from(open)..close(),
          Paint()
            ..style = PaintingStyle.fill
            ..color = Nocturne.accent.withValues(alpha: 0.16),
        )
        ..drawPath(open, line);
    }

    // The rubber band: from the last point placed to wherever the pointer is. Dashed, because it
    // is not a line that exists yet.
    if (cursor case final cursor?) {
      _dashed(canvas, vertices.last, _at(cursor.dx, cursor.dy));
    }

    for (final vertex in vertices.skip(1)) {
      canvas
        ..drawCircle(vertex, 8, Paint()..color = Serval.panel)
        ..drawCircle(
          vertex,
          8,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 3.5
            ..color = Nocturne.accent300,
        );
    }

    // The first point, which is the one that closes the shape. It grows a ring when the pointer is
    // on it, which is the only cue that clicking there does something different.
    final first = vertices.first;
    if (closing) {
      canvas
        ..drawCircle(
          first,
          15,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 2
            ..color = Nocturne.accent300.withValues(alpha: 0.55),
        )
        ..drawCircle(first, 9, Paint()..color = Nocturne.accent300);
    } else {
      canvas
        ..drawCircle(first, 8, Paint()..color = Serval.panel)
        ..drawCircle(
          first,
          8,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 3.5
            ..color = Nocturne.accent300,
        );
    }
  }

  void _dashed(Canvas canvas, Offset from, Offset to) {
    const dash = 7.0;
    final total = (to - from).distance;
    if (total < 1) return;

    final step = (to - from) / total;
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2.5
      ..color = Nocturne.accent300.withValues(alpha: 0.75);

    for (var travelled = 0.0; travelled < total; travelled += dash * 2) {
      final end = math.min(travelled + dash, total);
      canvas.drawLine(from + (step * travelled), from + (step * end), paint);
    }
  }

  @override
  bool shouldRepaint(_MaskCanvasPainter old) =>
      old.picture != picture ||
      old.masks != masks ||
      old.hidden != hidden ||
      old.selected != selected ||
      old.draft != draft ||
      old.cursor != cursor ||
      old.closing != closing ||
      old.showHandles != showHandles;
}

/// The mask names, floating over the shapes they belong to.
///
/// Separate from the painter so they are real text: laid out by Flutter, selectable by the
/// accessibility tree, and legible at whatever text scale the viewer is using.
class MaskNamePills extends StatelessWidget {
  const MaskNamePills({super.key, required this.masks, required this.hidden});

  final List<DetectionMaskSettings> masks;
  final Set<int> hidden;

  @override
  Widget build(BuildContext context) => IgnorePointer(
    child: LayoutBuilder(
      builder: (context, constraints) => Stack(
        children: [
          for (var i = 0; i < masks.length; i++)
            if (!hidden.contains(i))
              if (_anchor(masks[i]) case final at?)
                Positioned(
                  left: at.dx * constraints.maxWidth,
                  top: at.dy * constraints.maxHeight,
                  child: Pill(
                    label: maskTitle(masks[i]),
                    leadingDot: Serval.alert,
                    background: Serval.tile.withValues(alpha: 0.8),
                    border: Serval.alert.withValues(alpha: 0.4),
                    foreground: Serval.alertText,
                    fontSize: 11.5,
                    fontWeight: FontWeight.w400,
                    padding: const EdgeInsets.symmetric(
                      horizontal: 9,
                      vertical: 4,
                    ),
                  ),
                ),
        ],
      ),
    ),
  );

  /// The polygon's topmost vertex, so the pill sits on the shape rather than over its middle where
  /// it would cover whatever the mask was drawn around.
  static Offset? _anchor(DetectionMaskSettings mask) {
    if (mask.points.length < 6) return null;

    var best = Offset(mask.points[0], mask.points[1]);
    for (var i = 2; i + 1 < mask.points.length; i += 2) {
      if (mask.points[i + 1] < best.dy) {
        best = Offset(mask.points[i], mask.points[i + 1]);
      }
    }
    return best;
  }
}

/// The keyboard rules, said where the hands are.
class MaskCanvasStatus extends StatelessWidget {
  const MaskCanvasStatus({super.key, required this.name, required this.points});

  final String name;
  final int points;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 8),
    decoration: BoxDecoration(
      color: Serval.tile.withValues(alpha: 0.88),
      borderRadius: BorderRadius.circular(7),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          '$name · $points point${points == 1 ? '' : 's'}',
          style: monoStyle(
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
        Container(
          width: 1,
          height: 12,
          margin: const EdgeInsets.symmetric(horizontal: 10),
          color: Nocturne.mix(Nocturne.text, 14),
        ),
        Text(
          'Backspace removes the last · Esc cancels',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 42),
          ),
        ),
      ],
    ),
  );
}
