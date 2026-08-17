import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/time_labels.dart';
import '../models/alert.dart';
import '../theme/nocturne.dart';
import 'alert_preview.dart';
import 'nocturne_button.dart';

/// One alert, in the 372px column beside the queue.
///
/// **Two verbs, and only one of them is always there.** The preview answers "what was that" without
/// leaving the screen. *Watch* answers "and then what" by opening the camera's recording at that
/// second, which is where trimming, saving and export already live — so there is deliberately no
/// export here. On a camera that was not recording there is no recording to open and the button is
/// simply absent; the preview still plays, which is the whole reason it is cut from the detect
/// stream rather than from the archive.
class AlertDetailPane extends ConsumerStatefulWidget {
  const AlertDetailPane({
    super.key,
    required this.alert,
    this.poster,
    this.onWatch,
    this.onDismiss,
    this.compact = false,
  });

  final Alert alert;
  final Uri? poster;

  /// Opens the camera's recording at this second. Null when there is none.
  final VoidCallback? onWatch;

  final VoidCallback? onDismiss;

  /// The phone's taller stage and full-width buttons.
  final bool compact;

  @override
  ConsumerState<AlertDetailPane> createState() => _AlertDetailPaneState();
}

class _AlertDetailPaneState extends ConsumerState<AlertDetailPane> {
  Uri? _clip;

  @override
  void initState() {
    super.initState();
    unawaited(_loadClip());
  }

  /// Asks again once the clip exists.
  ///
  /// An alert opened from a notification is built while its clip is still being cut, so the call in
  /// [initState] turns back at the guard below and there is nothing to play. The screen above
  /// rebuilds this with the settled alert when the Server republishes it; that is the only moment
  /// the answer changes, so the comparison is against the state rather than the whole alert.
  @override
  void didUpdateWidget(AlertDetailPane old) {
    super.didUpdateWidget(old);
    if (widget.alert.clipState != old.alert.clipState) {
      unawaited(_loadClip());
    }
  }

  Future<void> _loadClip() async {
    if (!widget.alert.clipState.playable) return;

    try {
      final url = await ref
          .read(repositoryProvider)
          .alertClipUrl(widget.alert.id);
      if (mounted) setState(() => _clip = url);
    } on Object {
      // The still is still worth showing. A preview that cannot be fetched leaves the frame the
      // alert fired on, which is what the card had before previews existed at all.
    }
  }

  @override
  Widget build(BuildContext context) {
    final alert = widget.alert;
    final repository = ref.read(repositoryProvider);

    // The alert's own camera, for its gate. Null for an alert whose camera has since been removed,
    // which leaves the clip ungated — the level still applies, since that is kept per camera id
    // here rather than on the record.
    final camera = repository.cameraById(alert.cameraId);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: widget.compact ? 232 : 209,
          child: AlertPreview(
            alert: alert,
            clip: _clip,
            poster: widget.poster,
            showFiredLabel: !widget.compact,
            volume: repository.playbackVolumeFor(alert.cameraId),
            gateRms: camera?.playbackGateRms,
          ),
        ),
        Padding(
          padding: EdgeInsets.fromLTRB(widget.compact ? 16 : 14, 14, 16, 0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: 14,
            children: [
              if (!widget.compact) _heading(alert),
              _actions(alert),
              if (!widget.compact && alert.recorded == true) _caption(alert),
            ],
          ),
        ),
      ],
    );
  }

  /// The title word for word, and when — with seconds, unlike the row.
  Widget _heading(Alert alert) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    spacing: 3,
    children: [
      Text(
        alert.title,
        style: const TextStyle(
          fontFamily: Nocturne.fontHeading,
          fontSize: 16,
          fontWeight: Nocturne.headingWeight,
          color: Nocturne.text,
        ),
      ),
      Text(
        '${alert.cameraName} · ${clipGroupLabel(alert.at, DateTime.now())}, '
        '${preciseClockLabel(alert.at)}',
        style: TextStyle(
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );

  Widget _actions(Alert alert) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 10,
    children: [
      if (widget.onWatch case final watch?)
        NocturneButton(
          label: 'Watch it on ${alert.cameraName}',
          icon: PhosphorIconsFill.play,
          variant: NocturneButtonVariant.primary,
          height: widget.compact ? 48 : 40,
          fontSize: widget.compact ? 15 : 13.5,
          onPressed: watch,
        ),
      NocturneButton(
        label: 'Dismiss',
        icon: PhosphorIconsRegular.check,
        height: widget.compact ? 46 : 36,
        fontSize: widget.compact ? 14.5 : 13,
        onPressed: widget.onDismiss,
      ),
    ],
  );

  /// Says where *Watch* goes and what you can do once you are there, so the absence of an export
  /// button here reads as a decision rather than an omission.
  Widget _caption(Alert alert) => Text(
    'Opens ${alert.cameraName} at ${preciseClockLabel(alert.at)}, where the '
    'recording is — trim and save from there.',
    style: TextStyle(
      fontSize: 11.5,
      height: 1.45,
      color: Nocturne.mix(Nocturne.text, 42),
    ),
  );
}
