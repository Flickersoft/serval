part of '../notifications_screen.dart';

// -------------------------------------------------------------------------------------- cameras

/// Every camera's card, wrapping as many across as the width allows.
///
/// Rows are built by hand rather than by `GridView`: the cards are different heights — a camera
/// with two classes against one with eleven — and the design draws the ones on a row to a shared
/// height, which is what an [IntrinsicHeight] row does and a fixed aspect ratio cannot.
class _CameraGrid extends StatelessWidget {
  const _CameraGrid({
    required this.cameras,
    required this.query,
    required this.preferences,
    required this.defaults,
    required this.enabled,
    required this.padding,
    required this.onChanged,
  });

  /// The narrowest a card can be and still hold a chip like *Glass breaking* beside another.
  static const _minCardWidth = 340.0;
  static const _gap = 12.0;

  final List<CameraRecord> cameras;
  final String query;
  final UserPreferences preferences;
  final ServerCameraDefaults defaults;
  final bool enabled;
  final EdgeInsets padding;
  final Future<void> Function(CameraNotificationRule) onChanged;

  @override
  Widget build(BuildContext context) {
    if (cameras.isEmpty) {
      return Padding(
        padding: padding,
        child: SettingsNoMatches(
          query: query,
          empty:
              'No cameras are configured, so there is nothing to be told about.',
        ),
      );
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        final available = constraints.maxWidth - padding.horizontal;
        final columns = ((available + _gap) / (_minCardWidth + _gap))
            .floor()
            .clamp(1, 3);

        final cards = [
          for (final camera in cameras)
            _CameraRuleCard(
              camera: camera,
              rule:
                  preferences.ruleFor(camera.id) ??
                  CameraNotificationRule(cameraId: camera.id),
              defaults: defaults,
              enabled: enabled,
              onChanged: onChanged,
            ),
        ];

        return SingleChildScrollView(
          padding: padding,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              for (var start = 0; start < cards.length; start += columns) ...[
                if (start > 0) const SizedBox(height: _gap),
                _row(cards, start, columns),
              ],
            ],
          ),
        );
      },
    );
  }

  Widget _row(List<Widget> cards, int start, int columns) {
    // One across is the card itself, not a Row of one. `stretch` needs a bounded height to stretch
    // to, and inside a scroll view there is none — the row would be asked to be infinitely tall.
    if (columns == 1) return cards[start];

    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var column = 0; column < columns; column++) ...[
            if (column > 0) const SizedBox(width: _gap),
            Expanded(
              child: start + column < cards.length
                  ? cards[start + column]
                  : const SizedBox.shrink(),
            ),
          ],
        ],
      ),
    );
  }
}

/// One camera's rule: whether it may notify at all, and which of the things it alerts on do.
class _CameraRuleCard extends StatelessWidget {
  const _CameraRuleCard({
    required this.camera,
    required this.rule,
    required this.defaults,
    required this.enabled,
    required this.onChanged,
  });

  final CameraRecord camera;
  final CameraNotificationRule rule;
  final ServerCameraDefaults defaults;
  final bool enabled;
  final Future<void> Function(CameraNotificationRule) onChanged;

  /// What this camera actually raises object alerts for: its own override if it has one, otherwise
  /// the deployment's list. The same resolution the Server does in `CameraAiOptions.For`, which is
  /// why the menu here can never offer something that would not arrive.
  List<String> get _alertClasses =>
      camera.detectionTuning?.alertClasses ??
      defaults.listFor(CameraSetting.alertClasses);

  List<String> get _alertLabels =>
      camera.soundTuning?.alertLabels ??
      defaults.listFor(CameraSetting.soundAlertLabels);

  static bool _has(List<String> selected, String option) =>
      selected.any((entry) => entry.toLowerCase() == option.toLowerCase());

  /// The presets, plus whatever this rule already holds if that is not one of them.
  ///
  /// A value can arrive from a restored backup or a hand-written API call, and a chip row that
  /// silently drew none of its options lit would read as *Default* while the Server waited fifteen
  /// minutes. The same posture the class lists take: what is stored is shown, not corrected.
  List<int?> get _waitOptions => _waitPresets.contains(rule.cooldownSeconds)
      ? _waitPresets
      : [..._waitPresets, rule.cooldownSeconds];

  /// What each chip says. The inherit chip carries the number it resolves to, because *Default* on
  /// its own is a control that cannot be reasoned about — the whole question somebody has when they
  /// look at this row is how long the wait currently is.
  ///
  /// When the deployment's own wait happens to equal a preset the two chips read alike, and that is
  /// left alone: picking the twin pins the value so it stops following the Server, which is the same
  /// thing null-against-a-number means everywhere else on this screen.
  String _waitOptionLabel(int? option) {
    if (option != null) return _waitLabel(option);

    final seconds = defaults.pushCooldownSeconds;
    return seconds == null ? 'Default' : 'Default · ${_waitLabel(seconds)}';
  }

  @override
  Widget build(BuildContext context) {
    final classes = _alertClasses;
    final labels = _alertLabels;
    final muted = !rule.enabled;

    // Null means every one of them, which is what somebody who has never touched this has.
    final objects = rule.objectClasses ?? classes;
    final sounds = rule.soundLabels ?? labels;

    return Container(
      padding: const EdgeInsets.fromLTRB(14, 13, 14, 13),
      decoration: BoxDecoration(
        color: muted ? null : Nocturne.mix(Nocturne.text, 2),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: muted ? Serval.hairline : Nocturne.mix(Nocturne.text, 10),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      camera.name,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 14,
                        fontWeight: Nocturne.headingWeight,
                        color: muted
                            ? Nocturne.mix(Nocturne.text, 45)
                            : Nocturne.text,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      _subtitle(classes, labels, objects, sounds),
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 11.5,
                        color: Nocturne.mix(Nocturne.text, 42),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 10),
              NocturneToggle(
                value: rule.enabled,
                compact: true,
                onChanged: enabled
                    ? (value) => onChanged(rule.copyWith(enabled: value))
                    : null,
              ),
            ],
          ),
          if (classes.isNotEmpty || labels.isNotEmpty) ...[
            const SizedBox(height: 11),
            Opacity(
              opacity: muted ? 0.45 : 1,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (classes.isNotEmpty)
                    _ChipRow(
                      options: classes,
                      selected: objects,
                      enabled: enabled && rule.enabled,
                      iconFor: detectionIcon,
                      onChanged: (picked) => onChanged(
                        picked.length == classes.length
                            ? rule.copyWith(clearObjectClasses: true)
                            : rule.copyWith(objectClasses: picked),
                      ),
                    ),
                  if (classes.isNotEmpty && labels.isNotEmpty) ...[
                    const SizedBox(height: 7),
                    Container(height: 1, color: Nocturne.mix(Nocturne.text, 6)),
                    const SizedBox(height: 7),
                  ],
                  if (labels.isNotEmpty)
                    _ChipRow(
                      options: labels,
                      selected: sounds,
                      enabled: enabled && rule.enabled,
                      onChanged: (picked) => onChanged(
                        picked.length == labels.length
                            ? rule.copyWith(clearSoundLabels: true)
                            : rule.copyWith(soundLabels: picked),
                      ),
                    ),
                  const SizedBox(height: 7),
                  Container(height: 1, color: Nocturne.mix(Nocturne.text, 6)),
                  const SizedBox(height: 8),
                  Text(
                    // Word for word the label `Serval:Push:CooldownSeconds` carries on the Server
                    // settings page, because this row overrides exactly that setting. The chip
                    // beside it reads *Default · <wait>* off the same value.
                    'Wait before notifying about the same thing',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11.5,
                      color: Nocturne.mix(Nocturne.text, 42),
                    ),
                  ),
                  const SizedBox(height: 7),
                  _ChoiceRow(
                    options: _waitOptions,
                    selected: rule.cooldownSeconds,
                    labelFor: _waitOptionLabel,
                    enabled: enabled && rule.enabled,
                    onChanged: (picked) => onChanged(
                      picked == null
                          ? rule.copyWith(clearCooldownSeconds: true)
                          : rule.copyWith(cooldownSeconds: picked),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }

  /// `5 of 11 on`, counting objects and sounds together — the card's own question is how much of
  /// what this camera can say gets through, and splitting that in two answers neither.
  String _subtitle(
    List<String> classes,
    List<String> labels,
    List<String> objects,
    List<String> sounds,
  ) {
    if (!rule.enabled) return 'Muted — nothing from this camera';
    if (classes.isEmpty && labels.isEmpty) {
      return 'Nothing on this camera raises an alert';
    }

    final total = classes.length + labels.length;
    final on =
        classes.where((option) => _has(objects, option)).length +
        labels.where((option) => _has(sounds, option)).length;

    return '$on of $total on';
  }
}

/// A row of chips over a fixed set, each lit or not.
///
/// Deliberately not `LabelChipList`, which the camera and server pages use: that one takes free
/// text, and free text is exactly wrong here. A label typed in that the camera does not alert on
/// would be accepted, stored, and then never match anything — a control that appears to work and
/// silently does nothing.
class _ChipRow extends StatelessWidget {
  const _ChipRow({
    required this.options,
    required this.selected,
    required this.enabled,
    required this.onChanged,
    this.iconFor,
  });

  final List<String> options;
  final List<String> selected;
  final bool enabled;
  final ValueChanged<List<String>> onChanged;

  /// The glyph a class reads as. Null for sounds, which have no picture — a chip for *Glass
  /// breaking* carries whether it is on and nothing else.
  final PhosphorIconData Function(String)? iconFor;

  bool _on(String option) =>
      selected.any((entry) => entry.toLowerCase() == option.toLowerCase());

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 6,
    runSpacing: 6,
    children: [
      for (final option in options)
        _SelectableChip(
          label: titleCaseLabel(option),
          icon: iconFor?.call(option),
          on: _on(option),
          enabled: enabled,
          onTap: () => onChanged([
            for (final other in options)
              if (other == option ? !_on(other) : _on(other)) other,
          ]),
        ),
    ],
  );
}

/// The presets offered for the wait between two notifications about the same thing. Null is the
/// deployment's own, which is where a rule that has never been touched sits.
const _waitPresets = <int?>[null, 0, 60, 300, 900];

/// A wait, in the fewest words that still say it. `Every time` rather than `Off`, because this
/// control sits inches from a mute toggle and *off* would be read as the wrong one.
String _waitLabel(int seconds) {
  if (seconds <= 0) return 'Every time';
  if (seconds < 60) return '$seconds sec';
  if (seconds % 60 == 0) return '${seconds ~/ 60} min';
  return '${seconds ~/ 60}m ${seconds % 60}s';
}

/// A row of chips over a fixed set where exactly one is lit — the rate control, next to
/// [_ChipRow]'s two which are about kind.
///
/// Its own widget rather than a flag on [_ChipRow] because the two emit different things: that one
/// hands back the whole selected list, and this hands back the single choice. Folding them together
/// would mean a callback that means two things depending on a bool.
class _ChoiceRow extends StatelessWidget {
  const _ChoiceRow({
    required this.options,
    required this.selected,
    required this.labelFor,
    required this.enabled,
    required this.onChanged,
  });

  final List<int?> options;
  final int? selected;
  final String Function(int?) labelFor;
  final bool enabled;
  final ValueChanged<int?> onChanged;

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 6,
    runSpacing: 6,
    children: [
      for (final option in options)
        _SelectableChip(
          label: labelFor(option),
          // An explicit glyph for every chip, lit or not. Without one [_SelectableChip] falls back
          // to a tick or a plus, and a plus reads as *add another* — which is what this row is not.
          icon: PhosphorIconsRegular.timer,
          on: option == selected,
          enabled: enabled,
          // Tapping the lit one is left inert rather than clearing it: there is no "none" here, and
          // the way back to inheriting is the Default chip, which is one of the options.
          onTap: () => onChanged(option),
        ),
    ],
  );
}

class _SelectableChip extends StatelessWidget {
  const _SelectableChip({
    required this.label,
    required this.icon,
    required this.on,
    required this.enabled,
    required this.onTap,
  });

  final String label;
  final PhosphorIconData? icon;
  final bool on;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    // A class carries its own glyph in both states; a sound has none, so the state itself becomes
    // the glyph — a tick for one that is on, a plus for one you could add.
    final glyph =
        icon ?? (on ? PhosphorIconsRegular.check : PhosphorIconsRegular.plus);

    final chip = Container(
      height: 26,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: on ? Nocturne.mix(Nocturne.accent, 13) : null,
        borderRadius: BorderRadius.circular(20),
        border: Border.all(
          color: on
              ? Nocturne.mix(Nocturne.accent, 55)
              : Nocturne.mix(Nocturne.text, 11),
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          PhosphorIcon(
            glyph,
            size: icon == null ? 11 : 13,
            color: on ? Nocturne.accent400 : Nocturne.mix(Nocturne.text, 32),
          ),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12,
              color: on ? Nocturne.text : Nocturne.mix(Nocturne.text, 42),
            ),
          ),
        ],
      ),
    );

    if (!enabled) return chip;

    return Semantics(
      button: true,
      toggled: on,
      label: label,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(onTap: onTap, child: chip),
      ),
    );
  }
}

// --------------------------------------------------------------------------------------- pieces

/// The hairline box both header cards are drawn in.
class _CardFrame extends StatelessWidget {
  const _CardFrame({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
    clipBehavior: Clip.antiAlias,
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(9),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: children,
    ),
  );
}

/// Something that happened, said back — the accepted count after a test, or why a subscription did
/// not take. Not [VitalsAlertStrip], which only has red and amber: neither is what *sent to three
/// devices* means.
class _Note extends StatelessWidget {
  const _Note({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.accent, 12),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Nocturne.mix(Nocturne.accent, 35)),
    ),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const PhosphorIcon(
          PhosphorIconsRegular.info,
          size: 15,
          color: Nocturne.accent300,
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            message,
            style: const TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.45,
              color: Nocturne.text,
            ),
          ),
        ),
      ],
    ),
  );
}

/// An action written as a word inside a sentence, for the phone's sub-line.
