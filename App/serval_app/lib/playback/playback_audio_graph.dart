import 'playback_audio_graph_stub.dart'
    if (dart.library.js_interop) 'playback_audio_graph_web.dart'
    as platform;

/// Where a camera's playback gain is actually applied in a browser.
///
/// The three players carrying audio all reach a `<video>` or `<audio>` element whose `volume` the
/// HTML spec clamps to 1.0, so there is no way to ask any of them to be louder than the recording.
/// WebAudio is the way past that: the element's output is rerouted into a graph, and a `GainNode`
/// there has no ceiling.
///
/// The graph is **gate → gain → limiter**, and each stage answers a different measured problem:
///
/// * The gate keeps silence silent. These streams sit on their noise floor most of the time, and
///   100x of unguarded gain turns the codec's own quantisation noise into audible hiss.
/// * The gain does the lifting the recording needs.
/// * The limiter catches the rare transient. Every camera reaches within a few dB of full scale
///   occasionally, and without something to catch those, boost trades inaudible content for
///   clipping.
///
/// Only ever built when the volume is above unity. Below it the players keep their plain
/// `element.volume` path, which is one less thing between the recording and the speakers and cannot
/// be broken by anything in here.
///
/// A no-op on the desktop, where libmpv's own filter chain does this job — see `mpvAudioFilter`.
abstract interface class PlaybackGainChain {
  /// Sets the level, where [volume] is the app's 0..1 and [db] how far above unity to amplify — the
  /// two halves `playbackFromTravel` splits a slider position into.
  ///
  /// Takes both rather than a pre-multiplied number so the limiter's threshold stays meaningful:
  /// listening at 50% should be half as loud, not half as compressed.
  void setLevel({required double volume, required double db});

  /// Sets the gate's threshold as the RMS of a 16 kHz window, or opens it fully when null.
  void setGate(double? rms);

  /// Releases the graph's own nodes. Does not release the shared `AudioContext`, which outlives any
  /// one player.
  void dispose();
}

/// Builds a chain around a media element, by its DOM id.
///
/// By id rather than by reference because one of the two callers does not own its element: the live
/// view's audio plays through an `<audio>` element that `flutter_webrtc` creates and keeps private,
/// findable only as `audio_RTCVideoRenderer-<textureId>`. Routing that element through the graph is
/// what gives the live view boost, and it is the same mechanism replay uses on its own `<video>` —
/// one code path rather than one for elements and another for `MediaStream`s.
///
/// Returns null when there is no such element, when the browser has no `AudioContext`, or when the
/// element has already been claimed by a graph. Callers treat null as "no boost available" and fall
/// back to the element's own `volume`, because a missing gain node must never mean missing audio.
PlaybackGainChain? attachPlaybackGain(String elementId) =>
    platform.attachPlatformPlaybackGain(elementId);

/// Resumes the shared `AudioContext` if the browser has it suspended.
///
/// An `AudioContext` created before any user gesture starts suspended under the autoplay policy, and
/// a suspended context passes no audio — so a boosted camera would be silent rather than loud. Called
/// from the gestures that start playback.
///
/// Safe to call when there is no context and on every platform; does nothing off the web.
void resumePlaybackAudio() => platform.resumePlatformPlaybackAudio();
