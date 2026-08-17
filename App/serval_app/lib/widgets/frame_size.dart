/// The intrinsic size of an encoded frame, for anything drawing normalised coordinates over one.
///
/// A mask's points are fractions of the *picture*, and the picture is drawn `contain`ed — so unless
/// the box happens to share the frame's aspect ratio there are bars down two sides, and a fraction
/// measured against the box lands somewhere the mask was never drawn. Getting this wrong does not
/// raise: the shape simply sits somewhere else, which is why it is worth its own file.
///
/// The App carries no camera resolution — `live_repository.dart` says so, and the snapshot cannot
/// stand in for one — so the shape has to come from the picture itself. Two ways in, because there
/// are two kinds of picture: bytes the App is holding, decoded once and thrown away, and a URL the
/// browser fetches, measured through the cache that is already fetching it.
library;

import 'dart:async';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/widgets.dart';

/// Decodes just enough of [bytes] to learn the frame's dimensions.
///
/// The image is disposed immediately: this wants the two numbers, not the pixels, and the widget
/// tree decodes its own copy for painting anyway.
Future<Size?> decodeFrameSize(Uint8List bytes) async {
  try {
    final codec = await ui.instantiateImageCodec(bytes);
    final frame = await codec.getNextFrame();
    final size = Size(
      frame.image.width.toDouble(),
      frame.image.height.toDouble(),
    );
    frame.image.dispose();
    codec.dispose();
    return size.isEmpty ? null : size;
  } on Object {
    // A frame that will not decode is one the Image widget will not draw either, and the caller's
    // fallback aspect is no worse than a crash.
    return null;
  }
}

/// The picture behind [provider], measured through the image cache.
///
/// A cache hit rather than a second fetch: an `Image` and this both resolve the same provider, whose
/// key is its own URL, so asking a poster's shape while it is being drawn costs one request between
/// them. That is what makes measuring a *URL* affordable at all — [decodeFrameSize] wants bytes, and
/// a poster is a link the browser fetches for itself.
///
/// The [ImageInfo] a listener is handed is its own reference to the picture and is disposed here:
/// this wants the two numbers, and whatever is drawing it holds its own.
///
/// Null for anything that will not resolve — a 404, an expired stream token, a frame that will not
/// decode. The caller's fallback aspect is the honest answer to all three.
Future<Size?> resolveImageSize(ImageProvider provider) {
  final completer = Completer<Size?>();
  final stream = provider.resolve(ImageConfiguration.empty);
  late final ImageStreamListener listener;

  void finish(Size? size) {
    stream.removeListener(listener);
    if (!completer.isCompleted) completer.complete(size);
  }

  listener = ImageStreamListener(
    (info, _) {
      final size = Size(
        info.image.width.toDouble(),
        info.image.height.toDouble(),
      );
      info.dispose();
      finish(size.isEmpty ? null : size);
    },
    // An animated picture calls back once a frame and a failure can follow a success, so both paths
    // drop the listener and the first answer is the one kept.
    onError: (_, _) => finish(null),
  );

  stream.addListener(listener);
  return completer.future;
}

/// Keeps the decoded size of whatever frame it is given, re-decoding when the bytes change.
///
/// Mixed into a [State] so both the editor's canvas and the camera page's thumbnail resolve their
/// frame the same way rather than each carrying their own copy of it.
mixin FrameSizeReader<T extends StatefulWidget> on State<T> {
  Size? _frameSize;

  /// The frame's own dimensions, or null until they are known.
  Size? get frameSize => _frameSize;

  /// The frame's aspect ratio, or [fallback] while it is unknown — the first moments after a still
  /// is grabbed, and any frame that will not decode.
  double frameAspect(double fallback) => _frameSize?.aspectRatio ?? fallback;

  /// Where a `contain`ed picture of this shape actually sits inside [box].
  Rect pictureRectIn(Size box, {required double fallbackAspect}) {
    if (box.isEmpty) return Rect.zero;

    final aspect = frameAspect(fallbackAspect);
    final width = box.width < box.height * aspect
        ? box.width
        : box.height * aspect;
    final height = width / aspect;

    return Rect.fromLTWH(
      (box.width - width) / 2,
      (box.height - height) / 2,
      width,
      height,
    );
  }

  /// Call from `initState` and whenever the bytes change.
  void readFrameSize(Uint8List? bytes) {
    if (bytes == null) {
      if (_frameSize != null) setState(() => _frameSize = null);
      return;
    }

    decodeFrameSize(bytes).then((size) {
      if (!mounted || size == _frameSize) return;
      setState(() => _frameSize = size);
    });
  }
}
