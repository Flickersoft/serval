import 'package:flutter/widgets.dart';

import '../data/byte_labels.dart';
import '../data/time_labels.dart';
import '../models/camera.dart';
import '../models/saved_clip.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'tile_placeholder.dart';

/// One clip in the grid, and one in the phone's list.
///
/// Cards rather than a table, because a clip *is* a picture and the thumbnail is how anyone finds
/// one. A list of names would make the library a filing cabinet, which is exactly the thing footage
/// is worst at being.
class ClipCard extends StatelessWidget {
  const ClipCard({
    super.key,
    required this.clip,
    required this.onOpen,
    this.poster,
    this.selected = false,
  }) : _row = false;

  const ClipCard.row({
    super.key,
    required this.clip,
    required this.onOpen,
    this.poster,
  }) : selected = false,
       _row = true;

  final SavedClip clip;
  final VoidCallback onOpen;

  /// The clip's own poster frame, where a Server was there to make one.
  final Widget? poster;

  final bool selected;

  final bool _row;

  @override
  Widget build(BuildContext context) => GestureDetector(
    onTap: onOpen,
    behavior: HitTestBehavior.opaque,
    child: _row ? _buildRow() : _buildCard(),
  );

  Widget _buildCard() => Container(
    padding: const EdgeInsets.all(8),
    decoration: BoxDecoration(
      color: selected ? Nocturne.mix(Nocturne.accent, 10) : null,
      borderRadius: BorderRadius.circular(9),
      border: Border.all(
        color: selected
            ? Nocturne.mix(Nocturne.accent, 55)
            : Nocturne.mix(Nocturne.text, 9),
      ),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      spacing: 8,
      children: [
        // A shape rather than a height, because the card is as wide as the window allows and a
        // thumbnail pinned to a number of pixels would grow to any proportion the grid happened to
        // leave it. The shape is the design's; the frame's is the camera's, and where they disagree
        // the picture is contained inside this and the leftover painted — never cropped to fill it.
        AspectRatio(aspectRatio: 16 / 9, child: _thumbnail(radius: 6)),
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 3,
          children: [
            Text(
              clip.name,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 13.5,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
            Text(
              _when,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
          ],
        ),
      ],
    ),
  );

  /// 13b: a 124px thumbnail beside two lines reads faster in a column than two-up cards do, and the
  /// name gets the full width it needs — clip names are sentences, not labels.
  Widget _buildRow() => Row(
    crossAxisAlignment: CrossAxisAlignment.center,
    spacing: 12,
    children: [
      SizedBox(width: 124, height: 70, child: _thumbnail(radius: 7)),
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 4,
          children: [
            Text(
              clip.name,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 15,
                fontWeight: Nocturne.headingWeight,
                height: 1.3,
                color: Nocturne.text,
              ),
            ),
            Text(
              _when,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
            // Stated on every clip, because these are the only files that never roll off and the
            // only ones whose weight is the user's to manage.
            Text(
              formatBytes(clip.sizeBytes),
              style: monoStyle(
                fontSize: 11,
                color: Nocturne.mix(Nocturne.text, 35),
              ),
            ),
          ],
        ),
      ),
    ],
  );

  Widget _thumbnail({required double radius}) => DecoratedBox(
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(radius),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 8)),
    ),
    child: ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      child: Stack(
        fit: StackFit.expand,
        children: [
          // Under the poster rather than instead of it. The camera's own stripe rather than a
          // shared grey, so a grid with no posters yet still reads as several cameras — the same
          // reason a wall tile has one — and it is what a contained frame's bars would otherwise
          // show through, which is why the poster brings its own ground.
          TilePlaceholderView(
            placeholder: TilePlaceholder.forCameraId(clip.cameraId),
          ),
          ?poster,
          Positioned(
            right: _row ? 6 : 7,
            bottom: _row ? 6 : 7,
            child: Container(
              padding: EdgeInsets.symmetric(
                horizontal: _row ? 5 : 6,
                vertical: _row ? 1 : 2,
              ),
              decoration: BoxDecoration(
                color: Serval.overlay.withValues(alpha: 0.78),
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                clipLengthLabel(clip.duration),
                style: monoStyle(fontSize: 10, color: Nocturne.text),
              ),
            ),
          ),
        ],
      ),
    ),
  );

  /// `Front door · Tue, 4:03 pm` — where it was and when the thing happened.
  String get _when {
    final at = clip.from;
    final now = DateTime.now();
    final back = DateTime(
      now.year,
      now.month,
      now.day,
    ).difference(DateTime(at.year, at.month, at.day)).inDays;

    // A weekday alone inside the last week, where it is unambiguous and shorter; a date past that,
    // because the second Tuesday back is not a thing anyone can name.
    final day = back < 7 ? weekdayLabel(at) : dayLabel(at);

    return back < 7
        ? '${clip.cameraName} · $day, ${clockLabel(at)}'
        : '${clip.cameraName} · $day';
  }
}
