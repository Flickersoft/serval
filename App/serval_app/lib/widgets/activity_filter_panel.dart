import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/activity.dart';
import '../models/camera.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'activity_glyph.dart';
import 'nocturne_editable_text.dart';
import 'section_kicker.dart';

/// The facets the "What's happening" column filters by, as a panel drawn over
/// the feed.
///
/// It sits over the column rather than beside it or in a dialog for one reason:
/// the counts beside every option describe the column underneath, and a panel
/// that covered a different surface — or floated free of one — would be
/// reporting on something the reader cannot see.
///
/// Filters apply on tick, so [onDone] only closes it. Nothing in here is a
/// pending edit waiting on a confirmation, which is what lets the chips under
/// the search field stay true while the panel is open.
///
/// On a phone it is not a card over a column but the sheet itself — see
/// [compact].
class ActivityFilterPanel extends StatefulWidget {
  const ActivityFilterPanel({
    super.key,
    required this.filter,
    required this.facets,
    required this.onChanged,
    required this.onDone,
    this.cameras = const [],
    this.compact = false,
    this.shown,
  });

  final ActivityFilter filter;
  final ActivityFacets facets;
  final ValueChanged<ActivityFilter> onChanged;
  final VoidCallback onDone;

  /// The cameras the *Which camera* facet offers. Empty on the single-camera
  /// panel, where that question is already settled by being on that screen, and
  /// the whole section is then dropped rather than drawn with one option.
  final List<Camera> cameras;

  /// Phone metrics, and no card.
  ///
  /// Two things change. Every target grows to the 44px the rest of the compact
  /// design is built on — a 16px checkbox is a thing you aim a mouse at. And the
  /// panel gives up its own radius, border and shadow, because the sheet it
  /// fills is already all three: a card inside a sheet is a column inside a
  /// column, which is the shape round 11 rejected.
  final bool compact;

  /// The N in *N of the M events in view*, for the compact footer.
  ///
  /// Null on the desktop panel, which has no footer count — there the header
  /// above the column is still on screen and already says it.
  final int? shown;

  @override
  State<ActivityFilterPanel> createState() => _ActivityFilterPanelState();
}

class _ActivityFilterPanelState extends State<ActivityFilterPanel> {
  /// How many label chips show before the dashed *N more…* — enough to cover a
  /// normal house, short enough that the section does not become the panel.
  static const _labelsShown = 5;

  bool _allLabels = false;

  bool get _compact => widget.compact;

  @override
  Widget build(BuildContext context) {
    final labels = widget.facets.labels;
    final shown = _allLabels ? labels : labels.take(_labelsShown).toList();

    return Container(
      decoration: BoxDecoration(
        color: Serval.overlay,
        borderRadius: _compact ? null : BorderRadius.circular(9),
        border: _compact
            ? null
            : Border.all(color: Nocturne.mix(Nocturne.text, 14)),
        boxShadow: _compact ? null : Nocturne.shadowLg,
      ),
      clipBehavior: _compact ? Clip.none : Clip.antiAlias,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _header(),
          Expanded(
            child: SingleChildScrollView(
              padding: _compact
                  ? const EdgeInsets.fromLTRB(16, 12, 16, 12)
                  : const EdgeInsets.fromLTRB(14, 10, 14, 10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _alertsOnly(),
                  SizedBox(height: _compact ? 12 : 9),
                  _kinds(),
                  if (widget.cameras.isNotEmpty) ...[
                    _divider(),
                    _whichCamera(),
                  ],
                  if (labels.isNotEmpty) ...[
                    _divider(),
                    _whatWasSeen(shown, labels.length),
                  ],
                ],
              ),
            ),
          ),
          _footer(),
        ],
      ),
    );
  }

  Widget _divider() => Padding(
    padding: EdgeInsets.symmetric(vertical: _compact ? 12 : 9),
    child: Container(height: 1, color: Nocturne.mix(Nocturne.text, 8)),
  );

  Widget _header() => Container(
    padding: _compact
        ? const EdgeInsets.fromLTRB(16, 4, 16, 12)
        : const EdgeInsets.fromLTRB(14, 12, 14, 10),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Nocturne.mix(Nocturne.text, 8))),
    ),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Filter',
                style: TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: _compact ? 16 : 13,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                'Counts are what the column is holding now',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: _compact ? 12 : 11,
                  color: Nocturne.mix(Nocturne.text, 42),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(width: 10),
        // Leaves the search field alone: it is on screen in its own box with
        // its own clear, and a *Reset all* in here that emptied it too would be
        // reaching outside the panel to undo something it never did.
        ActivityLink(
          'Reset all',
          fontSize: _compact ? 13.5 : 11.5,
          onTap: widget.filter.facetCount == 0
              ? null
              : () => widget.onChanged(widget.filter.withoutFacets),
        ),
      ],
    ),
  );

  Widget _footer() => Container(
    padding: _compact
        ? const EdgeInsets.symmetric(horizontal: 16, vertical: 12)
        : const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 2),
      border: Border(top: BorderSide(color: Nocturne.mix(Nocturne.text, 8))),
    ),
    child: Row(
      children: [
        // On a phone the count comes down here with the button. The header that
        // carries it on a desktop is behind the sheet, so without this you would
        // find out what you had narrowed to only after dismissing the thing that
        // narrowed it.
        if (widget.shown case final shown? when _compact)
          Expanded(
            child: Text(
              eventsInView(shown, widget.facets.total),
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 13,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
          )
        else
          const Spacer(),
        const SizedBox(width: 12),
        // *Done*, not *Apply* — every tick above has already landed on the feed
        // behind this panel, and a button claiming otherwise would be asking
        // for a confirmation of something that needs none.
        _Tappable(
          onTap: widget.onDone,
          child: Container(
            height: _compact ? 44 : 30,
            alignment: Alignment.center,
            padding: EdgeInsets.symmetric(horizontal: _compact ? 22 : 13),
            decoration: BoxDecoration(
              color: Nocturne.mix(Nocturne.accent, 16),
              borderRadius: BorderRadius.circular(_compact ? 8 : 7),
              border: Border.all(color: Nocturne.mix(Nocturne.accent, 65)),
            ),
            child: Text(
              'Done',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: _compact ? 14 : 12.5,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.accent300,
              ),
            ),
          ),
        ),
      ],
    ),
  );

  /// A toggle above the list rather than a fifth kind, because `is_alert` is a
  /// flag that crosses sounds and detections both — filing it under *What
  /// happened* would make it a rival of the very rows it selects from.
  Widget _alertsOnly() {
    final on = widget.filter.alertsOnly;

    return _Tappable(
      onTap: () => widget.onChanged(widget.filter.copyWith(alertsOnly: !on)),
      child: Container(
        padding: EdgeInsets.symmetric(
          horizontal: _compact ? 12 : 11,
          vertical: _compact ? 10 : 9,
        ),
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(_compact ? 8 : 7),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
        ),
        child: Row(
          children: [
            PhosphorIcon(
              PhosphorIconsRegular.bellRinging,
              size: _compact ? 18 : 15,
              color: Serval.alert,
            ),
            SizedBox(width: _compact ? 12 : 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    "Only what's marked an alert",
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: _compact ? 14 : 12.5,
                      color: Nocturne.mix(Nocturne.text, on ? 100 : 88),
                    ),
                  ),
                  SizedBox(height: _compact ? 2 : 1),
                  Text(
                    '${widget.facets.alerts} of them, across everything below',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: _compact ? 12 : 11,
                      color: Nocturne.mix(Nocturne.text, 45),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            _Switch(
              value: on,
              width: _compact ? 44 : 34,
              height: _compact ? 26 : 20,
            ),
          ],
        ),
      ),
    );
  }

  Widget _kinds() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      const SectionKicker('What happened'),
      for (final kind in ActivityKind.values)
        _checkRow(
          label: kind.label,
          checked: widget.filter.kinds.contains(kind),
          count: widget.facets.kinds[kind] ?? 0,
          trailing: _count(widget.facets.kinds[kind] ?? 0),
          onTap: () => widget.onChanged(widget.filter.toggleKind(kind)),
        ),
    ],
  );

  /// A [_CheckRow] at this panel's metrics. On a phone the row grows to the
  /// 44px target the rest of the compact design is built on, and its box grows
  /// with it — a 16px checkbox under a thumb is a target for a mouse.
  Widget _checkRow({
    required String label,
    required bool checked,
    required int count,
    required Widget trailing,
    required VoidCallback onTap,
    bool dim = false,
  }) => _CheckRow(
    label: label,
    checked: checked,
    count: count,
    trailing: trailing,
    onTap: onTap,
    dim: dim,
    minHeight: _compact ? 44 : 0,
    checkSize: _compact ? 20 : 16,
    fontSize: _compact ? 14 : 12.5,
  );

  Widget _whichCamera() {
    final chosen = widget.filter.cameraIds;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            const Expanded(child: SectionKicker('Which camera')),
            ActivityLink(
              'All ${widget.cameras.length}',
              fontSize: _compact ? 12.5 : 11.5,
              onTap: chosen.isEmpty
                  ? null
                  : () => widget.onChanged(
                      widget.filter.copyWith(cameraIds: const {}),
                    ),
            ),
          ],
        ),
        for (final camera in widget.cameras)
          _checkRow(
            label: camera.name,
            checked: chosen.contains(camera.id),
            count: widget.facets.cameras[camera.id] ?? 0,
            // An offline camera keeps its count and says so: what it recorded
            // before it went quiet is still in the column, and a row that
            // dropped the number would read as a camera with nothing to show.
            //
            // Offline only. A camera we have simply not heard from yet keeps an
            // ordinary row — what it recorded is as valid as any other camera's,
            // and only a confirmed absence earns the annotation.
            trailing: camera.connection != CameraConnection.offline
                ? _count(widget.facets.cameras[camera.id] ?? 0)
                : Text(
                    'offline · ${widget.facets.cameras[camera.id] ?? 0}',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11,
                      color: Nocturne.mix(Nocturne.text, 35),
                    ),
                  ),
            dim: camera.connection == CameraConnection.offline,
            onTap: () =>
                widget.onChanged(widget.filter.toggleCamera(camera.id)),
          ),
      ],
    );
  }

  Widget _whatWasSeen(List<ActivityLabelFacet> shown, int total) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      const SectionKicker('What was seen or heard'),
      const SizedBox(height: 7),
      Wrap(
        spacing: 5,
        runSpacing: 5,
        children: [
          for (final facet in shown)
            _LabelChip(
              facet: facet,
              checked: widget.filter.labels.contains(facet.label),
              onTap: () =>
                  widget.onChanged(widget.filter.toggleLabel(facet.label)),
            ),
          if (total > shown.length)
            _MoreChip(
              label: '${total - shown.length} more…',
              onTap: () => setState(() => _allLabels = true),
            ),
        ],
      ),
    ],
  );

  Widget _count(int n) => Text(
    '$n',
    style: monoStyle(fontSize: 11, color: Nocturne.mix(Nocturne.text, 38)),
  );
}

/// The search box above the chips — 32px beside a mouse, 44 under a thumb.
///
/// Its own control rather than a [NocturneField] variant: that one is a 12.5px
/// label stacked over a 36px input, and this has no label, a different height
/// and a clear button where that one puts a reveal. What it does borrow is the
/// focus treatment — the accent as a line with a soft ring outside it — so the
/// two agree wherever they appear on the same screen.
class ActivitySearchField extends StatefulWidget {
  const ActivitySearchField({
    super.key,
    required this.controller,
    required this.onChanged,
    this.hint = 'Search what was said or seen',
    this.compact = false,
  });

  final TextEditingController controller;
  final ValueChanged<String> onChanged;
  final String hint;

  /// Phone metrics: a 44px box and the type and glyphs to match.
  final bool compact;

  @override
  State<ActivitySearchField> createState() => _ActivitySearchFieldState();
}

class _ActivitySearchFieldState extends State<ActivitySearchField> {
  final _focus = FocusNode();

  @override
  void initState() {
    super.initState();
    _focus.addListener(() => setState(() {}));
  }

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final focused = _focus.hasFocus;
    final empty = widget.controller.text.isEmpty;
    final compact = widget.compact;
    final fontSize = compact ? 14.0 : 12.5;
    final glyph = compact ? 16.0 : 14.0;

    return Container(
      height: compact ? 44 : 32,
      padding: EdgeInsets.symmetric(horizontal: compact ? 13 : 11),
      decoration: BoxDecoration(
        color: focused
            ? Nocturne.mix(Nocturne.accent, 7)
            : Nocturne.mix(Nocturne.text, 3),
        borderRadius: BorderRadius.circular(compact ? 8 : 7),
        border: Border.all(
          color: focused
              ? Nocturne.mix(Nocturne.accent, 60)
              : Nocturne.mix(Nocturne.text, 12),
        ),
        boxShadow: focused
            ? [
                BoxShadow(
                  color: Nocturne.mix(Nocturne.accent, 12),
                  spreadRadius: 3,
                ),
              ]
            : null,
      ),
      child: Row(
        children: [
          PhosphorIcon(
            PhosphorIconsRegular.magnifyingGlass,
            size: glyph,
            color: Nocturne.mix(Nocturne.text, focused ? 40 : 42),
          ),
          SizedBox(width: compact ? 9 : 8),
          Expanded(
            child: Stack(
              alignment: Alignment.centerLeft,
              children: [
                NocturneEditableText(
                  controller: widget.controller,
                  focusNode: _focus,
                  cursorHeight: compact ? 16 : 14,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: fontSize,
                    color: Nocturne.text,
                  ),
                  onChanged: (value) {
                    widget.onChanged(value);
                    setState(() {});
                  },
                ),
                // An editor has no placeholder of its own, so the hint is
                // painted behind it and taken out of the hit test — the
                // whole box is still the field.
                if (empty)
                  IgnorePointer(
                    child: Text(
                      widget.hint,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: fontSize,
                        color: Nocturne.mix(Nocturne.text, 42),
                      ),
                    ),
                  ),
              ],
            ),
          ),
          if (!empty)
            _Tappable(
              onTap: () {
                widget.controller.clear();
                widget.onChanged('');
                setState(() {});
              },
              child: PhosphorIcon(
                PhosphorIconsRegular.xCircle,
                size: glyph,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),
        ],
      ),
    );
  }
}

/// The button that opens the panel, with the number of facets on it.
///
/// Quiet at rest and accent-tinted once anything is on, so the column says it
/// is filtered even with every chip scrolled out of sight.
class ActivityFilterButton extends StatelessWidget {
  const ActivityFilterButton({
    super.key,
    required this.count,
    required this.onTap,
    this.compact = false,
  });

  final int count;
  final VoidCallback onTap;

  /// Phone metrics, so it stands the same height as the search field beside it.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final on = count > 0;
    final badge = compact ? 18.0 : 16.0;

    return _Tappable(
      onTap: onTap,
      child: Container(
        height: compact ? 44 : 32,
        padding: EdgeInsets.symmetric(horizontal: compact ? 13 : 10),
        decoration: BoxDecoration(
          color: on ? Nocturne.mix(Nocturne.accent, 16) : null,
          borderRadius: BorderRadius.circular(compact ? 8 : 7),
          border: Border.all(
            color: on
                ? Nocturne.mix(Nocturne.accent, 55)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            PhosphorIcon(
              on ? PhosphorIconsFill.funnel : PhosphorIconsRegular.funnel,
              size: compact ? 16 : 14,
              color: on ? Nocturne.accent300 : Nocturne.mix(Nocturne.text, 62),
            ),
            const SizedBox(width: 7),
            Text(
              'Filter',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: compact ? 14 : 12.5,
                fontWeight: on ? Nocturne.headingWeight : FontWeight.w400,
                color: on
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, 62),
              ),
            ),
            if (on) ...[
              const SizedBox(width: 7),
              Container(
                constraints: BoxConstraints(minWidth: badge),
                height: badge,
                alignment: Alignment.center,
                padding: EdgeInsets.symmetric(horizontal: compact ? 5 : 4),
                decoration: BoxDecoration(
                  color: Nocturne.accent,
                  borderRadius: BorderRadius.circular(badge / 2),
                ),
                child: Text(
                  '$count',
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: compact ? 11 : 10.5,
                    fontWeight: FontWeight.w600,
                    color: Nocturne.bg,
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// One thing the filter is doing, with the × that undoes it.
class ActivityFilterChip extends StatelessWidget {
  const ActivityFilterChip({
    super.key,
    required this.label,
    required this.onRemove,
    this.compact = false,
  });

  final String label;
  final VoidCallback onRemove;

  /// Phone metrics. The chip is the one control here that does not reach 44 —
  /// it is a readout you can also undo, not a target you go looking for, and a
  /// row of 44px chips would be the head.
  final bool compact;

  @override
  Widget build(BuildContext context) => _Tappable(
    onTap: onRemove,
    child: Container(
      height: compact ? 28 : 20,
      padding: EdgeInsets.symmetric(horizontal: compact ? 10 : 7),
      decoration: BoxDecoration(
        color: Nocturne.mix(Nocturne.accent, 12),
        borderRadius: BorderRadius.circular(compact ? 6 : 5),
        border: Border.all(color: Nocturne.mix(Nocturne.accent, 40)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: compact ? 12.5 : 10.5,
              color: Nocturne.text,
            ),
          ),
          SizedBox(width: compact ? 6 : 5),
          PhosphorIcon(
            PhosphorIconsRegular.x,
            size: compact ? 11 : 9,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ],
      ),
    ),
  );
}

/// The system's inline text action — *Clear*, *Reset all*, *All 5*.
///
/// Accent text and nothing else. A button's box around four characters sitting
/// beside a heading would weigh more than what it does.
class ActivityLink extends StatelessWidget {
  const ActivityLink(this.label, {super.key, this.onTap, this.fontSize = 11.5});

  final String label;

  /// Null renders it at the system's disabled step and takes the cursor away —
  /// a *Reset all* with nothing to reset.
  final VoidCallback? onTap;

  final double fontSize;

  @override
  Widget build(BuildContext context) => _Tappable(
    onTap: onTap,
    child: Text(
      label,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: fontSize,
        color: onTap == null
            ? Nocturne.mix(Nocturne.text, 28)
            : Nocturne.accent400,
      ),
    ),
  );
}

/// A checkbox row: the box, the label, and the count at the right.
class _CheckRow extends StatelessWidget {
  const _CheckRow({
    required this.label,
    required this.checked,
    required this.count,
    required this.trailing,
    required this.onTap,
    this.dim = false,
    this.minHeight = 0,
    this.checkSize = 16,
    this.fontSize = 12.5,
  });

  final String label;
  final bool checked;

  /// The floor under the whole row, so a phone's rows are the 44px the rest of
  /// the compact design aims at. Zero leaves the row its content's height.
  final double minHeight;

  final double checkSize;
  final double fontSize;

  /// How many rows this option would leave. Zero and unchecked makes it inert:
  /// there is nothing behind it, and letting it be ticked is letting the panel
  /// hand back a blank column it could see coming.
  final int count;

  final Widget trailing;
  final VoidCallback onTap;

  /// An offline camera, set back a step — still selectable, because what it
  /// recorded is still in the column.
  final bool dim;

  /// A checked option always comes back off, even at zero — otherwise the tick
  /// that emptied it would be the one thing you could not undo.
  bool get _enabled => checked || count > 0;

  @override
  Widget build(BuildContext context) => _Tappable(
    onTap: _enabled ? onTap : null,
    child: Opacity(
      opacity: _enabled ? 1 : 0.4,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 2.5),
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: minHeight),
          child: Row(
            children: [
              _Check(checked: checked, size: checkSize),
              SizedBox(width: checkSize >= 20 ? 12 : 9),
              Expanded(
                child: Text(
                  label,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: fontSize,
                    color: checked
                        ? Nocturne.text
                        : Nocturne.mix(Nocturne.text, dim ? 50 : 72),
                  ),
                ),
              ),
              const SizedBox(width: 8),
              trailing,
            ],
          ),
        ),
      ),
    ),
  );
}

/// The box. Accent tint and an accent check when on, a bare hairline square
/// when off — the system has no checkbox of its own, and Material's is a filled
/// box in the theme's primary color, which is the flood this design forbids.
///
/// 16px beside a mouse, 20 under a thumb; the corner and the glyph follow the
/// box rather than staying put, or the larger one reads as the same mark
/// enlarged by accident.
class _Check extends StatelessWidget {
  const _Check({required this.checked, this.size = 16});

  final bool checked;
  final double size;

  @override
  Widget build(BuildContext context) => Container(
    width: size,
    height: size,
    alignment: Alignment.center,
    decoration: BoxDecoration(
      color: checked ? Nocturne.mix(Nocturne.accent, 25) : null,
      borderRadius: BorderRadius.circular(size >= 20 ? 5 : 4),
      border: Border.all(
        color: checked
            ? Nocturne.mix(Nocturne.accent, 70)
            : Nocturne.mix(Nocturne.text, 20),
      ),
    ),
    // Fill rather than a heavier weight: the golden harness loads only the
    // regular and fill faces, so anything else renders as tofu there while
    // looking right in the app — a difference the goldens exist to catch.
    child: checked
        ? PhosphorIcon(
            PhosphorIconsFill.check,
            size: size >= 20 ? 12 : 10,
            color: Nocturne.accent300,
          )
        : null,
  );
}

/// The alert facet's switch. `NocturneToggle` is stateful and drives its own
/// callback; here the whole row is the tap target, so this is the same shape
/// drawn from a value.
class _Switch extends StatelessWidget {
  const _Switch({required this.value, this.width = 34, this.height = 20});

  final bool value;
  final double width;
  final double height;

  @override
  Widget build(BuildContext context) => AnimatedContainer(
    duration: const Duration(milliseconds: 120),
    width: width,
    height: height,
    padding: const EdgeInsets.symmetric(horizontal: 3),
    alignment: value ? Alignment.centerRight : Alignment.centerLeft,
    decoration: BoxDecoration(
      color: value
          ? Nocturne.mix(Nocturne.accent, 30)
          : Nocturne.mix(Nocturne.text, 10),
      borderRadius: BorderRadius.circular(height / 2),
      border: Border.all(
        color: value
            ? Nocturne.mix(Nocturne.accent, 60)
            : Nocturne.mix(Nocturne.text, 16),
      ),
    ),
    child: Container(
      width: height - 6,
      height: height - 6,
      decoration: BoxDecoration(
        color: value ? Nocturne.accent300 : Nocturne.mix(Nocturne.text, 45),
        shape: BoxShape.circle,
      ),
    ),
  );
}

/// One class the server has seen, as a chip: its glyph, the model's word for
/// it, and how many.
class _LabelChip extends StatelessWidget {
  const _LabelChip({
    required this.facet,
    required this.checked,
    required this.onTap,
  });

  final ActivityLabelFacet facet;
  final bool checked;
  final VoidCallback onTap;

  /// Same rule as a checkbox row: nothing behind it means nothing to tick, and
  /// a chip already on always comes back off.
  bool get _enabled => checked || facet.count > 0;

  @override
  Widget build(BuildContext context) => _Tappable(
    onTap: _enabled ? onTap : null,
    child: Opacity(opacity: _enabled ? 1 : 0.4, child: _chip()),
  );

  Widget _chip() => Container(
    height: 24,
    padding: const EdgeInsets.symmetric(horizontal: 8),
    decoration: BoxDecoration(
      color: checked ? Nocturne.mix(Nocturne.accent, 16) : null,
      borderRadius: BorderRadius.circular(6),
      border: Border.all(
        color: checked
            ? Nocturne.mix(Nocturne.accent, 55)
            : Nocturne.mix(Nocturne.text, 14),
      ),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        PhosphorIcon(
          activityGlyph(facet.icon),
          size: 11,
          color: checked ? Nocturne.accent300 : Nocturne.mix(Nocturne.text, 55),
        ),
        const SizedBox(width: 5),
        // Flexible, because these are the model's own words and some of them
        // are a sentence — AudioSet calls a car horn *Vehicle horn, car horn,
        // honking*. A chip wider than the panel is content nobody can see, and
        // the glyph and the count are what make a truncated one still readable.
        Flexible(
          child: Text(
            facet.label,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: checked ? Nocturne.text : Nocturne.mix(Nocturne.text, 70),
            ),
          ),
        ),
        const SizedBox(width: 5),
        Text(
          '${facet.count}',
          style: monoStyle(
            fontSize: 10,
            color: Nocturne.mix(Nocturne.text, 38),
          ),
        ),
      ],
    ),
  );
}

/// The dashed chip that reveals the rest of the labels in place.
class _MoreChip extends StatelessWidget {
  const _MoreChip({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => _Tappable(
    onTap: onTap,
    child: Container(
      height: 24,
      alignment: Alignment.center,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 16)),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 45),
        ),
      ),
    ),
  );
}

/// A hit target with the pointer cursor, for the many small controls in here
/// that are a shape and a tap and nothing else.
class _Tappable extends StatelessWidget {
  const _Tappable({required this.onTap, required this.child});

  final VoidCallback? onTap;
  final Widget child;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: onTap == null ? SystemMouseCursors.basic : SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      behavior: HitTestBehavior.opaque,
      child: child,
    ),
  );
}
