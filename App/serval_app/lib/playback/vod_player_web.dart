import 'dart:js_interop';
import 'dart:ui_web' as ui_web;

import 'package:flutter/foundation.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';
import 'package:web/web.dart' as web;

import 'hls_js.dart';
import 'playback_audio_graph.dart';
import 'playback_volume.dart';
import 'vod_player.dart';

VodPlayer createPlatformVodPlayer() => WebVodPlayer();

/// Nothing to load: hls.js comes in with the page, from the script tag in `web/index.html`.
void ensurePlatformPlaybackInitialized() {}

/// Replay in the browser: a `<video>` element in a platform view, fed by hls.js.
///
/// media_kit is not used here even though it claims web support — on the web it drops libmpv for
/// a plain `<video>`, and Chrome cannot play an HLS playlist from one. So the desktop and the web
/// share the playlist URL and nothing else.
///
/// The element only exists once Flutter has created the platform view, which is after the widget
/// is built and therefore possibly after [open] has been called. So an open that arrives early is
/// recorded and replayed on attach.
class WebVodPlayer implements VodPlayer {
  static const _viewType = 'serval-vod';
  static bool _factoryRegistered = false;

  /// The element's DOM id, derived from the view id so the factory and the player agree on it without
  /// having to pass anything between them.
  static String _domIdFor(int viewId) => '$_viewType-$viewId';

  /// Element per view id. Flutter hands the factory an id and the widget the same id, and this is
  /// what joins them up.
  static final _elements = <int, web.HTMLVideoElement>{};

  static void _registerFactory() {
    if (_factoryRegistered) return;
    _factoryRegistered = true;

    ui_web.platformViewRegistry.registerViewFactory(_viewType, (int viewId) {
      final video = web.HTMLVideoElement()
        ..autoplay = false
        ..controls = false
        // Findable from the DOM, which is how the WebAudio chain gets hold of it — the graph is built
        // against an element id rather than a reference because the live view's element belongs to
        // flutter_webrtc and an id is all it exposes. See [_elementDomId].
        ..id = _domIdFor(viewId)
        // Same-origin in production, since the web build reads its base URL from `Uri.base`. A
        // `--dart-define` dev URL is not, though, and `createMediaElementSource` on a cross-origin
        // element without CORS outputs silence rather than failing — so boost would break in
        // development in a way that looks like the gain being wrong. The Server allows any origin
        // when none are configured, so asking for CORS costs nothing.
        ..crossOrigin = 'anonymous'
        // The stage is a fixed rectangle and the picture must not be cropped to fill it, which is
        // the same choice `BoxFit.contain` makes on the desktop and `RTCVideoViewObjectFitContain`
        // makes for the live view.
        ..style.width = '100%'
        ..style.height = '100%'
        ..style.objectFit = 'contain'
        ..style.backgroundColor = 'transparent'
        // Flutter sets this on its own glass pane, and a platform view sits outside it. Without it
        // the browser claims a two-finger gesture over the element as page zoom, and the pinch
        // that should be magnifying the recording never reaches Flutter at all — so replay would
        // be the one surface on a phone that does not zoom.
        ..style.touchAction = 'none';

      _elements[viewId] = video;
      return video;
    });
  }

  web.HTMLVideoElement? _video;
  Hls? _hls;
  int? _viewId;

  /// The id of the element currently attached, or null before one is.
  String? get _elementDomId {
    final id = _viewId;
    return id == null ? null : _domIdFor(id);
  }

  /// The WebAudio chain, built on first boost and never for an unboosted camera.
  PlaybackGainChain? _gain;

  /// Set once a chain could not be built, so it is not retried on every level change. The element
  /// keeps its own `volume` from then on, which costs boost and nothing else.
  bool _gainUnavailable = false;

  /// The camera's gain and gate, held for the same reason [_volume] is — and re-applied to a freshly
  /// built chain rather than assumed, since the chain can outlive or postdate any one of these calls.
  double _gainDb = 0;
  double? _gateRms;

  /// An [open] that arrived before the element existed, replayed on attach.
  ({Uri playlist, DateTime windowFrom, DateTime at})? _pending;

  /// An [openFile] that arrived before the element existed, replayed on attach.
  Uri? _pendingFile;

  final _position = ValueNotifier<Duration>(Duration.zero);
  final _duration = ValueNotifier<Duration?>(null);
  final _playing = ValueNotifier<bool>(false);
  final _videoSize = ValueNotifier<Size?>(null);
  final _failure = ValueNotifier<String?>(null);

  @override
  ValueListenable<Duration> get position => _position;

  @override
  ValueListenable<Duration?> get duration => _duration;

  @override
  ValueListenable<bool> get playing => _playing;

  @override
  ValueListenable<Size?> get videoSize => _videoSize;

  @override
  ValueListenable<String?> get failure => _failure;

  bool _muted = false;

  /// Held rather than read back off the element, because [open] builds a fresh one and a fresh
  /// `<video>` starts at 1.0 — so the level has to be re-applied there or every window reopen
  /// would jump back to full volume.
  double _volume = 1;

  /// Held for the same reason [_volume] is, and re-applied in [open] alongside it.
  double _rate = 1;

  int _session = 0;

  @override
  Future<void> open(
    Uri playlist, {
    required DateTime windowFrom,
    required DateTime at,
  }) async {
    final video = _video;
    if (video == null) {
      _pending = (playlist: playlist, windowFrom: windowFrom, at: at);
      return;
    }

    final session = ++_session;
    _failure.value = null;

    if (!hlsLoaded) {
      _failure.value =
          'hls.js did not load — check that web/hls.js is served with the page.';
      return;
    }

    _hls?.destroy();
    _hls = null;

    if (Hls.isSupported()) {
      final hls = Hls(replayHlsConfig())
        ..on(
          hlsErrorEvent,
          ((JSAny _, JSObject data) {
            // hls.js reports recoverable errors too; only a fatal one is worth the stage.
            if (data.dartify() case {
              'fatal': true,
              'details': final String details,
            }) {
              _failure.value = 'Playback failed: $details';
            }
          }).toJS,
        )
        ..attachMedia(video)
        ..loadSource(playlist.toString());

      _hls = hls;
    } else {
      // Safari, which plays the playlist natively — and honours EXT-X-START doing it.
      video.src = playlist.toString();
    }

    if (session != _session) return;

    // Where in the playlist's own media the instant asked for sits. Written here and again after
    // `play()` resolves, because only the second one is reliable: `loadSource` attaches the
    // MediaSource asynchronously, and a `currentTime` written before the element has a source is
    // discarded without complaint. When that happens the picture starts wherever the playlist
    // does — up to a whole segment behind where playback already was, which is the step backwards
    // at every window turnover.
    final offset = at.difference(windowFrom);
    if (offset > Duration.zero) {
      video.currentTime = offset.inMilliseconds / 1000;
    }

    // All three re-applied, not just mute: this is a fresh source on the element, and every one of
    // them starts at its default. The gain chain is not among them — it is wired to the element
    // rather than to the source, so it survives a reopen untouched.
    video.muted = _muted;
    _applyLevel();
    video.playbackRate = _rate;

    // Before `play()`, because a context created before any user gesture starts suspended and a
    // suspended context passes no audio at all — a boosted camera would come back silent rather than
    // loud. This call is on the far side of the click that opened the window, which is the gesture
    // that makes it legal.
    resumePlaybackAudio();

    try {
      await video.play().toDart;
    } on Object catch (error) {
      if (!_isPlayInterrupted(error)) rethrow;
    }

    // The source is attached and playing now, so this write cannot be dropped. Re-applied rather
    // than assumed: whether the one before `play()` survived depends on when the MediaSource
    // finished attaching, which is not something to leave to chance on every window turnover.
    if (offset > Duration.zero) {
      final target = offset.inMilliseconds / 1000;
      if ((video.currentTime - target).abs() > 0.05) {
        video.currentTime = target;
      }
    }

    // Read straight off the element rather than waiting for the next `timeupdate`. That event is
    // ~4 Hz, so for up to a quarter of a second after an open this notifier would still be
    // carrying the position the element reported while its media was detached — zero — and
    // anything mapping that onto the new window lands a whole segment in the past.
    _position.value = Duration(
      milliseconds: (video.currentTime * 1000).round(),
    );
  }

  /// A saved clip: a plain MP4 the element plays by itself.
  ///
  /// hls.js is deliberately torn down rather than handed the URL. It exists here because Chrome
  /// cannot play a HLS playlist on its own; an MP4 it can, and putting a media-source library in
  /// front of one would add a loader, a buffer and a whole class of failure to something the
  /// browser does natively — including the byte-range seeking the Server enables for exactly this.
  @override
  Future<void> openFile(Uri file) async {
    final video = _video;
    if (video == null) {
      _pendingFile = file;
      return;
    }

    _session++;
    _failure.value = null;
    _duration.value = null;

    _hls?.destroy();
    _hls = null;

    video.src = file.toString();

    video.muted = _muted;
    _applyLevel();
    video.playbackRate = _rate;

    // Not played on open. A clip is opened by choosing it in a list, which is a different
    // intention from pressing play on it — 13a's column shows a poster with a play button over it.
    _position.value = Duration.zero;
  }

  /// Whether a rejected `play()` was interrupted rather than failed.
  ///
  /// A `<video>` rejects an outstanding `play()` with `AbortError` when something pauses it or
  /// loads a new source underneath it — which is the transport doing exactly what it was asked,
  /// most often a viewer pausing during the reopen at the end of a window. Reported as a failure
  /// it costs the whole stage: the controller has nothing to distinguish it from a decoder that
  /// could not read the recording, so the surface is replaced by "Replay unavailable" over a
  /// window that is open, paused, and perfectly playable, and it stays there until the next seek.
  static bool _isPlayInterrupted(Object error) =>
      '$error'.contains('AbortError');

  @override
  Future<void> play() async {
    resumePlaybackAudio();
    await _video?.play().toDart;
  }

  @override
  Future<void> pause() async => _video?.pause();

  @override
  Future<void> seekWithin(Duration offset) async =>
      _video?.currentTime = offset.inMilliseconds / 1000;

  @override
  Future<void> setRate(double rate) async {
    _rate = rate;
    _video?.playbackRate = rate;
  }

  @override
  Future<void> setMuted(bool muted) async {
    _muted = muted;
    _video?.muted = muted;
  }

  @override
  Future<void> setVolume(double volume) async {
    _volume = volume;
    _applyLevel();
  }

  @override
  Future<void> setGain(double db, double? gateRms) async {
    _gainDb = db;
    _gateRms = gateRms;

    // Built on first use rather than up front: at 0 dB the element's own `volume` is the whole story,
    // and that path has nothing between the recording and the speakers that could go wrong.
    if (db > 0) _ensureGainChain();

    _gain?.setGate(gateRms);
    _applyLevel();
  }

  /// The level, through whichever of the two routes is live.
  ///
  /// With a gain chain attached the element is pinned open and the level rides the `GainNode`, because
  /// `element.volume` is spec-clamped to 1.0 and would silently cap the boost. Without one it is the
  /// element's own `volume`, exactly as before.
  void _applyLevel() {
    final gain = _gain;
    if (gain == null) {
      _video?.volume = htmlVideoVolume(_volume);
      return;
    }

    _video?.volume = 1;
    gain.setLevel(volume: _volume, db: _gainDb);
  }

  void _ensureGainChain() {
    if (_gain != null || _gainUnavailable) return;

    final id = _elementDomId;
    if (id == null) return;

    final chain = attachPlaybackGain(id);
    if (chain == null) {
      // No WebAudio, or this element is already claimed by a graph that outlived its player. Recorded
      // so it is not retried on every level change, and the element keeps its own `volume` — a camera
      // that will not boost is much better than a camera that will not play.
      _gainUnavailable = true;
      return;
    }

    _gain = chain;
    chain.setGate(_gateRms);
  }

  @override
  Widget buildView() {
    _registerFactory();
    return HtmlElementView(
      viewType: _viewType,
      onPlatformViewCreated: _attach,
      // The surface is a picture, not a control: nothing is clicked on the video itself — its
      // `controls` are off — so it must not take the hit test from the widgets around it, which
      // the default `opaque` does. The wall's tiles are where that shows. A tile opens its camera
      // when clicked, and while replaying the picture is this platform view rather than a painted
      // frame, so an opaque surface swallows the click and only the *replaying* wall stops
      // opening cameras — the one state where the instant on screen is the thing worth keeping.
      hitTestBehavior: PlatformViewHitTestBehavior.transparent,
    );
  }

  void _attach(int viewId) {
    // Not necessarily the first element this player has been handed. The stage swaps the surface
    // out for a plate for as long as a failure is showing, and swaps a new one back in when it
    // clears — the one before it went with its platform view, so leaving it in the registry leaks
    // it, and leaving hls.js bound to it is a picture that never returns.
    if (_viewId case final previous? when previous != viewId) {
      // The gain chain belonged to the element that went with the old platform view. Released and
      // cleared rather than carried over: it is wired to a node that no longer plays anything, and
      // `_ensureGainChain` will build a fresh one against the new element on the next level change.
      _gain?.dispose();
      _gain = null;
      _gainUnavailable = false;

      _elements.remove(previous);
    }

    _viewId = viewId;
    final video = _elements[viewId];
    if (video == null) return;
    _video = video;
    _hls?.attachMedia(video);

    // `timeupdate` is the browser's own position tick, ~4 Hz — which is exactly the cadence the
    // playhead wants, and cheaper than a timer polling `currentTime`.
    video.addEventListener(
      'timeupdate',
      ((web.Event _) {
        _position.value = Duration(
          milliseconds: (video.currentTime * 1000).round(),
        );
      }).toJS,
    );
    // The first event that knows the picture's real size. Read on every one rather than once:
    // the element is reused across window reopens, and a camera whose resolution changed between
    // two stretches of recording would otherwise keep the old aspect for the rest of the session.
    video.addEventListener(
      'loadedmetadata',
      ((web.Event _) {
        final width = video.videoWidth;
        final height = video.videoHeight;
        _videoSize.value = width > 0 && height > 0
            ? Size(width.toDouble(), height.toDouble())
            : null;

        // Infinite for a live-ish HLS window, finite for a file — which is exactly the question
        // `duration` is asked, so it needs no separate flag for which kind is open.
        final seconds = video.duration;
        _duration.value = seconds.isFinite && seconds > 0
            ? Duration(milliseconds: (seconds * 1000).round())
            : null;
      }).toJS,
    );
    video.addEventListener(
      'play',
      ((web.Event _) => _playing.value = true).toJS,
    );
    video.addEventListener(
      'pause',
      ((web.Event _) => _playing.value = false).toJS,
    );

    if (_pending case final pending?) {
      _pending = null;
      open(pending.playlist, windowFrom: pending.windowFrom, at: pending.at);
    }

    if (_pendingFile case final file?) {
      _pendingFile = null;
      openFile(file);
    }
  }

  @override
  Future<void> dispose() async {
    _session++;
    _hls?.destroy();
    _hls = null;

    _gain?.dispose();
    _gain = null;

    _video
      ?..pause()
      ..removeAttribute('src')
      ..load();
    _video = null;

    if (_viewId case final id?) _elements.remove(id);

    _position.dispose();
    _duration.dispose();
    _playing.dispose();
    _videoSize.dispose();
    _failure.dispose();
  }
}
