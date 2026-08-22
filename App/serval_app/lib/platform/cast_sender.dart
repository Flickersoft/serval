// Google Cast, behind the same conditional-import shape `frame_watchdog.dart` and
// `push_client.dart` use. `dart.library.io` is true under the VM — including `flutter test` — so
// the stub is what the test binary compiles, and no widget test ever reaches for a Chromecast.
import 'cast_sender_stub.dart'
    if (dart.library.js_interop) 'cast_sender_web.dart'
    if (dart.library.io) 'cast_sender_stub.dart'
    as platform;

/// Sending a camera to a Chromecast or a Google TV, from the browser, without Google's cloud in
/// the middle.
///
/// **Why this exists beside the Google Home integration.** Because that integration cannot do it.
/// Google routes camera streams to Nest displays and to the Home app and refuses televisions — it
/// never even calls Serval — and the same refusal happens for other vendors' certified
/// integrations, so it is not something a Serval change or a certification would unlock. This path
/// talks to the Cast device directly and skips the Assistant entirely.
///
/// **What the Cast device plays is WebRTC**, the same sub-second stream the App shows. It launches
/// Serval's own receiver application, which negotiates a peer connection back to the server; media
/// then flows straight from go2rtc to the television over the LAN. The URL handed over is a live
/// HLS playlist, and that is what the receiver plays if the peer connection does not come up — so
/// there is always a picture, just occasionally a delayed one.
///
/// **It needs a receiver to launch.** The application id is registered by the operator against
/// their own server and served by [ServalRepository.castTarget]; where none is registered there is
/// nothing to cast to and [available] stays false.
///
/// **Web only, and Chrome only at that.** Google's sender SDK runs on Chrome and Chromium-based
/// browsers on desktop and Android; it is absent on Safari, on Firefox, and on iOS altogether, and
/// it needs the page itself served over HTTPS. [available] is what the UI asks, so the button
/// simply is not there rather than being there and failing.
abstract final class CastSender {
  /// Loads Google's sender SDK and starts looking for [appId].
  ///
  /// The application id is required rather than supplied at launch: the SDK discovers only devices
  /// that can run a *named* application, so nothing is ever found until it has one — and with
  /// nothing found there is no button to launch anything from. Safe to call more than once, and a
  /// no-op anywhere the SDK cannot run.
  static Future<void> initialise(String appId) => platform.initialise(appId);

  /// Whether a Cast device has been found and can be cast to *right now*.
  ///
  /// A stream rather than a getter because discovery is asynchronous and ongoing: a TV that is
  /// switched on after the page loads should make the button appear, and one that goes away should
  /// take it back.
  static Stream<bool> get available => platform.available;

  /// Whether a session is currently playing, so the button can offer to stop it.
  static Stream<bool> get casting => platform.casting;

  /// Asks the viewer to pick a receiver, then plays [url] on it through receiver [appId].
  ///
  /// [title] is what the receiver shows on screen while it connects. [live] says whether [url] is
  /// the live camera or a recording; a recording is cast as buffered media, which is what gives the
  /// television a duration and working transport controls. Returns the error Google reported, or
  /// null on success — including the viewer simply dismissing the picker, which is not an error
  /// worth showing.
  /// [startAt] is how far into a recording to open, and is ignored when [live].
  ///
  /// Sent because the playlist covers the whole visible timeline rather than the playhead, so
  /// without it a cast begins hours before whatever is being watched. The playlist says the same
  /// thing in an `EXT-X-START` tag and the receiver ignores it — that tag needs HLS version 6, and
  /// a playlist of MPEG-TS segments is version 3.
  static Future<String?> cast(
    String appId,
    Uri url, {
    required String title,
    required bool live,
    Duration startAt = Duration.zero,
  }) => platform.cast(appId, url, title: title, live: live, startAt: startAt);

  /// Moves the television to [position] into what it is already playing.
  ///
  /// A seek rather than a fresh cast, so scrubbing here lands there immediately instead of
  /// restarting the receiver's media. Only meaningful while a recording is playing, and silently
  /// does nothing otherwise.
  static Future<void> seek(Duration position) => platform.seek(position);

  /// Ends the session and returns the receiver to its idle screen.
  static Future<void> stop() => platform.stop();
}
