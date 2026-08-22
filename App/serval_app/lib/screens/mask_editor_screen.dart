/// Design 9b: the mask editor, on a screen of its own.
///
/// Not a dialog, and the design says why: drawing wants the frame at full size and both hands on
/// the keyboard, and a mask drawn at 214px wide is a mask you will redraw. So this replaces the
/// whole window — no icon rail, no settings sidebar, no camera list — and gets back to the camera
/// through its own back arrow.
///
/// **Nothing commits until *Save masks*.** The record is edited as a copy and sent whole, exactly
/// as the camera form does, because `PUT /api/cameras/{id}` replaces rather than merges.
library;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/camera_record.dart';
import '../data/providers.dart';
import '../data/serval_api.dart';
import '../data/serval_repository.dart';
import '../models/server_camera_defaults.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/label_chips.dart';
import '../widgets/mask_canvas.dart';
import '../widgets/mask_preview.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_field.dart';
import '../widgets/nocturne_toggle.dart';
import '../widgets/segmented_control.dart';
import '../widgets/settings_cards.dart';

class MaskEditorScreen extends ConsumerStatefulWidget {
  const MaskEditorScreen({super.key, required this.cameraId});

  final String cameraId;

  @override
  ConsumerState<MaskEditorScreen> createState() => _MaskEditorScreenState();
}

class _MaskEditorScreenState extends ConsumerState<MaskEditorScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  /// The masks as they will be saved. Seeded from the record and never written back until *Save
  /// masks*, which is the whole of this screen's transaction.
  List<DetectionMaskSettings> _masks = const [];

  /// The polygon being drawn, flat and still open. Empty when nothing is in hand.
  List<double> _draft = const [];

  int? _selected;
  final Set<int> _hidden = {};

  MaskTool _tool = MaskTool.draw;
  bool _snapToEdges = true;
  bool _showAll = true;

  /// The frame this was opened on, held still. A live picture under a polygon being drawn would
  /// move the thing being drawn around, and a mask is a fact about the view rather than about a
  /// moment of it.
  Uint8List? _still;
  DateTime? _stillTakenAt;

  bool _saving = false;
  String? _failure;

  final _name = TextEditingController();

  /// What the camera falls back to, for the class chips. Read once and never in the way.
  ServerCameraDefaults _defaults = ServerCameraDefaults.unknown;

  @override
  void initState() {
    super.initState();
    _masks = List.of(_record?.detectionTuning?.masks ?? const []);
    _takeStill();
    _loadDefaults();
  }

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  CameraRecord? get _record => _repository.cameraRecordById(widget.cameraId);

  Future<void> _loadDefaults() async {
    try {
      final settings = await _repository.settings();
      if (!mounted) return;
      setState(() => _defaults = ServerCameraDefaults.from(settings));
    } on ServalApiException {
      // The chips fall back to whatever this camera already names. Nothing to retry.
    }
  }

  void _takeStill() => setState(() {
    _still = _repository.snapshotFor(widget.cameraId);
    _stillTakenAt = _still == null ? null : DateTime.now();
  });

  // ------------------------------------------------------------------ editing

  bool get _dirty {
    final saved = _record?.detectionTuning?.masks ?? const [];
    if (saved.length != _masks.length) return true;
    for (var i = 0; i < saved.length; i++) {
      if (saved[i] != _masks[i]) return true;
    }
    return false;
  }

  DetectionMaskSettings? get _current {
    final index = _selected;
    if (index == null || index >= _masks.length) return null;
    return _masks[index];
  }

  /// Closes the draft into a mask and selects it, so the name field is ready for the thing just
  /// drawn rather than for whatever was selected before.
  void _commitDraft() {
    if (_draft.length < 6) return;

    setState(() {
      _masks = [..._masks, DetectionMaskSettings(points: List.of(_draft))];
      _draft = const [];
      _selected = _masks.length - 1;
      _name.text = '';
    });
  }

  /// Removes the last point placed. Also the toolbar's *Undo point*, because a keyboard rule
  /// nobody can see is a rule nobody uses.
  void _undoPoint() {
    if (_draft.length < 2) return;
    setState(() => _draft = _draft.sublist(0, _draft.length - 2));
  }

  void _abandonDraft() => setState(() => _draft = const []);

  void _select(int index) => setState(() {
    _selected = index;
    _name.text = _masks[index].name ?? '';
  });

  void _replaceSelected(DetectionMaskSettings mask) {
    final index = _selected;
    if (index == null) return;
    setState(
      () => _masks = [
        for (var i = 0; i < _masks.length; i++)
          if (i == index) mask else _masks[i],
      ],
    );
  }

  void _delete(int index) => setState(() {
    _masks = [
      for (var i = 0; i < _masks.length; i++)
        if (i != index) _masks[i],
    ];
    _selected = null;
    _hidden.remove(index);
    _name.text = '';
  });

  /// Why saving is not possible yet, or null. The Server's own rules, mirrored — see
  /// `CameraRepository.ValidateDetectionTuning`.
  String? get _problem {
    for (final mask in _masks) {
      if (mask.points.length < 6 || mask.points.length.isOdd) {
        return '“${maskTitle(mask)}” needs at least three points.';
      }
      for (final value in mask.points) {
        if (value.isNaN || value < 0 || value > 1) {
          return '“${maskTitle(mask)}” has a point outside the frame.';
        }
      }
    }
    return null;
  }

  Future<void> _save() async {
    final record = _record;
    if (record == null || _saving) return;

    setState(() {
      _saving = true;
      _failure = null;
    });

    try {
      // Collapsed the way every other section collapses its bag: an all-null tuning is no tuning.
      final tuning = (record.detectionTuning ?? const DetectionTuningSettings())
          .copyWith(masks: _masks.isEmpty ? null : _masks);

      await _repository.updateCamera(
        record.copyWith(detectionTuning: tuning.isEmpty ? null : tuning),
      );

      if (!mounted) return;
      _close();
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() => _failure = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _close() => context.go('/settings/cameras?camera=${widget.cameraId}');

  // -------------------------------------------------------------------- build

  @override
  Widget build(BuildContext context) {
    final compact = isCompact(context);

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      // The keyboard rules the canvas advertises. Autofocused, because the whole screen is the
      // drawing surface and there is nothing else here to type into until a mask exists.
      child: Shortcuts(
        shortcuts: const {
          SingleActivator(LogicalKeyboardKey.backspace): _UndoPointIntent(),
          SingleActivator(LogicalKeyboardKey.escape): _AbandonDraftIntent(),
        },
        child: Actions(
          actions: {
            _UndoPointIntent: _CanvasAction<_UndoPointIntent>(
              onInvoke: (_) {
                _undoPoint();
                return null;
              },
            ),
            _AbandonDraftIntent: _CanvasAction<_AbandonDraftIntent>(
              onInvoke: (_) {
                _abandonDraft();
                return null;
              },
            ),
          },
          child: Focus(
            autofocus: true,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _appBar(compact),
                if (_failure case final failure?) _failureStrip(failure),
                Expanded(
                  child: compact
                      ? Column(
                          children: [
                            Expanded(child: _canvasColumn),
                            SizedBox(height: 260, child: _inspector),
                          ],
                        )
                      : Row(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Expanded(child: _canvasColumn),
                            SizedBox(width: 340, child: _inspector),
                          ],
                        ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _appBar(bool compact) => Container(
    padding: EdgeInsets.fromLTRB(compact ? 14 : 20, 14, compact ? 14 : 20, 14),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        _BackToCamera(name: _record?.name ?? widget.cameraId, onTap: _close),
        if (!compact) ...[
          Container(
            width: 1,
            height: 18,
            margin: const EdgeInsets.symmetric(horizontal: 14),
            color: Nocturne.mix(Nocturne.text, 14),
          ),
          const Text(
            'Masks & zones',
            style: TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: 17,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
          const SizedBox(width: 12),
          _StillChip(takenAt: _stillTakenAt),
          const SizedBox(width: 10),
          SettingsLinkText('Take a fresh still', onTap: _takeStill),
        ],
        const Spacer(),
        if (_draft.isNotEmpty) ...[
          NocturneButton(
            label: 'Undo point',
            icon: PhosphorIconsRegular.arrowCounterClockwise,
            height: 32,
            onPressed: _undoPoint,
          ),
          const SizedBox(width: 9),
        ],
        NocturneButton(
          label: 'Discard',
          horizontalPadding: compact ? 8 : 12,
          onPressed: _saving || !_dirty ? null : _close,
        ),
        const SizedBox(width: 9),
        NocturneButton(
          label: _saving
              ? 'Saving…'
              : compact
              ? 'Save'
              : 'Save masks',
          icon: PhosphorIconsRegular.check,
          variant: NocturneButtonVariant.primary,
          horizontalPadding: compact ? 10 : 12,
          onPressed: _saving || !_dirty || _problem != null ? null : _save,
        ),
      ],
    ),
  );

  Widget _failureStrip(String message) => Container(
    width: double.infinity,
    padding: const EdgeInsets.fromLTRB(20, 10, 20, 10),
    color: Serval.recording.withValues(alpha: 0.12),
    child: Text(
      message,
      style: const TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12.5,
        color: Serval.recordingText,
      ),
    ),
  );

  Widget get _canvasColumn => Padding(
    padding: const EdgeInsets.fromLTRB(20, 18, 20, 18),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _tools,
        const SizedBox(height: 12),
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(9),
            child: DecoratedBox(
              decoration: BoxDecoration(
                color: Serval.tile,
                border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
                borderRadius: BorderRadius.circular(9),
              ),
              child: Stack(
                fit: StackFit.expand,
                children: [
                  MaskCanvas(
                    frame: _still,
                    masks: _masks,
                    draft: _draft,
                    selected: _selected,
                    hidden: _showAll ? _hidden : _allIndices,
                    tool: _tool,
                    snapToEdges: _snapToEdges,
                    onDraftChanged: (points) => setState(() => _draft = points),
                    onCommitDraft: _commitDraft,
                    onMaskChanged: (index, points) => setState(
                      () => _masks = [
                        for (var i = 0; i < _masks.length; i++)
                          if (i == index)
                            _masks[i].copyWith(points: points)
                          else
                            _masks[i],
                      ],
                    ),
                    onSelect: _select,
                  ),
                  if (_showAll) MaskNamePills(masks: _masks, hidden: _hidden),
                  Positioned(
                    right: 14,
                    bottom: 14,
                    child: MaskCanvasStatus(
                      name: _draft.isEmpty
                          ? maskTitle(
                              _current ??
                                  const DetectionMaskSettings(points: []),
                            )
                          : 'Drawing',
                      points: _draft.isEmpty
                          ? (_current?.points.length ?? 0) ~/ 2
                          : _draft.length ~/ 2,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            PhosphorIcon(
              PhosphorIconsRegular.info,
              size: 13,
              color: Nocturne.mix(Nocturne.text, 35),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                'Points are stored as fractions of the frame, so a mask survives a resolution '
                'change. Drag a point to move it, drag an edge’s midpoint to add one.',
                style: settingHelpStyle(),
              ),
            ),
          ],
        ),
      ],
    ),
  );

  Set<int> get _allIndices => {for (var i = 0; i < _masks.length; i++) i};

  Widget get _tools => Row(
    children: [
      SegmentedControl(
        segments: [
          for (final tool in MaskTool.values)
            Segment(
              tool.label,
              icon: tool == MaskTool.draw
                  ? PhosphorIconsRegular.polygon
                  : PhosphorIconsRegular.cursor,
            ),
        ],
        selectedIndex: MaskTool.values.indexOf(_tool),
        height: 32,
        onChanged: (index) => setState(() => _tool = MaskTool.values[index]),
      ),
      const Spacer(),
      _EyeToggle(
        on: _showAll,
        onChanged: (value) => setState(() => _showAll = value),
      ),
      const SizedBox(width: 16),
      Text(
        'Snap to edges',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 55),
        ),
      ),
      const SizedBox(width: 8),
      NocturneToggle(
        value: _snapToEdges,
        compact: true,
        onChanged: (value) => setState(() => _snapToEdges = value),
      ),
    ],
  );

  // ---------------------------------------------------------------- inspector

  Widget get _inspector => DecoratedBox(
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(left: BorderSide(color: Serval.hairline)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 13),
          child: _maskList,
        ),
        Container(height: 1, color: Serval.hairline),
        Expanded(
          child: _current == null
              ? const _NothingSelected()
              : SingleChildScrollView(
                  padding: const EdgeInsets.fromLTRB(16, 15, 16, 15),
                  child: _maskInspector(_current!),
                ),
        ),
      ],
    ),
  );

  Widget get _maskList => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        children: [
          const Text(
            'Masks',
            style: TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: 14.5,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              _maskSummary,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 11.5,
                color: Nocturne.mix(Nocturne.text, 42),
              ),
            ),
          ),
        ],
      ),
      const SizedBox(height: 10),
      for (var i = 0; i < _masks.length; i++) ...[
        if (i > 0) const SizedBox(height: 6),
        _MaskRow(
          mask: _masks[i],
          selected: i == _selected,
          hidden: _hidden.contains(i),
          onTap: () => _select(i),
          onToggleHidden: () => setState(
            () => _hidden.contains(i) ? _hidden.remove(i) : _hidden.add(i),
          ),
          onDelete: () => _delete(i),
        ),
      ],
    ],
  );

  Widget _maskInspector(DetectionMaskSettings mask) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        children: [
          Expanded(
            child: Text(
              maskTitle(mask),
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 13.5,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
          ),
          if (_dirty) ...[
            const SizedBox(width: 8),
            const SettingBadge('not saved', accent: true),
          ],
        ],
      ),
      const SizedBox(height: 12),
      NocturneField(
        label: 'Name',
        controller: _name,
        onChanged: (text) => _replaceSelected(
          mask.copyWith(name: text.trim().isEmpty ? null : text.trim()),
        ),
      ),
      const SizedBox(height: 13),
      _IgnoreCallout(everything: mask.classes == null),
      const SizedBox(height: 13),
      Row(
        children: [
          Expanded(
            child: Text(
              'Ignore only these',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 72),
              ),
            ),
          ),
          if (mask.classes != null)
            SettingsLinkText(
              'Everything',
              onTap: () => _replaceSelected(mask.copyWith(classes: null)),
            ),
        ],
      ),
      const SizedBox(height: 8),
      // Null is the *everything* state, so an emptied chip row means the same as never having
      // narrowed it — which is what the Server reads an empty array as too.
      LabelChipList(
        value: mask.classes ?? const [],
        fallback: _classChoices,
        onChanged: (items) => _replaceSelected(
          mask.copyWith(classes: items.isEmpty ? null : items),
        ),
      ),
      const SizedBox(height: 8),
      Text(
        'Pick labels to ignore just those — a pavement mask can ignore cars but still report '
        'people. Left as everything, the shape silences all of it, and is the only form that '
        'stops the work before it happens.',
        style: settingHelpStyle(),
      ),
    ],
  );

  /// The one-line count under the heading.
  ///
  /// Split by form, because the two cost different amounts of work. A shape left as everything
  /// stops the detector being pointed at it; one narrowed to labels cannot, since the label it
  /// filters on does not exist until the model has run, so it can only discard the answer
  /// afterwards. An operator choosing between them deserves to see which they have.
  String get _maskSummary {
    if (_masks.isEmpty) return 'none yet — click to place points';

    final ignored = _masks.where((mask) => mask.classes == null).length;
    if (ignored == _masks.length) {
      return '$ignored ${ignored == 1 ? 'area' : 'areas'} ignored';
    }
    if (ignored == 0) {
      return '${_masks.length} filtered by class';
    }
    return '$ignored ignored, ${_masks.length - ignored} filtered by class';
  }

  /// What this camera is actually looking for, offered as the labels worth naming: its own
  /// override if it has one, otherwise the Server's list.
  List<String> get _classChoices {
    final own = _record?.detectionTuning?.classes;
    if (own != null && own.isNotEmpty) return own;
    return _defaults.listFor(CameraSetting.detectionClasses);
  }
}

class _UndoPointIntent extends Intent {
  const _UndoPointIntent();
}

class _AbandonDraftIntent extends Intent {
  const _AbandonDraftIntent();
}

/// A canvas rule that stands down while the caret is in a field.
///
/// The rules hang over the whole screen because the whole screen is the drawing surface — but the
/// inspector has a name box in it, and Backspace there is a character, not a point. `Shortcuts`
/// asks the nearest enclosing `Actions` first and reaches the editing shortcuts the framework
/// installs above the app *only* when the action it finds is disabled: an action that merely
/// declines to do anything still eats the key. So being disabled is the whole of it.
class _CanvasAction<T extends Intent> extends CallbackAction<T> {
  _CanvasAction({required super.onInvoke});

  @override
  bool isEnabled(T intent) => !_caretIsInAField;
}

/// Whether the keyboard is currently for typing.
///
/// [EditableText] builds its focus node inside itself, so a focused node with one above it is a
/// field with the caret — every box in the app is an [EditableText], including the class chips'.
bool get _caretIsInAField {
  final focused = FocusManager.instance.primaryFocus?.context;
  return focused != null &&
      focused.findAncestorWidgetOfExactType<EditableText>() != null;
}

class _BackToCamera extends StatelessWidget {
  const _BackToCamera({required this.name, required this.onTap});

  final String name;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          PhosphorIcon(
            PhosphorIconsRegular.arrowLeft,
            size: 15,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
          const SizedBox(width: 8),
          Text(
            name,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 13,
              color: Nocturne.mix(Nocturne.text, 72),
            ),
          ),
        ],
      ),
    ),
  );
}

/// When the still was grabbed. The picture is live underneath, so this says which moment the
/// polygons are being drawn against.
class _StillChip extends StatelessWidget {
  const _StillChip({required this.takenAt});

  final DateTime? takenAt;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(4),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
    ),
    child: Text(
      takenAt == null ? 'no still yet' : 'still from ${_clock(takenAt!)}',
      style: monoStyle(fontSize: 11, color: Nocturne.mix(Nocturne.text, 45)),
    ),
  );

  static String _clock(DateTime at) {
    final hour = at.hour % 12 == 0 ? 12 : at.hour % 12;
    final minute = at.minute.toString().padLeft(2, '0');
    return '$hour:$minute ${at.hour < 12 ? 'am' : 'pm'}';
  }
}

class _EyeToggle extends StatelessWidget {
  const _EyeToggle({required this.on, required this.onChanged});

  final bool on;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: () => onChanged(!on),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          PhosphorIcon(
            on ? PhosphorIconsRegular.eye : PhosphorIconsRegular.eyeClosed,
            size: 14,
            color: Nocturne.mix(Nocturne.text, on ? 55 : 35),
          ),
          const SizedBox(width: 7),
          Text(
            'Show all masks',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, on ? 55 : 35),
            ),
          ),
        ],
      ),
    ),
  );
}

class _MaskRow extends StatelessWidget {
  const _MaskRow({
    required this.mask,
    required this.selected,
    required this.hidden,
    required this.onTap,
    required this.onToggleHidden,
    required this.onDelete,
  });

  final DetectionMaskSettings mask;
  final bool selected;
  final bool hidden;
  final VoidCallback onTap;
  final VoidCallback onToggleHidden;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      behavior: HitTestBehavior.opaque,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 8),
        decoration: BoxDecoration(
          color: selected ? Nocturne.mix(Nocturne.accent, 14) : null,
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: selected
                ? Nocturne.mix(Nocturne.accent, 45)
                : Nocturne.mix(Nocturne.text, 8),
          ),
        ),
        child: Opacity(
          opacity: hidden ? 0.5 : 1,
          child: Row(
            children: [
              Container(
                width: 26,
                height: 20,
                decoration: BoxDecoration(
                  color: Serval.alert.withValues(alpha: 0.18),
                  border: Border.all(color: Serval.alert),
                  borderRadius: BorderRadius.circular(3),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      maskTitle(mask),
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 12.5,
                        color: Nocturne.mix(Nocturne.text, selected ? 90 : 78),
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      'Ignore · ${maskScope(mask)} · ${maskPointCount(mask)}',
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 11,
                        color: Nocturne.mix(Nocturne.text, 40),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 6),
              NocturneButton.icon(
                icon: hidden
                    ? PhosphorIconsRegular.eyeClosed
                    : PhosphorIconsRegular.eye,
                height: 26,
                onPressed: onToggleHidden,
              ),
              const SizedBox(width: 4),
              NocturneButton.icon(
                icon: PhosphorIconsRegular.trash,
                variant: NocturneButtonVariant.danger,
                height: 26,
                onPressed: onDelete,
              ),
            ],
          ),
        ),
      ),
    ),
  );
}

/// The one thing about masks people get wrong, said where a mask is being edited.
class _IgnoreCallout extends StatelessWidget {
  const _IgnoreCallout({required this.everything});

  /// Whether this shape is left as everything, which is the only form that stops the work before
  /// it happens. Narrowed to labels, the detector still has to look in order to find out.
  final bool everything;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 10),
    decoration: BoxDecoration(
      color: Serval.alert.withValues(alpha: 0.1),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Serval.alert.withValues(alpha: 0.5)),
    ),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        PhosphorIcon(
          PhosphorIconsFill.eyeClosed,
          size: 14,
          color: Serval.alertText,
        ),
        const SizedBox(width: 9),
        Expanded(
          child: Text(
            everything
                ? 'Anything standing in this shape is ignored. What counts is where a thing '
                      'meets the ground, so someone whose head or shoulders cross the edge is '
                      'still reported as long as their feet are outside. The area is still '
                      'filmed and still kept.'
                : 'Only the labels below are ignored, and only when they stand in this shape — '
                      'judged by where they meet the ground, not by overlapping the edge. '
                      'Everything else here is still detected. The area is still filmed and '
                      'still kept.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Serval.alertText,
            ),
          ),
        ),
      ],
    ),
  );
}

class _NothingSelected extends StatelessWidget {
  const _NothingSelected();

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Text(
        'Click on the frame to place points. Click the first one again to close the shape.',
        textAlign: TextAlign.center,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
      ),
    ),
  );
}
