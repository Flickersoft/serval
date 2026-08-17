import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/byte_labels.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// The media volume as one bar in three parts — Serval's recordings, whatever else is on the
/// volume, and what is left.
///
/// Three parts rather than one because the distinction matters when the bar is nearly full: a
/// pool shared with other apps can be at 95% with Serval holding a tenth of it, and a single
/// "used" bar would put the blame in the wrong place. Where the Server has not measured the
/// per-camera total — the walk can be switched off — [mediaBytes] is null and this collapses to
/// used-and-free, which is still the figure the alerts are built on.
///
/// The segments are steps of one ramp separated by a hairline gap of the surface colour, rather
/// than three unrelated hues. They are parts of one quantity, and colouring them categorically
/// would say they were three different things.
class StorageBar extends StatelessWidget {
  const StorageBar({
    super.key,
    required this.totalBytes,
    required this.freeBytes,
    this.mediaBytes,
    this.height = 10,
    this.showLegend = true,
  });

  final int? totalBytes;
  final int? freeBytes;
  final int? mediaBytes;
  final double height;

  /// Off where the figures are already written beside the bar — the registry's footer says
  /// "1.7 TB of 4 TB" on the line above, and a key repeating it would not fit the 272px column
  /// anyway.
  final bool showLegend;

  @override
  Widget build(BuildContext context) {
    final total = totalBytes;
    final free = freeBytes;

    if (total == null || free == null || total <= 0) {
      return _EmptyTrack(height: height);
    }

    final used = (total - free).clamp(0, total);
    final media = (mediaBytes ?? 0).clamp(0, used);
    final other = used - media;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ClipRRect(
          borderRadius: BorderRadius.circular(height / 2),
          child: SizedBox(
            height: height,
            child: Row(
              children: [
                if (media > 0) ...[
                  Expanded(
                    flex: media,
                    child: Container(color: Nocturne.accent500),
                  ),
                  if (other > 0) _gap,
                ],
                if (other > 0)
                  Expanded(
                    flex: other,
                    child: Container(color: Nocturne.mix(Nocturne.accent, 38)),
                  ),
                if (used > 0 && free > 0) _gap,
                if (free > 0)
                  Expanded(
                    flex: free,
                    child: Container(color: Nocturne.mix(Nocturne.accent, 12)),
                  ),
              ],
            ),
          ),
        ),
        if (showLegend) ...[
          const SizedBox(height: 9),
          Wrap(
            spacing: 16,
            runSpacing: 4,
            children: [
              if (mediaBytes != null)
                _Key(
                  color: Nocturne.accent500,
                  label: 'Recordings',
                  value: formatBytes(media),
                ),
              if (other > 0)
                _Key(
                  color: Nocturne.mix(Nocturne.accent, 38),
                  label: mediaBytes == null ? 'In use' : 'Everything else',
                  value: formatBytes(other),
                ),
              _Key(
                color: Nocturne.mix(Nocturne.accent, 12),
                label: 'Free',
                value: formatBytes(free),
              ),
            ],
          ),
        ],
      ],
    );
  }

  /// Two pixels of the panel behind, so adjacent segments read as separate without a border that
  /// would shift every segment's width by a pixel.
  static const _gap = SizedBox(width: 2);
}

class _EmptyTrack extends StatelessWidget {
  const _EmptyTrack({required this.height});

  final double height;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(height / 2),
    child: Container(height: height, color: Nocturne.mix(Nocturne.text, 8)),
  );
}

class _Key extends StatelessWidget {
  const _Key({required this.color, required this.label, required this.value});

  final Color color;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Container(
        width: 8,
        height: 8,
        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
      ),
      const SizedBox(width: 7),
      // Flexible so a narrow column ellipsises the word rather than overflowing the row — the
      // figure beside it is the part that has to survive.
      Flexible(
        child: Text(
          label,
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            color: Nocturne.mix(Nocturne.text, 55),
          ),
        ),
      ),
      const SizedBox(width: 6),
      Text(value, style: monoStyle(fontSize: 11.5, color: Nocturne.text)),
    ],
  );
}

/// One camera's share of the volume, as a row in a descending list.
///
/// A bar list in one sequential hue rather than a pie or a categorical palette. Cameras are a set
/// that grows, and a colour per camera runs out of distinguishable hues around the ninth and
/// repaints every survivor when one is deleted — while the question here is only ever "which is
/// biggest, and by how much", which is what a sorted bar answers directly.
class StorageRow extends StatelessWidget {
  const StorageRow({
    super.key,
    required this.label,
    required this.bytes,
    required this.largestBytes,
    this.detail,
    this.muted = false,
  });

  final String label;
  final int bytes;

  /// The biggest row's figure, so the bars are relative to each other rather than to the volume —
  /// six cameras at 3% each would otherwise all be invisible slivers.
  final int largestBytes;

  /// The line under the bar: span, retention, measured write rate.
  final String? detail;

  /// The conversation-audio row, which is not a camera and is drawn quieter to say so.
  final bool muted;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                label,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: muted
                      ? Nocturne.mix(Nocturne.text, 50)
                      : Nocturne.mix(Nocturne.text, 82),
                ),
              ),
            ),
            const SizedBox(width: 8),
            Text(
              formatBytes(bytes),
              style: monoStyle(fontSize: 12, color: Nocturne.text),
            ),
          ],
        ),
        const SizedBox(height: 6),
        ClipRRect(
          borderRadius: BorderRadius.circular(3),
          child: Container(
            height: 5,
            color: Nocturne.mix(Nocturne.accent, 12),
            child: FractionallySizedBox(
              alignment: Alignment.centerLeft,
              widthFactor: largestBytes <= 0
                  ? 0
                  : (bytes / largestBytes).clamp(0.0, 1.0),
              child: Container(
                color: muted
                    ? Nocturne.mix(Nocturne.accent, 40)
                    : Nocturne.accent500,
              ),
            ),
          ),
        ),
        if (detail != null) ...[
          const SizedBox(height: 5),
          Text(
            detail!,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
          ),
        ],
      ],
    ),
  );
}

/// The banner the wall shows and the settings page repeats — the one place a status hue is spent.
///
/// Icon and words as well as colour, never colour alone. That is the same contract `_AlertCard` in
/// [activity_column.dart](activity_column.dart) already honours, and the reason the meters above
/// stay on the accent ramp however high they read.
///
/// Two shapes for two jobs. Edge to edge with a rule under it is a banner interrupting a page that
/// is about something else — the wall, the settings form. [boxed] is the card the Server status
/// page opens with, where the alert is not an interruption but the first thing the page is for.
class VitalsAlertStrip extends StatelessWidget {
  const VitalsAlertStrip({
    super.key,
    required this.message,
    required this.critical,
    this.action,
    this.onDismiss,
    this.padding = const EdgeInsets.fromLTRB(22, 12, 22, 12),
    this.boxed = false,
  });

  final String message;

  /// Critical spends the red role rather than the amber one, and the wall refuses to let it be
  /// dismissed.
  final bool critical;

  final Widget? action;
  final VoidCallback? onDismiss;
  final EdgeInsets padding;

  /// A rounded card with a warning glyph, rather than a full-bleed strip with a dot.
  final bool boxed;

  @override
  Widget build(BuildContext context) {
    final hue = critical ? Serval.recording : Serval.alert;

    return Container(
      padding: padding,
      decoration: boxed
          ? BoxDecoration(
              color: Nocturne.mix(hue, 9),
              borderRadius: BorderRadius.circular(8),
              border: Border.all(color: Nocturne.mix(hue, 35)),
            )
          : BoxDecoration(
              color: Nocturne.mix(hue, 10),
              border: Border(bottom: BorderSide(color: Nocturne.mix(hue, 32))),
            ),
      child: Row(
        crossAxisAlignment: boxed
            ? CrossAxisAlignment.start
            : CrossAxisAlignment.center,
        children: [
          if (boxed)
            PhosphorIcon(PhosphorIconsRegular.warning, size: 16, color: hue)
          else
            Container(
              width: 7,
              height: 7,
              decoration: BoxDecoration(color: hue, shape: BoxShape.circle),
            ),
          SizedBox(width: boxed ? 10 : 11),
          Expanded(
            child: Text(
              message,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.4,
                color: critical ? Serval.recordingText : Serval.alertText,
              ),
            ),
          ),
          if (action != null) ...[const SizedBox(width: 12), action!],
          if (onDismiss != null) ...[
            const SizedBox(width: 8),
            _DismissButton(onTap: onDismiss!),
          ],
        ],
      ),
    );
  }
}

class _DismissButton extends StatelessWidget {
  const _DismissButton({required this.onTap});

  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
        child: Text(
          'Dismiss',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12,
            color: Nocturne.mix(Nocturne.text, 55),
          ),
        ),
      ),
    ),
  );
}
