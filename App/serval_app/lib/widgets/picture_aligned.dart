import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

/// Lays its child over the *picture* rather than over the box the picture was given.
///
/// Both video layers on the camera stage are fitted without distorting them — `object-fit: contain`
/// on the replay `<video>`, `BoxFit.contain` inside `RTCVideoView` — so unless the stage happens to
/// share the stream's aspect ratio the picture is letterboxed or pillarboxed within it, and the two
/// rectangles are not the same. Anything drawn in normalised coordinates means a fraction of the
/// picture, so laying it out against the stage puts it a bar's width out: at 640x480 in the
/// single-camera stage that is about 46 px, which is most of a person.
///
/// [videoSize] is the picture's own dimensions, from whichever layer is showing — only it knows.
/// It must already account for rotation, since what matters here is the shape on screen rather than
/// the shape that was encoded.
///
/// Falls back to filling the given box while the size is unknown: the first moments of a window or
/// a session, and any backend that never reports one. A box slightly out beats no box at all, and
/// it corrects itself as soon as the first frame's dimensions land.
class PictureAligned extends StatelessWidget {
  const PictureAligned({
    super.key,
    required this.videoSize,
    required this.child,
  });

  final ValueListenable<Size?> videoSize;
  final Widget child;

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<Size?>(
    valueListenable: videoSize,
    builder: (context, size, _) {
      if (size == null || size.isEmpty) return child;

      return Center(
        child: AspectRatio(aspectRatio: size.aspectRatio, child: child),
      );
    },
  );
}
