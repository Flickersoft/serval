/// What the camera editor shows about masks, short of editing them.
///
/// Drawing is its own screen — see [MaskEditorScreen](../screens/mask_editor_screen.dart) — and the
/// argument for that is the design's: a polygon you drew at 214px wide is a polygon you will
/// redraw. What belongs on the camera page is the frame with the shapes on it, a list of what they
/// are, and the way in.
library;

import 'dart:typed_data';

import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/camera_record.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'frame_size.dart';
import 'nocturne_button.dart';
import 'picture_aligned.dart';
import 'settings_cards.dart';

/// Every mask is an instruction to the detector and nothing else. Said here, on the way in, rather
/// than left for someone to discover by scrubbing back and finding the footage they expected.
const String kMaskExplanation =
    'Masks are drawn on a still from this camera and mark ground it should ignore. Something '
    'counts as inside when its feet are, so a shape can cross a person’s head without hiding '
    'them. Recording is untouched — a masked area is still filmed, still kept, still there when '
    'you scrub back. It simply stops raising events.';

/// The still with its masks on it, and the button that opens the editor.
class MaskPreviewCard extends StatelessWidget {
  const MaskPreviewCard({
    super.key,
    required this.cameraId,
    required this.masks,
    required this.frame,
    required this.onEdit,
  });

  final String cameraId;
  final List<DetectionMaskSettings> masks;

  /// The latest frame, or null on a camera that has published none yet.
  final Uint8List? frame;

  /// Null disables the button — a camera being added has nothing to draw on.
  final VoidCallback? onEdit;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
    ),
    child: LayoutBuilder(
      builder: (context, constraints) {
        final thumb = _MaskThumbnail(frame: frame, masks: masks);
        final prose = Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(kMaskExplanation, style: settingHelpStyle()),
            const SizedBox(height: 10),
            Row(
              children: [
                NocturneButton(
                  label: 'Edit masks',
                  icon: PhosphorIconsRegular.polygon,
                  variant: NocturneButtonVariant.primary,
                  height: 34,
                  onPressed: onEdit,
                ),
                const SizedBox(width: 10),
                Flexible(
                  child: Text(
                    onEdit == null
                        ? 'Save the camera first'
                        : 'Opens the frame full size',
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11.5,
                      color: Nocturne.mix(Nocturne.text, 40),
                    ),
                  ),
                ),
              ],
            ),
          ],
        );

        // Side by side is the design's shape and needs the design's width; below it the still
        // takes the full row rather than shrinking to a stamp beside a paragraph.
        if (constraints.maxWidth < 460) {
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              SizedBox(height: 140, child: thumb),
              const SizedBox(height: 12),
              prose,
            ],
          );
        }

        return Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(width: 214, height: 120, child: thumb),
            const SizedBox(width: 14),
            Expanded(child: prose),
          ],
        );
      },
    ),
  );
}

/// A camera's frame with its masks drawn over it.
///
/// **`BoxFit.contain`, never `cover`, and the overlay measured against the picture rather than the
/// box.** Mask points are fractions of the *picture*: a crop puts every one of them somewhere else,
/// and so does a letterbox that the overlay ignores — a 4:3 camera in a 16:9 box is barred by an
/// eighth of the width down each side. See [PictureAligned], which makes the same argument for the
/// detection boxes over live video and quantifies what the error costs.
class _MaskThumbnail extends StatefulWidget {
  const _MaskThumbnail({required this.frame, required this.masks}) : radius = 6;

  final Uint8List? frame;
  final List<DetectionMaskSettings> masks;
  final double radius;

  @override
  State<_MaskThumbnail> createState() => _MaskThumbnailState();
}

class _MaskThumbnailState extends State<_MaskThumbnail>
    with FrameSizeReader<_MaskThumbnail> {
  @override
  void initState() {
    super.initState();
    readFrameSize(widget.frame);
  }

  @override
  void didUpdateWidget(_MaskThumbnail old) {
    super.didUpdateWidget(old);
    if (!identical(old.frame, widget.frame)) readFrameSize(widget.frame);
  }

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(widget.radius),
    child: DecoratedBox(
      decoration: const BoxDecoration(color: Serval.tile),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final picture = pictureRectIn(
            Size(constraints.maxWidth, constraints.maxHeight),
            fallbackAspect: Serval.pictureAspect,
          );

          return Stack(
            fit: StackFit.expand,
            children: [
              if (widget.frame case final bytes?)
                Image.memory(bytes, fit: BoxFit.contain, gaplessPlayback: true)
              else
                Center(
                  child: Text(
                    'No picture yet',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11.5,
                      color: Nocturne.mix(Nocturne.text, 35),
                    ),
                  ),
                ),
              Positioned.fromRect(
                rect: picture,
                child: CustomPaint(painter: _MaskPainter(masks: widget.masks)),
              ),
            ],
          );
        },
      ),
    ),
  );
}

/// Masks as amber polygons, in fractions of the box given.
class _MaskPainter extends CustomPainter {
  const _MaskPainter({required this.masks}) : strokeWidth = 2;

  final List<DetectionMaskSettings> masks;
  final double strokeWidth;

  @override
  void paint(Canvas canvas, Size size) {
    final fill = Paint()
      ..style = PaintingStyle.fill
      ..color = Serval.alert.withValues(alpha: 0.2);
    final stroke = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth
      ..strokeJoin = StrokeJoin.round
      ..color = Serval.alert;

    for (final mask in masks) {
      final path = maskPath(mask, size);
      if (path == null) continue;
      canvas.drawPath(path, fill);
      canvas.drawPath(path, stroke);
    }
  }

  @override
  bool shouldRepaint(_MaskPainter old) =>
      old.masks != masks || old.strokeWidth != strokeWidth;
}

/// One mask as a closed path over a box of [size], or null where it has too few points to enclose
/// anything — the same rule the Server applies with `DetectionMask.IsUsable`.
Path? maskPath(DetectionMaskSettings mask, Size size) {
  final points = mask.points;
  if (points.length < 6 || points.length.isOdd) return null;

  final path = Path()..moveTo(points[0] * size.width, points[1] * size.height);
  for (var i = 2; i < points.length; i += 2) {
    path.lineTo(points[i] * size.width, points[i + 1] * size.height);
  }
  return path..close();
}

/// One mask in the camera page's list: what it is called, what it ignores, and how to remove it.
class MaskListRow extends StatelessWidget {
  const MaskListRow({super.key, required this.mask, required this.onDelete});

  final DetectionMaskSettings mask;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 9),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(7),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
    ),
    child: Row(
      children: [
        Container(
          width: 26,
          height: 20,
          decoration: BoxDecoration(
            color: Serval.alert.withValues(alpha: 0.18),
            border: Border.all(color: Serval.alert),
            borderRadius: BorderRadius.circular(3),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Flexible(
                    child: Text(
                      maskTitle(mask),
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 12.5,
                        color: Nocturne.mix(Nocturne.text, 80),
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  const _IgnoreBadge(),
                ],
              ),
              const SizedBox(height: 2),
              Text(
                maskScope(mask),
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  color: Nocturne.mix(Nocturne.text, 42),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(width: 10),
        Text(
          maskPointCount(mask),
          style: monoStyle(
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 35),
          ),
        ),
        if (onDelete != null) ...[
          const SizedBox(width: 10),
          NocturneButton.icon(
            icon: PhosphorIconsRegular.trash,
            variant: NocturneButtonVariant.danger,
            height: 28,
            onPressed: onDelete,
          ),
        ],
      ],
    ),
  );
}

class _IgnoreBadge extends StatelessWidget {
  const _IgnoreBadge();

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(4),
      border: Border.all(color: Serval.alert.withValues(alpha: 0.5)),
    ),
    child: Text(
      'Ignore',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 10.5,
        color: Serval.alertText,
      ),
    ),
  );
}

/// What to call a mask that was never named. Unnamed is legal — the Server's `Name` is optional and
/// never matched on — so the list needs something to show rather than a gap.
String maskTitle(DetectionMaskSettings mask) {
  final name = mask.name?.trim();
  return name == null || name.isEmpty ? 'Unnamed area' : name;
}

/// What a mask applies to, in words. An empty or absent class list means every detection, which is
/// the only sensible reading of "ignore this shape" with nothing narrowing it.
String maskScope(DetectionMaskSettings mask) {
  final classes = mask.classes;
  if (classes == null || classes.isEmpty) return 'everything';
  return classes.join(', ');
}

String maskPointCount(DetectionMaskSettings mask) {
  final count = mask.points.length ~/ 2;
  return '$count point${count == 1 ? '' : 's'}';
}
