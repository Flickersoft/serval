import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/alert.dart';
import '../theme/nocturne.dart';
import 'alert_still.dart';

/// The glyph a class reads as.
///
/// A small table rather than one icon for everything, because the whole point of a queue is that
/// you can read down it without reading it — a car and a person should not look the same at a
/// glance. Anything the table has no opinion about falls through to the generic mark; a detector
/// with its own classes is normal, and an unknown one is not an error.
PhosphorIconData alertIcon(Alert alert) => alert.kind == AlertKind.sound
    ? PhosphorIconsRegular.waveform
    : detectionIcon(alert.label);

/// The same table for a bare class name, which is what the notification rules hold — they are a
/// list of strings a camera might one day raise, not alerts that happened.
PhosphorIconData detectionIcon(String label) => switch (label.toLowerCase()) {
  'person' => PhosphorIconsRegular.person,
  'car' || 'truck' || 'bus' => PhosphorIconsRegular.car,
  'motorcycle' || 'bicycle' => PhosphorIconsRegular.bicycle,
  'cat' || 'dog' || 'bird' || 'horse' => PhosphorIconsRegular.pawPrint,
  'package' || 'suitcase' || 'backpack' => PhosphorIconsRegular.package,
  _ => PhosphorIconsRegular.shapes,
};

/// The class name as a chip reads it — `person` becomes `Person`.
String alertLabelText(Alert alert) => titleCaseLabel(alert.label);

/// [alertLabelText] for a bare class name. Takes the first of a comma-joined list, because a
/// scene's label can name several things and a chip has room for one.
String titleCaseLabel(String label) {
  final first = label.trim().split(',').first;
  return first.isEmpty
      ? 'Detection'
      : first[0].toUpperCase() + first.substring(1);
}

/// One row of the alert queue.
///
/// **A row, not the card saved clips use.** A saved clip is something you chose to keep and will
/// come back looking for, so it earns a picture. An alert arrives whether you asked for it that
/// morning or not, and a queue is read top to bottom — so the picture is a thumbnail and the
/// sentence is the thing.
///
/// New alerts carry an accent dot and sit at full strength; seen ones drop back but stay, because
/// clearing the list is a decision rather than something time does.
class AlertRow extends StatefulWidget {
  const AlertRow({
    super.key,
    required this.alert,
    this.poster,
    this.selected = false,
    this.onOpen,
    this.onWatch,
    this.onDismiss,
    this.compact = false,
  });

  final Alert alert;
  final Uri? poster;

  /// Lit in the desktop queue, where picking a row fills the column beside it. Never on a phone,
  /// where picking a row is a move to another screen.
  final bool selected;

  final VoidCallback? onOpen;

  /// Opens the camera's recording at this second. Null when there is none — the button goes rather
  /// than sitting there dead.
  final VoidCallback? onWatch;

  /// The desktop row's ✕. On a phone the swipe carries this instead, so the row has no button.
  final VoidCallback? onDismiss;

  final bool compact;

  @override
  State<AlertRow> createState() => _AlertRowState();
}

class _AlertRowState extends State<AlertRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final alert = widget.alert;
    final seen = alert.read;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onOpen,
        behavior: HitTestBehavior.opaque,
        child: Opacity(
          // Seen rows drop back rather than leaving. The queue is meant to still show what happened
          // last night after you have read it.
          opacity: seen ? 0.7 : 1.0,
          child: Container(
            padding: widget.compact
                ? const EdgeInsets.symmetric(horizontal: 16, vertical: 8)
                : const EdgeInsets.all(9),
            decoration: BoxDecoration(
              color: widget.selected
                  ? Nocturne.mix(Nocturne.accent, 9)
                  : _hovered
                  ? Nocturne.mix(Nocturne.text, 4)
                  : null,
              // Full-bleed on a phone, so the swipe surface reaches the screen edge.
              borderRadius: widget.compact
                  ? null
                  : BorderRadius.circular(Nocturne.radiusMd),
              border: widget.compact
                  ? null
                  : Border.all(
                      color: widget.selected
                          ? Nocturne.mix(Nocturne.accent, 55)
                          : Nocturne.mix(Nocturne.text, seen ? 5 : 9),
                    ),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.center,
              spacing: 11,
              children: [
                _unreadDot(seen),
                _thumbnail(),
                Expanded(child: _text()),
                if (!widget.compact) ..._actions(),
              ],
            ),
          ),
        ),
      ),
    );
  }

  /// Present on every row, filled only when unread — a spacer on the seen ones, so the titles of a
  /// mixed list still line up.
  Widget _unreadDot(bool seen) => Container(
    width: 7,
    height: 7,
    decoration: BoxDecoration(
      color: seen ? null : Nocturne.accent300,
      shape: BoxShape.circle,
    ),
  );

  Widget _thumbnail() => ClipRRect(
    borderRadius: BorderRadius.circular(widget.compact ? 7 : 6),
    child: SizedBox(
      width: widget.compact ? 112 : 132,
      height: widget.compact ? 64 : 74,
      child: DecoratedBox(
        decoration: BoxDecoration(
          border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
          borderRadius: BorderRadius.circular(widget.compact ? 7 : 6),
        ),
        child: AlertStill(
          alert: widget.alert,
          poster: widget.poster,
          boxOpacity: 0.5,
        ),
      ),
    ),
  );

  Widget _text() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    spacing: 5,
    children: [
      Text(
        widget.alert.title,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: widget.compact ? 15 : 14.5,
          fontWeight: FontWeight.w500,
          color: Nocturne.text,
        ),
      ),
      Text(
        // Minutes here, seconds on the card. A row only has to be findable; the card is where you
        // say exactly when.
        '${widget.alert.cameraName} · ${clockLabel(widget.alert.at)}',
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
      Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 6,
        children: [
          // The class is the only thing on this line whose width the detector decides, so it is
          // the thing that gives way when both chips are here and the screen is a phone. *Not
          // recorded* stays whole: it is the chip that changes what the row can do.
          Flexible(child: AlertChip(alert: widget.alert)),
          if (widget.alert.recorded == false) const NotRecordedChip(),
        ],
      ),
    ],
  );

  List<Widget> _actions() => [
    _SquareAction(
      icon: PhosphorIconsFill.play,
      label: 'Watch',
      accent: widget.selected,
      onPressed: widget.onWatch,
    ),
    _SquareAction(
      icon: PhosphorIconsRegular.x,
      tooltip: 'Dismiss',
      onPressed: widget.onDismiss,
    ),
  ];
}

/// What was detected, as a chip.
///
/// Give it a bounded width. A class name is whatever the detector calls it, so the name ellipsizes
/// down to the icon rather than pushing the chip past whatever is holding it.
class AlertChip extends StatelessWidget {
  const AlertChip({super.key, required this.alert});

  final Alert alert;

  @override
  Widget build(BuildContext context) => Container(
    height: 22,
    padding: const EdgeInsets.symmetric(horizontal: 9),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(20),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      spacing: 5,
      children: [
        Icon(alertIcon(alert), size: 12, color: Nocturne.accent300),
        Flexible(
          child: Text(
            alertLabelText(alert),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            softWrap: false,
            style: TextStyle(
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 78),
            ),
          ),
        ),
      ],
    ),
  );
}

/// The camera was not recording when this fired.
///
/// Dashed, and quiet: it is not a warning. Detections fire whether or not a camera is set to
/// record, and the preview plays either way — this is only why there is nothing to open beyond it.
class NotRecordedChip extends StatelessWidget {
  const NotRecordedChip({super.key});

  @override
  Widget build(BuildContext context) => Container(
    height: 22,
    padding: const EdgeInsets.symmetric(horizontal: 9),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(20),
      border: Border.all(
        color: Nocturne.mix(Nocturne.text, 14),
        style: BorderStyle.solid,
      ),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      spacing: 5,
      children: [
        Icon(
          PhosphorIconsRegular.videoCameraSlash,
          size: 12,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
        Text(
          'Not recorded',
          style: TextStyle(
            fontSize: 11.5,
            color: Nocturne.mix(Nocturne.text, 42),
          ),
        ),
      ],
    ),
  );
}

class _SquareAction extends StatefulWidget {
  const _SquareAction({
    required this.icon,
    this.label,
    this.tooltip,
    this.accent = false,
    this.onPressed,
  });

  final PhosphorIconData icon;
  final String? label;
  final String? tooltip;
  final bool accent;
  final VoidCallback? onPressed;

  @override
  State<_SquareAction> createState() => _SquareActionState();
}

class _SquareActionState extends State<_SquareAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onPressed != null;
    final color = !enabled
        ? Nocturne.mix(Nocturne.text, 26)
        : widget.accent
        ? Nocturne.accent300
        : Nocturne.mix(Nocturne.text, 72);

    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        child: Container(
          height: 32,
          width: widget.label == null ? 32 : null,
          padding: widget.label == null
              ? null
              : const EdgeInsets.symmetric(horizontal: 11),
          alignment: Alignment.center,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(7),
            color: !enabled
                ? null
                : widget.accent
                ? Nocturne.mix(Nocturne.accent, 12)
                : _hovered
                ? Nocturne.mix(Nocturne.text, 7)
                : null,
            border: Border.all(
              color: !enabled
                  ? Nocturne.mix(Nocturne.text, 8)
                  : widget.accent
                  ? Nocturne.mix(Nocturne.accent, 60)
                  : Nocturne.mix(Nocturne.text, 14),
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            spacing: 6,
            children: [
              Icon(widget.icon, size: 13, color: color),
              if (widget.label case final label?)
                Text(label, style: TextStyle(fontSize: 12.5, color: color)),
            ],
          ),
        ),
      ),
    );
  }
}
