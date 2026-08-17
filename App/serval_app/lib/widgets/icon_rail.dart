import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/widgets.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../models/source_offer.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// One destination on the rail.
class RailItem {
  const RailItem({
    required this.icon,
    required this.tooltip,
    this.badge = false,
  });

  final PhosphorIconData icon;
  final String tooltip;

  /// A small orange dot over the glyph — something is waiting here.
  final bool badge;
}

/// The 64px navigation rail: the Serval mark, the destinations, settings
/// pinned to the bottom, and the source offer under them.
///
/// Settings is a destination like any other — it just sits at the far end of
/// the column rather than in [items], because it is where you go last.
///
/// The active item takes an accent tint and the light step of the ramp; the
/// rest sit at half-strength text. Nothing here is labelled — at this width
/// the glyph is the label. [_SourceLink] is the one exception, and says why.
class IconRail extends StatelessWidget {
  const IconRail({
    super.key,
    required this.items,
    required this.selectedIndex,
    this.onSelected,
    this.onSettings,
    this.settingsSelected = false,
    this.onLogout,
  });

  final List<RailItem> items;
  final int selectedIndex;
  final ValueChanged<int>? onSelected;
  final VoidCallback? onSettings;

  /// Lights the gear the same way [selectedIndex] lights an item — true on the
  /// settings screens, which have no glyph in [items] of their own.
  final bool settingsSelected;

  /// Null hides the button entirely — the sample repository's rail (tests, goldens) has no
  /// session to sign out of.
  final VoidCallback? onLogout;

  @override
  Widget build(BuildContext context) => Container(
    width: Serval.railWidth,
    padding: const EdgeInsets.symmetric(vertical: 20),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(right: BorderSide(color: Serval.hairline)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        const _BrandMark(),
        const SizedBox(height: 18),
        for (var i = 0; i < items.length; i++)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: _RailButton(
              item: items[i],
              selected: i == selectedIndex,
              onTap: onSelected == null ? null : () => onSelected!(i),
            ),
          ),
        const Spacer(),
        _RailButton(
          item: const RailItem(
            icon: PhosphorIconsRegular.gearSix,
            tooltip: 'Settings',
          ),
          selected: settingsSelected,
          onTap: onSettings,
        ),
        if (onLogout != null) ...[
          const SizedBox(height: 6),
          _RailButton(
            item: const RailItem(
              icon: PhosphorIconsRegular.signOut,
              tooltip: 'Sign out',
            ),
            selected: false,
            onTap: onLogout,
          ),
        ],
        const SizedBox(height: 12),
        const _SourceLink(),
      ],
    ),
  );
}

/// The *Source* offer AGPL section 13 requires, on the surface people actually sit on.
///
/// It lives here rather than in settings because the rail is under every wide screen, and somebody
/// who never opens settings would otherwise never be offered anything. `SettingsNav` draws the
/// same offer on a phone, where there is no rail to carry it.
///
/// The version and commit ride in the tooltip rather than on the rail, because 64px has room for
/// the word and nothing after it — and the link resolves to that commit either way.
///
/// It is the one thing on the rail that is written rather than drawn: no glyph reads as *source* to
/// somebody who is not already looking for it, and an offer you have to hover to find is not an
/// offer.
class _SourceLink extends StatefulWidget {
  const _SourceLink();

  @override
  State<_SourceLink> createState() => _SourceLinkState();
}

class _SourceLinkState extends State<_SourceLink> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: () => launchUrl(
          Uri.parse(SourceOffer.url),
          mode: LaunchMode.externalApplication,
        ),
        child: Tooltip(
          message: 'Source · ${SourceOffer.label}',
          child: Text(
            'Source',
            textAlign: TextAlign.center,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 10.5,
              color: Nocturne.mix(Nocturne.text, _hovered ? 70 : 40),
            ),
          ),
        ),
      ),
    );
  }
}

class _BrandMark extends StatelessWidget {
  const _BrandMark();

  @override
  Widget build(BuildContext context) =>
      SvgPicture.asset('assets/icons/serval_mark.svg', width: 26, height: 28);
}

class _RailButton extends StatefulWidget {
  const _RailButton({required this.item, required this.selected, this.onTap});

  final RailItem item;
  final bool selected;
  final VoidCallback? onTap;

  @override
  State<_RailButton> createState() => _RailButtonState();
}

class _RailButtonState extends State<_RailButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final selected = widget.selected;
    final color = selected
        ? Nocturne.accent300
        : Nocturne.mix(Nocturne.text, 50);

    return MouseRegion(
      cursor: widget.onTap == null
          ? SystemMouseCursors.basic
          : SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Tooltip(
          message: widget.item.tooltip,
          child: Container(
            width: 38,
            height: 38,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(Nocturne.radiusMd),
              color: selected
                  ? Nocturne.mix(Nocturne.accent, 16)
                  : _hovered
                  ? Nocturne.mix(Nocturne.text, 7)
                  : null,
            ),
            child: Stack(
              clipBehavior: Clip.none,
              children: [
                PhosphorIcon(widget.item.icon, size: 18, color: color),
                if (widget.item.badge)
                  Positioned(
                    top: -1,
                    right: -3,
                    child: Container(
                      width: 6,
                      height: 6,
                      decoration: const BoxDecoration(
                        color: Serval.alert,
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
  }
}
