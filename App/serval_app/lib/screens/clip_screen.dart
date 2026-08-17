import 'package:flutter/material.dart'
    show PopupMenuButton, PopupMenuItem, PopupMenuPosition;
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/saved_clip.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/clip_detail_pane.dart';
import '../widgets/compact_app_bar.dart';

/// One clip on a phone — 13c.
///
/// Its own screen rather than a sheet over the list, because a clip is something you watch: it
/// wants the whole 412px, and a list peeking round the edge of a video is a list competing with it.
class ClipScreen extends ConsumerStatefulWidget {
  const ClipScreen({super.key, required this.clipId, required this.onBack});

  final String clipId;
  final VoidCallback onBack;

  @override
  ConsumerState<ClipScreen> createState() => _ClipScreenState();
}

class _ClipScreenState extends ConsumerState<ClipScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);
  final _paneKey = GlobalKey<ClipDetailPaneState>();

  late Future<SavedClipDetail> _detail = _repository.savedClip(widget.clipId);

  @override
  Widget build(BuildContext context) => FutureBuilder<SavedClipDetail>(
    future: _detail,
    builder: (context, snapshot) {
      final clip = snapshot.data?.clip;

      return DecoratedBox(
        decoration: const BoxDecoration(color: Serval.panel),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            CompactAppBar(
              title: clip?.name ?? 'Clip',
              subtitle: clip == null
                  ? null
                  : '${clip.cameraName} · ${weekdayLabel(clip.from)} ${dayLabel(clip.from)}',
              onBack: widget.onBack,
              actions: [
                // Rename and delete live behind the ⋮, following 7b — the same reasoning that put
                // *Remove camera* there. Neither is something to reach for while watching.
                if (clip != null && _mayEdit(clip))
                  _Overflow(paneKey: _paneKey),
              ],
            ),
            Expanded(
              child: clip == null
                  ? const SizedBox.shrink()
                  : ClipDetailPane(
                      key: _paneKey,
                      clip: clip,
                      compact: true,
                      onChanged: () => setState(
                        () => _detail = _repository.savedClip(widget.clipId),
                      ),
                      onDeleted: widget.onBack,
                    ),
            ),
          ],
        ),
      );
    },
  );

  bool _mayEdit(SavedClip clip) => clip.mayEdit(
    user: ref.watch(currentUsernameProvider),
    isAdmin: ref.watch(isAdminProvider),
  );
}

/// The ⋮, driving the pane's own rename and delete rather than duplicating them.
class _Overflow extends StatelessWidget {
  const _Overflow({required this.paneKey});

  final GlobalKey<ClipDetailPaneState> paneKey;

  @override
  Widget build(BuildContext context) => PopupMenuButton<int>(
    tooltip: 'More',
    position: PopupMenuPosition.under,
    color: Serval.rail,
    icon: const Icon(
      PhosphorIconsRegular.dotsThreeVertical,
      size: 20,
      color: Nocturne.text,
    ),
    itemBuilder: (context) => [
      PopupMenuItem(
        value: 0,
        onTap: () => paneKey.currentState?.rename(),
        child: const Text(
          'Rename',
          style: TextStyle(fontSize: 14, color: Nocturne.text),
        ),
      ),
      PopupMenuItem(
        value: 1,
        onTap: () => paneKey.currentState?.delete(),
        child: Text(
          'Delete',
          style: TextStyle(fontSize: 14, color: Serval.recordingText),
        ),
      ),
    ],
  );
}
