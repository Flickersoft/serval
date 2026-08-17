import 'package:flutter/widgets.dart';

/// Nocturne — the design system's tokens, transcribed from its `styles.css`.
///
/// That stylesheet is the source of truth for the system's look; nothing here
/// invents a value. Take every color, size, radius and shadow from these
/// constants rather than hard-coding a hex or a px the tokens already carry.
///
/// The system's own rules, which the widgets in this app follow:
///   * the accent is a line and a glow, never a flood
///   * primary actions are a 1px accent outline on transparent, never a fill
///   * no pure black and no pure white — every value comes from the ramps
///   * on a dark ground, elevation is a hairline edge plus ambient darkness
abstract final class Nocturne {
  // ── roles ────────────────────────────────────────────────────────────────
  static const bg = Color(0xFF161826);
  static const surface = Color(0xFF232532);
  static const text = Color(0xFFE9E9ED);

  /// OKLCH hue 289.2 — the product's own Pro accent hue, at the chroma that
  /// hue carries in the app, so the accent reads as an accent against the
  /// desaturated ramps.
  static const accent = Color(0xFF9184D9);

  /// `color-mix(in srgb, #e9e9ed 16%, transparent)`.
  static const divider = Color(0x29E9E9ED);

  // ── tonal ramps ──────────────────────────────────────────────────────────
  // Generated in OKLCH on one shared lightness scale, so the same step of any
  // role matches the others in visual value. On this dark ground: 700–900 for
  // tinted fills, hovers and subtle borders; 500 as the role's base; 100–300
  // for text on those tints and for pressed states.

  static const neutral100 = Color(0xFFF3F5FE);
  static const neutral200 = Color(0xFFE4E7F5);
  static const neutral300 = Color(0xFFCFD3E5);
  static const neutral400 = Color(0xFFB2B6CA);
  static const neutral500 = Color(0xFF9397AB);
  static const neutral600 = Color(0xFF75798C);
  static const neutral700 = Color(0xFF595D6C);
  static const neutral800 = Color(0xFF3F424D);
  static const neutral900 = Color(0xFF292B31);

  static const accent100 = Color(0xFFF5F4FF);
  static const accent200 = Color(0xFFE7E5FE);
  static const accent300 = Color(0xFFD2CEFD);
  static const accent400 = Color(0xFFB5ABFC);
  static const accent500 = Color(0xFF968AE0);
  static const accent600 = Color(0xFF796CBF);
  static const accent700 = Color(0xFF5D5294);
  static const accent800 = Color(0xFF423A6A);
  static const accent900 = Color(0xFF2B2741);

  // ── type ─────────────────────────────────────────────────────────────────
  static const fontHeading = 'Inter';
  static const fontBody = 'Inter';
  static const headingWeight = FontWeight.w500;

  /// The design's `ui-monospace` role — uppercase kickers, timestamps, and the
  /// telemetry labels painted over video.
  static const fontMono = 'JetBrainsMono';

  // ── spacing ──────────────────────────────────────────────────────────────
  // Density 0.70x is already baked in; this system is dense on purpose.
  static const space1 = 2.8;
  static const space2 = 5.6;
  static const space3 = 8.4;
  static const space4 = 11.2;
  static const space6 = 16.8;
  static const space8 = 22.4;

  // ── radii ────────────────────────────────────────────────────────────────
  static const radiusSm = 4.0;
  static const radiusMd = 8.0;
  static const radiusLg = 14.0;

  // ── elevation ────────────────────────────────────────────────────────────
  // Derived from the ground: on a dark theme a hairline edge plus ambient
  // darkness. Never stack these.
  static const shadowSm = <BoxShadow>[
    BoxShadow(color: neutral800, spreadRadius: 1),
  ];
  static const shadowMd = <BoxShadow>[
    BoxShadow(color: neutral700, spreadRadius: 1),
    BoxShadow(color: Color(0x8C000000), blurRadius: 18, offset: Offset(0, 6)),
  ];
  static const shadowLg = <BoxShadow>[
    BoxShadow(color: neutral500, spreadRadius: 1),
    BoxShadow(color: Color(0xA6000000), blurRadius: 40, offset: Offset(0, 16)),
  ];

  /// The CSS `color-mix(in srgb, <color> <percent>%, transparent)` the
  /// stylesheet leans on for hovers, tints and muted text.
  static Color mix(Color color, double percent) =>
      color.withValues(alpha: percent / 100);

  /// `.text-muted` — text at 55%.
  static Color get textMuted => mix(text, 55);
}
