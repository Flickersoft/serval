import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/providers.dart';
import '../data/serval_api.dart';
import '../data/serval_repository.dart';
import '../models/server_settings.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/label_chips.dart';
import '../widgets/nocturne_select.dart';
import '../widgets/nocturne_toggle.dart';
import '../widgets/paired_rows.dart';
import '../widgets/section_kicker.dart';
import '../widgets/settings_cards.dart';
import '../widgets/storage_bar.dart' show VitalsAlertStrip;
import '../widgets/waiting_note.dart';

/// What this Server is *told* to do — the *Server settings* page, design 4a.
///
/// Sibling to [ServerScreen](server_screen.dart), which is what it is *doing*.
///
/// **The Server owns the catalogue.** Every field here — label, range, explanation, whether it
/// needs a restart — comes from `GET /api/settings`; see `Docs/configuration.md`. This screen draws
/// what arrives and knows nothing about any particular setting, so a knob added on the Server
/// appears here with no change to this file, and one the Server does not list cannot be written
/// from here however it is spelled.
///
/// It has the shape of the users page: the catalogue's groups are the left list, one group fills
/// the pane beside it, and each setting is a card in a two-column flow.
///
/// Three things to preserve when editing this:
///
/// **Every field says what it does, in the flow** — not behind a hover or an info icon. A wrong
/// value here is not an error but a system that quietly stops noticing things, and the sentence
/// under the field is the only thing standing between someone and that.
///
/// **A changed field says so, and says what it would go back to.** Without the before-and-after,
/// *Use the default* is a guess.
///
/// **A setting the running Server is not using says so on itself.** The restart-gated ones carry a
/// badge on the card and a mark on their group in the list.
class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  @override
  ConsumerState<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends ConsumerState<SettingsScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  ServerSettings? _settings;
  String? _error;
  bool _saving = false;

  /// Fields edited but not yet sent, keyed the way a write is. A value of null in here is a real
  /// instruction — reset this setting — which is why it is a map of nullable values and why
  /// [_pending] tests key presence rather than nullness.
  final Map<String, Object?> _draft = {};

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final settings = await _repository.settings();
      if (!mounted) return;
      setState(() {
        _settings = settings;
        _error = null;
        _draft.clear();
      });
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() => _error = error.message);
    }
  }

  Future<void> _save() async {
    if (_draft.isEmpty || _saving) return;

    setState(() => _saving = true);
    try {
      // The response is the new state, not an acknowledgement, so nothing here has to work out
      // for itself what the change did to the reset values or to the restart marks.
      final settings = await _repository.updateSettings(Map.of(_draft));
      if (!mounted) return;
      setState(() {
        _settings = settings;
        _draft.clear();
        _error = null;
      });
    } on ServalApiException catch (error) {
      // Shown verbatim. The Server's 400s name the setting and the bound it broke, which is more
      // use than anything this screen could say instead.
      if (!mounted) return;
      setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  bool _pending(String key) => _draft.containsKey(key);

  /// The value a field should show: what is staged if anything is, otherwise what the Server says.
  Object? _valueOf(ServerSetting setting) =>
      _pending(setting.key) ? _draft[setting.key] : setting.value;

  /// Why the configuration being edited would not read this setting, or null when it would.
  ///
  /// Resolved here rather than on the Server because the controlling value may be one staged a
  /// moment ago and not yet saved — see [SettingDependency]. Reading it through [_valueOf] is the
  /// whole point: it is the draft that decides, so choosing a device un-dims the fields that device
  /// uses immediately, while there is still time to set them before the restart.
  String? _inapplicable(ServerSetting setting) {
    final rule = setting.appliesWhen;
    if (rule == null) return null;

    final controlling = _settings?.byKey(rule.key);
    if (controlling == null) return null;

    return rule.satisfiedBy(_valueOf(controlling)) ? null : rule.reason;
  }

  void _stage(ServerSetting setting, Object? value) =>
      setState(() => _draft[setting.key] = value);

  /// Stops overriding a setting. Staged as an explicit null rather than by dropping the key —
  /// dropping it would mean "leave it as it is", which is the opposite instruction.
  void _reset(ServerSetting setting) =>
      setState(() => _draft[setting.key] = null);

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: const BoxDecoration(color: Serval.panel),
    child: SettingsScreenBody(
      settings: _settings,
      error: _error,
      saving: _saving,
      changedCount: _draft.length,
      valueOf: _valueOf,
      isPending: _pending,
      inapplicable: _inapplicable,
      onChanged: _stage,
      onReset: _reset,
      onSave: _save,
      onDiscard: () => setState(_draft.clear),
      // PUT /api/settings is Admin-only; the read is not.
      readOnly: !ref.watch(isAdminProvider),
    ),
  );
}

String? _alwaysApplies(ServerSetting setting) => null;

/// The page without its container, so the widget tests can render every state without a Server.
///
/// Stateful for the two things that are a view rather than a change: which group is open, and
/// what is typed into the search box. Neither belongs in the draft, which is only the edits that
/// would be written.
class SettingsScreenBody extends StatefulWidget {
  const SettingsScreenBody({
    super.key,
    required this.settings,
    required this.valueOf,
    required this.isPending,
    required this.onChanged,
    required this.onReset,
    required this.onSave,
    required this.onDiscard,
    this.inapplicable = _alwaysApplies,
    this.error,
    this.saving = false,
    this.changedCount = 0,
    this.readOnly = false,
  });

  /// Show the settings without offering to change any of them.
  ///
  /// `PUT /api/settings` is Admin-only while the read is not, so a Viewer can see what the server
  /// is configured to do and cannot alter it. Defaults to false so the widget tests, which drive
  /// this body directly, keep exercising the editing path.
  final bool readOnly;

  /// Null while the first fetch is in flight.
  final ServerSettings? settings;

  final Object? Function(ServerSetting) valueOf;
  final bool Function(String key) isPending;

  /// Why the configuration being edited would not read a setting, or null when it would. Defaults
  /// to "everything applies" so the widget tests that drive this body directly need not model it.
  final String? Function(ServerSetting) inapplicable;

  final void Function(ServerSetting, Object?) onChanged;
  final void Function(ServerSetting) onReset;
  final VoidCallback onSave;
  final VoidCallback onDiscard;

  final String? error;
  final bool saving;
  final int changedCount;

  @override
  State<SettingsScreenBody> createState() => _SettingsScreenBodyState();
}

class _SettingsScreenBodyState extends State<SettingsScreenBody> {
  final _search = TextEditingController();

  /// Which group was clicked, not which one is on screen. The open group is derived per build so
  /// a search that filters the clicked one away falls back to the first that survives.
  String? _clickedGroup;

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  String get _query => _search.text.trim().toLowerCase();

  /// The settings a search leaves. Matched on the label and on the key, so both the person who
  /// knows it as *Keep recordings for* and the one who knows it as `Serval:Media:RetentionDays`
  /// find it.
  bool _matches(ServerSetting setting) {
    final query = _query;
    if (query.isEmpty) return true;
    return setting.label.toLowerCase().contains(query) ||
        setting.key.toLowerCase().contains(query) ||
        setting.group.toLowerCase().contains(query);
  }

  @override
  Widget build(BuildContext context) {
    final settings = widget.settings;
    final compact = isCompact(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Header(
          settings: settings,
          // Narrow, a group is a screen: the bar names the one that is open and its arrow goes
          // back to the group list rather than out to the settings index.
          openGroup: compact ? _clickedGroup : null,
          onCloseGroup: () => setState(() => _clickedGroup = null),
        ),
        Expanded(
          child: settings == null
              ? WaitingNote(
                  message: 'Reading this Server’s settings…',
                  error: widget.error,
                  padding: const EdgeInsets.all(28),
                )
              : _body(settings, compact),
        ),
        if (settings != null)
          if (widget.readOnly)
            SettingsSaveBar.viewOnly(what: 'A server setting', compact: compact)
          else
            SettingsSaveBar(
              status: SettingsSaveBar.changeSummary(
                widget.changedCount,
                _pendingGroups(settings),
              ),
              dirty: widget.changedCount > 0,
              saving: widget.saving,
              onSave: widget.onSave,
              onDiscard: widget.onDiscard,
              compact: compact,
            ),
      ],
    );
  }

  Widget _body(ServerSettings settings, bool compact) {
    // A group with nothing matching drops out of the list rather than opening onto an empty pane.
    final matching = <String, List<ServerSetting>>{
      for (final group in settings.groups)
        if (settings.inGroup(group).where(_matches).toList() case final found
            when found.isNotEmpty)
          group: found,
    };

    final groups = matching.keys.toList();

    // Beside the list the pane is never empty when there is something to put in it; on a screen of
    // its own it opens nothing nobody asked for, exactly as the camera registry does.
    final open = groups.contains(_clickedGroup)
        ? _clickedGroup!
        : compact
        ? null
        : groups.firstOrNull;

    // The keys of the settings that are stored but not in the running Server, so the card and the
    // group row can both mark them without either working it out itself.
    final restartPending = {
      for (final setting in settings.pendingRestart) setting.key,
    };

    final groupList = SettingsGroupList(
      groups: groups,
      counts: {
        for (final entry in matching.entries) entry.key: entry.value.length,
      },
      changed: {
        for (final group in groups)
          if (matching[group]!.any((s) => widget.isPending(s.key))) group,
      },
      restartPending: {
        for (final group in groups)
          if (matching[group]!.any((s) => restartPending.contains(s.key)))
            group,
      },
      selected: open,
      search: _search,
      onSearchChanged: () => setState(() {}),
      onSelect: (group) => setState(() => _clickedGroup = group),
      compact: compact,
    );

    final pane = Padding(
      padding: compact
          ? const EdgeInsets.fromLTRB(18, 16, 18, 0)
          : const EdgeInsets.fromLTRB(24, 16, 24, 0),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (widget.error case final message?) ...[
            VitalsAlertStrip(
              message: message,
              critical: true,
              padding: const EdgeInsets.all(14),
            ),
            const SizedBox(height: 14),
          ],
          // Only the pane, so the group list beside it stays navigable — reading what a server is
          // set to is not editing it.
          Expanded(
            child: ReadOnlyPane(
              readOnly: widget.readOnly,
              child: open == null
                  ? _noMatches
                  : _GroupPane(
                      title: open,
                      settings: matching[open]!,
                      restartPending: restartPending,
                      valueOf: widget.valueOf,
                      isPending: widget.isPending,
                      inapplicable: widget.inapplicable,
                      onChanged: widget.onChanged,
                      onReset: widget.onReset,
                      showTitle: !compact,
                    ),
            ),
          ),
        ],
      ),
    );

    // One screen or the other. The group name is on the bar above, so the pane does not repeat it,
    // and *No matches* belongs with the search that found none — which is in the list.
    if (compact) {
      return open == null
          ? Padding(
              padding: const EdgeInsets.fromLTRB(18, 14, 18, 8),
              child: groups.isEmpty
                  ? Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        groupList,
                        Expanded(child: _noMatches),
                      ],
                    )
                  : groupList,
            )
          : pane;
    }

    return Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 16, 0, 8),
          child: groupList,
        ),
        Expanded(child: pane),
      ],
    );
  }

  Widget get _noMatches => SettingsNoMatches(
    query: _search.text.trim(),
    empty: 'This Server lists no settings.',
  );

  /// The groups an unsaved change sits in, so the save bar can say where to look for it.
  List<String> _pendingGroups(ServerSettings? settings) {
    if (settings == null) return const [];
    final groups = <String>[];
    for (final setting in settings.settings) {
      if (widget.isPending(setting.key) && !groups.contains(setting.group)) {
        groups.add(setting.group);
      }
    }
    return groups;
  }
}

class _Header extends StatelessWidget {
  const _Header({
    required this.settings,
    this.openGroup,
    required this.onCloseGroup,
  });

  final ServerSettings? settings;

  /// The group the drill-down has open, which the bar names in place of the page. Null is the page
  /// itself, and always so at full width, where a group is a column selection rather than a screen.
  final String? openGroup;

  final VoidCallback onCloseGroup;

  @override
  Widget build(BuildContext context) {
    final updatedBy = settings?.updatedBy;

    const blurb =
        'How this Server behaves for every camera · a camera can override some of '
        'these on its own page';

    // Who last changed something, when anyone has. Absent rather than "never" on a Server
    // still running exactly what it was deployed with.
    final changedBy = updatedBy == null
        ? null
        : Text(
            'last changed by $updatedBy',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
          );

    if (isCompact(context)) {
      // A group has the screen: the bar carries its name and the way back to the list of them, and
      // the page's own sentence would be describing something that is no longer on screen.
      if (openGroup != null) {
        return CompactAppBar(title: openGroup!, onBack: onCloseGroup);
      }

      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          CompactAppBar(
            title: 'Server settings',
            onBack: () => context.go('/settings'),
          ),
          CompactSubBar(
            children: [
              Text(
                blurb,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  height: 1.45,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
              if (changedBy != null) ...[const SizedBox(height: 6), changedBy],
            ],
          ),
        ],
      );
    }

    return Container(
      padding: const EdgeInsets.fromLTRB(24, 18, 24, 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: Serval.hairline)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text(
                  'Server settings',
                  style: TextStyle(
                    fontFamily: Nocturne.fontHeading,
                    fontSize: 20,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                    letterSpacing: -0.01 * 20,
                  ),
                ),
                const SizedBox(height: 5),
                Text(
                  blurb,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 50),
                  ),
                ),
              ],
            ),
          ),
          if (changedBy != null) ...[const SizedBox(width: 16), changedBy],
        ],
      ),
    );
  }
}

/// One group, filling the pane: its name, a way to put all of it back, and its settings as cards.
/// The break between a group's everyday settings and the ones only somebody setting the machine up
/// has any business changing.
///
/// A rule with a name on it rather than a disclosure. What sits below is a model file, a thread
/// count, a filter's noise term — not a *dangerous* setting, but one whose label cannot be made to
/// mean anything without the code beside it. Collapsing them would put a click between somebody
/// following a support thread and the field it names, which buys nothing this line does not already
/// say.
class _AdvancedRule extends StatelessWidget {
  const _AdvancedRule();

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 6, bottom: 14),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        const SectionKicker('Advanced'),
        const SizedBox(width: 10),
        Expanded(child: Container(height: 1, color: Serval.hairline)),
      ],
    ),
  );
}

class _GroupPane extends StatelessWidget {
  const _GroupPane({
    required this.title,
    required this.settings,
    required this.restartPending,
    required this.valueOf,
    required this.isPending,
    required this.onChanged,
    required this.onReset,
    this.inapplicable = _alwaysApplies,
    this.showTitle = true,
  });

  final String title;
  final List<ServerSetting> settings;
  final Set<String> restartPending;
  final Object? Function(ServerSetting) valueOf;
  final bool Function(String key) isPending;
  final String? Function(ServerSetting) inapplicable;
  final void Function(ServerSetting, Object?) onChanged;
  final void Function(ServerSetting) onReset;

  /// False where the app bar above already carries the group's name — the pane keeps the count and
  /// the *reset* link, which the bar has no room for.
  final bool showTitle;

  bool _resettable(ServerSetting setting) =>
      setting.isOverridden || isPending(setting.key);

  @override
  Widget build(BuildContext context) {
    final resettable = settings.where(_resettable).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (showTitle) ...[
                    Text(
                      title,
                      style: const TextStyle(
                        fontFamily: Nocturne.fontHeading,
                        fontSize: 16,
                        fontWeight: Nocturne.headingWeight,
                        color: Nocturne.text,
                      ),
                    ),
                    const SizedBox(height: 3),
                  ],
                  Text(
                    _subtitle(resettable.length),
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12,
                      height: 1.45,
                      color: Nocturne.mix(Nocturne.text, 50),
                    ),
                  ),
                ],
              ),
            ),
            if (resettable.isNotEmpty) ...[
              const SizedBox(width: 12),
              SettingsLinkText(
                'Reset this section',
                onTap: () {
                  for (final setting in resettable) {
                    onReset(setting);
                  }
                },
              ),
            ],
          ],
        ),
        const SizedBox(height: 12),
        Expanded(
          child: Stack(
            children: [
              LayoutBuilder(
                builder: (context, constraints) {
                  // Two to a row is the design's shape and needs the design's width. Below it
                  // the pair becomes one column rather than two cards too narrow to hold a
                  // label and a value.
                  final paired = constraints.maxWidth >= kPairedMinWidth;
                  final width = paired
                      ? (constraints.maxWidth - 12) / 2
                      : constraints.maxWidth;

                  return SingleChildScrollView(
                    padding: const EdgeInsets.only(bottom: 22),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: _rows(paired: paired, compact: width < 300),
                    ),
                  );
                },
              ),
              // The last card fades into the bar rather than being cut off by it, so a group
              // that runs past the fold says so.
              Positioned(
                left: 0,
                right: 0,
                bottom: 0,
                child: IgnorePointer(
                  child: Container(
                    height: 22,
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.topCenter,
                        end: Alignment.bottomCenter,
                        colors: [
                          Serval.panel.withValues(alpha: 0),
                          Serval.panel,
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  /// The group as rows of cards: everything everyday, then the *Advanced* rule, then the rest.
  ///
  /// **Two bands, packed independently.** Each band gets its own [pairedRows] call and does its own
  /// wide-card hoisting, which is what keeps this cheap — a band boundary is a hard break, so
  /// neither half can pull a card across it to close a gap in the other. Arbitrary bands
  /// interleaved through a group would not work this way and are deliberately not offered.
  ///
  /// The divider is drawn only when there is something on both sides of it. A group that is
  /// entirely advanced — *Object tracking* is the one — simply opens as one band, because a rule
  /// with nothing above it announces a distinction the reader cannot act on.
  List<Widget> _rows({required bool paired, required bool compact}) {
    final everyday = settings.where((s) => !s.advanced).toList();
    final advanced = settings.where((s) => s.advanced).toList();

    return [
      ..._band(everyday, paired: paired, compact: compact),
      if (everyday.isNotEmpty && advanced.isNotEmpty) const _AdvancedRule(),
      ..._band(advanced, paired: paired, compact: compact),
    ];
  }

  /// One band of cards as rows.
  ///
  /// The ones that hold a sentence or a list take the width — a half-width box for a file path or
  /// a set of labels would truncate what it holds — and are sorted to the front, so the pairs below
  /// them close up instead of leaving a hole in the middle of the grid. In one column there is no
  /// hole to close and nothing to gain from moving them, so the catalogue's own order stands.
  ///
  /// [pairedRows] does the packing; the classification is this page's, because only it knows a
  /// `textList` from a number.
  List<Widget> _band(
    List<ServerSetting> band, {
    required bool paired,
    required bool compact,
  }) {
    if (band.isEmpty) return const [];

    final ordered = paired
        ? [...band.where(_isWide), ...band.where((s) => !_isWide(s))]
        : band;

    final rows = pairedRows(
      paired: paired,
      // These have edges: a short card beside a long one would open a gap the next row starts
      // below, which the design does not draw.
      matchHeights: true,
      items: [
        for (final setting in ordered)
          PairedItem(
            // A card with the row to itself has the room for its full layout. In one column the
            // width is whatever the pane has, which can be narrower than either.
            _card(
              setting,
              compact: paired && _isWide(setting) ? false : compact,
            ),
            wide: _isWide(setting),
          ),
      ],
    );

    return [
      for (final row in rows) ...[row, const SizedBox(height: 12)],
    ];
  }

  Widget _card(ServerSetting setting, {required bool compact}) =>
      _ServerSettingCard(
        setting: setting,
        value: valueOf(setting),
        pending: isPending(setting.key),
        restartPending: restartPending.contains(setting.key),
        inapplicable: inapplicable(setting),
        compact: compact,
        onChanged: (value) => onChanged(setting, value),
        onReset: () => onReset(setting),
      );

  String _subtitle(int changed) {
    final count =
        '${settings.length} '
        '${settings.length == 1 ? 'setting' : 'settings'}';
    return changed == 0 ? count : '$count · $changed changed from the default';
  }

  static bool _isWide(ServerSetting setting) =>
      setting.kind == SettingKind.textList || setting.kind == SettingKind.text;
}

/// One catalogue setting as a [SettingCard].
///
/// The chrome is shared; what is this page's own is the mapping — which `SettingKind` gets which
/// control, what a list's help says, and how a reset names the value it restores.
class _ServerSettingCard extends StatelessWidget {
  const _ServerSettingCard({
    required this.setting,
    required this.value,
    required this.pending,
    required this.restartPending,
    required this.onChanged,
    required this.onReset,
    this.inapplicable,
    this.compact = false,
  });

  final ServerSetting setting;

  /// What to show — the staged value when there is one, otherwise the Server's.
  final Object? value;

  /// True when this field has been edited but not saved.
  final bool pending;

  /// True when the saved value is not the one the running Server is using.
  final bool restartPending;

  /// Why the configuration being edited would not read this, or null when it would.
  ///
  /// **Dimmed in place rather than dropped from the page.** A setting nothing reads looks exactly
  /// like one that works, which is the whole problem — but removing the card takes the evidence
  /// away with it, and a compose file setting an inert value is precisely what needs to be
  /// visible. So the card keeps its position and its chips, the control stops taking edits, and
  /// this replaces the help text.
  final String? inapplicable;

  /// Drawn narrower than the design's card. The reset takes its own line and the bounds give up
  /// their place at the end of the row, rather than either being cut off.
  final bool compact;

  final ValueChanged<Object?> onChanged;
  final VoidCallback onReset;

  /// Whether resetting would do anything: only a value someone set here can be taken back off.
  bool get _resettable => setting.isOverridden || pending;

  bool get _numeric =>
      setting.kind == SettingKind.integer || setting.kind == SettingKind.number;

  @override
  Widget build(BuildContext context) => SettingCard(
    label: setting.label,
    source: setting.source,
    pending: pending,
    restartRequired: setting.restartRequired,
    restartPending: restartPending,
    headerTrailing: _headerTrailing,
    // Absorbed rather than removed: the control still draws its value, which is the thing worth
    // seeing. Reset stays live when there is something to reset — a value being ignored is exactly
    // one somebody may want to clear, and offering only the sight of it would be a half-measure.
    control: inapplicable == null
        ? _control
        : ExcludeFocus(
            child: IgnorePointer(
              child: Opacity(opacity: 0.45, child: _control),
            ),
          ),
    help: _help(context),
    resetLabel: _showsReset ? _resetLabel : null,
    onReset: _showsReset ? onReset : null,
    compact: compact,
  );

  /// The explanation, in the flow. See the library doc on why this is not a tooltip.
  ///
  /// A setting the chosen configuration does not read says so instead. The help describes a knob
  /// that is doing nothing here, and leaving it in place would be the page arguing with itself.
  Widget _help(BuildContext context) => switch (setting) {
    _ when inapplicable != null => Text(
      inapplicable!,
      style: settingHelpStyle().copyWith(color: Serval.alertText),
    ),
    _ when setting.kind == SettingKind.textList => _listHelp(context),
    _ => Text(setting.help, style: settingHelpStyle()),
  };

  /// A list carries its reset in the header, where it can say *list* and mean the whole set.
  bool get _showsReset => _resettable && setting.kind != SettingKind.textList;

  /// *Use the default*, with the value it would restore. Without the figure the label is a
  /// promise that something will change and no statement of what to.
  ///
  /// A prompt is a whole paragraph and cannot be named in a link; [settingBrief] cuts it to the
  /// opening words, which is enough to tell one default from the one in the box.
  String get _resetLabel {
    final fallback = _describe(setting.defaultValue);
    return fallback == null
        ? 'Use the default'
        : 'Use the default · ${settingBrief(fallback)}';
  }

  /// A list's explanation, with the way to paste a whole one running on from the sentence — a link
  /// at the far edge of a card this wide reads as belonging to nothing.
  ///
  /// What the Server falls back on is not restated here: it is drawn as ghost chips in the row
  /// itself, which is where someone looks for what a list holds.
  Widget _listHelp(BuildContext context) {
    final items = _asList(value);

    return Text.rich(
      TextSpan(
        children: [
          TextSpan(text: '${setting.help} '),
          WidgetSpan(
            alignment: PlaceholderAlignment.baseline,
            baseline: TextBaseline.alphabetic,
            child: SettingsLinkText(
              'Paste a list',
              onTap: () => _paste(context, items),
            ),
          ),
        ],
      ),
      style: settingHelpStyle(),
    );
  }

  Future<void> _paste(BuildContext context, List<String> current) async {
    final added = await showPasteListDialog(
      context: context,
      label: setting.label,
      current: current,
    );
    if (added != null) onChanged(added);
  }

  /// What sits at the right of the name: a slider's value read out in numerals, or — for a list,
  /// which cannot name its default in a line — the way back to the whole built-in set.
  Widget? get _headerTrailing {
    if (_numeric && settingSlidable(setting.min, setting.max)) {
      return Text(
        settingReadout(value, setting.unit),
        style: monoStyle(fontSize: 12.5, color: Nocturne.text),
      );
    }
    if (setting.kind == SettingKind.textList && _resettable) {
      return SettingsLinkText('Use the default list', onTap: onReset);
    }
    return null;
  }

  /// What sits at the end of a number's row: its bounds. The reset lives on the last line, beside
  /// the explanation, so the row stays a value and its limits.
  Widget? get _rowTrailing {
    // The bounds are the first thing to give up a narrow row: the Server refuses a value outside
    // them and says so, which the box cannot.
    if (compact) return null;
    if (setting.rangeShort case final range?) {
      return Text(
        range,
        style: monoStyle(
          fontSize: 10.5,
          color: Nocturne.mix(Nocturne.text, 35),
        ),
      );
    }
    return null;
  }

  static String? _describe(Object? value) => switch (value) {
    null => null,
    true => 'on',
    false => 'off',
    final num number => settingFigure(number),
    final List<dynamic> items when items.isEmpty => null,
    final List<dynamic> items => items.join(', '),
    _ => '$value',
  };

  Widget get _control => switch (setting.kind) {
    SettingKind.boolean => Align(
      alignment: Alignment.centerLeft,
      child: NocturneToggle(
        value: value == true,
        onChanged: (picked) => onChanged(picked),
      ),
    ),
    SettingKind.choice => NocturneSelect<String>(
      label: '',
      value: value == null ? null : '$value',
      options: setting.choices ?? const [],
      optionLabel: (option) => option,
      optionNote: (option) => setting.unavailableChoices[option],
      onChanged: (picked) => onChanged(picked),
    ),
    SettingKind.textList => LabelChipList(
      value: _asList(value),
      fallback: setting.listFallback,
      onChanged: (items) => onChanged(items),
    ),
    SettingKind.integer || SettingKind.number => SettingNumberControl(
      value: value,
      whole: setting.kind == SettingKind.integer,
      min: setting.min,
      max: setting.max,
      unit: setting.unit,
      trailing: _rowTrailing,
      onChanged: onChanged,
    ),
    SettingKind.text => SettingTextControl(
      value: value == null ? '' : '$value',
      onChanged: onChanged,
    ),
  };
}

List<String> _asList(Object? value) => switch (value) {
  final List<dynamic> items => [for (final item in items) '$item'],
  _ => const [],
};
