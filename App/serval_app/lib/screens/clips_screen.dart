import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/saved_clip.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/activity_filter_panel.dart';
import '../widgets/clip_card.dart';
import '../widgets/clip_detail_pane.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/section_kicker.dart';

/// How wide a card is allowed to get before the grid spends the room on another column instead.
///
/// Three columns at the 1440px the screen was drawn at, four past ~1500 — a thumbnail this size is
/// already big enough to recognise a face in, and past it the extra pixels do nothing.
const double _cardWidth = 360;

/// Saved clips — a peer of the wall.
///
/// The only footage in Serval that does not roll off, so it gets a place of its own beside the wall
/// rather than a page inside settings. On a phone it splits in two: this is the list, and
/// [ClipScreen] is one clip.
class ClipsScreen extends ConsumerStatefulWidget {
  const ClipsScreen({super.key, this.onBack, this.onOpenClip});

  /// The phone's way back to the wall. Null on a desktop, where the rail is how you left.
  final VoidCallback? onBack;

  /// The phone opens a clip as its own screen; wide, it opens in the column beside the list.
  final ValueChanged<String>? onOpenClip;

  @override
  ConsumerState<ClipsScreen> createState() => _ClipsScreenState();
}

class _ClipsScreenState extends ConsumerState<ClipsScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);
  final _search = TextEditingController();

  Future<List<SavedClip>>? _clips;
  String _query = '';
  Set<String> _cameraIds = const {};

  /// Poster URLs for what is currently listed, fetched once per list rather than once per card.
  Map<String, Uri> _posters = const {};

  /// Which clip the detail column is showing. Null opens the newest, so the column is never a
  /// blank 372px waiting to be told what to do.
  String? _openId;

  /// Whether the phone's search field is showing. Wide it always is; at 412px the app bar has room
  /// for a title or a field, and the title is what tells you where you are.
  bool _searching = false;

  @override
  void initState() {
    super.initState();

    // The first read is assigned directly rather than through [_reload], which calls `setState` —
    // there is no state to change yet, and doing it here is what makes the field non-null for the
    // first build.
    _clips = _repository.savedClips();
    unawaited(_loadPosters(_clips!));
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  void _reload() {
    final clips = _repository.savedClips(
      query: _query.isEmpty ? null : _query,
      // The filter's empty set means *everything*, as it does on the wall — so a single camera is
      // the only case that narrows the request.
      cameraId: _cameraIds.length == 1 ? _cameraIds.first : null,
    );

    // A block body, not an arrow: `=> _clips = clips` *returns* the assigned Future, and setState
    // rejects a callback that returns one.
    setState(() {
      _clips = clips;
    });

    // Chained rather than awaited alongside: the poster URLs are keyed by clip id, so there is
    // nothing to ask for until the list has arrived. A failure leaves the stripes, which is a
    // worse screen rather than a broken one.
    unawaited(_loadPosters(clips));
  }

  Future<void> _loadPosters(Future<List<SavedClip>> clips) async {
    try {
      final list = await clips;
      final posters = await _repository.clipPosterUrls([
        for (final c in list) c.id,
      ]);
      if (mounted) setState(() => _posters = posters);
    } on Object {
      if (mounted) setState(() => _posters = const {});
    }
  }

  /// The clip's own frame, where the Server made one.
  ///
  /// `Image.network` rather than the authenticated client: the browser fetches this itself and
  /// cannot set a header, which is why the URL carries a stream token instead. A poster that fails
  /// to load falls through to the camera's stripe rather than an error box — a clip without a
  /// thumbnail still plays.
  ///
  /// The card's shape is the design's 16:9 and the frame's is the camera's, so the picture is
  /// contained and the leftover painted: a 4:3 camera reads as a 4:3 camera in the grid rather than
  /// as a crop of one.
  Widget? _posterFor(SavedClip clip) {
    final url = _posters[clip.id];
    if (url == null) return null;

    return Image.network(
      url.toString(),
      fit: BoxFit.contain,
      // The bars get the video ground, and only once there is a picture for them to be the edge of
      // — painted from the first build they would blank the camera's stripe for the length of a
      // fetch, saying this clip has no frame where the stripe says one is coming.
      frameBuilder: (context, child, frame, _) =>
          frame == null ? child : ColoredBox(color: Serval.tile, child: child),
      errorBuilder: (context, error, stack) => const SizedBox.shrink(),
    );
  }

  @override
  Widget build(BuildContext context) => FutureBuilder<List<SavedClip>>(
    future: _clips,
    builder: (context, snapshot) {
      final clips = snapshot.data ?? const <SavedClip>[];
      final loading = snapshot.connectionState == ConnectionState.waiting;

      // Client-side, because the Server narrows to one camera and the chips allow several. The
      // pool is a library rather than a feed, so this is tens of rows and not thousands.
      final shown = _cameraIds.isEmpty
          ? clips
          : [
              for (final clip in clips)
                if (_cameraIds.contains(clip.cameraId)) clip,
            ];

      return isCompact(context)
          ? _buildCompact(shown, loading: loading)
          : _buildDesktop(shown, loading: loading);
    },
  );

  // ------------------------------------------------------------------ 13a

  Widget _buildDesktop(List<SavedClip> clips, {required bool loading}) {
    final open = _openClip(clips);

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _header(clips),
          Expanded(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(child: _list(clips, loading: loading, compact: false)),
                if (open != null)
                  SizedBox(
                    // The same 372px the single-camera view gives its panel, so a clip's detail and
                    // a camera's detail are the same column in two places rather than two columns.
                    width: Serval.detailPanelWidth,
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: Serval.rail,
                        border: Border(
                          left: BorderSide(color: Serval.hairline),
                        ),
                      ),
                      // Keyed on the clip, so opening another rebuilds the player and the panel
                      // rather than pouring a second clip's transcript into the first's state.
                      child: ClipDetailPane(
                        key: ValueKey(open.id),
                        clip: open,
                        onChanged: _reload,
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

  SavedClip? _openClip(List<SavedClip> clips) {
    if (clips.isEmpty) return null;

    for (final clip in clips) {
      if (clip.id == _openId) return clip;
    }
    return clips.first;
  }

  Widget _header(List<SavedClip> clips) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      spacing: 16,
      children: [
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          spacing: 2,
          children: [
            const Text(
              'Saved clips',
              style: TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 17,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
            Text(
              _subtitle(clips),
              style: TextStyle(
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
          ],
        ),
        const Spacer(),
        SizedBox(
          width: 300,
          child: ActivitySearchField(
            controller: _search,
            hint: 'Search names and what was said',
            onChanged: _onSearch,
          ),
        ),
        _cameraFilter(clips),
      ],
    ),
  );

  /// `14 clips · kept until you delete them` — the count, and the one fact that makes these
  /// different from everything else on the machine.
  String _subtitle(List<SavedClip> clips) {
    final count = clips.length;
    final noun = count == 1 ? 'clip' : 'clips';
    return '$count $noun · kept until you delete them';
  }

  void _onSearch(String value) {
    _query = value;
    _reload();
  }

  /// The design's labelled chip rather than the feed's funnel-and-badge.
  ///
  /// The feed's button sits beside a search field in a 376px column and can only afford a glyph.
  /// This header has the room, and a chip that says which cameras are in view answers the question
  /// without being pressed.
  Widget _cameraFilter(List<SavedClip> clips) {
    final names = <String, String>{
      for (final clip in clips) clip.cameraId: clip.cameraName,
    };

    final label = switch (_cameraIds.length) {
      0 => 'All cameras',
      1 => names[_cameraIds.first] ?? '1 camera',
      final n => '$n cameras',
    };

    return GestureDetector(
      onTap: () => _pickCameras(names),
      behavior: HitTestBehavior.opaque,
      child: Container(
        height: 34,
        padding: const EdgeInsets.symmetric(horizontal: 12),
        decoration: BoxDecoration(
          color: _cameraIds.isEmpty ? null : Nocturne.mix(Nocturne.accent, 14),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: _cameraIds.isEmpty
                ? Nocturne.mix(Nocturne.text, 14)
                : Nocturne.mix(Nocturne.accent, 55),
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          spacing: 7,
          children: [
            Icon(
              PhosphorIconsRegular.funnel,
              size: 15,
              color: _cameraIds.isEmpty
                  ? Nocturne.mix(Nocturne.text, 72)
                  : Nocturne.accent300,
            ),
            Text(
              label,
              style: TextStyle(
                fontSize: 13,
                color: _cameraIds.isEmpty
                    ? Nocturne.mix(Nocturne.text, 72)
                    : Nocturne.text,
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _pickCameras(Map<String, String> names) async {
    final chosen = await showNocturneDialog<Set<String>>(
      context: context,
      builder: (context) => _CameraPicker(names: names, chosen: _cameraIds),
    );

    if (chosen == null || !mounted) return;
    setState(() => _cameraIds = chosen);
    _reload();
  }

  // ------------------------------------------------------------------ 13b

  Widget _buildCompact(List<SavedClip> clips, {required bool loading}) =>
      DecoratedBox(
        decoration: const BoxDecoration(color: Serval.panel),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            CompactAppBar(
              title: 'Saved clips',
              subtitle: '${clips.length} · kept until you delete them',
              onBack: widget.onBack,
              actions: [
                CompactBarAction(
                  icon: _searching
                      ? PhosphorIconsRegular.x
                      : PhosphorIconsRegular.magnifyingGlass,
                  tooltip: _searching ? 'Stop searching' : 'Search',
                  onPressed: () => setState(() {
                    _searching = !_searching;
                    if (!_searching && _query.isNotEmpty) {
                      _search.clear();
                      _onSearch('');
                    }
                  }),
                ),
              ],
            ),
            if (_searching)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                child: ActivitySearchField(
                  controller: _search,
                  hint: 'Search names and what was said',
                  onChanged: _onSearch,
                  compact: true,
                ),
              ),
            Expanded(child: _list(clips, loading: loading, compact: true)),
          ],
        ),
      );

  // ----------------------------------------------------------------- shared

  Widget _list(
    List<SavedClip> clips, {
    required bool loading,
    required bool compact,
  }) {
    if (clips.isEmpty) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Text(
            loading
                ? ''
                : _query.isNotEmpty
                ? 'Nothing saved matches “$_query”.'
                : 'No clips yet. Press Save clip while watching a camera and the range you '
                      'choose will be kept here.',
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
    final groups = <String, List<SavedClip>>{};
    for (final clip in clips) {
      groups.putIfAbsent(clipGroupLabel(clip.from, now), () => []).add(clip);
    }

    return SingleChildScrollView(
      padding: compact
          ? const EdgeInsets.fromLTRB(16, 14, 16, 20)
          : const EdgeInsets.fromLTRB(22, 18, 18, 22),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: compact ? 14 : 18,
        children: [
          for (final group in groups.entries)
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 10,
              children: [
                if (compact)
                  SectionKicker(group.key, padding: EdgeInsets.zero)
                else
                  Row(
                    spacing: 10,
                    children: [
                      SectionKicker(group.key, padding: EdgeInsets.zero),
                      Expanded(
                        child: Container(
                          height: 1,
                          color: Nocturne.mix(Nocturne.text, 7),
                        ),
                      ),
                    ],
                  ),
                if (compact)
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    spacing: 10,
                    children: [
                      for (final clip in group.value)
                        ClipCard.row(
                          clip: clip,
                          poster: _posterFor(clip),
                          onOpen: () => _open(clip),
                        ),
                    ],
                  )
                else
                  _grid(group.value, openId: _openClip(clips)?.id),
              ],
            ),
        ],
      ),
    );
  }

  /// The card grid, in as many columns as the window has room for.
  ///
  /// A fixed column count and a fixed tile shape is what cropped the picture: past the width the
  /// design was drawn at the tile kept growing sideways while the thumbnail kept its height, and
  /// `cover` answered that by taking a band out of the middle of the frame. Here a card is a width
  /// and the thumbnail's 16:9 decides the height, so the frame always arrives whole — and a wider
  /// window buys another column rather than three enormous cards.
  Widget _grid(List<SavedClip> clips, {String? openId}) => LayoutBuilder(
    builder: (context, constraints) {
      const spacing = 12.0;
      final columns = math.max(1, (constraints.maxWidth / _cardWidth).ceil());

      // Floored, because a fraction of a pixel over the line is a card on a row of its own.
      final width = ((constraints.maxWidth - spacing * (columns - 1)) / columns)
          .floorToDouble();

      return Wrap(
        spacing: spacing,
        runSpacing: spacing,
        children: [
          for (final clip in clips)
            SizedBox(
              width: width,
              child: ClipCard(
                clip: clip,
                poster: _posterFor(clip),
                selected: clip.id == openId,
                onOpen: () => _open(clip),
              ),
            ),
        ],
      );
    },
  );

  void _open(SavedClip clip) {
    if (widget.onOpenClip != null && isCompact(context)) {
      widget.onOpenClip!(clip.id);
      return;
    }

    setState(() => _openId = clip.id);
  }
}

/// Which cameras the list is narrowed to.
///
/// The empty set means everything, as it does on the wall's filter — so *All* clears rather than
/// selecting, and there is no sentinel entry standing for "all of them".
class _CameraPicker extends StatefulWidget {
  const _CameraPicker({required this.names, required this.chosen});

  final Map<String, String> names;
  final Set<String> chosen;

  @override
  State<_CameraPicker> createState() => _CameraPickerState();
}

class _CameraPickerState extends State<_CameraPicker> {
  late Set<String> _chosen = {...widget.chosen};

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Which camera',
    width: 360,
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      spacing: 4,
      children: [
        Align(
          alignment: Alignment.centerLeft,
          child: ActivityLink(
            'All ${widget.names.length}',
            onTap: () => setState(() => _chosen = {}),
          ),
        ),
        for (final entry in widget.names.entries)
          _CameraRow(
            label: entry.value,
            checked: _chosen.contains(entry.key),
            onChanged: () => setState(() {
              _chosen.contains(entry.key)
                  ? _chosen.remove(entry.key)
                  : _chosen.add(entry.key);
            }),
          ),
      ],
    ),
    actions: [
      NocturneButton(
        label: 'Done',
        variant: NocturneButtonVariant.primary,
        onPressed: () => Navigator.of(context).pop(_chosen),
      ),
    ],
  );
}

class _CameraRow extends StatelessWidget {
  const _CameraRow({
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
          // Hand-drawn rather than Material's Checkbox, which brings a ripple and a fill the
          // system forbids — the same reason the activity filter draws its own.
          Container(
            width: 16,
            height: 16,
            decoration: BoxDecoration(
              color: checked ? Nocturne.mix(Nocturne.accent, 25) : null,
              borderRadius: BorderRadius.circular(4),
              border: Border.all(
                color: checked
                    ? Nocturne.accent
                    : Nocturne.mix(Nocturne.text, 20),
              ),
            ),
            child: checked
                ? const Icon(
                    PhosphorIconsBold.check,
                    size: 11,
                    color: Nocturne.accent300,
                  )
                : null,
          ),
          Expanded(
            child: Text(
              label,
              style: const TextStyle(fontSize: 13.5, color: Nocturne.text),
            ),
          ),
        ],
      ),
    ),
  );
}
