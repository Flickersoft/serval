/// The conditional import's default branch — neither `dart.library.js_interop` nor a browser.
/// Reached by `flutter test`, which runs on the VM, and by the Linux build.
///
/// There is nothing to watch off the web. The wedge this recovers from is browser frame scheduling
/// specifically — a `requestAnimationFrame` that a hidden page never receives — and a native
/// embedder drives its frames from a vsync signal that a backgrounded app is simply not sent.
void watchFrames(String Function() route) {}
