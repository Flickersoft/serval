import 'package:flutter/widgets.dart';

import '../models/alert.dart';
import '../models/camera.dart';
import '../theme/serval_tokens.dart';
import 'frame_size.dart';
import 'picture_aligned.dart';
import 'tile_placeholder.dart';

/// The frame an alert fired on, with its detection box drawn over it.
///
/// **Over, not into.** The Server stores the box as normalised fractions and writes the poster as an
/// ordinary frame, so one picture serves the 132×74 row thumbnail, the phone's smaller row and the
/// full-width hero — the box scales with the picture and stays a hairline at every size. Burning it
/// in would mean either three renders on the Server or a box that thickens as the picture shrinks.
///
/// **Contained, and the box measured against the picture rather than against the slot.** The row's
/// 132×74 and the card's 232 are the design's shapes; a frame's is its camera's sensor, and the two
/// agree only by luck. Filling the slot crops whichever edge is long, out of the one picture of the
/// thing that fired — on a 4:3 doorbell in the row that is a quarter of the frame's height, top and
/// bottom, which is where the face and the parcel are. Contained, the fractions are fractions of a
/// picture that no longer fills its slot, so the box is laid out inside [PictureAligned]: at 4:3 in
/// the row the picture is 99 of the 132 px, and a box hung on the slot starts 17 px to the left of
/// the thing it describes.
///
/// A sound alert carries no box, because a sound has no place in the frame. That is the only thing
/// this draws differently, and it needs no special case: [Alert.box] is simply null.
class AlertStill extends StatefulWidget {
  const AlertStill({
    super.key,
    required this.alert,
    this.poster,
    this.boxOpacity = 0.55,
  });

  final Alert alert;

  /// Where the still is. Null draws the camera's stripe alone — which is what a queue looks like
  /// while its posters are being fetched, and permanently for an alert whose clip could not be cut.
  final Uri? poster;

  /// How strongly to draw the box. The hero pushes this up; a row at 74px high wants it quieter,
  /// because at that size a bright rectangle is most of what you see.
  final double boxOpacity;

  @override
  State<AlertStill> createState() => _AlertStillState();
}

class _AlertStillState extends State<AlertStill> {
  /// The provider the picture is drawn from, held rather than built for each frame so that drawing
  /// it and measuring it are one entry in the image cache and so one fetch between them.
  ImageProvider? _image;

  /// The picture's own dimensions, for [PictureAligned]. Null until the first frame lands, and for
  /// any poster that will not resolve — both of which fall back to the slot, which is a box slightly
  /// out rather than no box at all.
  final ValueNotifier<Size?> _picture = ValueNotifier<Size?>(null);

  @override
  void initState() {
    super.initState();
    _readPoster();
  }

  @override
  void didUpdateWidget(AlertStill old) {
    super.didUpdateWidget(old);
    // The URL moves under a live alert. An alert is given the camera's snapshot the moment it is
    // raised and the exact frame once its clip is cut, and `ServalApi.alertPosterUrl` carries the
    // clip state in the query so the second one is actually fetched — a different picture, possibly
    // a different shape. A recycled row is the other way this arrives: the same still redrawn for
    // another alert on another camera.
    if (old.poster != widget.poster) _readPoster();
  }

  @override
  void dispose() {
    _picture.dispose();
    super.dispose();
  }

  void _readPoster() {
    final poster = widget.poster;
    if (poster == null) {
      _image = null;
      _picture.value = null;
      return;
    }

    final image = _image = NetworkImage(poster.toString());
    resolveImageSize(image).then((size) {
      // A newer poster may have landed while this one was in flight. The newest is what is on
      // screen and so the only shape the box may be measured against.
      if (!mounted || !identical(image, _image)) return;
      _picture.value = size;
    });
  }

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: [
      // Under the poster, so a still that fails to load leaves the camera's own ground rather than
      // a gap — and an alert whose clip never happened still reads as the camera it came from.
      TilePlaceholderView(
        placeholder: TilePlaceholder.forCameraId(widget.alert.cameraId),
      ),
      if (_image case final image?)
        Image(
          image: image,
          fit: BoxFit.contain,
          // The bars get the video ground, and only once there is a picture for them to be the edge
          // of. Painted from the first build they would blank the stripe for the length of a fetch,
          // saying this camera has no picture where the stripe says one is coming — which is also
          // why the old frame is not held across a change of poster.
          frameBuilder: (context, child, frame, _) => frame == null
              ? child
              : ColoredBox(color: Serval.tile, child: child),
          errorBuilder: (context, error, stack) => const SizedBox.shrink(),
        ),
      if (widget.alert.box case final box?)
        Positioned.fill(
          child: PictureAligned(
            videoSize: _picture,
            child: LayoutBuilder(
              builder: (context, constraints) => Stack(
                children: [
                  Positioned(
                    left: box.x * constraints.maxWidth,
                    top: box.y * constraints.maxHeight,
                    width: box.width * constraints.maxWidth,
                    height: box.height * constraints.maxHeight,
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        border: Border.all(
                          color: Serval.alert.withValues(
                            alpha: widget.boxOpacity,
                          ),
                          width: 1.5,
                        ),
                        borderRadius: BorderRadius.circular(2),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
    ],
  );
}
