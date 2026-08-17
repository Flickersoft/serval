import 'package:flutter/widgets.dart';

/// Magnifies the picture where it is, without asking the camera for anything.
///
/// Distinct from the zoom in `ZoomControl`, which drives a lens over ONVIF. That one only exists on
/// a camera that has a motor, only applies to live, and cannot help a recording at all — the
/// footage was shot at whatever the lens was doing then. This is the other half: a transform on the
/// decoded frame, so it works on a fixed bullet camera and on three-day-old footage alike, and it
/// is what a phone's pinch reaches for.
///
/// The [child] must be the video layer *and* everything drawn over it, so they go under one matrix.
/// Split between two of these and the boxes drift off the objects — `PictureAligned` measures
/// against the box it is given, so an overlay left unzoomed keeps laying out against the whole
/// stage while the picture under it no longer fills one.
class PictureZoom extends StatefulWidget {
  const PictureZoom({super.key, required this.scale, required this.child});

  /// The magnification on screen, published for the readout that says the picture is cropped.
  ///
  /// Owned by the caller for the reason `_liveVideoSize` is: the thing that knows and the thing
  /// that draws it are siblings on the stage rather than nested.
  final ValueNotifier<double> scale;

  final Widget child;

  /// As far in as the picture is worth taking. Past about this a 640x360 sub stream is showing its
  /// own pixels rather than anything in the scene.
  static const maxScale = 8.0;

  @override
  State<PictureZoom> createState() => _PictureZoomState();
}

class _PictureZoomState extends State<PictureZoom> {
  final _transform = TransformationController();

  @override
  void initState() {
    super.initState();
    _transform.addListener(_publishScale);

    // A fresh state is a fresh 1x — another camera, or a move between the band and the full
    // screen. Deferred a frame rather than written here: the readout is a sibling that this frame
    // has already built, and a notifier written from `initState` rebuilds it out of order.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _publishScale();
    });
  }

  void _publishScale() =>
      widget.scale.value = _transform.value.getMaxScaleOnAxis();

  @override
  void dispose() {
    _transform.removeListener(_publishScale);
    _transform.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => GestureDetector(
    // Around the viewer rather than inside it, and it does not have to fight for the pointer to
    // get there: a scale gesture claims the arena only once it has moved past slop, so a pair of
    // taps that go nowhere is still resolved by the sweep and still reaches this.
    onDoubleTap: () => _transform.value = Matrix4.identity(),
    child: InteractiveViewer(
      transformationController: _transform,
      minScale: 1,
      maxScale: PictureZoom.maxScale,
      // Zero, so the picture's edges can never come inside the stage. At 1x that leaves no pan at
      // all, which is what keeps a stray drag over a full-frame picture from doing anything.
      boundaryMargin: EdgeInsets.zero,
      // A wheel already zooms; without this a trackpad's two fingers would pan instead, and the
      // same hand would get two different answers on the same machine.
      trackpadScrollCausesScale: true,
      child: widget.child,
    ),
  );
}
