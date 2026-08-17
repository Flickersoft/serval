import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_editable_text.dart';

/// Nocturne's `.field` + `.input`: a 12.5px label over a 36px input.
///
/// The system's rules that matter here — the input is a hairline edge on a barely-lifted ground
/// rather than a filled box, focus is the accent as a line, and nothing is ever a browser
/// default.
class NocturneField extends StatefulWidget {
  const NocturneField({
    super.key,
    required this.label,
    required this.controller,
    this.hint,
    this.leadingIcon,
    this.mono = false,
    this.obscure = false,
    this.maskedPreview,
    this.maskedNote,
    this.copyable = false,
    this.enabled = true,
    this.onChanged,
    this.lines = 1,
  });

  final String label;
  final TextEditingController controller;

  /// A glyph ahead of the value — the sign-in form's `ph-user`/`ph-lock-simple`. Null everywhere
  /// else; nothing in the settings forms uses one.
  final PhosphorIconData? leadingIcon;

  /// Shown in place of an empty value — "Pick automatically", not a label.
  final String? hint;

  /// Mono for anything an installer reads character by character — URLs, tokens, addresses.
  final bool mono;

  /// Starts masked, with the design's eye to reveal.
  ///
  /// The design is explicit that passwords are "masked with a reveal, never plain in the page".
  final bool obscure;

  /// What to show instead of dots while [obscure] is hiding the value.
  ///
  /// A stream's address carries its credentials in the URL, so it has to be masked — but
  /// replacing the whole thing with dots hides the host and the path too, and those are the
  /// part an installer is reading. Passing the URL with only its password starred keeps the
  /// address legible and the secret hidden, which is exactly what the design draws.
  ///
  /// While it is showing, the field is not editable: revealing is how you get a caret. That is
  /// deliberate — it makes overwriting a working camera's address a decision rather than a
  /// stray keystroke. Say so in [maskedNote], or the decision is one nobody knows is theirs
  /// to make.
  final String? maskedPreview;

  /// Why the box will not take typing yet, shown under it while [maskedPreview] is standing in.
  ///
  /// A field that quietly refuses the caret is a dead end without this — the reveal is a 14px
  /// glyph among others, and there is nothing else on the page that names it as the way in.
  final String? maskedNote;

  /// Adds the design's copy affordance. Copying yields the *real* value, not the mask — a copy
  /// button that hands you dots is worse than no copy button.
  final bool copyable;

  final bool enabled;
  final ValueChanged<String>? onChanged;

  /// How many lines the editor shows. One everywhere except the settings lists, where a value is
  /// one entry per line and a single-line box would show the first of them and hide the rest —
  /// which reads as a field holding one thing when it holds several.
  final int lines;

  @override
  State<NocturneField> createState() => _NocturneFieldState();
}

class _NocturneFieldState extends State<NocturneField> {
  final _focus = FocusNode();
  late bool _hidden = widget.obscure;
  bool _copied = false;

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

  Future<void> _copy() async {
    await Clipboard.setData(ClipboardData(text: widget.controller.text));
    if (!mounted) return;

    setState(() => _copied = true);
    await Future<void>.delayed(const Duration(seconds: 2));
    if (mounted) setState(() => _copied = false);
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      // An empty label is a field drawn under a heading of its own — the settings cards, the
      // camera form's lists — where the box is the whole control.
      if (widget.label.isNotEmpty) ...[
        Text(
          widget.label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12.5,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
        const SizedBox(height: 6),
      ],
      Container(
        height: 36,
        padding: const EdgeInsets.symmetric(horizontal: 11),
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, widget.enabled ? 3 : 1.5),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            // Focus is the accent as a line, per the system — not a ring, not a glow.
            color: _focus.hasFocus
                ? Nocturne.mix(Nocturne.accent, 65)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Row(
          children: [
            if (widget.leadingIcon != null) ...[
              PhosphorIcon(
                widget.leadingIcon!,
                size: 16,
                color: Nocturne.mix(Nocturne.text, 40),
              ),
              const SizedBox(width: 9),
            ],
            Expanded(child: _input),
            if (widget.obscure)
              _iconButton(
                _hidden
                    ? PhosphorIconsRegular.eye
                    : PhosphorIconsRegular.eyeSlash,
                () => setState(() => _hidden = !_hidden),
                tooltip: _hidden
                    ? (widget.maskedPreview != null
                          ? 'Reveal to edit'
                          : 'Reveal')
                    : 'Hide again',
              ),
            if (widget.copyable)
              _iconButton(
                _copied
                    ? PhosphorIconsRegular.check
                    : PhosphorIconsRegular.copy,
                _copy,
                tooltip: 'Copy',
              ),
          ],
        ),
      ),
      if (_masked && widget.maskedNote != null) ...[
        const SizedBox(height: 5),
        Text(
          widget.maskedNote!,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            height: 1.4,
            color: Nocturne.mix(Nocturne.text, 42),
          ),
        ),
      ],
    ],
  );

  /// Whether the preview is standing in for the editor, rather than the editor obscuring its own
  /// characters. Only then is the box unable to take typing.
  bool get _masked => _hidden && widget.maskedPreview != null;

  TextStyle get _style => widget.mono
      ? monoStyle(fontSize: 12, color: Nocturne.mix(Nocturne.text, 82))
      : TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 13.5,
          color: widget.enabled
              ? Nocturne.text
              : Nocturne.mix(Nocturne.text, 45),
        );

  Widget get _input {
    // A masked preview stands in for the editor entirely, rather than obscuring its characters:
    // the point is to show a readable address with one part starred, which dots cannot do.
    if (_masked) {
      // Clicking the value is what someone does when they mean to type in it, so that is what
      // reveals — the eye is the affordance for people who found it, not the only way through.
      return MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: () => setState(() => _hidden = false),
          child: Text(
            widget.maskedPreview!,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: _style,
          ),
        ),
      );
    }

    final style = _style;

    return Stack(
      alignment: Alignment.centerLeft,
      children: [
        // An editor has no placeholder of its own — that belongs to TextField, which brings
        // Material's underline and focus colour with it.
        if (widget.hint != null && widget.controller.text.isEmpty)
          Text(
            widget.hint!,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: style.copyWith(color: Nocturne.mix(Nocturne.text, 38)),
          ),
        NocturneEditableText(
          controller: widget.controller,
          focusNode: _focus,
          readOnly: !widget.enabled,
          obscureText: _hidden,
          style: style,
          maxLines: widget.lines,
          minLines: widget.lines,
          onChanged: (value) {
            // The hint appears and disappears with the text.
            if (widget.hint != null) setState(() {});
            widget.onChanged?.call(value);
          },
        ),
      ],
    );
  }

  Widget _iconButton(
    PhosphorIconData icon,
    VoidCallback onTap, {
    required String tooltip,
  }) => Tooltip(
    message: tooltip,
    child: MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: onTap,
        // What a pointer has to hit is the box, not the 14px glyph inside it: full field height
        // and wide enough that the eye and the copy are not the same target.
        child: SizedBox(
          width: 24,
          height: double.infinity,
          child: Center(
            child: PhosphorIcon(
              icon,
              size: 14,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ),
      ),
    ),
  );
}

/// A read-only field: the same 36px box, no caret, for values that exist but cannot be edited
/// here — the camera id after creation, chiefly.
class NocturneReadOnlyField extends StatelessWidget {
  const NocturneReadOnlyField({
    super.key,
    required this.label,
    required this.value,
    this.mono = false,
    this.note,
  });

  final String label;
  final String value;
  final bool mono;

  /// Why it cannot be edited, shown under the box.
  final String? note;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 62),
        ),
      ),
      const SizedBox(height: 6),
      Container(
        height: 36,
        alignment: Alignment.centerLeft,
        padding: const EdgeInsets.symmetric(horizontal: 11),
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, 2),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
        ),
        child: Text(
          value,
          overflow: TextOverflow.ellipsis,
          style: mono
              ? monoStyle(fontSize: 12, color: Nocturne.mix(Nocturne.text, 55))
              : TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13.5,
                  color: Nocturne.mix(Nocturne.text, 55),
                ),
        ),
      ),
      if (note != null) ...[
        const SizedBox(height: 5),
        Text(
          note!,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            height: 1.4,
            color: Nocturne.mix(Nocturne.text, 42),
          ),
        ),
      ],
    ],
  );
}

/// A section heading with the sentence under it that says what the section is for.
///
/// The design leads every settings group this way — "Streams / Most cameras send a sharp stream
/// and a smaller one. Tell Serval what each is for." — which is what lets a homeowner read the
/// page while the technical field stays visible underneath.
class SettingsSection extends StatelessWidget {
  const SettingsSection({
    super.key,
    required this.title,
    required this.blurb,
    required this.child,
    this.trailing,
  });

  final String title;

  /// Plain language. Not a tooltip, not help text behind an icon — the design puts it in the
  /// flow because it is the part a non-installer actually reads.
  final Widget blurb;

  final Widget child;

  /// A figure about the section rather than in it, set against the far edge of the heading — how
  /// long the volume walk took. Absent on every section that has nothing to say there.
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final heading = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          title,
          style: const TextStyle(
            fontFamily: Nocturne.fontHeading,
            fontSize: 14.5,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.text,
          ),
        ),
        const SizedBox(height: 5),
        DefaultTextStyle(
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12.5,
            height: 1.45,
            color: Nocturne.mix(Nocturne.text, 50),
          ),
          child: blurb,
        ),
      ],
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (trailing == null)
          heading
        else
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(child: heading),
              const SizedBox(width: 12),
              trailing!,
            ],
          ),
        const SizedBox(height: 12),
        child,
      ],
    );
  }
}

/// The system's rule for a rule: it fades to transparent at both ends over 48px rather than
/// stopping cleanly.
class SettingsDivider extends StatelessWidget {
  const SettingsDivider({super.key});

  @override
  Widget build(BuildContext context) => Container(
    height: 1,
    decoration: BoxDecoration(
      gradient: LinearGradient(
        colors: [
          Serval.panel.withValues(alpha: 0),
          Nocturne.mix(Nocturne.text, 10),
          Nocturne.mix(Nocturne.text, 10),
          Serval.panel.withValues(alpha: 0),
        ],
        stops: const [0, 0.06, 0.94, 1],
      ),
    ),
  );
}
