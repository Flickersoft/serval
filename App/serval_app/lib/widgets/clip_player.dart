import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/camera.dart';
import '../models/saved_clip.dart';
import '../playback/playback_volume.dart';
import '../playback/vod_player.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'tile_placeholder.dart';

/// A saved clip, played in place.
///
/// In place is the whole point: 13a's column plays the clip you picked without leaving the grid,
/// so choosing between six clips does not cost six screen loads. That means one player owned by
/// the panel rather than a route per clip.
class ClipPlayer extends StatefulWidget {
  const ClipPlayer({
    super.key,
    required this.clip,
    required this.source,
    this.poster,
    this.compact = false,
    this.volume,
    this.gateRms,
  });

  final SavedClip clip;

  /// Where the video is. Null on a build with no Server behind it, which draws the poster and no
  /// controls rather than a dead play button.
  final Uri? source;

  /// The clip's own frame, shown until something is played.
  ///
  /// Null falls through to the camera's stripe. Worth having even though the video could show its
  /// own first frame: that would cost a decode on every clip somebody clicked past, and the poster
  /// is the middle of the clip rather than whatever was happening a moment before it.
  final Uri? poster;

  /// The phone's taller stage, with its own scrub bar and clock.
  final bool compact;

  /// This clip's camera's stored volume, as a slider position, and that camera's gate. Both are
  /// wired so a saved clip honours the level set on every other surface in the app — without them a
  /// quiet camera's clip is the loudest thing about it.
  ///
  /// Null [volume] leaves the player at its default, which is what a build with no Server behind it
  /// wants — there is no stored level to honour, and nothing to lift.
  final ValueListenable<double>? volume;
  final double? gateRms;

  @override
  State<ClipPlayer> createState() => ClipPlayerState();
}

class ClipPlayerState extends State<ClipPlayer> {
  VodPlayer? _player;

  /// Whether anything has been played yet. Until it has, the poster covers the video.
  ///
  /// A cover rather than a substitution, and the difference is load-bearing: the surface has to be
  /// in the tree for the platform player to attach to it, and the web player cannot open a file
  /// before it has an element. Building the surface only on the first press meant `play()` ran
  /// against a `<video>` that did not exist yet, so the first press did nothing at all.
  bool _started = false;

  /// How far into the clip the picture is — handed up so the transcript can follow it.
  ValueListenable<Duration>? get position => _player?.position;

  @override
  void initState() {
    super.initState();
    _openIfPossible();
    widget.volume?.addListener(_applyAudio);
  }

  @override
  void didUpdateWidget(ClipPlayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.source != widget.source) _openIfPossible();

    if (oldWidget.volume != widget.volume) {
      oldWidget.volume?.removeListener(_applyAudio);
      widget.volume?.addListener(_applyAudio);
    }

    if (oldWidget.gateRms != widget.gateRms) _applyAudio();
  }

  void _openIfPossible() {
    final source = widget.source;
    if (source == null) return;

    final player = _player ??= createVodPlayer();

    // Before the open and again after it, the rule `ReplaySource` states: a player reverts to its own
    // defaults when a fresh source lands on it, so the call before is what makes the first moment
    // correct and the call after is what keeps it that way.
    _applyAudio();
    player.openFile(source);
    _applyAudio();
  }

  void _applyAudio() {
    final player = _player;
    if (player == null) return;

    // One position splits into the two the player takes — see [playbackFromTravel]. Below unity that
    // is an attenuation and no filter chain; above it the player sits at unity and the lift rides
    // behind it.
    if (widget.volume case final travel?) {
      final level = playbackFromTravel(travel.value);
      player.setVolume(level.volume);
      player.setGain(level.db, widget.gateRms);
      return;
    }

    // No stored position to honour: the player keeps its own volume, and nothing is lifted.
    player.setGain(0, widget.gateRms);
  }

  @override
  void dispose() {
    widget.volume?.removeListener(_applyAudio);
    _player?.dispose();
    super.dispose();
  }

  Future<void> _toggle() async {
    final player = _player;
    if (player == null) return;

    setState(() => _started = true);
    player.playing.value ? await player.pause() : await player.play();
  }

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: Serval.tile,
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Stack(
      fit: StackFit.expand,
      children: [
        if (_player case final player?) player.buildView(),

        // Over the video rather than instead of it — see [_started]. The stripe is under the
        // poster so that a poster which fails to load leaves the camera's own ground rather than
        // a gap, and a clip whose poster could not be made still reads as that camera.
        if (!_started) ...[
          TilePlaceholderView(
            placeholder: TilePlaceholder.forCameraId(widget.clip.cameraId),
          ),
          if (widget.poster case final poster?)
            Image.network(
              poster.toString(),
              // The fit the video underneath takes, so pressing play moves the picture instead of
              // re-framing it: they are frames of the same clip, and a `cover` poster over a
              // `contain` video visibly re-crops on the first one that plays.
              fit: BoxFit.contain,
              // The bars get the video ground, and only once there is a picture for them to be the
              // edge of — painted from the first build they would blank the stripe for the length
              // of a fetch, saying this camera has no picture where the stripe says one is coming.
              frameBuilder: (context, child, frame, _) => frame == null
                  ? child
                  : ColoredBox(color: Serval.tile, child: child),
              errorBuilder: (context, error, stack) => const SizedBox.shrink(),
            ),
        ],

        if (widget.compact) _compactOverlay() else _desktopOverlay(),
      ],
    ),
  );

  /// 13a: a play button in the middle over the poster, and a thin progress line at the foot.
  Widget _desktopOverlay() => Stack(
    children: [
      if (!_started)
        Center(
          child: _RoundPlay(
            playing: false,
            size: 52,
            onPressed: widget.source == null ? null : _toggle,
          ),
        ),
      Positioned(
        left: 12,
        right: 12,
        bottom: 11,
        child: Row(
          spacing: 9,
          children: [
            if (_started)
              _RoundPlay(
                playing: _player?.playing.value ?? false,
                size: 28,
                onPressed: _toggle,
              ),
            Expanded(child: _progressBar(height: 3)),
            _clock(compact: false),
          ],
        ),
      ),
    ],
  );

  /// 13c: the same stage taller, with a real scrub bar and the OSD time in the corner.
  Widget _compactOverlay() => Stack(
    children: [
      Positioned(
        left: 12,
        top: 12,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: Serval.panel.withValues(alpha: 0.72),
            borderRadius: BorderRadius.circular(20),
            border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
          ),
          child: _WallClock(clip: widget.clip, position: position),
        ),
      ),
      Positioned(
        left: 0,
        right: 0,
        bottom: 0,
        child: Container(
          padding: const EdgeInsets.fromLTRB(12, 36, 12, 12),
          decoration: BoxDecoration(
            gradient: LinearGradient(
              begin: Alignment.bottomCenter,
              end: Alignment.topCenter,
              colors: [
                Serval.overlay.withValues(alpha: 0.72),
                const Color(0x00000000),
              ],
            ),
          ),
          child: Row(
            spacing: 11,
            children: [
              _RoundPlay(
                playing: _player?.playing.value ?? false,
                size: 38,
                onPressed: widget.source == null ? null : _toggle,
              ),
              Expanded(child: _progressBar(height: 4, knob: true)),
              _clock(compact: true),
            ],
          ),
        ),
      ),
    ],
  );

  Widget _progressBar({required double height, bool knob = false}) {
    final player = _player;
    if (player == null) return _emptyBar(height);

    return ValueListenableBuilder<Duration>(
      valueListenable: player.position,
      builder: (context, at, _) {
        // The clip's own length rather than the player's, where the player has not reported one
        // yet: the Server measured this file and the bar must not be a different width for the
        // first second after it opens.
        final total = player.duration.value ?? widget.clip.duration;
        final fraction = total <= Duration.zero
            ? 0.0
            : (at.inMilliseconds / total.inMilliseconds).clamp(0.0, 1.0);

        return LayoutBuilder(
          builder: (context, constraints) => GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapDown: (details) =>
                _scrubTo(details.localPosition.dx, constraints.maxWidth, total),
            onHorizontalDragUpdate: (details) =>
                _scrubTo(details.localPosition.dx, constraints.maxWidth, total),
            child: SizedBox(
              height: knob ? 16 : height + 8,
              child: Center(
                child: Stack(
                  clipBehavior: Clip.none,
                  children: [
                    _emptyBar(height),
                    FractionallySizedBox(
                      widthFactor: fraction,
                      child: Container(
                        height: height,
                        decoration: BoxDecoration(
                          color: Nocturne.accent300,
                          borderRadius: BorderRadius.circular(height / 2),
                        ),
                      ),
                    ),
                    if (knob)
                      Positioned(
                        left: (constraints.maxWidth * fraction) - 6,
                        top: -4,
                        child: Container(
                          width: 12,
                          height: 12,
                          decoration: const BoxDecoration(
                            color: Nocturne.accent300,
                            shape: BoxShape.circle,
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
          ),
        );
      },
    );
  }

  void _scrubTo(double x, double width, Duration total) {
    if (width <= 0 || total <= Duration.zero) return;

    final fraction = (x / width).clamp(0.0, 1.0);
    setState(() => _started = true);
    _player?.seekWithin(total * fraction);
  }

  Widget _emptyBar(double height) => Container(
    height: height,
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 22),
      borderRadius: BorderRadius.circular(height / 2),
    ),
  );

  /// `0:19 / 0:55` on a phone; just the length in the desktop column, which has less room and a
  /// header already saying when the clip is from.
  Widget _clock({required bool compact}) {
    final style = monoStyle(
      fontSize: compact ? 11 : 10,
      color: Nocturne.mix(Nocturne.text, compact ? 75 : 60),
    );

    final player = _player;
    if (player == null || !_started) {
      return Text(clipLengthLabel(widget.clip.duration), style: style);
    }

    return ValueListenableBuilder<Duration>(
      valueListenable: player.position,
      builder: (context, at, _) => Text(
        compact
            ? '${clipLengthLabel(at)} / ${clipLengthLabel(widget.clip.duration)}'
            : clipLengthLabel(widget.clip.duration),
        style: style,
      ),
    );
  }
}

/// The wall-clock time of the frame on screen, which is what an operator quotes.
class _WallClock extends StatelessWidget {
  const _WallClock({required this.clip, required this.position});

  final SavedClip clip;
  final ValueListenable<Duration>? position;

  @override
  Widget build(BuildContext context) {
    final style = monoStyle(
      fontSize: 10.5,
      color: Nocturne.mix(Nocturne.text, 60),
    );

    if (position case final position?) {
      return ValueListenableBuilder<Duration>(
        valueListenable: position,
        builder: (context, at, _) =>
            Text(preciseClockLabel(clip.from.add(at)), style: style),
      );
    }

    return Text(preciseClockLabel(clip.from), style: style);
  }
}

class _RoundPlay extends StatelessWidget {
  const _RoundPlay({
    required this.playing,
    required this.size,
    required this.onPressed,
  });

  final bool playing;
  final double size;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => GestureDetector(
    onTap: onPressed,
    child: Container(
      width: size,
      height: size,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: Serval.overlay.withValues(alpha: 0.75),
        shape: BoxShape.circle,
        border: Border.all(color: Nocturne.mix(Nocturne.accent, 55)),
      ),
      child: Icon(
        playing ? PhosphorIconsFill.pause : PhosphorIconsFill.play,
        size: size * 0.42,
        color: onPressed == null
            ? Nocturne.mix(Nocturne.text, 30)
            : Nocturne.accent300,
      ),
    ),
  );
}
