import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../playback/playback_volume.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// How loud this camera is, and whether it is playing at all.
///
/// One control, because "make this louder" is one question. The track reads 0 to 100 and nothing
/// else: full volume with nothing added sits at [unityTravel] — 75%, marked — and the quarter above
/// it amplifies. A camera too quiet to make out is fixed by dragging further right, which is the only
/// thing anyone tries.
///
/// How much amplification is deliberately not on screen. The top of the track is ten times, and a
/// readout that said so would be four digits of arithmetic nobody asked for. The colour past the mark
/// is the whole of what a listener needs to know.
///
/// The position is remembered per camera on this machine, so the quiet side yard opens loud and the
/// doorbell opens where it was left. That is why there is no second control for the camera itself:
/// the one place a camera's loudness is decided is the place you find out it is wrong, and you find
/// that out while listening.
///
/// The speaker glyph is the mute button. Muting and the level are distinct settings — muting stops
/// the audio arriving at all, the level scales what does — but they are one question, and they
/// belong in one pill: the glyph is where a player puts mute, and it already draws the level's
/// state.
class VolumeControl extends StatelessWidget {
  const VolumeControl({
    super.key,
    required this.volume,
    required this.onChanged,
    required this.muted,
    required this.onMutedChanged,
  });

  /// The slider's position, 0..1, as [playbackFromTravel] reads it — not the volume a player takes.
  ///
  /// Rides a listenable so a drag rebuilds this pill and nothing else. At one rebuild per pointer
  /// sample, a `setState` on the screen would relayout the stage, the transcript panel and the
  /// scrubber for the length of the gesture — the convention this codebase states for the
  /// playhead and for camera frames, and a drag is the same shape of problem.
  final ValueListenable<double> volume;

  final ValueChanged<double> onChanged;

  final bool muted;
  final ValueChanged<bool> onMutedChanged;

  static PhosphorIconData _glyph(double travel, bool muted) {
    // The crossed glyph, not the empty one: silence you chose reads differently from a level
    // dragged to nothing.
    if (muted) return PhosphorIconsRegular.speakerSimpleSlash;
    if (travel <= 0) return PhosphorIconsRegular.speakerSimpleNone;
    // Half way to unity rather than half way along the track, so the glyph still turns over at the
    // level it describes rather than a quarter of the way into the amplifying half.
    if (travel < unityTravel / 2) return PhosphorIconsRegular.speakerSimpleLow;
    return PhosphorIconsRegular.speakerSimpleHigh;
  }

  @override
  Widget build(BuildContext context) => Container(
    height: 44,
    // Asymmetric: the glyph carries its own 34px target, and the pill's 15px repeated inside that
    // would push the whole row across. This lands the glyph where the right-hand inset implies.
    padding: const EdgeInsets.only(left: 6, right: 15),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(22),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 16)),
    ),
    child: ValueListenableBuilder<double>(
      valueListenable: volume,
      builder: (context, value, _) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Tooltip(
            message: muted ? 'Unmute' : 'Mute',
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: GestureDetector(
                onTap: () => onMutedChanged(!muted),
                // The glyph is 17px; the target is the pill's full height and a comfortable
                // width around it, because a 17px hit box is a dart-throw with a mouse.
                behavior: HitTestBehavior.opaque,
                child: SizedBox(
                  width: 34,
                  height: 44,
                  child: Center(
                    child: PhosphorIcon(
                      _glyph(value, muted),
                      size: 17,
                      // Dimmed while muted, like every other inactive control in the row.
                      color: Nocturne.mix(Nocturne.text, muted ? 45 : 80),
                    ),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(width: 8),
          // Faded while muted, but still live: reaching for the level is itself a statement that
          // you want to hear something, so it unmutes rather than setting a level nothing is
          // playing at.
          Opacity(
            opacity: muted ? 0.45 : 1,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                // Fixed rather than intrinsic: the pill must not resize as the readout goes from
                // one digit to three, which would shuffle every control to its right mid-drag.
                //
                // Wide enough that the amplifying quarter is a quarter of 170px rather than of 96 —
                // 42px to cross 20 dB is coarse, and any less would make the top of the range
                // something you land on by accident.
                SizedBox(
                  width: 170,
                  child: Tooltip(
                    message: 'Volume. Past the mark this camera is amplified.',
                    child: _VolumeTrack(
                      value: value,
                      onChanged: (next) {
                        if (muted) onMutedChanged(false);
                        onChanged(next);
                      },
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                SizedBox(
                  // Sized for "100%" rather than for what is showing.
                  width: 34,
                  child: Text(
                    volumeLabel(value),
                    textAlign: TextAlign.right,
                    style: TextStyle(
                      fontFamily: Nocturne.fontMono,
                      fontSize: 12,
                      // Lit once the level is amplifying, because that is a state worth noticing:
                      // it is the reason a camera might be hissing.
                      color: value > unityTravel
                          ? Serval.alert
                          : Nocturne.mix(Nocturne.text, 55),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    ),
  );
}

/// The volume track: a rail, a two-tone fill, a mark at unity and a knob that snaps to it.
///
/// Not `NocturneSlider`, whose look this keeps. That one is the retention track and has four
/// unrelated users; a second colour past a threshold, a mark on the rail and a detent are facts
/// about volume, and a slider shared with "keep recordings for N days" should not carry them.
///
/// Painted rather than stacked, which the retention track has no need to be. Three things have to
/// land on the same pixel here — the mark, the colour seam and the knob at unity — and a knob that
/// rides *inside* the rail does not travel the rail's full width, so a fill placed by fraction and a
/// knob placed in its own inset space disagree everywhere except the middle. On a plain slider that
/// is invisible. On this one it puts the knob three pixels off the one point the track labels, which
/// is the whole reason the mark is there. [_knobCentre] is that single coordinate system.
class _VolumeTrack extends StatelessWidget {
  const _VolumeTrack({required this.value, required this.onChanged});

  final double value;
  final ValueChanged<double> onChanged;

  static const _knob = 15.0;
  static const _height = 22.0;

  /// How close to the mark counts as on it, in logical pixels either side.
  ///
  /// A continuous track whose one labelled point cannot be hit exactly is the confusion this control
  /// exists to remove, wearing a different hat: 100% is where almost everyone wants to sit, and
  /// "99%" under the knob reads as a control fighting back.
  static const _detent = 5.0;

  /// Where the knob's centre sits for a position, given the track's width.
  ///
  /// Inset by half a knob at each end so it rides the rail rather than hanging off it — the rule the
  /// retention track states. Everything else on the track is placed through this, so the geometry is
  /// stated once.
  static double _knobCentre(double travel, double width) =>
      _knob / 2 + travel.clamp(0.0, 1.0) * (width - _knob);

  @override
  Widget build(BuildContext context) {
    void report(double width, Offset local) {
      if (width <= _knob) return;

      // The inverse of [_knobCentre], so the knob lands under the finger rather than a few pixels
      // inside it.
      final picked = ((local.dx - _knob / 2) / (width - _knob)).clamp(0.0, 1.0);
      final onMark =
          (local.dx - _knobCentre(unityTravel, width)).abs() <= _detent;
      onChanged(onMark ? unityTravel : picked);
    }

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      // The width comes from the constraints rather than from walking to a render object: this track
      // is always given a fixed width by the pill, and a measurement taken during layout cannot
      // disagree with the box the gesture arrives in.
      child: LayoutBuilder(
        builder: (context, constraints) => GestureDetector(
          onTapDown: (details) =>
              report(constraints.maxWidth, details.localPosition),
          onHorizontalDragUpdate: (details) =>
              report(constraints.maxWidth, details.localPosition),
          // Behavior.opaque so the whole 22px band is grabbable, not just the 4px rail — the
          // rail is a drawing, the target is the row.
          behavior: HitTestBehavior.opaque,
          child: CustomPaint(
            painter: _VolumeTrackPainter(value.clamp(0.0, 1.0)),
            child: const SizedBox(height: _height, width: double.infinity),
          ),
        ),
      ),
    );
  }
}

class _VolumeTrackPainter extends CustomPainter {
  const _VolumeTrackPainter(this.travel);

  final double travel;

  static const _rail = 4.0;
  static const _tick = 11.0;

  @override
  void paint(Canvas canvas, Size size) {
    final centre = size.height / 2;
    final knobX = _VolumeTrack._knobCentre(travel, size.width);
    final seamX = _VolumeTrack._knobCentre(unityTravel, size.width);
    final amplifying = travel > unityTravel;

    void bar(double from, double to, Color color) {
      if (to <= from) return;
      canvas.drawRRect(
        RRect.fromRectAndRadius(
          Rect.fromLTRB(from, centre - _rail / 2, to, centre + _rail / 2),
          const Radius.circular(_rail / 2),
        ),
        Paint()..color = color,
      );
    }

    bar(0, size.width, Nocturne.mix(Nocturne.text, 10));
    bar(0, math.min(knobX, seamX), Nocturne.accent);
    if (amplifying) bar(seamX, knobX, Serval.alert);

    // Over the fill, so the one labelled point on the track does not disappear under it, and taller
    // than the rail so it reads as a mark on the track rather than a gap in it.
    canvas.drawRect(
      Rect.fromLTRB(
        seamX - 0.5,
        centre - _tick / 2,
        seamX + 0.5,
        centre + _tick / 2,
      ),
      Paint()..color = Nocturne.mix(Nocturne.text, 30),
    );

    // The ring is the ground, not a stroke — the knob reads as punched out of the rail rather than
    // sitting on top of it. Painted as the larger circle behind the smaller one, which is the same
    // 2px inset a border would give.
    canvas.drawCircle(
      Offset(knobX, centre),
      _VolumeTrack._knob / 2,
      Paint()..color = Serval.panel,
    );
    canvas.drawCircle(
      Offset(knobX, centre),
      _VolumeTrack._knob / 2 - 2,
      Paint()..color = amplifying ? Serval.alert : Nocturne.accent300,
    );
  }

  @override
  bool shouldRepaint(_VolumeTrackPainter old) => old.travel != travel;
}
