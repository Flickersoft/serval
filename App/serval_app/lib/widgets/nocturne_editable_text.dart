import 'package:flutter/cupertino.dart'
    show
        cupertinoDesktopTextSelectionHandleControls,
        cupertinoTextSelectionHandleControls;
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart'
    show
        AdaptiveTextSelectionToolbar,
        TextSelectionThemeData,
        Theme,
        ThemeData,
        desktopTextSelectionHandleControls,
        materialTextSelectionHandleControls;
import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';

/// An [EditableText] you can actually select text in.
///
/// Every field in the app is built on [EditableText] rather than `TextField`, because `TextField`
/// arrives with Material's underline, its filled box and its focus blue — the flood Nocturne
/// forbids. What comes with `TextField` and *not* with a bare [EditableText] is the entire pointer
/// layer: drag to highlight, double-click a word, triple-click a line, a caret placed where you
/// clicked, and the copy/paste menu. [EditableText] paints characters and takes keystrokes and
/// nothing else; the gestures live in [TextSelectionGestureDetectorBuilder], which `TextField`
/// wires up itself.
///
/// So this is that layer, and only that layer. The look is still hand-built to the system: the
/// caret, the selection and its handles are the accent everywhere, which is why the colours are
/// fixed here rather than passed in — a field that took Material's blue would be the one thing the
/// system says never to leave in place.
class NocturneEditableText extends StatefulWidget {
  const NocturneEditableText({
    super.key,
    required this.controller,
    required this.focusNode,
    required this.style,
    this.readOnly = false,
    this.obscureText = false,
    this.obscuringCharacter = '•',
    this.cursorHeight,
    this.maxLines = 1,
    this.minLines,
    this.onChanged,
    this.onSubmitted,
  });

  final TextEditingController controller;
  final FocusNode focusNode;
  final TextStyle style;
  final bool readOnly;
  final bool obscureText;
  final String obscuringCharacter;

  /// A caret shorter than the line box, where the field's own height is tighter than the text's —
  /// the activity column's search box.
  final double? cursorHeight;

  final int? maxLines;
  final int? minLines;
  final ValueChanged<String>? onChanged;
  final ValueChanged<String>? onSubmitted;

  @override
  State<NocturneEditableText> createState() => _NocturneEditableTextState();
}

class _NocturneEditableTextState extends State<NocturneEditableText>
    implements TextSelectionGestureDetectorBuilderDelegate {
  @override
  final GlobalKey<EditableTextState> editableTextKey =
      GlobalKey<EditableTextState>();

  /// The pressure gesture is an iOS hardware feature and nothing else offers it.
  @override
  bool get forcePressEnabled => defaultTargetPlatform == TargetPlatform.iOS;

  @override
  bool get selectionEnabled => true;

  late final _gestures = TextSelectionGestureDetectorBuilder(delegate: this);

  /// Handles and toolbars per platform, the same split `TextField` makes.
  ///
  /// The `…HandleControls` forms rather than the plain ones: only those let [contextMenuBuilder]
  /// decide what the menu looks like, and the plain ones route through a deprecated path that
  /// ignores it.
  TextSelectionControls get _controls => switch (defaultTargetPlatform) {
    TargetPlatform.iOS => cupertinoTextSelectionHandleControls,
    TargetPlatform.macOS => cupertinoDesktopTextSelectionHandleControls,
    TargetPlatform.android ||
    TargetPlatform.fuchsia => materialTextSelectionHandleControls,
    TargetPlatform.linux ||
    TargetPlatform.windows => desktopTextSelectionHandleControls,
  };

  @override
  Widget build(BuildContext context) => Theme(
    // The handles are drawn by the platform's own controls, which read their colour from here
    // rather than from anything the editor is given.
    data: ThemeData(
      textSelectionTheme: TextSelectionThemeData(
        cursorColor: Nocturne.accent400,
        selectionColor: Nocturne.mix(Nocturne.accent, 30),
        selectionHandleColor: Nocturne.accent400,
      ),
    ),
    child: _gestures.buildGestureDetector(
      // The whole box is the field, including the stretch past the end of the text and whatever
      // hint is painted behind it.
      behavior: HitTestBehavior.translucent,
      child: EditableText(
        key: editableTextKey,
        controller: widget.controller,
        focusNode: widget.focusNode,
        readOnly: widget.readOnly,
        obscureText: widget.obscureText,
        obscuringCharacter: widget.obscuringCharacter,
        style: widget.style,
        cursorColor: Nocturne.accent400,
        backgroundCursorColor: Nocturne.mix(Nocturne.text, 20),
        cursorWidth: 1.5,
        cursorHeight: widget.cursorHeight,
        maxLines: widget.maxLines,
        minLines: widget.minLines,
        // The renderer keeps a tap recognizer of its own, which is the whole reason a bare
        // editor could be clicked into but not selected in: it claims the pointer, places a
        // caret, and the gestures above it never see the second click or the drag. Handing the
        // pointer to the detector is what makes a word, a line and a dragged run reachable.
        rendererIgnoresPointer: true,
        selectionColor: Nocturne.mix(Nocturne.accent, 30),
        selectionControls: _controls,
        contextMenuBuilder: (context, state) =>
            AdaptiveTextSelectionToolbar.editableText(editableTextState: state),
        onChanged: widget.onChanged,
        onSubmitted: widget.onSubmitted,
      ),
    ),
  );
}
