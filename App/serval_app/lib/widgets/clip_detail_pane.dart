import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../models/saved_clip.dart';
import '../theme/nocturne.dart';
import 'clip_detail.dart';
import 'clip_player.dart';
import 'nocturne_button.dart';
import 'nocturne_dialog.dart';
import 'nocturne_field.dart';

/// One clip, played and described — 13a's right-hand column.
///
/// The one place in the clips feature that reaches for the repository, which is why it is a
/// `ConsumerWidget` and everything under it takes what it needs as parameters. It owns three
/// asynchronous things at once — the detail, the video URL and whatever mutation is in flight —
/// and keeping them together is what stops a rename landing on a clip the panel has moved off.
class ClipDetailPane extends ConsumerStatefulWidget {
  const ClipDetailPane({
    super.key,
    required this.clip,
    required this.onChanged,
    this.compact = false,
    this.onDeleted,
  });

  final SavedClip clip;

  /// Something about this clip changed on the Server — the list should re-read.
  final VoidCallback onChanged;

  /// The clip is gone. On a phone that means leaving the screen it was on.
  final VoidCallback? onDeleted;

  final bool compact;

  @override
  ConsumerState<ClipDetailPane> createState() => ClipDetailPaneState();
}

class ClipDetailPaneState extends ConsumerState<ClipDetailPane> {
  late final ServalRepository _repository = ref.read(repositoryProvider);
  final _playerKey = GlobalKey<ClipPlayerState>();

  Future<SavedClipDetail>? _detail;
  Future<Uri?>? _source;
  Future<Map<String, Uri>>? _poster;

  /// The last thing that went wrong, shown in place rather than thrown away.
  String? _failure;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    _detail = _repository.savedClip(widget.clip.id);
    _source = _repository.savedClipUrl(widget.clip.id);
    _poster = _repository.clipPosterUrls([widget.clip.id]);
  }

  /// The signed-in account, for whether this clip is theirs to change.
  ///
  /// Read here and passed down, per the rule that Riverpod stops at the screens — and the Server
  /// enforces the same rule regardless, so this only avoids offering what would come back 403.
  bool get _mayEdit => widget.clip.mayEdit(
    user: ref.watch(currentUsernameProvider),
    isAdmin: ref.watch(isAdminProvider),
  );

  @override
  Widget build(BuildContext context) => FutureBuilder<SavedClipDetail>(
    future: _detail,
    builder: (context, snapshot) {
      final detail = snapshot.data ?? SavedClipDetail(clip: widget.clip);

      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          FutureBuilder<Uri?>(
            future: _source,
            builder: (context, source) => FutureBuilder<Map<String, Uri>>(
              future: _poster,
              builder: (context, poster) => SizedBox(
                // 13a's 209px column stage and 13c's 232px phone stage.
                height: widget.compact ? 232 : 209,
                child: ClipPlayer(
                  key: _playerKey,
                  clip: widget.clip,
                  source: source.data,
                  // The clip's own camera's level, so a clip cut from a quiet camera plays at the
                  // volume that camera was last watched at.
                  volume: _repository.playbackVolumeFor(widget.clip.cameraId),
                  // Null once the camera is gone — a saved clip deliberately outlives the footage it
                  // was cut from, and can outlive the camera too.
                  gateRms: _repository
                      .cameraById(widget.clip.cameraId)
                      ?.playbackGateRms,
                  // What the stage shows before anything is played: the clip's own frame under the
                  // play button, which is what makes 13a's column a picture rather than a panel.
                  poster: poster.data?[widget.clip.id],
                  compact: widget.compact,
                ),
              ),
            ),
          ),
          Expanded(
            child: SingleChildScrollView(
              padding: EdgeInsets.fromLTRB(
                16,
                widget.compact ? 14 : 16,
                16,
                20,
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: 14,
                children: [
                  if (_failure case final failure?)
                    Text(
                      failure,
                      style: TextStyle(
                        fontSize: 12.5,
                        height: 1.45,
                        color: Nocturne.mix(Nocturne.text, 60),
                      ),
                    ),
                  ClipDetail(
                    detail: detail,
                    compact: widget.compact,
                    // Wired only once the player exists, so the transcript follows the picture
                    // from the first frame rather than from the first rebuild after it.
                    playing: _playerKey.currentState?.position,
                    onDownload: _download,
                    onShare: _repository.canShare ? _share : null,
                    onRename: _mayEdit ? _rename : null,
                    onDelete: _mayEdit ? _delete : null,
                  ),
                ],
              ),
            ),
          ),
        ],
      );
    },
  );

  Future<void> _download() async {
    setState(() => _failure = null);
    try {
      await _repository.downloadSavedClip(widget.clip.id);
    } on Object catch (e) {
      if (mounted) setState(() => _failure = '$e');
    }
  }

  Future<void> _share() async {
    setState(() => _failure = null);
    try {
      await _repository.shareSavedClip(widget.clip.id);
    } on Object catch (e) {
      if (mounted) setState(() => _failure = '$e');
    }
  }

  /// Renaming and deleting, exposed so the phone's ⋮ can drive them.
  ///
  /// Public rather than duplicated in [ClipScreen]: the confirmation wording, the failure handling
  /// and the reload after are the same on both layouts, and the only difference is which chrome
  /// offers them — the pencil and trash on a desktop, the overflow menu on a phone.
  Future<void> rename() => _rename();

  Future<void> delete() => _delete();

  Future<void> _rename() async {
    final name = await showNocturneDialog<String>(
      context: context,
      builder: (context) => _RenameDialog(name: widget.clip.name),
    );

    if (name == null || !mounted) return;

    setState(() => _failure = null);
    try {
      await _repository.renameClip(widget.clip.id, name);
      widget.onChanged();
    } on Object catch (e) {
      if (mounted) setState(() => _failure = '$e');
    }
  }

  Future<void> _delete() async {
    final confirmed = await showNocturneDialog<bool>(
      context: context,
      builder: (context) => _DeleteClipDialog(name: widget.clip.name),
    );

    if (confirmed != true || !mounted) return;

    setState(() => _failure = null);
    try {
      await _repository.deleteClip(widget.clip.id);
      widget.onChanged();
      widget.onDeleted?.call();
    } on Object catch (e) {
      if (mounted) setState(() => _failure = '$e');
    }
  }
}

class _RenameDialog extends StatefulWidget {
  const _RenameDialog({required this.name});

  final String name;

  @override
  State<_RenameDialog> createState() => _RenameDialogState();
}

class _RenameDialogState extends State<_RenameDialog> {
  late final _name = TextEditingController(text: widget.name);

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Rename this clip',
    width: 420,
    body: NocturneField(
      label: 'Name',
      controller: _name,
      onChanged: (_) => setState(() {}),
    ),
    actions: [
      NocturneButton(
        label: 'Cancel',
        onPressed: () => Navigator.of(context).pop(),
      ),
      NocturneButton(
        label: 'Rename',
        variant: NocturneButtonVariant.primary,
        onPressed: _name.text.trim().isEmpty
            ? null
            : () => Navigator.of(context).pop(_name.text.trim()),
      ),
    ],
  );
}

/// Deleting a clip, which is the only way one ever goes away.
///
/// Confirmed rather than immediate, and without the typed-name ceremony a camera gets: a clip is
/// one file rather than a camera's whole history, so the cost of a misclick is smaller — but it is
/// still the only copy of footage that has already rolled off everywhere else, which is why there
/// is a confirmation at all.
class _DeleteClipDialog extends StatelessWidget {
  const _DeleteClipDialog({required this.name});

  final String name;

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Delete this clip?',
    width: 420,
    body: Text(
      '“$name” and its video will be removed. The footage it was taken from has probably already '
      'rolled off, so this is likely the only copy.',
      style: TextStyle(
        fontSize: 13.5,
        height: 1.5,
        color: Nocturne.mix(Nocturne.text, 75),
      ),
    ),
    actions: [
      NocturneButton(
        label: 'Keep it',
        onPressed: () => Navigator.of(context).pop(false),
      ),
      NocturneButton(
        label: 'Delete',
        variant: NocturneButtonVariant.danger,
        onPressed: () => Navigator.of(context).pop(true),
      ),
    ],
  );
}
