/// The settings idiom, as widgets both settings pages use.
///
/// The Server settings page and the camera editor answer the same shape of question — a list of
/// named values, each with an explanation, a place it came from, and a way to put it back — and
/// they drifted apart because none of this was shared. It lived as ten private widgets inside
/// `settings_screen.dart`, so the camera form grew its own chips, its own save bar and its own
/// idea of what a card looks like.
///
/// Nothing here knows what a *setting* is beyond a label, a control and a sentence. The Server
/// page maps its catalogue onto these; the camera editor maps typed record fields onto the same
/// ones. That is the whole reason the split is here rather than in either screen.
library;

import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/server_settings.dart' show SettingSource;
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'dashed_border.dart';
import 'nocturne_button.dart';
import 'nocturne_editable_text.dart';
import 'nocturne_field.dart';
import 'nocturne_slider.dart';
import 'paired_rows.dart';

/// Makes a pane of settings inert for an account that may read them but not change them.
///
/// Wrapping the pane rather than passing `enabled: false` down to each control is the deliberate
/// choice, and the reason is what happens to the *next* setting somebody adds: threading a flag
/// through every field means the one added a year from now is editable again, and nothing fails
/// until a Viewer finds it. A wrapper cannot be forgotten by a field that does not know it exists.
///
/// It is not the safeguard — the Server refuses these writes whoever asks, and that is the
/// safeguard. This is about not offering a control that can only fail.
///
/// The dimming matters as much as the absorbing: a pane that looks live and ignores clicks reads as
/// a broken page, which is a worse answer than a pane that plainly is not yours to edit.
class ReadOnlyPane extends StatelessWidget {
  const ReadOnlyPane({super.key, required this.readOnly, required this.child});

  final bool readOnly;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (!readOnly) return child;

    return Opacity(
      opacity: 0.55,
      // ExcludeSemantics too: a screen reader offering "text field, editable" on something that
      // cannot be edited is the same broken promise the dimming is there to avoid making.
      child: ExcludeSemantics(child: AbsorbPointer(child: child)),
    );
  }
}

/// Cards packed into rows the way both settings panes draw them.
///
/// Two to a row is the design's shape and needs the design's width; below it the pair becomes one
/// column rather than two cards too narrow to hold a label and a value. Heights are matched because
/// these have edges — a short card beside a long one would open a gap the next row starts below,
/// which the design does not draw.
List<Widget> settingCardRows({
  required List<PairedItem> items,
  required bool paired,
}) {
  final rows = pairedRows(paired: paired, matchHeights: true, items: items);
  return [
    for (final row in rows) ...[row, const SizedBox(height: 12)],
  ];
}

/// A glyph per section, so the list reads as a set of places rather than a column of sentences.
///
/// **Keyed on the name, which is why the six shared names appear once.** A camera section that
/// overrides a Server group carries that group's name exactly — *Streams*, *Recording*,
/// *Objects & alerts*, *Motion detection*, *Speech & transcription*, *Sound recognition* — so one
/// entry serves both pages and the two cannot drift to different glyphs. A name neither page knows
/// falls back to the settings glyph, which is what happens when the Server grows a group this App
/// has not seen.
PhosphorIconData settingsGroupIcon(String group) => switch (group) {
  // Shared by the Server's catalogue and the camera editor's sections.
  'Recording' => PhosphorIconsRegular.hardDrives,
  'Streams' => PhosphorIconsRegular.broadcast,
  'Objects & alerts' => PhosphorIconsRegular.boundingBox,
  'Motion detection' => PhosphorIconsRegular.wind,
  'Speech & transcription' => PhosphorIconsRegular.microphone,
  'Sound recognition' => PhosphorIconsRegular.waveform,
  // The Server's catalogue only.
  'Live view' => PhosphorIconsRegular.broadcast,
  'Pan, tilt & zoom' => PhosphorIconsRegular.arrowsOutCardinal,
  'System health' => PhosphorIconsRegular.pulse,
  'Sign-in sessions' => PhosphorIconsRegular.key,
  'Alerts' => PhosphorIconsRegular.warning,
  'Notifications' => PhosphorIconsRegular.bellRinging,
  'Detection engine' => PhosphorIconsRegular.cpu,
  'Object tracking' => PhosphorIconsRegular.crosshair,
  'Scene descriptions' => PhosphorIconsRegular.textAlignLeft,
  'Voice identification' => PhosphorIconsRegular.userSound,
  // The camera editor's sections only.
  'General' => PhosphorIconsRegular.identificationCard,
  'Camera control' => PhosphorIconsRegular.gameController,
  'Masks & zones' => PhosphorIconsRegular.polygon,
  'Analysis' => PhosphorIconsRegular.sparkle,
  'Playback' => PhosphorIconsRegular.speakerHigh,
  _ => PhosphorIconsRegular.slidersHorizontal,
};

/// The left column: a search box over a list of sections.
class SettingsGroupList extends StatelessWidget {
  const SettingsGroupList({
    super.key,
    required this.groups,
    required this.counts,
    required this.changed,
    required this.restartPending,
    required this.selected,
    required this.search,
    required this.onSearchChanged,
    required this.onSelect,
    this.searchPlaceholder = 'Search settings',
    this.compact = false,
  });

  final List<String> groups;
  final Map<String, int> counts;
  final Set<String> changed;
  final Set<String> restartPending;
  final String? selected;
  final TextEditingController search;
  final VoidCallback onSearchChanged;
  final ValueChanged<String> onSelect;

  /// What the empty search box says it searches. The Server page searches settings; the camera
  /// editor searches one camera, and saying so is the only hint that the list is not the Server's.
  final String searchPlaceholder;

  /// Takes the screen instead of a column beside the pane, and lights nothing: narrow, this is the
  /// menu you leave rather than the place you are.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SettingsSearchBox(
          controller: search,
          onChanged: onSearchChanged,
          placeholder: searchPlaceholder,
        ),
        const SizedBox(height: 6),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.only(bottom: 8),
            children: [
              for (final group in groups)
                _SettingsGroupRow(
                  group: group,
                  count: counts[group] ?? 0,
                  changed: changed.contains(group),
                  restartPending: restartPending.contains(group),
                  selected: !compact && group == selected,
                  compact: compact,
                  onTap: () => onSelect(group),
                ),
            ],
          ),
        ),
      ],
    );

    return compact ? body : SizedBox(width: 252, child: body);
  }
}

class _SettingsGroupRow extends StatefulWidget {
  const _SettingsGroupRow({
    required this.group,
    required this.count,
    required this.changed,
    required this.restartPending,
    required this.selected,
    required this.onTap,
    this.compact = false,
  });

  final String group;
  final int count;

  /// One of this group's settings is edited and not yet sent.
  final bool changed;

  /// One of them is saved and waiting for a restart before the Server uses it.
  final bool restartPending;

  final bool selected;
  final VoidCallback onTap;

  /// A row you open rather than a row you pick: 56px, with a caret.
  final bool compact;

  @override
  State<_SettingsGroupRow> createState() => _SettingsGroupRowState();
}

class _SettingsGroupRowState extends State<_SettingsGroupRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTap: widget.onTap,
      behavior: HitTestBehavior.opaque,
      child: Container(
        constraints: widget.compact
            ? const BoxConstraints(minHeight: 56)
            : null,
        margin: const EdgeInsets.only(bottom: 2),
        padding: widget.compact
            ? const EdgeInsets.symmetric(horizontal: 10, vertical: 10)
            : const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        decoration: BoxDecoration(
          color: widget.selected
              ? Nocturne.mix(Nocturne.accent, 16)
              : _hovered
              ? Nocturne.mix(Nocturne.text, 5)
              : null,
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: widget.selected
                ? Nocturne.mix(Nocturne.accent, 40)
                : const Color(0x00000000),
          ),
        ),
        child: Row(
          children: [
            PhosphorIcon(
              settingsGroupIcon(widget.group),
              size: widget.compact ? 18 : 15,
              color: widget.selected
                  ? Nocturne.accent300
                  : Nocturne.mix(Nocturne.text, 45),
            ),
            SizedBox(width: widget.compact ? 13 : 9),
            Expanded(
              child: Text(
                widget.group,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: widget.compact ? 14.5 : 13,
                  fontWeight: widget.selected
                      ? Nocturne.headingWeight
                      : FontWeight.w400,
                  color: widget.selected
                      ? Nocturne.text
                      : Nocturne.mix(Nocturne.text, widget.compact ? 85 : 72),
                ),
              ),
            ),
            if (widget.restartPending) ...[
              const SizedBox(width: 6),
              const SettingBadge('restart'),
            ],
            if (widget.changed) ...[
              const SizedBox(width: 6),
              Container(
                width: 6,
                height: 6,
                decoration: const BoxDecoration(
                  color: Serval.alertText,
                  shape: BoxShape.circle,
                ),
              ),
            ],
            const SizedBox(width: 6),
            Text(
              '${widget.count}',
              style: monoStyle(
                fontSize: 11,
                color: Nocturne.mix(Nocturne.text, widget.selected ? 50 : 30),
              ),
            ),
            if (widget.compact) ...[
              const SizedBox(width: 8),
              PhosphorIcon(
                PhosphorIconsRegular.caretRight,
                size: 14,
                color: Nocturne.mix(Nocturne.text, 35),
              ),
            ],
          ],
        ),
      ),
    ),
  );
}

class SettingsSearchBox extends StatefulWidget {
  const SettingsSearchBox({
    super.key,
    required this.controller,
    required this.onChanged,
    this.placeholder = 'Search settings',
  });

  final TextEditingController controller;
  final VoidCallback onChanged;
  final String placeholder;

  @override
  State<SettingsSearchBox> createState() => _SettingsSearchBoxState();
}

class _SettingsSearchBoxState extends State<SettingsSearchBox> {
  /// Owned rather than built in `build`: a node made fresh each frame is a different node every
  /// time the query changes, so the caret would be taken away by the keystroke that moved it.
  final _focus = FocusNode();

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Container(
    height: 32,
    padding: const EdgeInsets.symmetric(horizontal: 11),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(7),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Row(
      children: [
        PhosphorIcon(
          PhosphorIconsRegular.magnifyingGlass,
          size: 14,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Stack(
            alignment: Alignment.centerLeft,
            children: [
              if (widget.controller.text.isEmpty)
                Text(
                  widget.placeholder,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 42),
                  ),
                ),
              NocturneEditableText(
                controller: widget.controller,
                focusNode: _focus,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.text,
                ),
                onChanged: (_) => widget.onChanged(),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

class SettingsNoMatches extends StatelessWidget {
  const SettingsNoMatches({
    super.key,
    required this.query,
    required this.empty,
  });

  final String query;

  /// What to say when nothing was searched for and there is still nothing to show.
  final String empty;

  @override
  Widget build(BuildContext context) => Center(
    child: Text(
      query.isEmpty ? empty : 'Nothing matches “$query”.',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 13,
        color: Nocturne.mix(Nocturne.text, 45),
      ),
    ),
  );
}

/// One setting as a card: its name, what it holds, what that does, and where the value came from.
///
/// Deliberately knows nothing about the catalogue. Both callers work out their own label, help,
/// control and source; this supplies the chrome and the rules about what sits where, which is the
/// part that has to match between the two pages.
class SettingCard extends StatelessWidget {
  const SettingCard({
    super.key,
    required this.label,
    required this.control,
    required this.help,
    this.source,
    this.pending = false,
    this.restartRequired = false,
    this.restartPending = false,
    this.headerTrailing,
    this.resetLabel,
    this.onReset,
    this.compact = false,
  });

  final String label;

  /// The control itself — a slider, a box, a toggle, a row of chips.
  final Widget control;

  /// The explanation, in the flow. See the library doc on why this is not a tooltip.
  final Widget help;

  /// Where the value in force came from. Null draws no chip, for a field that has no such notion —
  /// a camera's name came from nowhere but itself.
  final SettingSource? source;

  /// True when this field has been edited but not saved.
  final bool pending;

  /// True when changing this needs the Server restarted before it takes effect.
  final bool restartRequired;

  /// True when the saved value is not the one the running Server is using.
  final bool restartPending;

  /// A slider's value read out in numerals, or a list's way back to the whole built-in set.
  final Widget? headerTrailing;

  /// The way back. Both must be set for the link to appear.
  final String? resetLabel;
  final VoidCallback? onReset;

  /// Drawn narrower than the design's card. The reset takes its own line and the bounds give up
  /// their place at the end of the row, rather than either being cut off.
  final bool compact;

  bool get _showsReset => resetLabel != null && onReset != null;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
    decoration: BoxDecoration(
      color: pending
          ? Nocturne.mix(Nocturne.accent, 8)
          : restartPending
          ? Serval.alert.withValues(alpha: 0.07)
          : Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(
        color: pending
            ? Nocturne.mix(Nocturne.accent, 45)
            : restartPending
            ? Serval.alert.withValues(alpha: 0.35)
            : Nocturne.mix(Nocturne.text, 10),
      ),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              // A wrap rather than a row: on one line at the design's width, and the chips fall
              // under the name rather than off the card when there is less of it.
              child: Wrap(
                spacing: 8,
                runSpacing: 4,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Text(
                    label,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12.5,
                      color: Nocturne.mix(Nocturne.text, pending ? 80 : 72),
                    ),
                  ),
                  // Every card says where its value came from. An edit in hand outranks it: the
                  // value on screen is not the stored one, and that is the more urgent fact.
                  if (pending)
                    const SettingBadge('not saved', accent: true)
                  else if (source case final source?)
                    _SettingSourceChip(source: source),
                  if (restartRequired) const SettingBadge('needs a restart'),
                ],
              ),
            ),
            if (headerTrailing case final trailing?) ...[
              const SizedBox(width: 8),
              trailing,
            ],
          ],
        ),
        const SizedBox(height: 7),
        control,
        const SizedBox(height: 7),
        if (compact) ...[
          help,
          if (_showsReset) ...[
            const SizedBox(height: 5),
            Align(
              alignment: Alignment.centerLeft,
              child: SettingsLinkText(resetLabel!, onTap: onReset!),
            ),
          ],
        ] else
          Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Expanded(child: help),
              // The way back, naming what it goes back to — a reset with the before-and-after on
              // it is a decision rather than a guess. A list keeps its own in the header, where
              // it can say *list* and mean the whole set.
              //
              // The link takes the width its text asks for and the explanation takes what is
              // left, so the label has to be a phrase: see [settingBrief], which is how a caller
              // cuts a value down to one.
              if (_showsReset) ...[
                const SizedBox(width: 10),
                SettingsLinkText(resetLabel!, onTap: onReset!),
              ],
            ],
          ),
        if (restartPending) ...[
          const SizedBox(height: 4),
          const Text(
            'Saved, but not in use — this is read while the Server starts. Restarting it is '
            'what applies it.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Serval.alertText,
            ),
          ),
        ],
      ],
    ),
  );
}

/// The style the cards write help text in, so a caller building its own rich span matches.
TextStyle settingHelpStyle() => TextStyle(
  fontFamily: Nocturne.fontBody,
  fontSize: 11.5,
  height: 1.45,
  color: Nocturne.mix(Nocturne.text, 45),
);

/// Whether a slider can express this range usefully. A fraction between 0 and 1 is the case it is
/// unambiguously right for; so is a percentage. A slider over 1–365 days is a control you cannot
/// land a specific value with, and "keep footage for about a fortnight" is not what anyone means.
bool settingSlidable(double? min, double? max) {
  if (min == null || max == null) return false;
  return max - min <= 100;
}

/// A number as the cards write it: whole ones plain, fractions to at least two places, so a floor
/// of 0.6 and one of 0.35 read as 0.60 and 0.35 and can be compared down a column.
String settingFigure(num value) {
  if (value == value.roundToDouble()) return '${value.round()}';

  final text = '$value';
  final decimals = text.length - text.indexOf('.') - 1;
  return decimals < 2 ? value.toStringAsFixed(2) : text;
}

/// A value as a link can carry it: one line, cut at a word when it runs on.
///
/// The reset link is laid out at the width its own text asks for and the explanation beside it
/// takes what is left, so a label that ran to a sentence would squeeze the prose into a column one
/// word wide. Whitespace is collapsed for the same reason: a prompt is stored over several lines
/// and a link is one.
String settingBrief(String value, {int max = 40}) {
  final flat = value.replaceAll(RegExp(r'\s+'), ' ').trim();
  if (flat.length <= max) return flat;

  final cut = flat.substring(0, max);
  final space = cut.lastIndexOf(' ');
  return '${(space > max ~/ 2 ? cut.substring(0, space) : cut).trimRight()}…';
}

/// A value with its unit, as the header reads it out beside a slider.
String settingReadout(Object? value, String? unit) {
  final current = switch (value) {
    final num number => number,
    _ => null,
  };
  if (current == null) return 'not set';

  final text = settingFigure(current);
  return unit == null ? text : '$text $unit';
}

/// A number: a slider where the range is small enough to reason about, and a box the width of a
/// number where it is not — with the unit beside it, since "14" on its own is not an answer to
/// *keep recordings for*.
class SettingNumberControl extends StatefulWidget {
  const SettingNumberControl({
    super.key,
    required this.value,
    required this.whole,
    required this.onChanged,
    this.min,
    this.max,
    this.unit,
    this.trailing,
  });

  final Object? value;

  /// Integers snap and parse as integers; anything else keeps two decimal places.
  final bool whole;

  final double? min;
  final double? max;
  final String? unit;

  final ValueChanged<Object?> onChanged;

  /// The bounds, or the way back to the default — whichever the card decided.
  final Widget? trailing;

  @override
  State<SettingNumberControl> createState() => _SettingNumberControlState();
}

class _SettingNumberControlState extends State<SettingNumberControl> {
  late final TextEditingController _controller = TextEditingController(
    text: _text(widget.value),
  );

  @override
  void didUpdateWidget(SettingNumberControl old) {
    super.didUpdateWidget(old);

    // Compared as a number, not as text. What is typed and what is staged are the same value in
    // two spellings — typing "1" into a fractional setting stages 1.0, which prints as "1.0" —
    // and rewriting the box to the other spelling mid-word takes the caret with it, which is a
    // field that accepts one character and then refuses the next.
    if (_parse(_controller.text) == widget.value) return;

    final text = _text(widget.value);
    if (text == _controller.text) return;

    // The value moved underneath the box — a slider drag, a reset, a save landing. Set the
    // selection with the text rather than after it: assigning `text` alone leaves the caret
    // nowhere, which is the same dead field by another route.
    _controller.value = TextEditingValue(
      text: text,
      selection: TextSelection.collapsed(offset: text.length),
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  /// The box writes a number the way the rest of the card does — "15", not "15.0", which is what
  /// a fractional setting's whole value prints as and reads as a stray decimal someone left.
  static String _text(Object? value) => switch (value) {
    final num number => settingFigure(number),
    null => '',
    _ => '$value',
  };

  @override
  Widget build(BuildContext context) {
    if (settingSlidable(widget.min, widget.max)) {
      final min = widget.min!;
      final max = widget.max!;
      final current = switch (widget.value) {
        final num number => number.toDouble(),
        _ => null,
      };

      return Row(
        children: [
          Expanded(
            child: NocturneSlider(
              value: (current ?? min).clamp(min, max),
              min: min,
              max: max,
              onChanged: (picked) => widget.onChanged(_snap(picked)),
            ),
          ),
          if (widget.trailing case final trailing?) ...[
            const SizedBox(width: 9),
            trailing,
          ],
        ],
      );
    }

    return Row(
      children: [
        SizedBox(
          width: 84,
          child: NocturneField(
            label: '',
            controller: _controller,
            mono: true,
            onChanged: (text) => widget.onChanged(_parse(text)),
          ),
        ),
        if (widget.unit case final unit?) ...[
          const SizedBox(width: 9),
          // Loose and unflexed: the unit takes the width of the word and leaves the rest of the
          // row to the spacer, so the bounds stay at the end of the card rather than halfway.
          Flexible(
            flex: 0,
            child: Text(
              unit,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 55),
              ),
            ),
          ),
        ],
        if (widget.trailing case final trailing?) ...[
          const Spacer(),
          const SizedBox(width: 9),
          trailing,
        ],
      ],
    );
  }

  /// Snapped to what the card displays, so the number on screen is the number stored. Without it
  /// the readout says "0.35" while the record holds 0.35029, and the two disagree in a way only
  /// an API client would ever see — the same reason the camera form snaps its speech threshold.
  Object _snap(double value) =>
      widget.whole ? value.round() : (value * 100).round() / 100;

  Object? _parse(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return null;
    return widget.whole ? int.tryParse(trimmed) : double.tryParse(trimmed);
  }
}

/// A text box that follows a value it does not own.
class SettingTextControl extends StatefulWidget {
  const SettingTextControl({
    super.key,
    required this.value,
    required this.onChanged,
  });

  final String value;
  final ValueChanged<String> onChanged;

  @override
  State<SettingTextControl> createState() => _SettingTextControlState();
}

class _SettingTextControlState extends State<SettingTextControl> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.value,
  );

  @override
  void didUpdateWidget(SettingTextControl old) {
    super.didUpdateWidget(old);
    if (widget.value == _controller.text) return;

    // With the selection, never the bare text — see [_SettingNumberControlState.didUpdateWidget].
    _controller.value = TextEditingValue(
      text: widget.value,
      selection: TextSelection.collapsed(offset: widget.value.length),
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => NocturneField(
    label: '',
    controller: _controller,
    onChanged: widget.onChanged,
  );
}

/// Where the value in force came from, as an outlined chip beside the name.
///
/// Said on every card rather than only on the ones someone has touched: "using the default" is
/// the fact that makes the other two mean something, and a page where only the changed fields
/// say anything leaves the rest ambiguous between untouched and unexplained.
///
/// A camera field uses the same three states, because it has the same three: a value set on this
/// camera is [SettingSource.user], one that falls through to the Server is [SettingSource.builtIn],
/// and [SettingSource.deployment] simply never occurs on a camera.
class _SettingSourceChip extends StatelessWidget {
  const _SettingSourceChip({required this.source});

  final SettingSource source;

  @override
  Widget build(BuildContext context) {
    final (label, colour, dashed) = switch (source) {
      SettingSource.user => ('changed here', Nocturne.accent400, false),
      SettingSource.deployment => (
        'set by this deployment',
        Nocturne.mix(Nocturne.text, 50),
        true,
      ),
      SettingSource.builtIn => (
        'using the default',
        Nocturne.mix(Nocturne.text, 42),
        false,
      ),
    };

    final text = Text(
      label,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 10.5,
        color: colour,
      ),
    );
    const padding = EdgeInsets.symmetric(horizontal: 6, vertical: 1);
    final border = source == SettingSource.user
        ? Nocturne.mix(Nocturne.accent, 35)
        : Nocturne.mix(Nocturne.text, dashed ? 20 : 12);

    // Dashed for the deployment's own: it is the one state this page cannot take off, and the
    // broken line says so without a second colour.
    return dashed
        ? CustomPaint(
            painter: DashedBorder(color: border, radius: 4),
            child: Padding(padding: padding, child: text),
          )
        : Container(
            padding: padding,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(4),
              border: Border.all(color: border),
            ),
            child: text,
          );
  }
}

class SettingBadge extends StatelessWidget {
  const SettingBadge(this.label, {super.key, this.accent = false});

  final String label;
  final bool accent;

  @override
  Widget build(BuildContext context) {
    final colour = accent ? Nocturne.accent400 : Serval.alert;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: colour.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 10.5,
          color: colour,
        ),
      ),
    );
  }
}

/// The bar along the bottom of the page, and the only place a change is committed.
///
/// Nothing on either page saves as you type. Several of these restart a camera's recording or
/// detection when they land, so committing them is a decision with a button on it rather than a
/// side effect of dragging a slider past a value on the way to another one.
///
/// It is always there, holding the state of the page: nothing outstanding, or a count of what is
/// and the section it is in — with one section on screen at a time the edit may be somewhere the
/// person cannot see. A bar that arrived with the first edit would also move the page under the
/// hand that made it.
class SettingsSaveBar extends StatelessWidget {
  const SettingsSaveBar({
    super.key,
    required this.status,
    required this.dirty,
    required this.saving,
    required this.onSave,
    required this.onDiscard,
    this.saveLabel = 'Save settings',
    this.compactSaveLabel = 'Save',
    this.discardLabel = 'Discard',
    this.blocked = false,
    this.compact = false,
  });

  /// What the bar says: the count and where to look, or that there is nothing outstanding.
  final String status;

  /// Whether [status] is news. Draws the dot and the amber.
  final bool dirty;

  final bool saving;
  final VoidCallback onSave;
  final VoidCallback onDiscard;

  final String saveLabel;

  /// Under a thumb the label shortens, because the bar is pinned to the page it saves and the noun
  /// is already at the top of it.
  final String compactSaveLabel;

  final String discardLabel;

  /// Something else is stopping the save — a validation problem the page is already naming in
  /// [status]. Discard stays live, because abandoning a broken edit is the way out of it.
  final bool blocked;

  final bool compact;

  @override
  Widget build(BuildContext context) => Container(
    padding: compact
        ? const EdgeInsets.fromLTRB(18, 12, 18, 12)
        : const EdgeInsets.fromLTRB(24, 12, 24, 12),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(top: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        if (dirty) ...[
          Container(
            width: 8,
            height: 8,
            decoration: const BoxDecoration(
              color: Serval.alert,
              shape: BoxShape.circle,
            ),
          ),
          const SizedBox(width: 8),
        ],
        Expanded(
          child: Text(
            status,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              color: dirty ? Serval.alertText : Nocturne.mix(Nocturne.text, 40),
            ),
          ),
        ),
        NocturneButton(
          label: discardLabel,
          horizontalPadding: compact ? 8 : 12,
          onPressed: saving || !dirty ? null : onDiscard,
        ),
        const SizedBox(width: 9),
        NocturneButton(
          label: saving
              ? 'Saving…'
              : compact
              ? compactSaveLabel
              : saveLabel,
          horizontalPadding: compact ? 10 : 12,
          variant: NocturneButtonVariant.primary,
          onPressed: saving || !dirty || blocked ? null : onSave,
        ),
      ],
    ),
  );

  /// The bar a Viewer gets where the save bar would be.
  ///
  /// It takes the same slot deliberately. The alternative — dropping the bar entirely — makes the
  /// page look like one that has nothing to save, and leaves someone hunting for a control that was
  /// never going to be there.
  static Widget viewOnly({required String what, bool compact = false}) =>
      Container(
        padding: compact
            ? const EdgeInsets.fromLTRB(18, 12, 18, 12)
            : const EdgeInsets.fromLTRB(24, 12, 24, 12),
        decoration: BoxDecoration(
          color: Serval.rail,
          border: Border(top: BorderSide(color: Serval.hairline)),
        ),
        child: Row(
          children: [
            Icon(
              PhosphorIconsRegular.eye,
              size: 14,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                'View only — $what is changed by an admin.',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.mix(Nocturne.text, 40),
                ),
              ),
            ),
          ],
        ),
      );

  /// How both pages phrase a count of unsaved edits and where they are.
  ///
  /// Shared so the camera editor does not invent a second way of saying the same thing.
  static String changeSummary(int changedCount, List<String> groups) {
    if (changedCount == 0) return 'No unsaved changes';

    final count = changedCount == 1
        ? '1 setting changed'
        : '$changedCount settings changed';

    final where = switch (groups) {
      [] => '',
      [final only] when changedCount == 1 => ' — in “$only”',
      [final only] when changedCount == 2 => ' — both in “$only”',
      [final only] => ' — all in “$only”',
      [final first, final second] => ' — in “$first” and “$second”',
      _ => ' — across ${groups.length} groups',
    };

    return '$count, not yet saved$where';
  }
}

class SettingsLinkText extends StatelessWidget {
  const SettingsLinkText(this.label, {super.key, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      child: Text(
        label,
        style: const TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          color: Nocturne.accent400,
        ),
      ),
    ),
  );
}
