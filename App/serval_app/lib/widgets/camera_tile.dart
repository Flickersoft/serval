import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/camera.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'pill.dart';
import 'tile_placeholder.dart';

/// One camera on the wall.
///
/// The chrome is deliberately thin: a gradient strip at the top carrying the
/// drag grip, the name and one status affordance, and nothing else over the
/// image. No microphone and no pan/tilt appear here — the design routes you
/// into the single-camera view to speak, so that you always speak from the
/// feed you are actually watching.
class CameraTile extends StatefulWidget {
  const CameraTile({
    super.key,
    required this.camera,
    this.frames,
    this.rearranging = false,
    this.dragging = false,
    this.onTap,
    this.replaying = false,
    this.replay,
    this.compact = false,
  });

  final Camera camera;

  /// One of a phone's full-width tiles rather than one of a desktop wall's.
  ///
  /// The tile is four times the area there and holds a single camera's whole
  /// row, so the header's type and glyphs come up to match. Nothing about what
  /// it draws changes — same name, same REC, same one status mark.
  final bool compact;

  /// The wall is showing the past rather than now.
  ///
  /// Separate from [replay] being null because the two say different things, and the tile has to
  /// draw both: replaying with a view is footage, replaying without one is a camera that recorded
  /// nothing at this instant.
  final bool replaying;

  /// This camera's video surface for the instant on the wall's playhead.
  final Widget? replay;

  /// This camera's frames off `WS /api/dashboard`, listened to per tile rather than pushed down
  /// from the wall: at ~1 fps per camera, rebuilding the whole wall to repaint one tile would be
  /// several full layouts a second for no reason. Null renders the placeholder forever, which is
  /// what the sample content and the goldens want.
  final ValueListenable<Uint8List?>? frames;

  /// Shows the drag grip, and hands the cursor to the wall — which owns the
  /// gestures, because a tile knows nothing about cell sizes.
  final bool rearranging;

  /// Lifted off the wall by a drag or a resize, so it reads as picked up rather
  /// than as one more tile that happens to be somewhere odd.
  final bool dragging;

  final VoidCallback? onTap;

  @override
  State<CameraTile> createState() => _CameraTileState();
}

class _CameraTileState extends State<CameraTile> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final camera = widget.camera;

    // Offline and offline alone. A camera still connecting keeps its ordinary tile, because that
    // path already *is* the connecting state — the placeholder underneath, and whatever frame was
    // last held over it — and a wall coming back from the background has every camera in it at
    // once. The dashed card is a claim about each of them, and there is nothing yet to base it on.
    //
    // Offline is also a statement about *now*, and while replaying now is not what is on screen. A
    // camera that has since dropped out still recorded everything before it did, and refusing to
    // draw its footage is the one case where the wall would be least useful.
    if (camera.connection == CameraConnection.offline && !widget.replaying) {
      return _OfflineTile(
        camera: camera,
        rearranging: widget.rearranging,
        dragging: widget.dragging,
      );
    }

    final lifted = widget.dragging;

    return MouseRegion(
      // While rearranging the wall owns the cursor — it shows `move` over the
      // tile and `resizeDownRight` over the corner, and the deepest region that
      // does not defer is the one that wins.
      cursor: widget.rearranging ? MouseCursor.defer : SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          decoration: BoxDecoration(
            color: Serval.tile,
            borderRadius: BorderRadius.circular(Nocturne.radiusMd),
            border: Border.all(
              color: _hovered || lifted
                  ? Nocturne.mix(Nocturne.text, 22)
                  : Serval.panelBorder,
            ),
            // A lifted tile gets a cast shadow, which is the one place the wall
            // has depth rather than tint. Nothing else on a tile is drawn by
            // its chrome: the only claim the wall makes about a camera is the
            // attention dot in the strip, and it is the only warm thing here.
            boxShadow: lifted
                ? const [
                    BoxShadow(
                      color: Color(0x66000000),
                      blurRadius: 24,
                      offset: Offset(0, 8),
                    ),
                  ]
                : null,
          ),
          clipBehavior: Clip.antiAlias,
          child: Stack(
            fit: StackFit.expand,
            children: [
              // The placeholder is not replaced by the video — it stays underneath as the
              // loading and reconnecting state, so a tile whose stream has stalled shows the
              // design's stripes rather than a black hole.
              TilePlaceholderView(placeholder: camera.placeholder),
              if (!widget.replaying)
                _TileFrame(camera: camera, frames: widget.frames)
              else
                widget.replay ?? const _NoFootage(),
              Positioned(
                left: 0,
                right: 0,
                top: 0,
                child: _TileHeader(
                  camera: camera,
                  rearranging: widget.rearranging,
                  replaying: widget.replaying,
                  compact: widget.compact,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The tile's picture: the newest wall frame, or the design's centered label until one arrives.
///
/// Its own widget so the `ValueListenableBuilder` rebuilds this subtree alone — the header, the
/// selection ring and the placeholder underneath are untouched by a new frame.
class _TileFrame extends StatelessWidget {
  const _TileFrame({required this.camera, required this.frames});

  final Camera camera;
  final ValueListenable<Uint8List?>? frames;

  @override
  Widget build(BuildContext context) {
    final listenable = frames;
    if (listenable == null) return _label;

    return ValueListenableBuilder<Uint8List?>(
      valueListenable: listenable,
      builder: (context, frame, _) => frame == null
          ? _label
          : Stack(
              fit: StackFit.expand,
              children: [
                // `contain`, never `cover`: a tile's shape comes from its span on the grid and a
                // camera's from its sensor, so the two rarely agree. Filling the tile would crop
                // whichever edge is long — on a 4:3 camera in a 16:9 tile that is the top and
                // bottom of the frame, which is where a doorbell keeps the face and the parcel.
                //
                // The bars get the video ground rather than the placeholder underneath, so they
                // read as the edge of the picture instead of as stripes showing through it.
                ColoredBox(
                  color: Serval.tile,
                  // gaplessPlayback keeps the previous frame on screen while the next decodes,
                  // which at ~1 fps is the difference between a live tile and one that blinks
                  // every second.
                  child: Image.memory(
                    frame,
                    fit: BoxFit.contain,
                    gaplessPlayback: true,
                  ),
                ),

                // The frame stays — a picture from a moment ago is worth more than stripes, and
                // dropping it would put the wall's flash back in a different typeface. But held
                // over a camera we have not heard from, it is a picture presented as current, and
                // this word is the whole difference between showing it and claiming it.
                if (camera.connection == CameraConnection.connecting)
                  const _TileCaption('CONNECTING'),
              ],
            ),
    );
  }

  Widget get _label => _TileCaption(camera.placeholderLabel);
}

/// Nothing was recorded here.
///
/// Said in words rather than left as a dark rectangle, because on a wall of eight tiles playing
/// together the difference between "this camera was down" and "the wall is broken" is exactly this
/// label. The dashed edge belongs to `_OfflineTile` and stays there: this camera is not offline,
/// it simply has no footage at the instant being looked at.
class _NoFootage extends StatelessWidget {
  const _NoFootage();

  @override
  Widget build(BuildContext context) => const _TileCaption('NO FOOTAGE');
}

/// The design's centered mono word over a tile — the placeholder's `DRIVEWAY · 1080p`, and the two
/// states a tile says in words rather than in chrome.
///
/// One widget for all three because they are one mark: the tile's quietest possible way of saying
/// something about itself, at a weight that never competes with the picture. A second copy of this
/// style is how the three of them drift apart.
class _TileCaption extends StatelessWidget {
  const _TileCaption(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Center(
    child: Text(
      text,
      style: TextStyle(
        fontFamily: Nocturne.fontMono,
        fontSize: 10.5,
        letterSpacing: 0.14 * 10.5,
        color: Nocturne.mix(Nocturne.text, 30),
      ),
    ),
  );
}

class _TileHeader extends StatelessWidget {
  const _TileHeader({
    required this.camera,
    required this.rearranging,
    this.replaying = false,
    this.compact = false,
  });

  final Camera camera;
  final bool rearranging;
  final bool replaying;
  final bool compact;

  @override
  Widget build(BuildContext context) => Container(
    padding: EdgeInsets.symmetric(horizontal: 12, vertical: compact ? 14 : 11),
    decoration: const BoxDecoration(
      gradient: LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [Color(0x8C000000), Color(0x00000000)],
      ),
    ),
    child: Row(
      children: [
        if (rearranging) ...[
          PhosphorIcon(
            PhosphorIconsRegular.dotsSixVertical,
            size: 16,
            color: Nocturne.mix(Nocturne.text, 50),
          ),
          const SizedBox(width: 8),
        ],
        // The name and its pill take all the room the mark does not, which is
        // what puts the mark against the right edge. A `Flexible` name beside a
        // `Spacer` does not: both are flex children of the same row, so they
        // halve the free space between them and the mark comes to rest in the
        // middle of the frame.
        Expanded(
          child: Row(
            children: [
              Flexible(
                child: Text(
                  camera.name,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: compact ? 15 : 13.5,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                  ),
                ),
              ),
              // REC and the status marks are all claims about the present, and none of them is
              // true of the frame on screen while the wall is replaying. A tile that kept
              // flashing REC over yesterday's footage would be saying the one thing most likely
              // to be believed and most likely to be wrong.
              if (camera.isRecording &&
                  !camera.needsAttention &&
                  !replaying) ...[
                const SizedBox(width: 8),
                Pill.recording(),
              ],
            ],
          ),
        ),
        if (!replaying) ...[const SizedBox(width: 8), ..._statusMark(camera)],
      ],
    ),
  );

  /// The single right-hand affordance. Only one ever shows: attention beats
  /// audio activity.
  List<Widget> _statusMark(Camera camera) {
    // The dot keeps its 8px on a phone while the waveform grows: it is a mark
    // rather than a glyph, and one drawn any larger reads as a light somebody
    // meant you to press.
    if (camera.needsAttention) {
      return const [
        SizedBox(
          width: 8,
          height: 8,
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: Serval.alert,
              shape: BoxShape.circle,
            ),
          ),
        ),
      ];
    }
    if (camera.hasAudioActivity) {
      return [
        PhosphorIcon(
          PhosphorIconsFill.waveform,
          size: compact ? 16 : 14,
          color: Nocturne.accent400,
        ),
      ];
    }
    return const [];
  }
}

/// A camera that has dropped out. The design gives it a dashed edge and no
/// image at all, so an absent feed never reads as a dark one.
class _OfflineTile extends StatelessWidget {
  const _OfflineTile({
    required this.camera,
    this.rearranging = false,
    this.dragging = false,
  });

  final Camera camera;

  /// An offline tile has no header, but it still moves — so it draws the grip
  /// itself, in the same corner and at the same inset the header would.
  final bool rearranging;

  final bool dragging;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: const Color(0xFF14161F),
      borderRadius: BorderRadius.circular(Nocturne.radiusMd),
      boxShadow: dragging
          ? const [
              BoxShadow(
                color: Color(0x66000000),
                blurRadius: 24,
                offset: Offset(0, 8),
              ),
            ]
          : null,
    ),
    child: CustomPaint(
      painter: _DashedBorderPainter(
        color: Nocturne.mix(Nocturne.text, 16),
        radius: Nocturne.radiusMd,
      ),
      child: Stack(
        children: [
          Center(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                PhosphorIcon(
                  PhosphorIconsRegular.videoCameraSlash,
                  size: 18,
                  color: Nocturne.mix(Nocturne.text, 32),
                ),
                const SizedBox(width: 9),
                Flexible(
                  child: Text(
                    '${camera.name} is offline',
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12.5,
                      color: Nocturne.mix(Nocturne.text, 55),
                    ),
                  ),
                ),
              ],
            ),
          ),
          if (rearranging)
            Positioned(
              left: 12,
              top: 11,
              child: PhosphorIcon(
                PhosphorIconsRegular.dotsSixVertical,
                size: 16,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
        ],
      ),
    ),
  );
}

class _DashedBorderPainter extends CustomPainter {
  const _DashedBorderPainter({required this.color, required this.radius});

  final Color color;
  final double radius;

  @override
  void paint(Canvas canvas, Size size) {
    final path = Path()
      ..addRRect(
        RRect.fromRectAndRadius(Offset.zero & size, Radius.circular(radius)),
      );
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1;

    for (final metric in path.computeMetrics()) {
      var distance = 0.0;
      while (distance < metric.length) {
        canvas.drawPath(metric.extractPath(distance, distance + 5), paint);
        distance += 9;
      }
    }
  }

  @override
  bool shouldRepaint(_DashedBorderPainter oldDelegate) =>
      oldDelegate.color != color || oldDelegate.radius != radius;
}
