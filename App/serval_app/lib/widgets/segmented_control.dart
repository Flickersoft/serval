import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';

/// One option in a [SegmentedControl].
class Segment {
  const Segment(this.label, {this.icon});

  final String label;
  final PhosphorIconData? icon;
}

/// The design's `.seg` — a hairline box divided by hairlines, with the
/// selected option carrying an accent tint rather than a fill.
///
/// Used at three sizes: Rearranging|Done in the wall header, 1h|12h|24h beside
/// the scrubber, and All|Speech|Sounds|Alerts under the "What's happening"
/// heading on both the wall and the single-camera panel — the one place it
/// takes [expand], having a whole column to itself.
class SegmentedControl extends StatelessWidget {
  const SegmentedControl({
    super.key,
    required this.segments,
    required this.selectedIndex,
    this.onChanged,
    this.height = 34,
    this.fontSize = 13,
    this.horizontalPadding = 12,
    this.borderRadius = 7,
    this.expand = false,
  });

  final List<Segment> segments;
  final int selectedIndex;
  final ValueChanged<int>? onChanged;
  final double height;
  final double fontSize;
  final double horizontalPadding;
  final double borderRadius;

  /// Divide the available width equally between the segments rather than
  /// letting each take its label's width. What the activity filter wants, where
  /// the control has a column to itself and four ragged segments would read as
  /// a mistake; the other three uses sit beside something and stay intrinsic.
  final bool expand;

  @override
  Widget build(BuildContext context) {
    final divider = Nocturne.mix(Nocturne.text, 12);

    return Container(
      height: height,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(borderRadius),
        border: Border.all(color: divider),
      ),
      clipBehavior: Clip.antiAlias,
      child: Row(
        mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
        // Stretch so each segment fills the track's full height. Left to the
        // default centre alignment the button only grows to its label, and the
        // selected tint paints as a bar floating inside the box.
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < segments.length; i++)
            if (expand)
              Expanded(child: _button(i, divider))
            else
              _button(i, divider),
        ],
      ),
    );
  }

  Widget _button(int i, Color divider) => _SegmentButton(
    segment: segments[i],
    selected: i == selectedIndex,
    showLeadingDivider: i > 0,
    dividerColor: divider,
    fontSize: fontSize,
    horizontalPadding: horizontalPadding,
    centered: expand,
    onTap: onChanged == null ? null : () => onChanged!(i),
  );
}

class _SegmentButton extends StatefulWidget {
  const _SegmentButton({
    required this.segment,
    required this.selected,
    required this.showLeadingDivider,
    required this.dividerColor,
    required this.fontSize,
    required this.horizontalPadding,
    required this.centered,
    this.onTap,
  });

  final Segment segment;
  final bool selected;
  final bool showLeadingDivider;
  final Color dividerColor;
  final double fontSize;
  final double horizontalPadding;

  /// Under an [Expanded] the button is wider than its label, so the label has
  /// to be told where to sit; packed left it would read as ragged.
  final bool centered;

  final VoidCallback? onTap;

  @override
  State<_SegmentButton> createState() => _SegmentButtonState();
}

class _SegmentButtonState extends State<_SegmentButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final selected = widget.selected;
    final background = selected
        ? Nocturne.mix(Nocturne.accent, 16)
        : _hovered
        ? Nocturne.mix(Nocturne.text, 7)
        : null;

    return MouseRegion(
      cursor: widget.onTap == null
          ? SystemMouseCursors.basic
          : SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          padding: EdgeInsets.symmetric(horizontal: widget.horizontalPadding),
          decoration: BoxDecoration(
            color: background,
            border: widget.showLeadingDivider
                ? Border(left: BorderSide(color: widget.dividerColor))
                : null,
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            mainAxisAlignment: widget.centered
                ? MainAxisAlignment.center
                : MainAxisAlignment.start,
            children: [
              if (widget.segment.icon != null) ...[
                PhosphorIcon(
                  widget.segment.icon!,
                  size: widget.fontSize + 2,
                  color: selected
                      ? Nocturne.accent300
                      : Nocturne.mix(Nocturne.text, 60),
                ),
                const SizedBox(width: 6),
              ],
              // Flexible so a segment that is a fraction narrower than its
              // label — which is what [expand] can hand it, since the width
              // comes from the column rather than from the text — ellipsizes
              // instead of painting the overflow stripes. Under the intrinsic
              // sizing the constraints are loose and this is a no-op.
              Flexible(
                child: Text(
                  widget.segment.label,
                  maxLines: 1,
                  softWrap: false,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: widget.fontSize,
                    fontWeight: selected
                        ? Nocturne.headingWeight
                        : FontWeight.w400,
                    color: selected
                        ? Nocturne.accent300
                        : Nocturne.mix(Nocturne.text, 60),
                    height: 1.2,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
