import 'package:flutter/material.dart';

import 'nocturne.dart';
import 'serval_tokens.dart';

/// Assembles Nocturne's tokens into a [ThemeData].
///
/// Material's own defaults are deliberately neutered here — filled buttons,
/// ripples and the stock focus ring all contradict the design system, which
/// outlines its primary actions and carries focus as a 2px accent outline.
/// Widgets in `lib/widgets/` paint their own states from the ramps; this theme
/// exists so anything that slips through still lands on the right ground.
ThemeData buildServalTheme() {
  const scheme = ColorScheme.dark(
    primary: Nocturne.accent,
    onPrimary: Nocturne.bg,
    primaryContainer: Nocturne.accent800,
    onPrimaryContainer: Nocturne.accent100,
    secondary: Nocturne.accent400,
    onSecondary: Nocturne.bg,
    error: Serval.recording,
    onError: Nocturne.bg,
    surface: Nocturne.bg,
    onSurface: Nocturne.text,
    surfaceContainerHighest: Nocturne.surface,
    outline: Nocturne.neutral700,
    outlineVariant: Nocturne.neutral800,
  );

  // Nocturne's own scale: 15px body at 1.55, headings at weight 500 with a
  // -0.015em tracking and 1.12 leading.
  const heading = TextStyle(
    fontFamily: Nocturne.fontHeading,
    fontWeight: Nocturne.headingWeight,
    height: 1.12,
    letterSpacing: -0.015 * 16,
    color: Nocturne.text,
  );
  const body = TextStyle(
    fontFamily: Nocturne.fontBody,
    fontWeight: FontWeight.w400,
    height: 1.55,
    color: Nocturne.text,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: scheme,
    scaffoldBackgroundColor: Serval.canvas,
    fontFamily: Nocturne.fontBody,
    splashFactory: NoSplash.splashFactory,
    highlightColor: Colors.transparent,
    textSelectionTheme: TextSelectionThemeData(
      cursorColor: Nocturne.accent,
      selectionColor: Nocturne.mix(Nocturne.accent, 30),
      selectionHandleColor: Nocturne.accent,
    ),
    dividerTheme: const DividerThemeData(
      color: Nocturne.divider,
      thickness: 1,
      space: 1,
    ),
    textTheme: TextTheme(
      displayLarge: heading.copyWith(fontSize: 42),
      displayMedium: heading.copyWith(fontSize: 32),
      displaySmall: heading.copyWith(fontSize: 25),
      headlineMedium: heading.copyWith(fontSize: 20),
      headlineSmall: heading.copyWith(fontSize: 16),
      titleMedium: heading.copyWith(fontSize: 17, letterSpacing: 0),
      titleSmall: heading.copyWith(fontSize: 15, letterSpacing: 0),
      bodyLarge: body.copyWith(fontSize: 15),
      bodyMedium: body.copyWith(fontSize: 13.5, height: 1.5),
      bodySmall: body.copyWith(fontSize: 12.5, height: 1.45),
      labelLarge: body.copyWith(
        fontSize: 14,
        fontWeight: Nocturne.headingWeight,
      ),
      labelMedium: body.copyWith(fontSize: 13),
      labelSmall: body.copyWith(fontSize: 11.5),
    ),
  );
}

/// The design's `ui-monospace` role: uppercase kickers, timestamps, the
/// resolution strings painted over video, and the detection label.
TextStyle monoStyle({
  double fontSize = 11,
  Color? color,
  FontWeight fontWeight = FontWeight.w400,
  double letterSpacing = 0,
  double? height,
}) => TextStyle(
  fontFamily: Nocturne.fontMono,
  fontSize: fontSize,
  fontWeight: fontWeight,
  letterSpacing: letterSpacing,
  height: height,
  color: color ?? Nocturne.text,
);

/// The uppercase mono kicker that labels a section — `RIGHT NOW`,
/// `EARLIER TODAY`, `LIVE TRANSCRIPT`.
TextStyle kickerStyle({Color? color, double fontSize = 10}) => monoStyle(
  fontSize: fontSize,
  letterSpacing: 0.14 * fontSize,
  color: color ?? Nocturne.mix(Nocturne.text, 35),
);
