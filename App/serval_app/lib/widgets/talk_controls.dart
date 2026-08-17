import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../playback/microphone_gate.dart';
import '../theme/nocturne.dart';

/// Hold to speak into the camera.
///
/// Deliberately a hold, not a toggle: you cannot leave the microphone open by
/// accident. Talk-back exists only on the single-camera view, so whatever you
/// say goes to the feed on screen.
///
/// The Server carries this over the *same* WebRTC session as the video — the
/// sending audio m-line rides the SDP offer already being POSTed to
/// `/api/cameras/{id}/webrtc`, and go2rtc routes it to the camera's audio
/// backchannel. There is no second signalling path. The camera must have
/// `twoWayAudio` set for the backchannel to be probed at all.
///
/// Pressing this is what *opens* the microphone — the m-line goes out empty and the device is
/// only asked for here — so the first press of a session can sit on a permission sheet for as
/// long as it takes to answer. [mic] is how that shows on the button rather than being guessed at.
class HoldToTalkButton extends StatefulWidget {
  const HoldToTalkButton({
    super.key,
    this.onTalkStart,
    this.onTalkEnd,
    this.enabled = true,
    this.disabledReason,
    this.mic,
    this.height = 44,
    this.expand = false,
  });

  /// The design pins this at 56 on a phone, where it is the biggest and lowest control on the
  /// screen; 44 is its size in a row of other controls.
  final double height;

  /// Takes the width it is given rather than hugging its label — the pinned form, which is a bar
  /// rather than a button and should be pressable anywhere along it.
  final bool expand;

  final VoidCallback? onTalkStart;
  final VoidCallback? onTalkEnd;

  /// False when the camera has no audio backchannel.
  final bool enabled;

  /// Shown in place of the label when there is a reason worth naming — currently only the browser
  /// withholding the microphone on an insecure origin, which no amount of retrying fixes.
  ///
  /// Deliberately the label rather than a tooltip: a dimmed button that does nothing when pressed
  /// is the same silent failure as a blank tile, and hover is not a thing on a phone. The other
  /// disabled cases (no backchannel on this camera, replaying rather than live) are already
  /// legible from the rest of the screen and pass null.
  final String? disabledReason;

  /// Where the live session's microphone has got to, if there is a live session at all.
  ///
  /// The listenable rather than the value, so a permission sheet being answered rebuilds this
  /// button and nothing else. Null reads as [MicStage.closed] — a replaying stage, or a repository
  /// that cannot stream live, has no session to ask and the button behaves as it always did.
  final ValueListenable<MicStage>? mic;

  @override
  State<HoldToTalkButton> createState() => _HoldToTalkButtonState();
}

class _HoldToTalkButtonState extends State<HoldToTalkButton> {
  bool _talking = false;
  bool _hovered = false;

  /// This is what opens the microphone, but only by way of the screen above and the session below,
  /// so `getUserMedia` runs a frame or two after the gesture rather than inside it. That is fine
  /// and does not want fixing: unlike `AudioContext.resume`, which the playback gain does depend
  /// on, `getUserMedia` is not gated on a live user-activation flag.
  void _start() {
    if (!widget.enabled) return;
    setState(() => _talking = true);
    widget.onTalkStart?.call();
  }

  void _end() {
    if (!_talking) return;
    setState(() => _talking = false);
    widget.onTalkEnd?.call();
  }

  @override
  Widget build(BuildContext context) {
    final mic = widget.mic;
    return mic == null
        ? _button(null)
        : ValueListenableBuilder<MicStage>(
            valueListenable: mic,
            builder: (context, stage, _) => _button(stage),
          );
  }

  /// [mic] is null where there is no live session to ask — a replay, or a wall that cannot stream.
  Widget _button(MicStage? mic) {
    // Held reads one step further up the accent ramp — the system's pressed
    // state — so the press itself is unmistakable.
    final tint = _talking
        ? 30.0
        : _hovered
        ? 24.0
        : 18.0;

    // The halo is the stronger claim — that a device is open to a room you are not in — so it
    // waits for the session to say one actually is. The first press of a session can sit on a
    // permission sheet for a while, and a button that haloes through it is lying.
    final open = _talking && (mic == null || mic == MicStage.open);

    final label = switch (mic) {
      _ when !widget.enabled && widget.disabledReason != null =>
        widget.disabledReason!,
      // Kept after a refusal rather than reverting, because *Hold to talk* on a button that has
      // just been refused a microphone is an invitation to press it again and hear nothing.
      MicStage.unavailable => 'Microphone unavailable',
      MicStage.closed ||
      MicStage.opening when _talking => 'Waiting for the microphone…',
      _ when _talking => 'Release to stop',
      _ => 'Hold to talk',
    };

    return Opacity(
      opacity: widget.enabled ? 1 : 0.45,
      child: MouseRegion(
        cursor: widget.enabled
            ? SystemMouseCursors.click
            : SystemMouseCursors.basic,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTapDown: (_) => _start(),
          onTapUp: (_) => _end(),
          onTapCancel: _end,
          child: Container(
            height: widget.height,
            width: widget.expand ? double.infinity : null,
            padding: const EdgeInsets.symmetric(horizontal: 20),
            decoration: BoxDecoration(
              color: Nocturne.mix(Nocturne.accent, tint),
              borderRadius: BorderRadius.circular(widget.height / 2),
              border: Border.all(
                color: Nocturne.mix(Nocturne.accent, _talking ? 85 : 65),
              ),
              // A held mic is the one state on either layout worth a halo: it says the microphone
              // is open to a room you are not in.
              boxShadow: open
                  ? [
                      BoxShadow(
                        color: Nocturne.mix(Nocturne.accent, 12),
                        blurRadius: 0,
                        spreadRadius: 6,
                      ),
                    ]
                  : null,
            ),
            child: Row(
              mainAxisSize: widget.expand ? MainAxisSize.max : MainAxisSize.min,
              mainAxisAlignment: widget.expand
                  ? MainAxisAlignment.center
                  : MainAxisAlignment.start,
              children: [
                PhosphorIcon(
                  PhosphorIconsFill.microphone,
                  size: widget.height >= 56 ? 20 : 18,
                  color: Nocturne.accent300,
                ),
                const SizedBox(width: 9),
                Flexible(
                  child: Text(
                    label,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: widget.height >= 56 ? 15.5 : 14,
                      fontWeight: Nocturne.headingWeight,
                      color: Nocturne.accent300,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A pill-shaped toggle in the video's control row — *Audio on*, *Subtitles*.
class VideoToggle extends StatefulWidget {
  const VideoToggle({
    super.key,
    required this.label,
    required this.icon,
    this.active = true,
    this.onTap,
  });

  final String label;
  final PhosphorIconData icon;
  final bool active;
  final VoidCallback? onTap;

  @override
  State<VideoToggle> createState() => _VideoToggleState();
}

class _VideoToggleState extends State<VideoToggle> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final color = widget.active
        ? Nocturne.mix(Nocturne.text, 80)
        : Nocturne.mix(Nocturne.text, 45);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          height: 44,
          padding: const EdgeInsets.symmetric(horizontal: 15),
          decoration: BoxDecoration(
            color: _hovered ? Nocturne.mix(Nocturne.text, 7) : null,
            borderRadius: BorderRadius.circular(22),
            border: Border.all(color: Nocturne.mix(Nocturne.text, 16)),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              PhosphorIcon(widget.icon, size: 17, color: color),
              const SizedBox(width: 7),
              Text(
                widget.label,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13.5,
                  color: color,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
