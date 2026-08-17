/// Two to a row, and the two drawn to the same height.
///
/// The shape the Server settings page established and the per-camera page follows. Things that hold
/// a sentence or a list take the width; the rest pair up, and the two in a pair share a height —
/// a short one beside a long one would otherwise open a gap the next row starts below.
///
/// Sharing the height is [matchHeights], and it is off by default. It costs an [IntrinsicHeight] —
/// `stretch` alone cannot do it, because these live in a scroll view where the incoming height is
/// unbounded and stretching would force an infinite height onto each child — and it buys nothing
/// unless the two have edges you can see. A bordered card beside a shorter bordered card looks
/// wrong; two headings and their controls do not. It also cannot be used over a child that measures
/// itself: `LayoutBuilder does not support returning intrinsic dimensions`, and a section that
/// pairs its own controls is exactly such a child.
library;

import 'package:flutter/widgets.dart';

/// The width below which a pair is worse than a column: two boxes too narrow to hold a label and a
/// value beside each other. Every paired thing in the App uses this one figure, so a window dragged
/// narrower collapses all of them at the same moment.
const double kPairedMinWidth = 640;

/// One thing to place, and whether it needs the whole row.
class PairedItem {
  const PairedItem(this.child, {this.wide = false});

  final Widget child;

  /// True for anything a half-width box would truncate — a file path, a list of labels, a group of
  /// tiles meant to sit three across.
  final bool wide;
}

/// [items] packed into rows, in the order given, with nothing between them: the caller decides what
/// separates one row from the next, because on one page that is 12px of air and on another it is a
/// rule.
///
/// A wide item takes a row to itself; a narrow one pairs with the next narrow item after it, and
/// keeps its half of the row when there is none — narrow means half-width here, and a last card
/// twice the width of the one above it reads as a wide one. In order, deliberately: a caller that
/// wants its wide items to lead, so the pairs below close up rather than leaving a hole mid-grid,
/// sorts them to the front itself, and one whose order is worth reading keeps it.
///
/// When [paired] is false everything is one column and [PairedItem.wide] stops meaning anything.
List<Widget> pairedRows({
  required List<PairedItem> items,
  required bool paired,
  double gap = 12,
  bool matchHeights = false,
}) {
  final rows = <Widget>[];

  for (var index = 0; index < items.length; index++) {
    final item = items[index];

    if (!paired || item.wide) {
      rows.add(item.child);
      continue;
    }

    final partner = index + 1 < items.length && !items[index + 1].wide
        ? items[index + 1].child
        : null;

    final row = Row(
      crossAxisAlignment: matchHeights
          ? CrossAxisAlignment.stretch
          : CrossAxisAlignment.start,
      children: [
        Expanded(child: item.child),
        SizedBox(width: gap),
        Expanded(child: partner ?? const SizedBox()),
      ],
    );

    rows.add(matchHeights ? IntrinsicHeight(child: row) : row);
    if (partner != null) index++;
  }

  return rows;
}
