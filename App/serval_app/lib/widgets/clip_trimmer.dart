import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/clip_selection.dart';
import '../models/timeline.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'section_kicker.dart';
import 'trim_track.dart';

/// Everything under the picture while a clip is being chosen: the day strip, the trim track, and
/// the controls that set an end without a fingertip.
///
/// Replaces the scrubber rather than sitting beside it. *Save clip* does not open a dialog asking
/// for two times — it turns the screen you are already on into a trimmer, and the timeline you were
/// scrubbing is the thing that becomes the track.
class ClipTrimmer extends StatelessWidget {
  const ClipTrimmer({
    super.key,
    required this.selection,
    required this.zoom,
    required this.window,
    required this.marks,
    required this.onChanged,
    required this.onZoomChanged,
    required this.onCancel,
    required this.onSave,
    this.onWholeEvent,
    this.compact = false,
    this.max = const Duration(minutes: 30),
    this.saving = false,
  });

  final ClipSelection selection;
  final TrimZoom zoom;

  /// The slice the track draws — [zoom]'s span, held inside the recorded session.
  final CoverageSpan window;

  final List<TimelineMark> marks;

  final ValueChanged<ClipSelection> onChanged;
  final ValueChanged<TrimZoom> onZoomChanged;
  final VoidCallback onCancel;
  final VoidCallback onSave;

  /// Snap to the event under the playhead. Null when there is none to snap to, which is what
  /// leaves 12c's *Whole event* disabled rather than absent.
  final VoidCallback? onWholeEvent;

  final bool compact;
  final Duration max;

  /// A save is already in flight, so the button goes inert rather than starting a second one.
  final bool saving;

  @override
  Widget build(BuildContext context) =>
      compact ? _buildCompact() : _buildDesktop();

  // ------------------------------------------------------------------ desktop

  Widget _buildDesktop() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 9,
    children: [
      _dayStrip(),
      TrimTrack(
        selection: selection,
        window: window,
        marks: marks,
        onChanged: onChanged,
        max: max,
      ),
      Row(
        spacing: 10,
        children: [
          _stepper(ClipEnd.start, 'From'),
          _stepper(ClipEnd.end, 'To'),
          Flexible(
            child: Text(
              // Rendered from the segments rather than written down, because under -c:v copy a
              // segment is as long as the camera's GOP made it — so a hardcoded "one second" would
              // promise precision the export cannot deliver.
              '${clipSpokenLabel(selection.nudge)} a nudge · up to $_maxLabel in a clip',
              style: TextStyle(
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
              overflow: TextOverflow.ellipsis,
            ),
          ),
          const Spacer(),
          NocturneButton(
            label: 'Cancel',
            variant: NocturneButtonVariant.secondary,
            onPressed: saving ? null : onCancel,
          ),
          NocturneButton(
            label: 'Save these ${clipSpokenLabel(selection.span)}…',
            icon: PhosphorIconsRegular.scissors,
            variant: NocturneButtonVariant.primary,
            onPressed: saving ? null : onSave,
          ),
        ],
      ),
    ],
  );

  /// The whole day above the track, with a lit box showing which twelve minutes are below it.
  ///
  /// The day does not disappear when the track zooms in — it moves up here, thin. Without it there
  /// is nothing on screen saying *where* the twelve minutes are, and a trimmer that has lost the
  /// day is a trimmer you cannot navigate back out of.
  Widget _dayStrip() => Row(
    spacing: 10,
    children: [
      SectionKicker(dayLabel(selection.from)),
      Expanded(child: _daySpan()),
      Text(
        'Below: ${clockLabel(window.from)} – ${clockLabel(window.to)}',
        style: TextStyle(fontSize: 12, color: Nocturne.mix(Nocturne.text, 45)),
      ),
      _widerLink(),
    ],
  );

  Widget _daySpan() => LayoutBuilder(
    builder: (context, constraints) {
      final day = _dayWindow();
      final total = day.duration.inMicroseconds;
      final left = total <= 0
          ? 0.0
          : window.from.difference(day.from).inMicroseconds /
                total *
                constraints.maxWidth;
      final width = total <= 0
          ? constraints.maxWidth
          : window.duration.inMicroseconds / total * constraints.maxWidth;

      return SizedBox(
        height: 18,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: Nocturne.mix(Nocturne.text, 5),
            borderRadius: BorderRadius.circular(5),
            border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
          ),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(5),
            child: Stack(
              children: [
                for (final mark in marks)
                  Positioned(
                    left:
                        (mark.at.difference(day.from).inMicroseconds /
                                (total == 0 ? 1 : total) *
                                constraints.maxWidth)
                            .clamp(0.0, constraints.maxWidth),
                    top: 0,
                    bottom: 0,
                    width: mark.kind == TimelineMarkKind.alert ? 4 : 3,
                    child: ColoredBox(
                      color: Nocturne.mix(
                        Serval.markHue(
                          mark.of,
                          alert: mark.kind == TimelineMarkKind.alert,
                        ),
                        mark.kind == TimelineMarkKind.alert ? 60 : 45,
                      ),
                    ),
                  ),
                Positioned(
                  left: left.clamp(0.0, constraints.maxWidth),
                  top: -1,
                  bottom: -1,
                  width: width.clamp(6.0, constraints.maxWidth),
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      color: Nocturne.mix(Nocturne.accent, 22),
                      borderRadius: BorderRadius.circular(4),
                      border: Border.all(color: Nocturne.accent300, width: 1.5),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      );
    },
  );

  /// One end, with its ±one-segment buttons.
  ///
  /// The lit one is the end the nudges move, which is the same rule the phone's two big fields
  /// follow — stated once here so the two layouts cannot drift.
  Widget _stepper(ClipEnd end, String label) {
    final live = selection.active == end;
    final at = end == ClipEnd.start ? selection.from : selection.to;

    return GestureDetector(
      onTap: () => onChanged(selection.withActive(end)),
      child: Container(
        height: 34,
        padding: const EdgeInsets.fromLTRB(11, 0, 4, 0),
        decoration: BoxDecoration(
          color: live
              ? Nocturne.mix(Nocturne.accent, 10)
              : Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: live
                ? Nocturne.mix(Nocturne.accent, 60)
                : Nocturne.mix(Nocturne.text, 15),
          ),
        ),
        child: Row(
          spacing: 8,
          children: [
            Text(
              label,
              style: TextStyle(
                fontSize: 12,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
            Text(
              preciseClockLabel(at),
              style: monoStyle(fontSize: 13, color: Nocturne.text),
            ),
            Row(
              spacing: 2,
              children: [
                _nudgeButton(end, -1, PhosphorIconsRegular.minus, live),
                _nudgeButton(end, 1, PhosphorIconsRegular.plus, live),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _nudgeButton(ClipEnd end, int steps, IconData icon, bool live) =>
      GestureDetector(
        onTap: () =>
            onChanged(selection.withActive(end).nudgeBy(steps, max: max)),
        child: Container(
          width: 24,
          height: 26,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(5),
            border: Border.all(
              color: live
                  ? Nocturne.mix(Nocturne.accent, 40)
                  : Nocturne.mix(Nocturne.text, 14),
            ),
          ),
          child: Icon(
            icon,
            size: 12,
            color: live ? Nocturne.accent300 : Nocturne.mix(Nocturne.text, 70),
          ),
        ),
      );

  Widget _widerLink() {
    final wider = zoom.isNear;

    return GestureDetector(
      onTap: () => onZoomChanged(wider ? TrimZoom.far : TrimZoom.near),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 6,
        children: [
          Icon(
            wider
                ? PhosphorIconsRegular.magnifyingGlassMinus
                : PhosphorIconsRegular.magnifyingGlassPlus,
            size: 13,
            color: Nocturne.accent300,
          ),
          Text(
            wider ? 'Wider' : 'Closer',
            style: const TextStyle(fontSize: 12, color: Nocturne.accent300),
          ),
        ],
      ),
    );
  }

  // ------------------------------------------------------------------- compact

  /// The phone keeps the same two steps and changes how an end is moved.
  ///
  /// A finger cannot land on a handle, so the two big fields below the track are the real control:
  /// whichever is lit is the end that drags and the end the nudges move. Precision stops depending
  /// on a fingertip, which is what makes this workable at all at 412px.
  Widget _buildCompact() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 9,
    children: [
      Row(
        children: [
          SectionKicker(
            '${clockLabel(window.from)} – ${clockLabel(window.to)}',
          ),
          const Spacer(),
          _widerLink(),
        ],
      ),
      TrimTrack(
        selection: selection,
        window: window,
        marks: marks,
        onChanged: onChanged,
        compact: true,
        max: max,
      ),
    ],
  );

  /// The *Starts* / *Ends* cards and the nudges, which scroll under the fixed track on a phone.
  Widget buildCompactControls() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 12,
    children: [
      Row(
        spacing: 8,
        children: [
          Expanded(child: _endCard(ClipEnd.start, 'Starts')),
          Expanded(child: _endCard(ClipEnd.end, 'Ends')),
        ],
      ),
      Row(
        spacing: 8,
        children: [
          Expanded(child: _compactNudge(-1, PhosphorIconsRegular.minus)),
          Expanded(child: _compactNudge(1, PhosphorIconsRegular.plus)),
          _wholeEventButton(),
        ],
      ),
      Text(
        'The nudges move whichever end you last touched. Whole event snaps to what Serval saw.',
        style: TextStyle(
          fontSize: 12.5,
          height: 1.45,
          color: Nocturne.mix(Nocturne.text, 45),
        ),
      ),
    ],
  );

  Widget _endCard(ClipEnd end, String label) {
    final live = selection.active == end;
    final at = end == ClipEnd.start ? selection.from : selection.to;

    return GestureDetector(
      onTap: () => onChanged(selection.withActive(end)),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 9),
        decoration: BoxDecoration(
          color: live
              ? Nocturne.mix(Nocturne.accent, 12)
              : Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(8),
          border: Border.all(
            color: live
                ? Nocturne.mix(Nocturne.accent, 65)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 3,
          children: [
            Text(
              live ? "$label · the one you're moving" : label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 11.5,
                color: live
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, 50),
              ),
            ),
            Text(
              preciseClockLabel(at),
              style: monoStyle(fontSize: 15, color: Nocturne.text),
            ),
          ],
        ),
      ),
    );
  }

  Widget _compactNudge(int steps, IconData icon) => GestureDetector(
    onTap: () => onChanged(selection.nudgeBy(steps, max: max)),
    child: Container(
      height: 44,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
      ),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        spacing: 6,
        children: [
          Icon(icon, size: 14, color: Nocturne.mix(Nocturne.text, 78)),
          Text(
            clipSpokenLabel(selection.nudge),
            style: TextStyle(
              fontSize: 14,
              color: Nocturne.mix(Nocturne.text, 78),
            ),
          ),
        ],
      ),
    ),
  );

  Widget _wholeEventButton() => GestureDetector(
    onTap: onWholeEvent,
    child: Container(
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 13),
      alignment: Alignment.center,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 7,
        children: [
          Icon(
            PhosphorIconsRegular.arrowsOutLineHorizontal,
            size: 16,
            color: Nocturne.mix(Nocturne.text, onWholeEvent == null ? 30 : 78),
          ),
          Text(
            'Whole event',
            style: TextStyle(
              fontSize: 14,
              color: Nocturne.mix(
                Nocturne.text,
                onWholeEvent == null ? 30 : 78,
              ),
            ),
          ),
        ],
      ),
    ),
  );

  /// Abbreviated, because this caption is the first thing the row gives up when the window
  /// narrows — and "up to 30 minut…" is worse than "up to 30 min".
  String get _maxLabel =>
      max.inMinutes >= 60 ? '${max.inHours} h' : '${max.inMinutes} min';

  /// The day the clip is in, for the strip above the track.
  CoverageSpan _dayWindow() {
    final start = DateTime(
      selection.from.year,
      selection.from.month,
      selection.from.day,
    );
    return CoverageSpan(start, start.add(const Duration(days: 1)));
  }
}
