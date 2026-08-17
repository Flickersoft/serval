import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/activity.dart';
import '../models/conversation.dart';
import '../models/timeline.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'activity_column.dart';

/// The right-hand column on the single-camera view: the wall's "What's
/// happening", scoped to this camera.
///
/// A column, and only a column. Below [Serval.compactWidth] the same rows are a tray over the
/// picture — see [ActivitySheet] — which is a different arrangement rather than this one with its
/// metrics changed, so it is a different widget. What the two share is composed from the same
/// pieces: [ActivityFeed], [_StillListening] and [_ActivitySummary].
///
/// **One list, not a transcript tab beside a feed.** The design draws two, and
/// they are the same data seen twice: both read the one in-memory document
/// cache, and a transcript is the feed's speech subset re-rendered, so a camera
/// with anything to say says it in both places. A two-sided transcript is not
/// available to redeem the split either — nothing captures the outbound side of
/// the push-to-talk, so every turn is attributed to the camera.
///
/// Per-turn attribution survives the merge: a settled conversation draws
/// numbered bubbles inside its single row, so the panel and the wall both show
/// which voice said what. The heading slot says where the voice was or how many
/// there were; see [ActivityFeed.showCamera].
///
/// What it gives up is the **clock time on each turn**. A settled conversation
/// is one row with one timestamp, and its turns are ordered rather than dated.
class ActivityPanel extends StatelessWidget {
  const ActivityPanel({
    super.key,
    required this.activity,
    required this.filter,
    required this.facets,
    this.onFilterChanged,
    this.onSeek,
    this.summary,
    this.listening = true,
    this.collapsed = false,
    this.onCollapsedChanged,
    this.range,
    this.horizon,
    this.inFocus,
  });

  /// Whether a row is inside whatever the screen is about — see [ActivityFeed.inFocus].
  final bool Function(DateTime at)? inFocus;

  /// This camera's slice of the wall's feed, newest first, already filtered.
  final List<ActivityItem> activity;

  /// The window the rows were scoped to, for the empty state to name. See
  /// [ActivityColumn.range].
  final TimelineRange? range;

  /// How far back this camera's feed can speak for. See
  /// [ServalRepository.feedHorizon].
  final DateTime? horizon;

  final ActivityFilter filter;

  /// Counted over this camera's slice before the filter, so every number in the
  /// panel is about the camera on screen.
  final ActivityFacets facets;

  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// Take the picture to a row's moment — the instant to play from, or null for a row too recent
  /// to have footage yet, which means the live view. See [replayStartFor].
  ///
  /// The wall's column routes to a camera; here there is nowhere to route to, so a click moves the
  /// playhead on the picture already on screen. Null where the camera keeps nothing, which leaves
  /// every row inert rather than seeking a track that does not exist.
  final ValueChanged<DateTime?>? onSeek;

  final SceneSummary? summary;

  /// Whether Serval is still hearing this camera.
  final bool listening;

  final bool collapsed;

  /// Null leaves the panel permanently open and draws no chevron.
  final ValueChanged<bool>? onCollapsedChanged;

  @override
  Widget build(BuildContext context) => ActivitySurface(
    width: Serval.detailPanelWidth,
    filter: filter,
    facets: facets,
    shown: activity.length,
    // No *Which camera* facet: the question is settled by being on this
    // screen, and a section offering the one camera you are already looking at
    // would be a filter that can only ever mean "yes".
    onFilterChanged: onFilterChanged,
    collapsed: collapsed,
    onCollapsedChanged: onCollapsedChanged,
    children: [
      Expanded(
        child: CameraActivity(
          activity: activity,
          filter: filter,
          onFilterChanged: onFilterChanged,
          onSeek: onSeek,
          summary: summary,
          listening: listening,
          range: range,
          horizon: horizon,
          inFocus: inFocus,
        ),
      ),
    ],
  );
}

/// One camera's rows, with the two things that are pinned around them rather than scrolled with
/// them.
///
/// The part [ActivityPanel] and the phone's tray genuinely have in common, which is why it is
/// here rather than in either of them. What differs between the two is the head above it and how
/// the room is decided — not the rows, and not what is pinned to them.
class CameraActivity extends StatelessWidget {
  const CameraActivity({
    super.key,
    required this.activity,
    required this.filter,
    this.onFilterChanged,
    this.onSeek,
    this.summary,
    this.listening = false,
    this.range,
    this.horizon,
    this.inFocus,
  });

  final List<ActivityItem> activity;
  final ActivityFilter filter;
  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// See [ActivityPanel.onSeek].
  final ValueChanged<DateTime?>? onSeek;

  /// Null draws nothing, which is how a caller with no room for a paragraph asks for none.
  final SceneSummary? summary;

  final bool listening;
  final TimelineRange? range;
  final DateTime? horizon;
  final bool Function(DateTime at)? inFocus;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      // Pinned above the feed rather than scrolling with it: it is a claim
      // about the camera right now, not an entry in the log, and a claim you
      // have to scroll back to find is not doing its job.
      if (listening)
        const Padding(
          padding: EdgeInsets.fromLTRB(20, 0, 18, 12),
          child: _StillListening(),
        ),
      Expanded(
        child: ActivityFeed(
          items: activity,
          horizon: horizonInView(range, horizon),
          // No camera name: every row is this camera — which is also why the
          // row it hands back is dropped, and only the instant is kept.
          // Dismissing is the wall's idea of housekeeping and stays there.
          showCamera: false,
          inFocus: inFocus,
          onOpen: onSeek == null ? null : (_, at) => onSeek!(at),
          empty: ActivityEmptyState(
            filter: filter,
            nothingAtAll: range == null
                ? 'Nothing heard or seen on this camera yet.'
                : 'Nothing heard or seen on this camera '
                      '${rangeScopeLabel(range!)}.',
            onFilterChanged: onFilterChanged,
          ),
        ),
      ),
      if (summary case final summary?)
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          child: _ActivitySummary(summary: summary),
        ),
    ],
  );
}

/// Four accent bars and a line of text — Serval is still hearing this camera.
class _StillListening extends StatelessWidget {
  const _StillListening();

  static const _heights = [6.0, 12.0, 8.0, 13.0];

  @override
  Widget build(BuildContext context) => Row(
    children: [
      SizedBox(
        height: 13,
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            for (var i = 0; i < _heights.length; i++) ...[
              if (i > 0) const SizedBox(width: 3),
              Container(
                width: 3,
                height: _heights[i],
                decoration: BoxDecoration(
                  // The last bar sits at half strength — the tail of the
                  // waveform, not a fifth level.
                  color: i == _heights.length - 1
                      ? Nocturne.mix(Nocturne.accent, 50)
                      : Nocturne.accent,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ],
          ],
        ),
      ),
      const SizedBox(width: 8),
      Text(
        'Still listening…',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );
}

/// Serval's read on the scene, pinned to the bottom of the rows.
class _ActivitySummary extends StatelessWidget {
  const _ActivitySummary({required this.summary});

  final SceneSummary summary;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Container(height: 1, color: Nocturne.mix(Nocturne.text, 8)),
      const SizedBox(height: 9),
      const Row(
        children: [
          PhosphorIcon(
            PhosphorIconsFill.sparkle,
            size: 14,
            color: Nocturne.accent400,
          ),
          SizedBox(width: 8),
          Text(
            "Serval's summary",
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
        ],
      ),
      const SizedBox(height: 9),
      Text(
        summary.text,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 13.5,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 80),
        ),
      ),
    ],
  );
}
