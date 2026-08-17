/// A frame whose shape is not its container's, and a way to put one behind a URL.
///
/// Both exist because a fit is invisible without them. `cover` and `contain` agree exactly when the
/// picture and the box are the same shape, which is the shape every fixture would otherwise be; and
/// a poster that never loads has no fit to inspect, because `flutter test` answers every request
/// with a 400.
///
/// Seeding the image cache under the provider's own key is not a trick played on the widget under
/// test: it is the same identity the App relies on when it measures a poster it is already drawing.
library;

import 'dart:convert';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/widgets.dart';

/// A 4x3 PNG — deliberately not the 16:9 the slots are built around, so a `cover` regression has
/// something to crop. For the surfaces that draw bytes they already hold.
final fourByThree = Uint8List.fromList(
  base64Decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAQAAAADCAIAAAA7ljmRAAAAEElEQVR4nGM4YWMDRww4OQArRg8'
    'Bc3oMDAAAAABJRU5ErkJggg==',
  ),
);

/// A picture of a given shape, made without decoding anything.
///
/// `decodeImageFromList` never returns under `testWidgets`: decoding is real work on another thread
/// and the test's clock is not, so awaiting it outside `runAsync` hangs. Recording an empty picture
/// and calling `toImageSync` is the same two numbers with none of that.
ui.Image pictureOf({int width = 4, int height = 3}) {
  final recorder = ui.PictureRecorder();
  Canvas(recorder).drawRect(
    Rect.fromLTWH(0, 0, width.toDouble(), height.toDouble()),
    Paint()..color = const Color(0xFF808080),
  );

  final picture = recorder.endRecording();
  final image = picture.toImageSync(width, height);
  picture.dispose();
  return image;
}

/// A poster at [url] whose arrival the test decides.
///
/// Held rather than landed at once because the interesting moment is the one *before* it lands: the
/// camera's stripe has to survive a fetch, and the letterbox ground must not be painted over a slot
/// with no picture in it yet.
class HeldPoster {
  HeldPoster(this.url) {
    PaintingBinding.instance.imageCache.putIfAbsent(
      NetworkImage(url.toString()),
      () => _completer,
    );
  }

  final Uri url;
  final _HeldCompleter _completer = _HeldCompleter();

  /// Puts a picture of the given shape — 4:3 unless told otherwise — behind the URL.
  void land({int width = 4, int height = 3}) =>
      _completer.land(pictureOf(width: width, height: height));
}

/// A poster already behind [url] before anything asks for it.
void seedPoster(Uri url, {int width = 4, int height = 3}) =>
    HeldPoster(url).land(width: width, height: height);

class _HeldCompleter extends ImageStreamCompleter {
  void land(ui.Image image) => setImage(ImageInfo(image: image));
}
