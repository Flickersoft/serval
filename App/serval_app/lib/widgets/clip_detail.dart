import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/byte_labels.dart';
import '../data/time_labels.dart';
import '../models/saved_clip.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import 'nocturne_button.dart';
import 'section_kicker.dart';

/// Everything one clip has to say, under whichever player is showing it.
///
/// One widget for 13a's 372px column and 13c's phone screen, because the content is identical and
/// only its width differs. What changes between them is the wording of one button — on a phone
/// *Download* means the camera roll, and it should say so.
class ClipDetail extends StatelessWidget {
  const ClipDetail({
    super.key,
    required this.detail,
    required this.onDownload,
    this.onShare,
    this.onRename,
    this.onDelete,
    this.playing,
    this.compact = false,
  });

  final SavedClipDetail detail;

  final VoidCallback onDownload;

  /// Null where the platform has no share sheet, which leaves the button undrawn rather than inert.
  final VoidCallback? onShare;

  /// Null where this clip is not the caller's to change — see [SavedClip.mayEdit]. The Server
  /// enforces the same rule; this only avoids offering what it will refuse.
  final VoidCallback? onRename;
  final VoidCallback? onDelete;

  /// How far into the clip the player has got, so the transcript can follow it.
  final ValueListenable<Duration>? playing;

  final bool compact;

  SavedClip get clip => detail.clip;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 14,
    children: [
      if (!compact) _titleRow(),
      if (compact) _facts() else _factsUnderTitle(),
      _actions(),
      Container(height: 1, color: Nocturne.mix(Nocturne.text, 8)),
      if (clip.summary case final summary?) _summary(summary),
      if (detail.speech.isNotEmpty) _saidInIt(),
    ],
  );

  /// The name, and the pencil that changes it. Phone puts both in its app bar instead.
  Widget _titleRow() => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 8,
    children: [
      Expanded(
        child: Text(
          clip.name,
          style: const TextStyle(
            fontFamily: Nocturne.fontHeading,
            fontSize: 16,
            fontWeight: Nocturne.headingWeight,
            height: 1.3,
            color: Nocturne.text,
          ),
        ),
      ),
      if (onRename != null)
        NocturneButton.icon(
          icon: PhosphorIconsRegular.pencilSimple,
          variant: NocturneButtonVariant.ghost,
          height: 28,
          onPressed: onRename,
        ),
    ],
  );

  Widget _factsUnderTitle() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 5,
    children: [
      Text(
        '${clip.cameraName} · $_range',
        style: TextStyle(
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
      Text(
        formatBytes(clip.sizeBytes),
        style: monoStyle(fontSize: 11, color: Nocturne.mix(Nocturne.text, 40)),
      ),
    ],
  );

  Widget _facts() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 4,
    children: [
      Text(
        _range,
        style: TextStyle(fontSize: 13, color: Nocturne.mix(Nocturne.text, 55)),
      ),
      Text(
        formatBytes(clip.sizeBytes),
        style: monoStyle(
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 40),
        ),
      ),
    ],
  );

  String get _range =>
      '${weekdayLabel(clip.from)} ${dayLabel(clip.from)}, '
      '${preciseClockLabel(clip.from)} – ${preciseClockLabel(clip.to)}';

  Widget _actions() => compact
      // On a phone the two share the row equally and *Save to phone* leads with the word that
      // means the camera roll here.
      ? Row(
          spacing: 8,
          children: [
            if (onShare != null)
              Expanded(
                child: NocturneButton(
                  label: 'Share',
                  icon: PhosphorIconsRegular.shareNetwork,
                  variant: NocturneButtonVariant.primary,
                  height: 46,
                  fontSize: 14.5,
                  borderRadius: 8,
                  onPressed: onShare,
                ),
              ),
            Expanded(
              child: NocturneButton(
                label: 'Save to phone',
                icon: PhosphorIconsRegular.downloadSimple,
                variant: onShare == null
                    ? NocturneButtonVariant.primary
                    : NocturneButtonVariant.secondary,
                height: 46,
                fontSize: 14.5,
                borderRadius: 8,
                onPressed: onDownload,
              ),
            ),
          ],
        )
      : Row(
          spacing: 7,
          children: [
            NocturneButton(
              label: 'Download',
              icon: PhosphorIconsRegular.downloadSimple,
              variant: NocturneButtonVariant.primary,
              onPressed: onDownload,
            ),
            if (onShare != null)
              NocturneButton(
                label: 'Share',
                icon: PhosphorIconsRegular.shareNetwork,
                onPressed: onShare,
              ),
            const Spacer(),
            if (onDelete != null)
              NocturneButton.icon(
                icon: PhosphorIconsRegular.trash,
                onPressed: onDelete,
              ),
          ],
        );

  /// Serval's own sentence about the clip.
  ///
  /// Absent rather than empty on a server with no vision model, and absent for the few seconds
  /// after a save while the pass runs — the block simply is not there, which says less than an
  /// empty heading would and is more honest than a placeholder.
  Widget _summary(String summary) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 8,
    children: [
      Row(
        spacing: 8,
        children: [
          Icon(PhosphorIconsFill.sparkle, size: 14, color: Nocturne.accent300),
          Text(
            "What's in it",
            style: TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: compact ? 13 : 12.5,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
        ],
      ),
      Text(
        summary,
        style: TextStyle(
          fontSize: compact ? 14 : 13.5,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 80),
        ),
      ),
    ],
  );

  /// The transcript, positioned against the clip rather than the wall clock.
  ///
  /// The line playing right now is the lit one, so the text scrolls with the video rather than
  /// sitting under it as a block. Without a player position nothing is lit — which is the right
  /// answer for a column showing a clip nobody has pressed play on.
  Widget _saidInIt() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: compact ? 11 : 10,
    children: [
      const SectionKicker('Said in it', padding: EdgeInsets.zero),
      if (playing case final playing?)
        ValueListenableBuilder<Duration>(
          valueListenable: playing,
          builder: (context, at, _) => Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            spacing: compact ? 11 : 10,
            children: [for (final line in detail.speech) _line(line, at)],
          ),
        )
      else
        for (final line in detail.speech) _line(line, null),
    ],
  );

  Widget _line(ClipSpeechLine line, Duration? at) {
    // Lit from when it is said until the next one starts, so exactly one line is ever lit and the
    // highlight moves rather than accumulating.
    final next = detail.speech
        .where((other) => other.offset > line.offset)
        .map((other) => other.offset)
        .fold<Duration?>(null, (a, b) => a == null || b < a ? b : a);

    final live = at != null && at >= line.offset && (next == null || at < next);

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: compact ? 11 : 10,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: Text(
            clipLengthLabel(line.offset),
            style: monoStyle(
              fontSize: compact ? 11 : 10.5,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
          ),
        ),
        Expanded(
          child: Text(
            '“${line.text}”',
            style: TextStyle(
              fontSize: compact ? 14 : 13.5,
              height: 1.45,
              color: live
                  ? Nocturne.accent300
                  : Nocturne.mix(Nocturne.text, 80),
            ),
          ),
        ),
      ],
    );
  }
}
