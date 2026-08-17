import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:media_kit/media_kit.dart';
import 'package:media_kit_video/media_kit_video.dart';

import '../theme/serval_tokens.dart';
import 'playback_volume.dart';
import 'vod_player.dart';

VodPlayer createPlatformVodPlayer() => NativeVodPlayer();

/// Loads libmpv. Called from `main` alone — see [NativeVodPlayer].
void ensurePlatformPlaybackInitialized() => MediaKit.ensureInitialized();

/// Replay on the desktop, through libmpv.
///
/// libmpv reads HLS with ffmpeg's demuxer, which handles the `EXT-X-MAP` and `EXT-X-DISCONTINUITY`
/// a VOD playlist carries across an ffmpeg restart, but does **not** implement `EXT-X-START`. So
/// the Server's start offset is inert here and this seeks by the same arithmetic itself.
///
/// libmpv is a system library. `flutter test` compiles this file — `dart.library.io` is true under
/// the VM — but never loads it, because nothing is constructed until a player is actually needed
/// and `MediaKit.ensureInitialized()` is called from `main()` alone.
class NativeVodPlayer implements VodPlayer {
  NativeVodPlayer() {
    _controller = VideoController(
      _player,
      configuration: const VideoControllerConfiguration(
        enableHardwareAcceleration: true,
      ),
    );
    _subscriptions.addAll([
      _player.stream.position.listen((at) => _position.value = at),
      _player.stream.playing.listen((it) => _playing.value = it),
      _player.stream.error.listen((message) => _failure.value = message),
      _player.stream.duration.listen(
        (length) => _duration.value = length > Duration.zero ? length : null,
      ),
      // libmpv reports the two independently, and either can arrive first, so both sides rebuild
      // the size from whatever the other last said rather than waiting for a pair.
      _player.stream.width.listen((width) => _setVideoSize(width: width)),
      _player.stream.height.listen((height) => _setVideoSize(height: height)),
    ]);
  }

  final Player _player = Player();
  late final VideoController _controller;
  final _subscriptions = <StreamSubscription<dynamic>>[];

  final _position = ValueNotifier<Duration>(Duration.zero);
  final _duration = ValueNotifier<Duration?>(null);
  final _playing = ValueNotifier<bool>(false);
  final _videoSize = ValueNotifier<Size?>(null);
  final _failure = ValueNotifier<String?>(null);

  int? _width;
  int? _height;

  void _setVideoSize({int? width, int? height}) {
    _width = width ?? _width;
    _height = height ?? _height;

    final w = _width;
    final h = _height;
    _videoSize.value = w != null && h != null && w > 0 && h > 0
        ? Size(w.toDouble(), h.toDouble())
        : null;
  }

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

  /// Guards a slow open against a newer one, the same way `WebRtcView` guards its session. A drag
  /// that crosses several windows fires several opens, and the one that finishes last is not
  /// necessarily the one that was asked for last.
  int _session = 0;

  /// A saved clip: one file, opened at its start.
  ///
  /// Much less work than [open] and for a reason — there is no playlist to wait for and no
  /// wall-clock instant to turn into an offset, so none of the seek-after-parse care below
  /// applies. libmpv takes an MP4 the same way it takes a playlist.
  @override
  Future<void> openFile(Uri file) async {
    _session++;
    _failure.value = null;
    _duration.value = null;

    // Paused: a clip is opened by being chosen in a list, which is not the same as being played.
    await _player.open(Media(file.toString()), play: false);
  }

  @override
  Future<void> open(
    Uri playlist, {
    required DateTime windowFrom,
    required DateTime at,
  }) async {
    final session = ++_session;
    _failure.value = null;

    // Opened paused and played after the seek: a playlist begins at the keyframe before the
    // instant asked for, and starting playback first would show a second or two of footage from
    // before the click every single time.
    await _player.open(Media(playlist.toString()), play: false);
    if (session != _session) return;

    final offset = at.difference(windowFrom);
    if (offset > Duration.zero) {
      // Not straight after `open`. That call resolves before libmpv has the playlist parsed, and
      // a seek issued into that gap is discarded without error — measured on every open, at every
      // offset: `wanted=3426ms landed=0ms`. A known duration is the signal that there is
      // something to seek within.
      //
      // Bounded, because a source that never reports one has to play from its start rather than
      // hang here waiting to be positioned.
      if (_player.state.duration <= Duration.zero) {
        await _player.stream.duration
            .firstWhere((duration) => duration > Duration.zero)
            .timeout(
              const Duration(seconds: 2),
              onTimeout: () => Duration.zero,
            );
        if (session != _session) return;
      }

      await _player.seek(offset);
    }
    if (session != _session) return;

    await _player.play();
    if (session != _session) return;

    // Seeked again once playback has actually started, if the first one did not take.
    //
    // `Player.open` resolves before libmpv has necessarily parsed the playlist and worked out
    // what it can seek within, so a seek issued against it here can be silently dropped — and a
    // dropped seek means the picture starts where the *playlist* starts, which is the segment
    // boundary up to four seconds before the instant asked for. Playing on from there is the
    // step backwards at every window reopen.
    if (offset > Duration.zero) {
      final landed = _player.state.position;
      if ((landed - offset).abs() > const Duration(milliseconds: 80)) {
        await _player.seek(offset);
      }
    }
  }

  @override
  Future<void> play() => _player.play();

  @override
  Future<void> pause() => _player.pause();

  @override
  Future<void> seekWithin(Duration offset) => _player.seek(offset);

  @override
  Future<void> setRate(double rate) => _player.setRate(rate);

  bool _muted = false;
  double _volume = 1;

  /// The camera's gain and gate, held so either can be changed without the other being re-sent — they
  /// are one filter string, so both are rewritten whichever one moved.
  double _gainDb = 0;
  double? _gateRms;

  @override
  Future<void> setMuted(bool muted) {
    _muted = muted;
    return _applyVolume();
  }

  @override
  Future<void> setVolume(double volume) {
    _volume = volume;
    return _applyVolume();
  }

  /// One write for both. libmpv has a single volume and no separate mute, so unmuting has to know
  /// the level to come back to — a `setVolume(muted ? 0 : 100)` would restore full volume on every
  /// unmute and throw away whatever the viewer had set.
  Future<void> _applyVolume() =>
      _player.setVolume(_muted ? 0 : mpvVolume(_volume));

  @override
  Future<void> setGain(double db, double? gateRms) {
    _gainDb = db;
    _gateRms = gateRms;
    return _applyGain();
  }

  /// The gain, its gate and the limiter, as libmpv's `af` chain.
  ///
  /// Not through `setVolume`, which would be the obvious place: mpv's software amplification clips
  /// rather than limiting, and the `volume` property it writes is capped by `volume-max` — a ceiling
  /// this app never raises. The filter chain has neither constraint, and it is also where ffmpeg's
  /// `agate` and `alimiter` live, so the desktop gets the same three stages the browser builds out of
  /// WebAudio nodes.
  ///
  /// Safe to own `af` outright because [Player] is constructed with the default configuration, which
  /// leaves `pitch` off — media_kit writes this property itself only on its pitch branch, while
  /// `setRate` on the default path writes `speed`. If media_kit is ever upgraded, that is the
  /// assumption to re-check.
  Future<void> _applyGain() async {
    // A property write rather than an API call, so it has to go through the platform player. Guarded
    // rather than asserted: media_kit's web backend is a different class, and while this file is
    // never compiled for web, a cast that could throw at runtime should not be the thing standing
    // between a viewer and their audio.
    if (_player.platform case final NativePlayer native) {
      try {
        await native.setProperty('af', mpvAudioFilter(_gainDb, _gateRms));
      } on Object catch (error) {
        // Unboosted playback is a worse experience; no playback is a bug. A filter string libmpv
        // will not take should cost the boost and nothing else.
        debugPrint('Serval: libmpv rejected the audio filter chain ($error).');
      }
    }
  }

  @override
  Widget buildView() => Video(
    controller: _controller,
    controls: NoVideoControls,
    fit: BoxFit.contain,
    fill: Serval.tile,
  );

  @override
  Future<void> dispose() async {
    _session++;
    for (final subscription in _subscriptions) {
      await subscription.cancel();
    }
    await _player.dispose();
    _position.dispose();
    _duration.dispose();
    _playing.dispose();
    _videoSize.dispose();
    _failure.dispose();
  }
}
