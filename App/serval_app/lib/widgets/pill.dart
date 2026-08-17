import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// A small rounded label — the design's `.tag`, in the variants the two
/// screens actually use.
///
/// Every variant is a tint plus a hairline of the same hue, never a saturated
/// fill; the one exception is [Pill.solid], which the design reserves for the
/// single loudest statement on a screen (*Someone's here*).
class Pill extends StatelessWidget {
  const Pill({
    super.key,
    required this.label,
    this.leadingDot,
    this.icon,
    this.background,
    this.border,
    this.foreground,
    this.fontSize = 11,
    this.fontWeight = Nocturne.headingWeight,
    this.padding = const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
  });

  /// The recording state: a red dot, a red hairline and a warm light text.
  ///
  /// `REC` rather than the word: it sits over live video on a tile that has a
  /// camera name to fit beside it, and three letters everyone already reads as
  /// this exact state cost the name less room than nine.
  factory Pill.recording({String label = 'REC', bool onVideo = false}) => Pill(
    label: label,
    leadingDot: Serval.recording,
    background: onVideo
        ? Nocturne.mix(Nocturne.bg, 70)
        : Nocturne.mix(Serval.recording, 18),
    border: Nocturne.mix(Serval.recording, onVideo ? 45 : 40),
    foreground: Serval.recordingText,
    fontSize: onVideo ? 11.5 : 11,
    padding: onVideo
        ? const EdgeInsets.symmetric(horizontal: 9, vertical: 4)
        : const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
  );

  /// The one filled pill in the design — attention, at the top of 1c.
  factory Pill.solid(String label) => Pill(
    label: label,
    background: Serval.alert,
    foreground: Nocturne.bg,
    fontWeight: FontWeight.w600,
    padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3),
  );

  /// An outlined chip — the summary's `1 person` / `Parcel` / `Doorbell rung`.
  factory Pill.outline(String label) => Pill(
    label: label,
    border: Nocturne.mix(Nocturne.text, 14),
    foreground: Nocturne.mix(Nocturne.text, 65),
    fontSize: 11.5,
    fontWeight: FontWeight.w400,
    padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3),
  );

  final String label;

  /// A 5–6px dot before the label, in this color.
  final Color? leadingDot;
  final PhosphorIconData? icon;
  final Color? background;
  final Color? border;
  final Color? foreground;
  final double fontSize;
  final FontWeight fontWeight;
  final EdgeInsets padding;

  @override
  Widget build(BuildContext context) {
    final dotSize = fontSize >= 11.5 ? 6.0 : 5.0;
    return Container(
      padding: padding,
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(20),
        border: border == null ? null : Border.all(color: border!),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (leadingDot != null) ...[
            Container(
              width: dotSize,
              height: dotSize,
              decoration: BoxDecoration(
                color: leadingDot,
                shape: BoxShape.circle,
              ),
            ),
            const SizedBox(width: 5),
          ],
          if (icon != null) ...[
            PhosphorIcon(icon!, size: fontSize + 3, color: foreground),
            const SizedBox(width: 5),
          ],
          Text(
            label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: fontSize,
              fontWeight: fontWeight,
              color: foreground ?? Nocturne.text,
              height: 1.3,
            ),
          ),
        ],
      ),
    );
  }
}
