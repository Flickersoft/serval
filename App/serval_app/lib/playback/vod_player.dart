import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

// The one place the two platforms differ. `dart.library.io` is true under the VM — including
// `flutter test` — so the desktop file is what the test binary compiles; nothing there touches
// libmpv until a player is actually constructed, and no test constructs one. The web file is the
// default branch, so there is no third throw-only stub for a platform that has neither library.
import 'vod_player_web.dart'
    if (dart.library.io) 'vod_player_native.dart'
    as platform;

/// Plays a window of the recording.
///
/// Two implementations, because no one player covers both targets. On Linux
/// this is libmpv through `media_kit`; on the web it is a `<video>` element
/// driven by hls.js, since media_kit's web backend is a plain `<video>` and
/// Chrome cannot play HLS on its own. Both open the *same* URL — the Server's
/// `GET /api/cameras/{id}/vod.m3u8?from&to` — so there is one playback contract
/// and the difference stops at this file.
///
/// Deliberately small. No duration, no rate, no track selection: the screen
/// needs an offset into the open window and a reason when it fails, and every
/// other knob would be a second way to say something the controller already
/// says.
abstract interface class VodPlayer {
  /// Opens [playlist] and plays from [at].
  ///
  /// [windowFrom] is the playlist's own start, so an implementation can turn a
  /// wall-clock instant into an offset. Both seek explicitly: hls.js will
  /// already be close, because the Server writes `EXT-X-START` into the
  /// playlist, but ffmpeg's HLS demuxer — which is what libmpv reads through —
  /// does not implement that tag. The tag is still what saves the web player
  /// from showing a flash of earlier footage before its seek lands.
  Future<void> open(
    Uri playlist, {
    required DateTime windowFrom,
    required DateTime at,
  });

  /// Opens a standalone file — a saved clip — from its start.
  ///
  /// The one place the "both open the same URL" contract above does not hold, and it is a
  /// difference in the *media* rather than in the platform: a saved clip is an ordinary MP4 with a
  /// real duration, not a playlist onto a rolling window. So there is no window to map a
  /// wall-clock instant into, and on the web there is no hls.js in the path at all — the element
  /// plays an MP4 by itself, and routing one through a HLS library would be a loader in the way of
  /// something the browser already does.
  Future<void> openFile(Uri file);

  Future<void> play();
  Future<void> pause();

  /// Seeks within the playlist already open. The cheap path — no request.
  Future<void> seekWithin(Duration offset);

  /// Playback speed, 1.0 being real time.
  ///
  /// Two callers, and the second is why this is a rate rather than a set of speed presets. A wall
  /// of cameras replaying together sets every player to the same whole-number rate; the drift
  /// correction underneath it then nudges one player a few percent either side of the others to
  /// close a gap, because a hard seek on every tile every few seconds is far more visible than
  /// being a few hundred milliseconds out.
  Future<void> setRate(double rate);

  Future<void> setMuted(bool muted);

  /// Playback volume, 0..1.
  ///
  /// The one addition to the list above, and it earns its place for the reason
  /// that list exists: the two implementations sit on players whose volume
  /// ranges disagree — libmpv is 0..100, a `<video>` element is 0..1 — so
  /// without this every caller would have to know which one it is holding,
  /// which is the thing this interface is for.
  ///
  /// Folded together with [setMuted] by both implementations rather than
  /// fighting it: they are different questions ("off" versus "how loud") and a
  /// mute must not forget the level to come back to.
  Future<void> setVolume(double volume);

  /// The camera's own playback gain, in dB, and the level its gate treats as silence.
  ///
  /// Separate from [setVolume] because it is a different axis with a different owner: the volume is
  /// how loudly *you* want to listen and belongs to the machine you are sitting at, while this says
  /// how far *this camera's* recording has to be lifted before there is anything to listen to. They
  /// multiply.
  ///
  /// Two values in one call because they are one setting in two parts — a gain without its gate turns
  /// the codec's noise floor into hiss, so nothing should be able to set one and forget the other.
  ///
  /// The two implementations reach it by completely different means, which is the reason this is on
  /// the interface at all: libmpv takes an `af` filter chain, while a browser has to reroute the
  /// element's audio through WebAudio because `<video>.volume` is spec-clamped to 1.0.
  Future<void> setGain(double db, double? gateRms);

  /// How far into the open window the picture is.
  ///
  /// A listenable rather than a callback because it ticks about four times a
  /// second: routed through `setState` it would relayout the single-camera
  /// screen, transcript panel and all, every 250 ms.
  ValueListenable<Duration> get position;

  /// How long the open media is, or null where that is not a meaningful question.
  ///
  /// It is not, for a VOD window: the playlist is a slice of a recording that keeps growing, and
  /// the screen already knows the window it asked for. A saved clip is a file with an end, and a
  /// progress bar without one has nothing to divide by.
  ValueListenable<Duration?> get duration;

  ValueListenable<bool> get playing;

  /// The picture's own pixel dimensions, or null until they are known.
  ///
  /// Anything drawn *over* the video needs these, because the surface is
  /// letterboxed inside whatever rectangle the layout gave it and a normalised
  /// coordinate means a fraction of the picture, not of the stage. A detection
  /// box laid out against the stage sits a pillarbox-width off — which is
  /// invisible on a stage that happens to match the stream's aspect and wrong
  /// everywhere else.
  ValueListenable<Size?> get videoSize;

  /// What went wrong, in words worth showing. Null while it is fine.
  ValueListenable<String?> get failure;

  /// The video surface. Its lifetime is the player's, not the widget tree's,
  /// so reopening a window does not tear down and rebuild the surface.
  Widget buildView();

  Future<void> dispose();
}

/// The player for whatever this is running on.
VodPlayer createVodPlayer() => platform.createPlatformVodPlayer();

/// Whatever the platform's player needs doing once, at startup.
///
/// Called from `main` and from nowhere else: on the desktop this loads libmpv, and the tests
/// build `ServalApp` directly precisely so they never do.
void ensurePlaybackInitialized() =>
    platform.ensurePlatformPlaybackInitialized();
