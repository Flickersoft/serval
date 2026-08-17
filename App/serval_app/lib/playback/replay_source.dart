import 'dart:async';

import 'package:flutter/foundation.dart';

import '../data/serval_repository.dart';
import '../models/timeline.dart';
import 'vod_player.dart';

/// Where a window opened at [from] should end.
///
/// Capped at [now], which is also how replay catches up: each reopen picks up whatever has been
/// recorded since, until the playhead is close enough to hand back to the live view.
///
/// And clipped to the end of the run of footage [from] sits in, so a playlist never spans a hole.
/// Not strictly required — `EXT-X-DISCONTINUITY` handles the decoder side — but a playlist that
/// claims a gap it cannot fill is a playlist that stalls in it.
///
/// Free of the class above because a wall of cameras computes this per camera against each one's
/// own coverage: they open at the same instant and end wherever their own footage does.
DateTime replayWindowEnd(
  DateTime from, {
  TimelineWindow? timeline,
  Duration window = ReplaySource.window,
  DateTime? now,
}) {
  var to = from.add(window);
  final ceiling = now ?? DateTime.now();
  if (to.isAfter(ceiling)) to = ceiling;

  for (final span in timeline?.coverage ?? const <CoverageSpan>[]) {
    if (span.contains(from) && span.to.isBefore(to)) to = span.to;
  }

  return to;
}

/// The transport, for whichever screen is driving playback.
///
/// Shared so the wall and the single camera cannot offer different speeds, or disagree about where
/// playing gives way to stepping — the two are the same control in two places, and a 4x that meant
/// something different depending on which screen you were on would be worse than not offering it.
abstract final class ReplayRates {
  static const values = <int>[1, 2, 4, 8];

  /// Above this the player is parked and seeked rather than played.
  ///
  /// hls.js cannot keep a buffer at 8x — it is fetching segments eight times faster than they play
  /// — so past this the picture is stepped instead, which reads as a fast flip-book and, unlike a
  /// stall, is legible.
  static const maxPlay = 4;

  /// How often a parked player is moved on while stepping. The step *is* the playback, so this is
  /// the frame rate you see; once a second reads as a stall between jumps rather than fast-forward.
  static const stepPeriod = Duration(milliseconds: 250);
}

/// How an open attempt ended.
///
/// [superseded] is not a failure and must not be shown as one: a fast drag or a camera switch
/// fires several opens, and the one that resolves last is not necessarily the one that was asked
/// for last. The loser has already had its state overwritten by the winner and has nothing left to
/// say.
enum ReplayOpen { opened, superseded, failed }

typedef ReplayOpenResult = ({ReplayOpen outcome, String? message});

/// One camera's replay: a player, the window of recording it has open, and the arithmetic that
/// turns a wall-clock instant into a position inside it.
///
/// Split out of `ReplayController` so that more than one camera can be replayed at once without a
/// second copy of the segment-boundary correction below. That correction is subtle, it is the
/// thing that decides whether a detection box lands on the right frame, and two implementations of
/// it would disagree in exactly the way nothing would report.
///
/// Deliberately holds no policy. Where to go when the footage runs out, what a drag means, whether
/// an instant is close enough to now to hand back to the live view — those differ between one
/// camera and a wall of them, and they live in the controller that owns this.
class ReplaySource {
  ReplaySource({
    required this.repository,
    required this.cameraId,
    VodPlayer Function()? playerFactory,
    DateTime Function()? clock,
    this._muted = false,
  }) : _playerFactory = playerFactory ?? createVodPlayer,
       _clock = clock ?? DateTime.now;

  final ServalRepository repository;
  final String cameraId;

  /// How a player gets made. Injectable so the window arithmetic — which URL is opened for which
  /// instant, when a drag costs a request and when it does not — is testable without libmpv, a
  /// decoder, or a video surface. Everything else here is exactly what ships.
  final VodPlayer Function() _playerFactory;

  /// Injectable alongside [_playerFactory], and for the same reason: an open costs real time, the
  /// picture keeps moving while it does, and a test must be able to spend that time without
  /// waiting for it.
  final DateTime Function() _clock;

  /// How much of the recording one playlist covers. ~225 four-second segments, against ~21,600
  /// for a day. The cost is a reopen — one playlist fetch and a decoder reset, so a visible
  /// hitch — roughly every fifteen minutes of continuous replay.
  ///
  /// Cannot go below twice [reopenGuard]: under that a reopen buys less than the guard that asked
  /// for it, `worthReopening` rejects it, and playback runs to the end of the playlist and stops
  /// rather than turning over. Worth knowing if this is ever turned down to watch a turnover
  /// happen — at 90 s one lands every minute of replay, and at 60 s it stalls instead.
  static const window = Duration(minutes: 15);

  /// Reopen this far before the open window runs out. HLS VOD playlists cannot be appended to, so
  /// running to the end would stall rather than continue.
  static const reopenGuard = Duration(seconds: 30);

  VodPlayer? _player;
  VodPlayer? get player => _player;

  bool get isOpen => _player != null;

  DateTime? _windowFrom;
  DateTime? _windowTo;

  DateTime? get windowFrom => _windowFrom;
  DateTime? get windowTo => _windowTo;

  final _position = ValueNotifier<Duration>(Duration.zero);

  /// How far into the open window the picture is.
  ///
  /// Mirrors the player's own notifier rather than exposing it, so a listener survives the player
  /// being built on first open and disposed on close.
  ValueListenable<Duration> get position => _position;

  /// Where the open playlist's media actually begins, relative to [windowFrom].
  ///
  /// Segments are four seconds long and a window can be asked for at any instant inside one, so a
  /// playlist starts at the boundary at or before what was requested and playback position counts
  /// from *there*. The picture at position `p` is therefore at `windowFrom - startOffset + p`,
  /// and dropping the term reads up to a whole segment ahead of the frame on screen.
  Duration _startOffset = Duration.zero;

  /// The wall-clock instant showing at a given playback position.
  DateTime instantAt(Duration position) =>
      _windowFrom!.subtract(_startOffset).add(position);

  /// Where to seek within the open playlist to show [at].
  Duration offsetOf(DateTime at) => at.difference(_windowFrom!) + _startOffset;

  /// The instant on screen right now, or null with nothing open.
  DateTime? get instant =>
      _windowFrom == null ? null : instantAt(_position.value);

  /// Re-applied on every [openWindow], which is what makes the constructor's value stick: a window
  /// is opened afresh roughly every fifteen minutes, and anything not re-applied there silently
  /// reverts to the player's own default the next time playback runs off the end.
  bool _muted;

  double _volume = 1;
  double _rate = 1;

  /// The camera's own gain and gate, held and re-applied alongside the three above. Not because a
  /// fresh source resets them — the gain is applied outside the player's own volume on both
  /// platforms — but because they are set once when the camera is chosen and a player built later
  /// would otherwise never hear about them.
  double _gainDb = 0;
  double? _gateRms;

  double get rate => _rate;

  /// Guards a slow open against a newer one — the same idiom `WebRtcView` uses for its WebRTC
  /// session, and the same race: a fast drag or a camera switch fires several opens, and the one
  /// that resolves last is not necessarily the one that was asked for last.
  int _session = 0;

  /// A seek is in flight, and the last offset asked for while it was.
  ///
  /// A drag inside the open window fires per pointer sample — up to 120 Hz on a trackpad — and
  /// handing every one of those straight to the decoder is re-buffer churn that reads as the
  /// picture refusing to track. Only one seek is ever outstanding; samples that arrive during it
  /// overwrite each other, and the last one runs when it lands.
  bool _seeking = false;
  Duration? _pendingSeek;

  /// A source is being swapped under the player, so its position says nothing about this window.
  bool _loading = false;

  /// Whether [at] can be shown without opening a new playlist.
  bool covers(DateTime at) =>
      _player != null &&
      _windowFrom != null &&
      _windowTo != null &&
      !at.isBefore(_windowFrom!) &&
      at.isBefore(_windowTo!.subtract(reopenGuard));

  /// Whether [at] falls inside the open window at all — including the last [reopenGuard] of it,
  /// which [covers] deliberately excludes.
  ///
  /// The gap between the two is what separates a reopen driven by playback, where the playhead has
  /// run into the guard at the end of its own window, from one driven by a seek, where it has gone
  /// somewhere else entirely. Only the first is ever optional.
  bool holds(DateTime at) =>
      _windowFrom != null &&
      _windowTo != null &&
      !at.isBefore(_windowFrom!) &&
      at.isBefore(_windowTo!);

  /// Whether a window ending at [to] would buy enough to be worth opening.
  ///
  /// A reopen costs a decoder reset — hls.js detaches the media element to swap playlists — so the
  /// picture drops for as long as the new playlist takes to fetch its init and first segment. That
  /// is affordable every fifteen minutes and unaffordable four times a second, which is what an
  /// unguarded reopen does near the live edge: [replayWindowEnd] caps the end at `now`, so each new
  /// window is a quarter of a second longer than the one it replaced and lands straight back inside
  /// the guard that asked for it. A window that does not reach past that guard is not opened.
  bool worthReopening(DateTime to) =>
      _windowTo == null || to.isAfter(_windowTo!.add(reopenGuard));

  /// Whether playback has run close enough to the end of the open window to want the next one.
  bool get needsReopen {
    final to = _windowTo;
    final at = instant;
    return to != null && at != null && at.isAfter(to.subtract(reopenGuard));
  }

  /// One decoder seek at a time, last offset wins.
  Future<void> seekWithin(Duration offset) async {
    if (_seeking) {
      _pendingSeek = offset;
      return;
    }

    _seeking = true;
    try {
      var next = offset;
      while (true) {
        await _player?.seekWithin(next);
        final pending = _pendingSeek;
        if (pending == null) break;
        _pendingSeek = null;
        next = pending;
      }
    } finally {
      _seeking = false;
      _pendingSeek = null;
    }
  }

  /// Opens the playlist covering [from] to [to] and plays from [from].
  ///
  /// [streamToken] lets a caller opening several cameras at once mint one token for all of them.
  /// Passing null mints one here, which is what a single camera wants: a long replay spanning
  /// several reopens must never play against an expired token.
  ///
  /// [onWindowSet] fires once the window is committed and before the playlist is awaited, so a
  /// caller can move its playhead with the gesture rather than a round trip later. Its argument
  /// says whether this open built the player — true only on the first, because the player is
  /// reused precisely so that reopening does not drop the video surface and flash the stage.
  Future<ReplayOpenResult> openWindow(
    DateTime from,
    DateTime to, {
    String? streamToken,
    bool followingPlayback = false,
    void Function(bool playerCreated)? onWindowSet,
  }) async {
    final requestedAt = _clock();

    final baseUrl = repository.vodUrlFor(cameraId, from: from, to: to);
    if (baseUrl == null || !to.isAfter(from)) {
      return (
        outcome: ReplayOpen.failed,
        message: 'There is no recording to play here.',
      );
    }

    final session = ++_session;

    // The player fetches this playlist and every `.m4s` under it directly, not through
    // ServalApi, so it cannot carry an Authorization header — the token travels as a query
    // parameter instead.
    // Started together and awaited together: the token and the playlist's own start offset are
    // each one request against the Server, and neither depends on the other, so serialising them
    // would put a round trip between the tap and the first frame for nothing.
    final tokenRequest = streamToken == null
        ? repository.mintStreamToken()
        : Future<String?>.value(streamToken);
    final offsetRequest = repository.vodStartOffsetFor(
      cameraId,
      from: from,
      to: to,
    );
    final token = await tokenRequest;
    final startOffset = await offsetRequest;

    if (session != _session) {
      return (outcome: ReplayOpen.superseded, message: null);
    }

    final url = token == null
        ? baseUrl
        : baseUrl.replace(
            queryParameters: {
              ...baseUrl.queryParameters,
              'stream_token': token,
            },
          );

    final isNew = _player == null;
    final player = _player ??= _playerFactory();
    _windowFrom = from;
    _windowTo = to;
    _startOffset = startOffset;

    // The boxes for this stretch of footage, fetched alongside the footage. Not awaited: the
    // video should start when it can rather than behind a telemetry request, and the overlay
    // appears when its answer lands.
    unawaited(repository.ensureReplayDetections(cameraId, from, to));

    if (isNew) {
      player.position.addListener(_onPlayerPosition);
      _position.value = player.position.value;
    }
    onWindowSet?.call(isNew);

    // The window is committed but the player is still being torn down and reloaded, and a
    // position measured against the source it is leaving means nothing against the one it is
    // joining. The web player's element reports zero the moment its media is detached, which
    // against a fresh window reads as `from - startOffset` — a playhead that jumps back by a
    // segment, on a picture that has not moved at all.
    _loading = true;

    try {
      // Before the open, and again after it.
      //
      // Before, because `open` starts playback: a player carrying its own defaults into a fresh
      // source is a player that is briefly unmuted and at 1x, and on a muted wall that is an
      // audible blip every time a window turns over. The web player reads these back off its held
      // fields while loading, so setting them first is what makes the first frame correct.
      //
      // After, because a window is opened afresh each time and a player that resets on load would
      // otherwise revert the moment playback ran past the window's edge — the trap the mute line
      // was already here to avoid, and the volume and rate fall into it identically.
      await _applySettings(player);

      // `windowFrom` is where the playlist's *media* begins, which is the segment boundary at or
      // before the instant asked for — not the instant itself. Passing `from` for both made the
      // offset zero, so the explicit seek both players do was always a no-op and the picture
      // started wherever the playlist did: up to a whole segment before the instant asked for,
      // and visibly backwards from wherever playback had already reached. `EXT-X-START` covers
      // that for hls.js once it has parsed the manifest, which is after the first frames can
      // already be on screen, and ffmpeg's HLS demuxer does not implement the tag at all. That
      // seek is authoritative: measured against a real playlist, a `currentTime` written before
      // `play()` wins over the tag rather than losing to it.
      //
      // And it opens where the picture has *got to*, not where the reopen was decided. The two
      // requests above are a round trip each and the old window plays throughout them, so a
      // reopen that opened at `from` would put its first frame behind the last frame of the
      // window it replaced — the picture stepping back by however long the Server took to
      // answer. Only for a reopen: a seek has to land exactly where it was asked to.
      final playedOn = followingPlayback
          ? _clock().difference(requestedAt)
          : Duration.zero;

      await player.open(
        url,
        windowFrom: from.subtract(startOffset),
        at: from.add(playedOn),
      );
      if (session != _session) {
        return (outcome: ReplayOpen.superseded, message: null);
      }

      await _applySettings(player);
      return (outcome: ReplayOpen.opened, message: null);
    } on Object catch (error) {
      if (session != _session) {
        return (outcome: ReplayOpen.superseded, message: null);
      }
      return (
        outcome: ReplayOpen.failed,
        message: 'Could not play this recording: $error',
      );
    } finally {
      // Only when this open is still the current one: a newer one has already claimed the flag,
      // and clearing it here would let the loser's teardown positions through after all.
      if (session == _session) {
        _loading = false;
        // The window was opened at [from], so that is where the playhead stands until the decoder
        // reports otherwise — and [offsetOf] of `from` is exactly the start offset.
        //
        // Not the player's own position, which is the whole trap: a `<video>` reports zero for as
        // long as its media is detached, and its notifier carries that zero until the next
        // `timeupdate` — which is after this returns. Republishing it here undoes the suppression
        // above and puts the playhead a segment into the past, on a picture that never moved.
        _position.value = _startOffset;
      }
    }
  }

  /// Everything the player holds that a fresh source would otherwise reset.
  Future<void> _applySettings(VodPlayer player) async {
    await player.setMuted(_muted);
    await player.setVolume(_volume);
    await player.setRate(_rate);
    await player.setGain(_gainDb, _gateRms);
  }

  Future<void> setMuted(bool muted) async {
    _muted = muted;
    await _player?.setMuted(muted);
  }

  Future<void> setVolume(double volume) async {
    _volume = volume;
    await _player?.setVolume(volume);
  }

  Future<void> setRate(double rate) async {
    _rate = rate;
    await _player?.setRate(rate);
  }

  Future<void> setGain(double db, double? gateRms) async {
    _gainDb = db;
    _gateRms = gateRms;
    await _player?.setGain(db, gateRms);
  }

  Future<void> play() async => _player?.play();

  Future<void> pause() async => _player?.pause();

  /// Tears down the player and forgets the window.
  ///
  /// The start offset goes with it: the next window has its own, and carrying this one across
  /// would misplace the playhead by the difference rather than by the whole segment.
  Future<void> close() async {
    _session++;
    final player = _player;
    _player = null;
    _loading = false;
    _windowFrom = null;
    _windowTo = null;
    _startOffset = Duration.zero;
    _position.value = Duration.zero;

    player?.position.removeListener(_onPlayerPosition);
    await player?.dispose();
  }

  void _onPlayerPosition() {
    if (_loading) return;

    // Below the start offset is before [windowFrom], and nothing can ask to go there: `covers`
    // refuses an instant before the window starts, so every legitimate seek lands at or after
    // this. What does arrive down here is a player between sources reporting zero, and taken at
    // face value it moves the playhead back by a whole segment while the picture stands still.
    final position = _player!.position.value;
    if (position < _startOffset) return;

    _position.value = position;
  }

  Future<void> dispose() async {
    await close();
    _position.dispose();
  }
}
