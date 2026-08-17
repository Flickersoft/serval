import 'playback_audio_graph.dart';

/// The non-web branch of the conditional import in [PlaybackGainChain]'s library.
///
/// Silent no-ops rather than an `UnsupportedError`, because this branch is reached on the
/// desktop, where boost works perfectly well — libmpv applies the gate, the gain and the limiter
/// in its own filter chain, so there is simply nothing for a WebAudio graph to do.
///
/// Returning null is the documented "no boost available here" answer, and on the desktop it is the
/// right one: nothing asks this file for a chain, because `NativeVodPlayer` sets `af` instead.
PlaybackGainChain? attachPlatformPlaybackGain(String elementId) => null;

void resumePlatformPlaybackAudio() {}
