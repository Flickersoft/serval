import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../models/source_offer.dart';
import '../theme/nocturne.dart';

/// The *Source* line AGPL section 13 requires: a user reaching Serval over a network is entitled
/// to the source of the version they are running, offered from the interface they are using.
///
/// It names this build rather than the project, because the entitlement is to *this* build and the
/// default branch moves — the release for a person to quote, the commit because only that
/// identifies what is running. Where neither is known — any build made outside the image workflow
/// — it says the licence and links to the repository, which is the honest answer rather than a
/// commit that might not be the one running. [SourceOffer.label] decides the wording.
///
/// Drawn where there is room to spell it out: the sign-in screen's footer, and the
/// settings drill-down on a phone. `IconRail` makes the same offer in 64px, as the word alone.
class SourceLine extends StatefulWidget {
  const SourceLine({super.key});

  @override
  State<SourceLine> createState() => _SourceLineState();
}

class _SourceLineState extends State<SourceLine> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final color = Nocturne.mix(Nocturne.text, _hovered ? 68 : 45);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: () => launchUrl(
          Uri.parse(SourceOffer.url),
          mode: LaunchMode.externalApplication,
        ),
        child: Row(
          children: [
            PhosphorIcon(
              PhosphorIconsRegular.codeSimple,
              size: 13,
              color: color,
            ),
            const SizedBox(width: 7),
            Expanded(
              child: Text(
                'Source · ${SourceOffer.label}',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  color: color,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
