import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/camera_record.dart';
import '../theme/nocturne.dart';

/// One of a stream's three jobs, as a chip you toggle.
///
/// The design chose chips over a multi-select for a stated reason: *"an installer can see at a
/// glance that no stream is set to record."* A select collapses to a summary, and the one thing
/// worth seeing about roles is the gap.
///
/// Assigned is a filled accent tint with a dismiss cross; unassigned is a dashed outline — so
/// the shape says whether a job is *taken* even before the colour does.
class RoleChip extends StatefulWidget {
  const RoleChip({
    super.key,
    required this.role,
    required this.assigned,
    this.onChanged,
  });

  final StreamRole role;
  final bool assigned;
  final ValueChanged<bool>? onChanged;

  @override
  State<RoleChip> createState() => _RoleChipState();
}

class _RoleChipState extends State<RoleChip> {
  bool _hovered = false;

  static PhosphorIconData _icon(StreamRole role) => switch (role) {
    StreamRole.record => PhosphorIconsFill.record,
    StreamRole.detect => PhosphorIconsFill.eye,
    StreamRole.live => PhosphorIconsFill.broadcast,
  };

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onChanged != null;

    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: enabled ? () => widget.onChanged!(!widget.assigned) : null,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3),
          decoration: BoxDecoration(
            color: widget.assigned ? Nocturne.mix(Nocturne.accent, 16) : null,
            borderRadius: BorderRadius.circular(20),
            border: widget.assigned
                ? Border.all(color: Nocturne.mix(Nocturne.accent, 45))
                : Border.all(
                    color: Nocturne.mix(
                      Nocturne.text,
                      _hovered && enabled ? 30 : 18,
                    ),
                    style: BorderStyle.solid,
                  ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.assigned) ...[
                PhosphorIcon(
                  _icon(widget.role),
                  size: 11,
                  color: Nocturne.accent300,
                ),
                const SizedBox(width: 5),
              ],
              Text(
                widget.role.label,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  fontWeight: widget.assigned
                      ? Nocturne.headingWeight
                      : FontWeight.w400,
                  color: widget.assigned
                      ? Nocturne.accent300
                      : Nocturne.mix(Nocturne.text, 45),
                ),
              ),
              if (widget.assigned) ...[
                const SizedBox(width: 5),
                PhosphorIcon(
                  PhosphorIconsRegular.x,
                  size: 10,
                  color: Nocturne.mix(Nocturne.accent300, 70),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
