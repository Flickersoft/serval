import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/alert.dart';
import '../models/timeline.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/alert_detail_pane.dart';
import '../widgets/alert_row.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/section_kicker.dart';

/// The alert queue — a peer of the wall and of saved clips.
///
/// **A queue, not a library.** Every row here has already been, or will be, a notification: this is
/// where the ones nobody tapped land. They are dismissable, and the only thing to do with one is go
/// and watch it. That is the whole difference from [ClipsScreen], and it decides the shape: rows
/// rather than cards, newest first, grouped by day, cross-camera by default.
///
/// Wide this is the list and a detail column beside it; on a phone it splits in two, with
/// [AlertScreen] as one alert and a swipe on the row to clear it.
class AlertsScreen extends ConsumerStatefulWidget {
  const AlertsScreen({
    super.key,
    this.initialAlertId,
    this.onBack,
    this.onOpenAlert,
    this.onWatch,
    this.onOpenSettings,
  });

  /// The alert a tapped notification named, when that is how this was opened.
  ///
  /// Wide there is no separate screen for one alert — the column beside the list is where an alert
  /// is read, and it is what a row opens — so `/alerts/:id` lands here with the id to select. Null
  /// when the rail brought you, which opens the newest.
  final String? initialAlertId;

  /// The phone's way back to the wall. Null on a desktop, where the rail is how you left.
  final VoidCallback? onBack;

  /// The phone opens an alert as its own screen; wide, it opens in the column beside the list.
  final ValueChanged<String>? onOpenAlert;

  /// Opens a camera's recording at a moment — what *Watch* does.
  final void Function(String cameraId, DateTime at)? onWatch;

  /// Goes to where what counts as an alert is decided. The one control the phone keeps from the
  /// desktop header, because it is the only one a swipe cannot do instead.
  final VoidCallback? onOpenSettings;

  @override
  ConsumerState<AlertsScreen> createState() => _AlertsScreenState();
}

class _AlertsScreenState extends ConsumerState<AlertsScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  Future<AlertQueue>? _queue;

  /// Poster URLs for what is currently listed, fetched once per list rather than once per row.
  Map<String, Uri> _posters = const {};

  /// Rows taken away here rather than by a re-fetch.
  ///
  /// Dismissing is the one action in Serval with no undo and no confirmation, so it has to feel
  /// instant — waiting on a round trip to make a row leave is what turns a queue you clear while
  /// walking into one you fight. The Server is told at the same time; a failure puts the row back.
  final Set<String> _gone = {};

  /// Which alert the detail column is showing. Null opens the newest, so the column is never a
  /// blank 372px waiting to be told what to do.
  late String? _openId = widget.initialAlertId;

  /// Which camera the list is narrowed to, or null for all of them.
  String? _cameraId;

  /// What the last load listed, for [_onAlertUpdate] to recognise its own rows by.
  Set<String> _listed = const {};

  /// Clips settling under the rows on screen. See [_onAlertUpdate].
  StreamSubscription<Alert>? _updates;

  @override
  void initState() {
    super.initState();
    _queue = _repository.alerts();
    unawaited(_loadPosters(_queue!));
    _updates = _repository.alertUpdates.listen(_onAlertUpdate);
    if (widget.initialAlertId != null) unawaited(_markDeepLinkRead());
  }

  @override
  void dispose() {
    unawaited(_updates?.cancel());
    super.dispose();
  }

  /// A row's clip finished being cut.
  ///
  /// An alert is raised some sixteen seconds before its clip exists, so a row that arrives while
  /// this screen is open is listed with no poster and a *waiting* pill, and [_loadPosters] runs only
  /// per load — leaving the camera's stripe under a detection box for as long as the screen stays
  /// put. Re-reading the page is what replaces both.
  ///
  /// Only for rows already listed, and only for a settle. A raise is a row this list has never had,
  /// and pulling one in under a thumb that is busy clearing the queue is the thing [_gone] exists to
  /// stop; the queue is read, not watched.
  void _onAlertUpdate(Alert alert) {
    if (alert.clipState.waiting || !_listed.contains(alert.id)) return;
    _reload();
  }

  /// Arriving from a notification is arriving at the alert, so it counts as seen the moment this
  /// opens — the same thing tapping a row does. Waiting for a tap that already happened elsewhere
  /// would leave the queue calling an alert new while it is open in front of you.
  Future<void> _markDeepLinkRead() async {
    try {
      await _repository.markAlertRead(widget.initialAlertId!);
      if (mounted) _reload();
    } on Object {
      // A count that is briefly one too high is not worth a message.
    }
  }

  void _reload() {
    final queue = _repository.alerts(cameraId: _cameraId);

    // A block body, not an arrow: `=> _queue = queue` *returns* the assigned Future, and setState
    // rejects a callback that returns one.
    //
    // [_gone] is deliberately not cleared. A dismissal is sent without being waited on, so a reload
    // that raced it would get a list still containing the row and put it back on screen for as long
    // as the round trip took. Keeping the ids costs nothing — the Server's own list stops returning
    // them anyway — and the set only ever grows within one visit to the screen.
    setState(() {
      _queue = queue;
    });

    unawaited(_loadPosters(queue));
  }

  Future<void> _loadPosters(Future<AlertQueue> queue) async {
    try {
      final page = await queue;
      _listed = {for (final alert in page.items) alert.id};

      // Every row, not only the settled ones. An alert is given a poster from the camera's live
      // snapshot the moment it is raised, so a pending row has a picture too — and the URL carries
      // each alert's clip state, which is what swaps the stand-in for the exact frame once the clip
      // has been cut.
      final posters = await _repository.alertPosterUrls(page.items);
      if (mounted) setState(() => _posters = posters);
    } on Object {
      if (mounted) setState(() => _posters = const {});
    }
  }

  // ------------------------------------------------------------------ actions

  Future<void> _open(Alert alert) async {
    if (widget.onOpenAlert case final open?) {
      open(alert.id);
    } else {
      setState(() => _openId = alert.id);
    }

    if (alert.read) return;

    // Opening is what marks an alert seen. Nothing else does — a row scrolling past under your
    // thumb is not you having read it, and time never clears this list.
    try {
      await _repository.markAlertRead(alert.id);
      if (mounted) _reload();
    } on Object {
      // A count that is briefly one too high is not worth a message.
    }
  }

  Future<void> _dismiss(Alert alert) async {
    setState(() => _gone.add(alert.id));

    try {
      await _repository.dismissAlert(alert.id);
    } on Object {
      if (mounted) setState(() => _gone.remove(alert.id));
    }
  }

  Future<void> _dismissAll(List<Alert> shown) async {
    setState(() => _gone.addAll(shown.map((a) => a.id)));

    try {
      await _repository.dismissAllAlerts(cameraId: _cameraId);
    } on Object {
      if (mounted) _reload();
    }
  }

  void _watch(Alert alert) {
    // The same lead-in the activity feed uses, so an alert and the row on the wall that describes
    // the same moment open the recording in the same place.
    final at = replayStartFor(alert.at) ?? alert.at;
    widget.onWatch?.call(alert.cameraId, at);
  }

  // ------------------------------------------------------------------- build

  @override
  Widget build(BuildContext context) => FutureBuilder<AlertQueue>(
    future: _queue,
    builder: (context, snapshot) {
      final page = snapshot.data ?? const AlertQueue();
      final loading = snapshot.connectionState == ConnectionState.waiting;

      final shown = [
        for (final alert in page.items)
          if (!_gone.contains(alert.id)) alert,
      ];

      final unread = shown.where((a) => !a.read).length;

      return isCompact(context)
          ? _buildCompact(shown, unread: unread, loading: loading)
          : _buildDesktop(shown, unread: unread, loading: loading);
    },
  );

  // --------------------------------------------------------------------- 14a

  Widget _buildDesktop(
    List<Alert> alerts, {
    required int unread,
    required bool loading,
  }) {
    final open = _openAlert(alerts);

    // Not while the queue is in flight: every alert is missing from a list that has not arrived, and
    // a card saying so for one frame is worse than an empty column for one frame.
    final gone = open == null && !loading && _linkedAlertIsGone(alerts);

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _header(alerts, unread: unread),
          Expanded(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(
                  child: _list(alerts, loading: loading, compact: false),
                ),
                if (open != null || gone)
                  SizedBox(
                    // The same 372px the wall and saved clips give their detail, so an alert's card
                    // and a clip's card are one column in three places rather than three columns.
                    width: Serval.detailPanelWidth,
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: Serval.rail,
                        border: Border(
                          left: BorderSide(color: Serval.hairline),
                        ),
                      ),
                      // Keyed on the alert, so opening another rebuilds the player rather than
                      // pouring a second clip into the first one's state.
                      child: open == null
                          ? _gonePane()
                          : AlertDetailPane(
                              key: ValueKey(open.id),
                              alert: open,
                              poster: _posters[open.id],
                              onWatch: open.recorded == true
                                  ? () => _watch(open)
                                  : null,
                              onDismiss: () => _dismiss(open),
                            ),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Alert? _openAlert(List<Alert> alerts) {
    for (final alert in alerts) {
      if (alert.id == _openId) return alert;
    }

    // A notification named one alert, so falling back to the newest would answer a question nobody
    // asked — with the address bar still naming the one it is not. [_gonePane] says so instead.
    if (_linkedAlertIsGone(alerts)) return null;

    return alerts.isEmpty ? null : alerts.first;
  }

  /// Whether the alert a notification named is not in the queue — dismissed somewhere else, or aged
  /// past the retention window.
  ///
  /// The comparison against [AlertsScreen.initialAlertId] is what makes this stop applying: picking
  /// any row moves [_openId] off the linked id, and from then on this is an ordinary queue.
  bool _linkedAlertIsGone(List<Alert> alerts) =>
      _openId != null &&
      _openId == widget.initialAlertId &&
      !alerts.any((alert) => alert.id == _openId);

  Widget _gonePane() => Center(
    child: Padding(
      padding: const EdgeInsets.all(28),
      child: Text(
        'That alert is no longer here. It may have been cleared, or aged past '
        'the time alerts are kept for.',
        textAlign: TextAlign.center,
        style: TextStyle(
          fontSize: 13,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 45),
        ),
      ),
    ),
  );

  Widget _header(List<Alert> alerts, {required int unread}) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      spacing: 12,
      children: [
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          spacing: 2,
          children: [
            const Text(
              'Alerts',
              style: TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 17,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
            Text(
              _subtitle(alerts, unread),
              style: TextStyle(
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
          ],
        ),
        const Spacer(),
        _cameraFilter(alerts),
        _HeaderAction(
          icon: PhosphorIconsRegular.slidersHorizontal,
          label: "What I'm alerted on",
          onPressed: widget.onOpenSettings,
        ),
        _HeaderAction(
          icon: PhosphorIconsRegular.check,
          label: 'Dismiss all',
          ghost: true,
          onPressed: alerts.isEmpty ? null : () => _dismissAll(alerts),
        ),
      ],
    ),
  );

  /// `4 new · everything you asked to be told about` — the count, and the one sentence that says
  /// what this screen is for.
  String _subtitle(List<Alert> alerts, int unread) {
    if (alerts.isEmpty) return 'nothing waiting';
    if (unread == 0) return 'all seen · everything you asked to be told about';
    return '$unread new · everything you asked to be told about';
  }

  Widget _cameraFilter(List<Alert> alerts) {
    final names = <String, String>{
      for (final alert in alerts) alert.cameraId: alert.cameraName,
    };

    return _HeaderAction(
      icon: PhosphorIconsRegular.funnel,
      label: _cameraId == null ? 'All cameras' : names[_cameraId] ?? '1 camera',
      accent: _cameraId != null,
      onPressed: () => _pickCamera(names),
    );
  }

  Future<void> _pickCamera(Map<String, String> names) async {
    final chosen = await showNocturneDialog<Set<String>>(
      context: context,
      builder: (context) => _AlertCameraPicker(
        names: names,
        chosen: _cameraId == null ? const {} : {_cameraId!},
      ),
    );

    if (chosen == null || !mounted) return;

    setState(() => _cameraId = chosen.isEmpty ? null : chosen.first);
    _reload();
  }

  // --------------------------------------------------------------------- 14b

  Widget _buildCompact(
    List<Alert> alerts, {
    required int unread,
    required bool loading,
  }) => DecoratedBox(
    decoration: const BoxDecoration(color: Serval.panel),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        CompactAppBar(
          title: 'Alerts',
          subtitle: _subtitle(alerts, unread),
          onBack: widget.onBack,
          // The desktop's three header controls collapse to one here: at 412px the filter and the
          // bulk clear both lose to the swipe, which is the phone's way of clearing anyway.
          actions: [
            CompactBarAction(
              icon: PhosphorIconsRegular.slidersHorizontal,
              tooltip: "What I'm alerted on",
              onPressed: widget.onOpenSettings,
            ),
          ],
        ),
        Expanded(child: _list(alerts, loading: loading, compact: true)),
      ],
    ),
  );

  // ------------------------------------------------------------------ shared

  Widget _list(
    List<Alert> alerts, {
    required bool loading,
    required bool compact,
  }) {
    if (alerts.isEmpty) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Text(
            loading
                ? ''
                : _cameraId != null
                ? 'Nothing waiting from this camera.'
                : "Nothing waiting. Alerts land here when something you asked to be told "
                      'about is seen or heard — set what counts under Detection engine.',
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: 13.5,
              height: 1.5,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ),
      );
    }

    final now = DateTime.now();
    final groups = <String, List<Alert>>{};
    for (final alert in alerts) {
      groups.putIfAbsent(clipGroupLabel(alert.at, now), () => []).add(alert);
    }

    return SingleChildScrollView(
      padding: compact
          ? const EdgeInsets.fromLTRB(0, 14, 0, 20)
          : const EdgeInsets.fromLTRB(22, 18, 16, 22),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: compact ? 14 : 18,
        children: [
          for (final group in groups.entries)
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: compact ? 4 : 10,
              children: [
                Padding(
                  padding: compact
                      ? const EdgeInsets.symmetric(horizontal: 16)
                      : EdgeInsets.zero,
                  child: compact
                      ? SectionKicker(
                          _groupLabel(group.key, group.value),
                          padding: EdgeInsets.zero,
                        )
                      : Row(
                          spacing: 10,
                          children: [
                            SectionKicker(
                              _groupLabel(group.key, group.value),
                              padding: EdgeInsets.zero,
                            ),
                            Expanded(
                              child: Container(
                                height: 1,
                                color: Nocturne.mix(Nocturne.text, 7),
                              ),
                            ),
                          ],
                        ),
                ),
                for (final alert in group.value)
                  compact ? _swipeable(alert) : _row(alert),
              ],
            ),
        ],
      ),
    );
  }

  /// `Today`, `Yesterday · seen` — the day, and whether there is anything left to read in it.
  ///
  /// The suffix is the point of grouping at all: a whole day of rows you have already read is one
  /// thing to skip rather than six things to check.
  String _groupLabel(String day, List<Alert> group) =>
      group.every((a) => a.read) ? '$day · seen' : day;

  Widget _row(Alert alert) => AlertRow(
    alert: alert,
    poster: _posters[alert.id],
    selected: alert.id == _openId,
    onOpen: () => _open(alert),
    onWatch: alert.recorded == true ? () => _watch(alert) : null,
    onDismiss: () => _dismiss(alert),
  );

  /// The phone row, with a swipe that clears it.
  ///
  /// Right to left only, and one action behind it. This is a list you clear while walking; a second
  /// destructive direction is a second thing to get wrong one-handed.
  Widget _swipeable(Alert alert) => Dismissible(
    key: ValueKey(alert.id),
    direction: DismissDirection.endToStart,
    onDismissed: (_) => _dismiss(alert),
    background: Container(
      alignment: Alignment.centerRight,
      color: Nocturne.mix(Nocturne.accent, 18),
      child: SizedBox(
        width: 104,
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          spacing: 4,
          children: [
            Icon(
              PhosphorIconsRegular.check,
              size: 21,
              color: Nocturne.accent300,
            ),
            Text(
              'Dismiss',
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: Nocturne.accent300,
              ),
            ),
          ],
        ),
      ),
    ),
    child: AlertRow(
      alert: alert,
      poster: _posters[alert.id],
      compact: true,
      onOpen: () => _open(alert),
    ),
  );
}

/// One of the queue header's three controls.
///
/// The filter and *What I'm alerted on* are outlined; *Dismiss all* is not. That difference is
/// deliberate and the only weighting on the row: the first two change what you are looking at, and
/// the third takes it away — the one action here with no undo should be the quietest thing to
/// reach for.
class _HeaderAction extends StatefulWidget {
  const _HeaderAction({
    required this.icon,
    required this.label,
    this.onPressed,
    this.accent = false,
    this.ghost = false,
  });

  final PhosphorIconData icon;
  final String label;
  final VoidCallback? onPressed;
  final bool accent;
  final bool ghost;

  @override
  State<_HeaderAction> createState() => _HeaderActionState();
}

class _HeaderActionState extends State<_HeaderAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onPressed != null;
    final color = !enabled
        ? Nocturne.mix(Nocturne.text, 26)
        : widget.accent
        ? Nocturne.accent300
        : Nocturne.mix(Nocturne.text, widget.ghost ? 55 : 72);

    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        behavior: HitTestBehavior.opaque,
        child: Container(
          height: 34,
          padding: const EdgeInsets.symmetric(horizontal: 12),
          alignment: Alignment.center,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(7),
            color: !enabled
                ? null
                : widget.accent
                ? Nocturne.mix(Nocturne.accent, 14)
                : _hovered
                ? Nocturne.mix(Nocturne.text, 7)
                : null,
            border: widget.ghost
                ? null
                : Border.all(
                    color: widget.accent
                        ? Nocturne.mix(Nocturne.accent, 55)
                        : Nocturne.mix(Nocturne.text, 14),
                  ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            spacing: 7,
            children: [
              Icon(widget.icon, size: 15, color: color),
              Text(widget.label, style: TextStyle(fontSize: 13, color: color)),
            ],
          ),
        ),
      ),
    );
  }
}

/// Which camera the queue is narrowed to.
///
/// One at a time, unlike the clips library's picker. The queue's whole posture is "everything you
/// asked to be told about"; narrowing it is a momentary thing you do to answer one question about
/// one camera, not a view you keep — so the choice is a radio rather than a set, and *All cameras*
/// is one press away rather than several.
class _AlertCameraPicker extends StatefulWidget {
  const _AlertCameraPicker({required this.names, required this.chosen});

  final Map<String, String> names;
  final Set<String> chosen;

  @override
  State<_AlertCameraPicker> createState() => _AlertCameraPickerState();
}

class _AlertCameraPickerState extends State<_AlertCameraPicker> {
  late String? _chosen = widget.chosen.isEmpty ? null : widget.chosen.first;

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Which camera',
    width: 360,
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      spacing: 4,
      children: [
        _PickerRow(
          label: 'All cameras',
          checked: _chosen == null,
          onChanged: () => setState(() => _chosen = null),
        ),
        for (final entry in widget.names.entries)
          _PickerRow(
            label: entry.value,
            checked: _chosen == entry.key,
            onChanged: () => setState(() => _chosen = entry.key),
          ),
      ],
    ),
    actions: [
      NocturneButton(
        label: 'Done',
        variant: NocturneButtonVariant.primary,
        onPressed: () => Navigator.of(
          context,
        ).pop(_chosen == null ? <String>{} : {_chosen!}),
      ),
    ],
  );
}

class _PickerRow extends StatelessWidget {
  const _PickerRow({
    required this.label,
    required this.checked,
    required this.onChanged,
  });

  final String label;
  final bool checked;
  final VoidCallback onChanged;

  @override
  Widget build(BuildContext context) => GestureDetector(
    onTap: onChanged,
    behavior: HitTestBehavior.opaque,
    child: Padding(
      padding: const EdgeInsets.symmetric(vertical: 7),
      child: Row(
        spacing: 10,
        children: [
          // Hand-drawn rather than Material's Radio, which brings a ripple and a fill the system
          // forbids — the same reason the activity filter draws its own.
          Container(
            width: 16,
            height: 16,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              border: Border.all(
                color: checked
                    ? Nocturne.mix(Nocturne.accent, 70)
                    : Nocturne.mix(Nocturne.text, 22),
              ),
            ),
            child: checked
                ? Container(
                    width: 7,
                    height: 7,
                    decoration: const BoxDecoration(
                      color: Nocturne.accent300,
                      shape: BoxShape.circle,
                    ),
                  )
                : null,
          ),
          Expanded(
            child: Text(
              label,
              style: TextStyle(
                fontSize: 13.5,
                color: Nocturne.mix(Nocturne.text, checked ? 100 : 72),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}
