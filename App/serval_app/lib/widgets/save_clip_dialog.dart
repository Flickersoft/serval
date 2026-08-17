import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/byte_labels.dart';
import '../data/time_labels.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_dialog.dart';
import 'nocturne_field.dart';

/// Where a clip goes once its range is settled.
enum ClipDestination {
  /// Kept on the Server, in the library, until somebody deletes it.
  library,

  /// Straight to this device, keeping nothing — what *Save clip* has always done.
  download,

  /// Handed to the platform's share sheet, keeping nothing.
  share,
}

/// What the dialog was closed with.
class SaveClipChoice {
  const SaveClipChoice({required this.name, required this.destination});

  final String name;
  final ClipDestination destination;
}

/// The dialog that appears once the range is right, and the two things a range cannot tell you.
///
/// Deliberately not a second editor. The range was settled on the screen behind this and reads here
/// as a fact — *Back to trimming* returns you there rather than offering the times again. There is
/// no quality picker and no audio switch either: a clip can only ever be what the camera recorded,
/// so the only file fact worth stating is how big it is.
class SaveClipDialog extends StatefulWidget {
  const SaveClipDialog({
    super.key,
    required this.cameraName,
    required this.from,
    required this.to,
    required this.estimatedBytes,
    this.suggestedName = '',
    this.poster,
    this.canShare = false,
    this.compact = false,
  });

  final String cameraName;
  final DateTime from;
  final DateTime to;

  /// What the file will weigh, reckoned from the segments. Approximate and labelled as such — the
  /// exact figure does not exist until ffmpeg has written it.
  final int estimatedBytes;

  /// Pre-filled from Serval's own description of the window, which is the one place the scene
  /// prose earns its keep twice.
  final String suggestedName;

  /// STUB: a frame from the middle of the range, and nowhere to get one — `snapshot.jpg` is the
  /// latest frame and no route extracts a still from the archive, so every caller passes null and
  /// the box below draws its duration pill over nothing. `/api/clips/{id}/poster.jpg` is the same
  /// picture from the wrong side of the save: it is addressed by clip id, which does not exist yet.
  final Widget? poster;

  final bool canShare;

  /// 12d: the same two decisions as a sheet rather than a dialog, for the reason the filter is one.
  final bool compact;

  @override
  State<SaveClipDialog> createState() => _SaveClipDialogState();
}

class _SaveClipDialogState extends State<SaveClipDialog> {
  late final TextEditingController _name = TextEditingController(
    text: widget.suggestedName,
  );
  ClipDestination _destination = ClipDestination.library;

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  Duration get _span => widget.to.difference(widget.from);

  bool get _valid => _name.text.trim().isNotEmpty;

  void _save() {
    if (!_valid) return;

    Navigator.of(
      context,
    ).pop(SaveClipChoice(name: _name.text.trim(), destination: _destination));
  }

  @override
  Widget build(BuildContext context) => widget.compact ? _sheet() : _dialog();

  Widget _dialog() => NocturneDialog(
    title: 'Save this clip',
    width: 520,
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: 16,
      children: [
        Row(
          // Not `stretch`: the dialog's body sits in a `mainAxisSize.min` column, so a stretched
          // cross axis asks for infinite height. The poster's own box is what sets the row's.
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 14,
          children: [
            SizedBox(width: 168, height: 95, child: _poster()),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: 11,
                children: [
                  NocturneField(
                    label: 'Name',
                    controller: _name,
                    hint: 'What was it?',
                    onChanged: (_) => setState(() {}),
                  ),
                  _facts(),
                ],
              ),
            ),
          ],
        ),
        Container(height: 1, color: Nocturne.mix(Nocturne.text, 8)),
        _whereItGoes(),
      ],
    ),
    actions: [
      NocturneButton(
        label: 'Back to trimming',
        variant: NocturneButtonVariant.secondary,
        onPressed: () => Navigator.of(context).pop(),
      ),
      NocturneButton(
        label: 'Save clip',
        variant: NocturneButtonVariant.primary,
        onPressed: _valid ? _save : null,
      ),
    ],
  );

  /// 12d. A sheet rather than a dialog on a phone, and the button is full width at the bottom
  /// where a thumb is.
  Widget _sheet() => Container(
    decoration: BoxDecoration(
      color: Serval.rail,
      borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
      border: Border(top: BorderSide(color: Nocturne.mix(Nocturne.text, 14))),
    ),
    child: SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 6, 16, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 16,
          children: [
            Center(
              child: Container(
                width: 38,
                height: 4,
                margin: const EdgeInsets.only(bottom: 6),
                decoration: BoxDecoration(
                  color: Nocturne.mix(Nocturne.text, 28),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const Text(
              'Save this clip',
              style: TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 18,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
            Row(
              // Centred against the thumbnail rather than stretched, for the same reason the
              // dialog's row is not stretched — and because 12d sets the two facts beside the
              // middle of the picture rather than at the top of it.
              crossAxisAlignment: CrossAxisAlignment.center,
              spacing: 12,
              children: [
                SizedBox(width: 132, height: 74, child: _poster()),
                Expanded(child: _facts()),
              ],
            ),
            NocturneField(
              label: 'Name',
              controller: _name,
              hint: 'What was it?',
              onChanged: (_) => setState(() {}),
            ),
            _whereItGoes(),
            // Stretches, because the Column stretches it — full width at the bottom of the sheet
            // is where a thumb already is.
            NocturneButton(
              label: 'Save clip',
              variant: NocturneButtonVariant.primary,
              height: 52,
              fontSize: 16,
              borderRadius: 9,
              onPressed: _valid ? _save : null,
            ),
          ],
        ),
      ),
    ),
  );

  Widget _poster() => DecoratedBox(
    decoration: BoxDecoration(
      color: Serval.tile,
      borderRadius: BorderRadius.circular(7),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
    ),
    child: ClipRRect(
      borderRadius: BorderRadius.circular(7),
      child: Stack(
        fit: StackFit.expand,
        children: [
          if (widget.poster != null) widget.poster!,
          Positioned(
            left: 8,
            bottom: 8,
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
              decoration: BoxDecoration(
                color: Serval.overlay.withValues(alpha: 0.7),
                borderRadius: BorderRadius.circular(4),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                spacing: 5,
                children: [
                  Icon(
                    PhosphorIconsFill.play,
                    size: 10,
                    color: Nocturne.mix(Nocturne.text, 55),
                  ),
                  Text(
                    clipSpokenLabel(_span),
                    style: monoStyle(
                      fontSize: 9.5,
                      color: Nocturne.mix(Nocturne.text, 55),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    ),
  );

  /// The range, as a fact rather than as a field, and the size.
  Widget _facts() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 5,
    children: [
      _fact(
        PhosphorIconsRegular.clock,
        '${dayLabel(widget.from)} · ${rangeLabel()}',
      ),
      _fact(
        PhosphorIconsRegular.fileVideo,
        '${formatBytes(widget.estimatedBytes)} or so',
      ),
    ],
  );

  String rangeLabel() =>
      '${preciseClockLabel(widget.from)} – ${preciseClockLabel(widget.to)}';

  Widget _fact(PhosphorIconData icon, String text) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: 7,
    children: [
      Padding(
        padding: const EdgeInsets.only(top: 1),
        child: Icon(icon, size: 13, color: Nocturne.mix(Nocturne.text, 50)),
      ),
      Expanded(
        child: Text(
          text,
          style: monoStyle(
            fontSize: 11.5,
            color: Nocturne.mix(Nocturne.text, 50),
            height: 1.4,
          ),
        ),
      ),
    ],
  );

  Widget _whereItGoes() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 7,
    children: [
      Text(
        'Where it goes',
        style: TextStyle(fontSize: 12, color: Nocturne.mix(Nocturne.text, 55)),
      ),
      Row(
        spacing: widget.compact ? 8 : 7,
        children: [
          _destinationButton(
            ClipDestination.library,
            'Saved clips',
            PhosphorIconsRegular.folderSimple,
          ),

          // Share where the platform has a sheet, download where it does not. Not both: a phone
          // saving to its camera roll and a desktop saving to Downloads are the same intention,
          // and offering two words for it on one screen is what makes people hesitate.
          if (widget.canShare)
            _destinationButton(
              ClipDestination.share,
              widget.compact ? 'Share…' : 'Share',
              PhosphorIconsRegular.shareNetwork,
            )
          else
            _destinationButton(
              ClipDestination.download,
              widget.compact ? 'Save to phone' : 'Download',
              PhosphorIconsRegular.downloadSimple,
            ),
        ],
      ),
      Text(
        'Saved clips are kept until you delete them — unlike the rest of the footage, which rolls '
        'off.',
        style: TextStyle(
          fontSize: widget.compact ? 12 : 11.5,
          height: 1.45,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
      ),
    ],
  );

  Widget _destinationButton(
    ClipDestination destination,
    String label,
    PhosphorIconData icon,
  ) {
    final chosen = _destination == destination;

    final button = GestureDetector(
      onTap: () => setState(() => _destination = destination),
      child: Container(
        height: widget.compact ? 46 : 36,
        padding: EdgeInsets.symmetric(horizontal: widget.compact ? 0 : 13),
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: chosen ? Nocturne.mix(Nocturne.accent, 12) : null,
          borderRadius: BorderRadius.circular(widget.compact ? 8 : 7),
          border: Border.all(
            color: chosen
                ? Nocturne.mix(Nocturne.accent, 60)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          spacing: 8,
          children: [
            Icon(
              icon,
              size: widget.compact ? 16 : 15,
              color: chosen
                  ? Nocturne.accent300
                  : Nocturne.mix(Nocturne.text, 75),
            ),
            Text(
              label,
              style: TextStyle(
                fontSize: widget.compact ? 14 : 13,
                color: chosen ? Nocturne.text : Nocturne.mix(Nocturne.text, 75),
              ),
            ),
          ],
        ),
      ),
    );

    // Equal halves on a phone, where the two are the whole row; intrinsic on a desktop, where they
    // sit at the left of a wider one.
    return widget.compact ? Expanded(child: button) : button;
  }
}
