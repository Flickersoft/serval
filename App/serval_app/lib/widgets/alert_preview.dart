import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/alert.dart';
import '../playback/playback_volume.dart';
import '../playback/vod_player.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'alert_still.dart';

/// An alert's preview clip, played where the card's picture is.
///
/// **Why an alert has a clip at all.** The design gave the card a still, because at the time a still
/// was all there was. A still cannot say whether the person walked away or tried the handle, and the
/// recording that could is not always there — a camera can be detecting while nobody is recording
/// it. So the Server keeps a rolling buffer of each camera's detect stream and cuts a short clip
/// around every alert, padded either side. This plays that.
///
/// It rests on the frame the alert fired on, box and all, and becomes the clip on the first press:
/// a queue is read, not watched, and twenty rows autoplaying at once is a queue nobody can read.
/// *Watch* is still the verb that leaves for the recording — this only answers "what was that",
/// which is the question a notification actually raises.
class AlertPreview extends StatefulWidget {
  const AlertPreview({
    super.key,
    required this.alert,
    this.clip,
    this.poster,
    this.showFiredLabel = true,
    this.volume,
    this.gateRms,
  });

  final Alert alert;

  /// The preview clip. Null draws the still and no controls rather than a dead play button — which
  /// is the state while the clip is being cut, on a build with no Server, and forever for an alert
  /// there was no footage for.
  final Uri? clip;

  final Uri? poster;

  /// Whether the time pill explains itself. The desktop card has the room for
  /// `8:12:04 am · the frame it fired on`; the phone shows the clock alone.
  final bool showFiredLabel;

  /// This alert's camera's stored volume, as a slider position, and that camera's gate. See
  /// [ClipPlayer.volume] — this player had the same omission, and an alert's whole job is to be
  /// heard.
  final ValueListenable<double>? volume;
  final double? gateRms;

  @override
  State<AlertPreview> createState() => _AlertPreviewState();
}

class _AlertPreviewState extends State<AlertPreview> {
  VodPlayer? _player;

  /// Until something is played, the still covers the video. A cover rather than a substitution:
  /// the surface has to be in the tree for the platform player to attach to it.
  bool _started = false;

  @override
  void initState() {
    super.initState();
    _openIfPossible();
    widget.volume?.addListener(_applyAudio);
  }

  @override
  void didUpdateWidget(AlertPreview oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.clip != widget.clip) _openIfPossible();

    if (oldWidget.volume != widget.volume) {
      oldWidget.volume?.removeListener(_applyAudio);
      widget.volume?.addListener(_applyAudio);
    }

    if (oldWidget.gateRms != widget.gateRms) _applyAudio();
  }

  void _openIfPossible() {
    final clip = widget.clip;
    if (clip == null) return;

    final player = _player ??= createVodPlayer();

    // Before and after, for the reason [ClipPlayer._openIfPossible] gives.
    _applyAudio();
    player.openFile(clip);
    _applyAudio();
  }

  void _applyAudio() {
    final player = _player;
    if (player == null) return;

    // Split as [ClipPlayer._applyAudio] splits it, for the reason given there.
    if (widget.volume case final travel?) {
      final level = playbackFromTravel(travel.value);
      player.setVolume(level.volume);
      player.setGain(level.db, widget.gateRms);
      return;
    }

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
    // The whole picture is the control, so the button can fade out of the way once it is playing
    // without taking the way to pause with it.
    child: GestureDetector(
      onTap: widget.clip == null ? null : _toggle,
      behavior: HitTestBehavior.opaque,
      child: Stack(
        fit: StackFit.expand,
        children: [
          if (_player case final player?) player.buildView(),
          if (!_started) ...[
            AlertStill(
              alert: widget.alert,
              poster: widget.poster,
              boxOpacity: 0.65,
            ),
            _timePill(),
          ],
          if (_player case final player?) _playButton(player),
          if (widget.alert.clipState.waiting) _waitingPill(),
        ],
      ),
    ),
  );

  /// The button, following the player rather than a snapshot of it.
  ///
  /// Built against the listenable because starting playback is asynchronous: `play()` returns
  /// before the picture moves, so a button rendered from `playing.value` at the moment of the tap
  /// draws the state the player is *leaving*. Read once, it says "play" over a clip that is already
  /// running and never corrects itself.
  Widget _playButton(VodPlayer player) => ValueListenableBuilder<bool>(
    valueListenable: player.playing,
    builder: (context, playing, _) => IgnorePointer(
      child: Center(
        child: _RoundPlay(playing: playing, faded: playing),
      ),
    ),
  );

  /// `8:12:04 am · the frame it fired on` — the label that makes the picture mean something.
  ///
  /// Seconds, unlike the row above it: the list groups by minute because that is enough to find a
  /// row, and the card is where you say exactly when.
  Widget _timePill() => Positioned(
    left: 12,
    top: 12,
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: Serval.panel.withValues(alpha: 0.72),
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
      ),
      child: Text(
        widget.showFiredLabel
            ? '${preciseClockLabel(widget.alert.peakAt)} · the frame it fired on'
            : preciseClockLabel(widget.alert.peakAt),
        style: monoStyle(
          fontSize: 10.5,
          color: Nocturne.mix(Nocturne.text, 60),
        ),
      ),
    ),
  );

  /// Says the clip is coming rather than leaving the card looking broken.
  ///
  /// An alert reaches the queue the moment it fires; its clip cannot exist until the seconds after
  /// it have been written. That gap is short and always the same length, so it is worth naming.
  Widget _waitingPill() => Positioned(
    right: 12,
    bottom: 12,
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: Serval.panel.withValues(alpha: 0.72),
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
      ),
      child: Text(
        'Clip in a moment',
        style: TextStyle(
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 55),
        ),
      ),
    ),
  );
}

class _RoundPlay extends StatelessWidget {
  const _RoundPlay({required this.playing, required this.faded});

  final bool playing;
  final bool faded;

  @override
  Widget build(BuildContext context) => AnimatedOpacity(
    opacity: faded ? 0.0 : 1.0,
    duration: const Duration(milliseconds: 180),
    child: Container(
      width: 52,
      height: 52,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: Serval.overlay.withValues(alpha: 0.75),
        shape: BoxShape.circle,
        border: Border.all(color: Nocturne.mix(Nocturne.accent, 55)),
      ),
      child: Icon(
        playing ? PhosphorIconsFill.pause : PhosphorIconsFill.play,
        size: 22,
        color: Nocturne.accent300,
      ),
    ),
  );
}
