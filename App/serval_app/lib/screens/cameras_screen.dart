import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/byte_labels.dart';
import '../data/camera_record.dart';
import '../data/serval_api.dart';
import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../models/camera.dart';
import '../models/server_camera_defaults.dart';
import '../models/system_stats.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/camera_settings_form.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/roster_widgets.dart';
import '../widgets/status_indicators.dart';
import '../widgets/storage_bar.dart';

/// Cameras, and the settings for the one you pick.
///
/// Until now the registry's only GUI was the Server's Scalar page — a generated schema form that
/// also prints every camera's ONVIF password into the browser. This is the replacement: the
/// design's screen 2a, where every control maps to one field of the camera record and the health
/// of the camera you are editing sits in the same header, because a setting and a fault are
/// usually the same visit.
class CamerasScreen extends ConsumerStatefulWidget {
  const CamerasScreen({super.key, this.initialCameraId, this.onSelectCamera});

  /// Which camera to open on, from `?camera=` — set by the single-camera view's gear. Null opens
  /// on the first in the registry, which is what the rail's gear does.
  final String? initialCameraId;

  /// Asks whoever owns the address to open a camera, or to close the one that is open with null.
  ///
  /// Passed in rather than reached for, the way `WallScreen.onOpenCamera` is: the drill-down needs
  /// each camera to be its own address so the back button walks out of the editor, and this screen
  /// is also pumped outside a router. Null keeps the selection in widget state.
  final ValueChanged<String?>? onSelectCamera;

  @override
  ConsumerState<CamerasScreen> createState() => _CamerasScreenState();
}

class _CamerasScreenState extends ConsumerState<CamerasScreen> {
  final _search = TextEditingController();

  late final ServalRepository _repository = ref.read(repositoryProvider);

  String? _selectedId;

  /// Whether the narrow list is showing its search field. The design's app bar keeps a magnifier
  /// where the panel heading kept a box, because at this width the field would cost a row of
  /// cameras to sit open on a screen nobody is searching.
  bool _searching = false;

  /// The Server's catalogue, which supplies every tuning card's label, sentence, bounds and the
  /// value a camera falls back to.
  ///
  /// Read once, and never in the way: a viewer without the settings endpoint keeps
  /// [ServerCameraDefaults.unknown] and the form falls back to its own wording, which is the whole
  /// cost of failing.
  ServerCameraDefaults _serverDefaults = ServerCameraDefaults.unknown;

  @override
  void initState() {
    super.initState();
    _selectedId = widget.initialCameraId;
    _loadServerLabels();
  }

  /// Follows the address. A drill-down move is a navigation, so the new `?camera=` arrives as a
  /// rebuilt widget rather than as a call into this state.
  @override
  void didUpdateWidget(CamerasScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.initialCameraId != oldWidget.initialCameraId) {
      _selectedId = widget.initialCameraId;
      if (widget.initialCameraId != null) _draft = null;
    }
  }

  Future<void> _loadServerLabels() async {
    try {
      final settings = await _repository.settings();
      if (!mounted) return;
      setState(() => _serverDefaults = ServerCameraDefaults.from(settings));
    } on ServalApiException {
      // Nothing to say and nothing to retry: the page works without them.
    }
  }

  /// The camera being added, before it has an id on the Server. Held apart from the registry so
  /// an abandoned *Add* leaves nothing behind.
  CameraRecord? _draft;

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  List<CameraRecord> get _records => _repository.cameraRecords();

  CameraRecord? get _selected {
    if (_draft != null) return _draft;
    final id = _selectedId;
    if (id == null) return _records.firstOrNull;
    return _repository.cameraRecordById(id) ?? _records.firstOrNull;
  }

  /// What the drill-down has open, or null for the list.
  ///
  /// No fallback to the first camera: beside a list the editor should never be empty when there is
  /// something to put in it, but on a screen of its own it must not open a record nobody asked for.
  CameraRecord? get _drilledInto {
    if (_draft != null) return _draft;
    final id = _selectedId;
    return id == null ? null : _repository.cameraRecordById(id);
  }

  /// How a camera is doing, read off the view model rather than re-derived here.
  ///
  /// The repository already works out the connection from snapshot freshness — it is the only
  /// thing that sees the frames — so asking it twice, in two places, with two thresholds, is how a
  /// camera ends up green in the list and offline on the wall.
  ///
  /// A straight map, now that [CameraConnection] draws the same three-way distinction this enum
  /// always has. The two stay separate deliberately: [CameraHealth] is a rendering vocabulary with
  /// labels and colours, and the other is what the data says.
  CameraHealth _healthOf(CameraRecord record) {
    // First, and not a re-derivation: this reads the authoritative field for a fact the frame
    // clock has no opinion about. A camera switched off on purpose is not a camera in trouble.
    if (!record.enabled) return CameraHealth.disabled;

    final view = _repository.cameraById(record.id);
    if (view == null) return CameraHealth.connecting;

    // Frames arriving, not segments landing. A camera set to keep nothing is working perfectly,
    // and asking isRecording here would leave it reading "Starting up" for as long as it ran.
    return switch (view.connection) {
      CameraConnection.online => CameraHealth.healthy,
      CameraConnection.connecting => CameraHealth.connecting,
      CameraConnection.offline => CameraHealth.unreachable,
    };
  }

  void _startAdd() => setState(() {
    _draft = CameraRecord.blank();
    _selectedId = null;
  });

  void _select(String id) {
    setState(() {
      _draft = null;
      _selectedId = id;
    });
    widget.onSelectCamera?.call(id);
  }

  /// Back to the list: the drill-down's way out, and where a save lands.
  void _closeEditor() {
    setState(() {
      _draft = null;
      _selectedId = null;
    });
    widget.onSelectCamera?.call(null);
  }

  Future<void> _save(CameraRecord edited) async {
    final creating = _draft != null;

    if (creating) {
      await _repository.createCamera(edited);
    } else {
      await _repository.updateCamera(edited);
    }

    if (!mounted) return;

    // Saving is what shortens the drill-down: it answers the visit the phone was for, and coming
    // back to the list is one press nearer the wall than staying on the record would be.
    if (isCompact(context)) {
      _closeEditor();
      return;
    }

    setState(() {
      _draft = null;
      _selectedId = edited.id;
    });
  }

  Future<void> _delete(CameraRecord record) async {
    final confirmed = await showNocturneDialog<bool>(
      context: context,
      builder: (context) => ConfirmDeleteDialog(
        cameraName: record.name.isEmpty ? record.id : record.name,
        // The Server is explicit that a delete leaves the media alone — the retention sweep ages
        // it out on the camera's own schedule. Worth saying, since "remove" reads as "erase".
        retentionNote:
            'Footage already recorded stays on disk until it ages out'
            '${record.retentionDays == null ? '' : ' after ${record.retentionDays} days'}.',
      ),
    );

    if (confirmed != true || !mounted) return;

    await _repository.deleteCamera(record.id);
    if (mounted) _closeEditor();
  }

  @override
  Widget build(BuildContext context) {
    final compact = isCompact(context);
    final records = _records;
    final stats = _repository.systemStats();

    // Every camera write is Admin-only on the Server, so below that role this page reads rather
    // than edits. Defaults to true with no session, which is the sample path the goldens capture.
    final canEdit = ref.watch(isAdminProvider);

    // The rail and the settings sidebar are drawn by `ServalShell` and `SettingsShell` — this
    // screen is what goes to the right of both, and the whole of it below `Serval.compactWidth`,
    // where design 7b makes the list and the editor two screens rather than two columns.
    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: compact
          ? _buildDrillDown(records, stats, canEdit)
          : Row(
              children: [
                _CameraList(
                  records: records,
                  search: _search,
                  selectedId: _selected?.id,
                  adding: _draft != null,
                  healthOf: _healthOf,
                  frameFor: _repository.snapshotFor,
                  onSelect: _select,
                  onAdd: canEdit ? _startAdd : null,
                  onSearchChanged: () => setState(() {}),
                  disk: stats?.disk,
                ),
                Expanded(child: _editorFor(_selected, records, stats, canEdit)),
              ],
            ),
    );
  }

  /// One screen or the other: the list until a camera is picked, then that camera.
  Widget _buildDrillDown(
    List<CameraRecord> records,
    SystemStats? stats,
    bool canEdit,
  ) {
    final open = _drilledInto;

    if (open != null) {
      return _editorFor(open, records, stats, canEdit);
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        CompactAppBar(
          title: 'Cameras',
          onBack: () => context.go('/settings'),
          actions: [
            CompactBarAction(
              icon: PhosphorIconsRegular.magnifyingGlass,
              tooltip: _searching ? 'Close search' : 'Search cameras',
              selected: _searching,
              onPressed: () => setState(() {
                _searching = !_searching;
                if (!_searching) _search.clear();
              }),
            ),
            if (canEdit)
              CompactBarAction(
                icon: PhosphorIconsRegular.plus,
                tooltip: 'Add a camera',
                onPressed: _startAdd,
              ),
          ],
        ),
        Expanded(
          child: _CameraList(
            records: records,
            search: _search,
            selectedId: null,
            adding: false,
            healthOf: _healthOf,
            frameFor: _repository.snapshotFor,
            onSelect: _select,
            onAdd: canEdit ? _startAdd : null,
            onSearchChanged: () => setState(() {}),
            disk: stats?.disk,
            compact: true,
            showSearch: _searching,
          ),
        ),
      ],
    );
  }

  /// The editor for [selected], or the empty state when the registry has nothing in it.
  Widget _editorFor(
    CameraRecord? selected,
    List<CameraRecord> records,
    SystemStats? stats,
    bool canEdit,
  ) {
    if (selected == null) {
      return const EmptyRoster(
        icon: PhosphorIconsRegular.videoCamera,
        title: 'No cameras yet',
        body:
            'Add one and Serval starts pulling it straight away — the ingest manager '
            'reconciles against the registry on its own loop, so there is nothing else to '
            'switch on.',
      );
    }

    return CameraSettingsForm(
      // Rebuild the form from scratch when the subject changes, so half-typed edits cannot leak
      // from one camera onto the next.
      key: ValueKey(_draft != null ? '__draft__' : selected.id),
      record: selected,
      creating: _draft != null,
      health: _draft != null ? CameraHealth.connecting : _healthOf(selected),
      knownLocations: _locationsIn(records),
      existingIds: {for (final record in records) record.id},
      readOnly: !canEdit,
      onSave: _save,
      onDelete: _draft != null || !canEdit ? null : () => _delete(selected),
      onDiscard: _draft == null ? null : _closeEditor,
      onBack: _closeEditor,
      watchAudioLevels: _repository.watchAudioLevels,
      deviceInformation: _draft != null
          ? null
          : _repository.deviceInformationFor(selected.id),
      // Passed in rather than read from a provider inside the form: everything in
      // `lib/widgets/` is prop-driven, and this screen already holds the repository.
      diskUsage: _draft != null ? null : _diskUsageFor(stats, selected.id),
      defaults: _serverDefaults,
      frameFor: _repository.snapshotFor,
      // Masks are drawn on a separate screen that saves the camera when it is done, so it is a
      // write path like any other and closes with the rest of them.
      onEditMasks: _draft != null || !canEdit
          ? null
          : () => context.go('/settings/cameras/masks?camera=${selected.id}'),
      onOpenServerSettings: () => context.go('/settings/server'),
    );
  }

  /// This camera's footprint out of the whole-server sample, or null when the Server publishes no
  /// vitals or has the per-camera walk switched off.
  static CameraDiskUsage? _diskUsageFor(SystemStats? stats, String cameraId) {
    for (final camera in stats?.disk.cameras ?? const <CameraDiskUsage>[]) {
      if (camera.cameraId == cameraId) return camera;
    }
    return null;
  }

  /// Every place already in use, so *Where it is* offers them rather than inviting a second
  /// spelling of a group that already exists.
  static List<String> _locationsIn(List<CameraRecord> records) {
    final locations = <String>{
      for (final record in records)
        if ((record.location ?? '').trim().isNotEmpty) record.location!.trim(),
    }.toList()..sort();
    return locations;
  }
}

/// The 272px list: add, search, cameras grouped by where they are, and how much disk the
/// recordings are using.
class _CameraList extends StatelessWidget {
  const _CameraList({
    required this.records,
    required this.search,
    required this.selectedId,
    required this.adding,
    required this.healthOf,
    required this.frameFor,
    required this.onSelect,
    this.onAdd,
    required this.onSearchChanged,
    this.disk,
    this.compact = false,
    this.showSearch = true,
  });

  final List<CameraRecord> records;
  final TextEditingController search;
  final String? selectedId;
  final bool adding;
  final CameraHealth Function(CameraRecord) healthOf;
  final Uint8List? Function(String) frameFor;
  final ValueChanged<String> onSelect;

  /// Null hides *Add*: only an Admin may create a camera, and a button that always answers
  /// 403 is worse than no button.
  final VoidCallback? onAdd;
  final VoidCallback onSearchChanged;

  /// The media volume, for the footer. Null on a Server that publishes no vitals.
  final DiskStats? disk;

  /// Takes the whole screen: the panel heading goes, since the app bar above already says
  /// *Cameras* and carries the add and search this heading holds when it is a column.
  final bool compact;

  /// Whether the search field is on screen at all. Always, in the column; on the phone only while
  /// the app bar's magnifier is lit.
  final bool showSearch;

  static const _ungrouped = 'Elsewhere';

  @override
  Widget build(BuildContext context) {
    final query = search.text.trim().toLowerCase();
    final matching = [
      for (final record in records)
        if (query.isEmpty ||
            record.name.toLowerCase().contains(query) ||
            record.id.toLowerCase().contains(query) ||
            (record.location ?? '').toLowerCase().contains(query))
          record,
    ];

    // Grouped by location, the way the design does, with everything unplaced gathered at the end
    // rather than each sitting under its own blank heading.
    final groups = <String, List<CameraRecord>>{};
    for (final record in matching) {
      final location = (record.location ?? '').trim();
      groups
          .putIfAbsent(location.isEmpty ? _ungrouped : location, () => [])
          .add(record);
    }

    final headings = groups.keys.toList()
      ..sort((a, b) {
        if (a == _ungrouped) return 1;
        if (b == _ungrouped) return -1;
        return a.toLowerCase().compareTo(b.toLowerCase());
      });

    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (compact)
          if (showSearch)
            Container(
              padding: const EdgeInsets.fromLTRB(18, 12, 18, 12),
              decoration: BoxDecoration(
                border: Border(bottom: BorderSide(color: Serval.hairline)),
              ),
              child: SearchBox(
                controller: search,
                onChanged: onSearchChanged,
                placeholder: 'Search cameras',
                height: 40,
              ),
            )
          else
            const SizedBox.shrink()
        else
          Container(
            padding: const EdgeInsets.fromLTRB(16, 18, 16, 14),
            decoration: BoxDecoration(
              border: Border(bottom: BorderSide(color: Serval.hairline)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    const Text(
                      'Cameras',
                      style: TextStyle(
                        fontFamily: Nocturne.fontHeading,
                        fontSize: 16,
                        fontWeight: Nocturne.headingWeight,
                        color: Nocturne.text,
                      ),
                    ),
                    const Spacer(),
                    if (onAdd != null)
                      NocturneButton(
                        label: 'Add',
                        icon: PhosphorIconsRegular.plus,
                        variant: NocturneButtonVariant.primary,
                        height: 28,
                        fontSize: 12.5,
                        horizontalPadding: 10,
                        borderRadius: 6,
                        onPressed: onAdd,
                      ),
                  ],
                ),
                const SizedBox(height: 11),
                SearchBox(
                  controller: search,
                  onChanged: onSearchChanged,
                  placeholder: 'Search cameras',
                ),
              ],
            ),
          ),
        Expanded(
          child: ListView(
            padding: compact
                ? const EdgeInsets.fromLTRB(8, 6, 8, 10)
                : const EdgeInsets.all(10),
            children: [
              if (adding)
                const DraftRow(
                  icon: PhosphorIconsRegular.plus,
                  label: 'New camera',
                )
              else if (matching.isEmpty)
                EmptyNote(
                  query.isEmpty
                      ? 'No cameras registered yet.'
                      : 'Nothing matches “${search.text.trim()}”.',
                ),
              for (final heading in headings) ...[
                Padding(
                  padding: EdgeInsets.fromLTRB(
                    8,
                    heading == headings.first ? 6 : 12,
                    8,
                    6,
                  ),
                  child: Text(
                    heading.toUpperCase(),
                    style: TextStyle(
                      fontFamily: Nocturne.fontMono,
                      fontSize: 10,
                      letterSpacing: 0.14 * 10,
                      color: Nocturne.mix(Nocturne.text, 35),
                    ),
                  ),
                ),
                for (final record in groups[heading]!)
                  _CameraRow(
                    record: record,
                    health: healthOf(record),
                    frame: frameFor(record.id),
                    selected: !adding && record.id == selectedId,
                    compact: compact,
                    onTap: () => onSelect(record.id),
                  ),
              ],
            ],
          ),
        ),
        _StorageFooter(disk: disk),
      ],
    );

    if (compact) return body;

    return Container(
      width: 272,
      decoration: BoxDecoration(
        color: Serval.rail,
        border: Border(right: BorderSide(color: Serval.hairline)),
      ),
      child: body,
    );
  }
}

/// One camera in the list: a thumbnail, its name, what it is doing, and a status dot.
class _CameraRow extends StatefulWidget {
  const _CameraRow({
    required this.record,
    required this.health,
    required this.frame,
    required this.selected,
    required this.onTap,
    this.compact = false,
  });

  final CameraRecord record;
  final CameraHealth health;
  final Uint8List? frame;
  final bool selected;
  final VoidCallback onTap;

  /// A 64px row with a larger preview — the design's phone list.
  final bool compact;

  @override
  State<_CameraRow> createState() => _CameraRowState();
}

class _CameraRowState extends State<_CameraRow> {
  bool _hovered = false;

  /// The design's second line: what this camera is doing, in the terms someone would use out
  /// loud. Every part is read off the record — nothing here is invented.
  String get _subtitle {
    final record = widget.record;
    return switch (widget.health) {
      CameraHealth.disabled => 'Turned off',
      CameraHealth.unreachable => 'Can’t reach it',
      CameraHealth.connecting => 'Starting up',
      // Ahead of the other healthy arms: retention and talk-back are both true of a camera that
      // keeps nothing, and either would put the word "Recording" on a row that records nothing.
      CameraHealth.healthy when !record.records =>
        record.twoWayAudio
            ? 'Watching · not kept · talk-back on'
            : 'Watching · not kept',
      CameraHealth.healthy when record.twoWayAudio =>
        'Recording · talk-back on',
      CameraHealth.healthy when record.retentionDays != null =>
        'Recording · ${record.retentionDays} days kept',
      CameraHealth.healthy => 'Recording',
    };
  }

  @override
  Widget build(BuildContext context) {
    final record = widget.record;
    final unreachable = widget.health == CameraHealth.unreachable;
    final compact = widget.compact;

    return Opacity(
      opacity: widget.health == CameraHealth.disabled ? 0.62 : 1,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: widget.onTap,
          behavior: HitTestBehavior.opaque,
          child: Container(
            constraints: compact ? const BoxConstraints(minHeight: 64) : null,
            padding: compact
                ? const EdgeInsets.symmetric(horizontal: 10, vertical: 8)
                : const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: widget.selected
                  ? Nocturne.mix(Nocturne.accent, 16)
                  : _hovered
                  ? Nocturne.mix(Nocturne.text, 5)
                  : null,
              borderRadius: BorderRadius.circular(8),
              border: widget.selected
                  ? Border.all(color: Nocturne.mix(Nocturne.accent, 40))
                  : Border.all(color: const Color(0x00000000)),
            ),
            child: Row(
              children: [
                _Thumbnail(
                  cameraId: record.id,
                  frame: widget.frame,
                  unreachable: unreachable,
                  compact: compact,
                ),
                SizedBox(width: compact ? 13 : 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        record.name.isEmpty ? record.id : record.name,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontFamily: Nocturne.fontBody,
                          fontSize: compact ? 15 : 13.5,
                          fontWeight: Nocturne.headingWeight,
                          color: widget.selected
                              ? Nocturne.text
                              : Nocturne.mix(Nocturne.text, 85),
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        _subtitle,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontFamily: Nocturne.fontBody,
                          fontSize: compact ? 12.5 : 11.5,
                          color: unreachable
                              ? Serval.recordingText
                              : Nocturne.mix(Nocturne.text, 50),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                StatusDot(health: widget.health),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The 34x26 preview: the camera's own frame where one has arrived, its stripe placeholder
/// otherwise, and a struck-through wifi glyph when it has dropped out.
class _Thumbnail extends StatelessWidget {
  const _Thumbnail({
    required this.cameraId,
    required this.frame,
    required this.unreachable,
    this.compact = false,
  });

  final String cameraId;
  final Uint8List? frame;
  final bool unreachable;

  /// The design's phone list draws it at 44x33 — the row is taller, and a preview this small is
  /// the only thing on it that says which camera it is before the name is read.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final placeholder = TilePlaceholder.forCameraId(cameraId);

    return SizedBox(
      width: compact ? 44 : 34,
      height: compact ? 33 : 26,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(compact ? 5 : 4),
        child: unreachable
            ? ColoredBox(
                color: const Color(0xFF1A1C25),
                child: Center(
                  child: PhosphorIcon(
                    PhosphorIconsRegular.wifiSlash,
                    size: compact ? 14 : 11,
                    color: Nocturne.mix(Nocturne.text, 35),
                  ),
                ),
              )
            : frame != null
            // `contain` in a box that is 4:3 while most of the cameras are not: a preview this
            // small is only here to say which camera the row is, and a cropped one says it worse —
            // filling it takes a quarter of the width off a 16:9 frame, a side each. The bars take
            // the video ground, as they do on a wall tile.
            ? ColoredBox(
                color: Serval.tile,
                child: Image.memory(
                  frame!,
                  fit: BoxFit.contain,
                  gaplessPlayback: true,
                ),
              )
            : DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.topLeft,
                    end: Alignment.bottomRight,
                    colors: [placeholder.stripeLight, placeholder.stripeDark],
                  ),
                ),
              ),
      ),
    );
  }
}

/// The design's storage bar — `1.8 TB of 4 TB`.
///
/// Stood empty and said *not reported* until `GET /api/system/stats` existed, on the grounds that
/// drawing a bar with nothing behind it would be a lie about free space. It has something behind
/// it now, and it keeps the honest fallback for the case that remains: a Server too old to answer,
/// or a volume that could not be measured.
class _StorageFooter extends StatelessWidget {
  const _StorageFooter({required this.disk});

  /// Null on a Server that publishes no vitals, and before the first sample lands.
  final DiskStats? disk;

  @override
  Widget build(BuildContext context) {
    final total = disk?.totalBytes;
    final free = disk?.freeBytes;
    final measured = total != null && free != null;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: Serval.hairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Recordings',
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 62),
                  ),
                ),
              ),
              const SizedBox(width: 8),
              Text(
                measured
                    ? '${formatBytes(total - free)} of ${formatBytes(total)}'
                    : 'not reported',
                style: monoStyle(
                  fontSize: 11,
                  color: measured
                      ? Nocturne.mix(Nocturne.text, 70)
                      : Nocturne.mix(Nocturne.text, 38),
                ),
              ),
            ],
          ),
          if (measured) ...[
            const SizedBox(height: 9),
            StorageBar(
              totalBytes: total,
              freeBytes: free,
              mediaBytes: disk!.mediaBytes,
              height: 5,
              showLegend: false,
            ),
          ],
        ],
      ),
    );
  }
}

/// The error a failed save shows, in the Server's own words.
///
/// Its 400s name the missing encoder, the unassigned role, the rejected codec — they are written
/// to be read, so nothing here paraphrases them.
class SaveFailureNote extends StatelessWidget {
  const SaveFailureNote({super.key, required this.error});

  final Object error;

  @override
  Widget build(BuildContext context) {
    final message = error is ServalApiException
        ? (error as ServalApiException).message
        : 'Could not reach the Server. $error';

    return Row(
      children: [
        PhosphorIcon(
          PhosphorIconsFill.warningCircle,
          size: 15,
          color: Serval.recording,
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            message,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.4,
              color: Serval.recordingText,
            ),
          ),
        ),
      ],
    );
  }
}
