import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/activity.dart';
import '../models/camera.dart';
import '../models/timeline.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'activity_filter_panel.dart';
import 'activity_glyph.dart';
import 'nocturne_button.dart';
import 'section_kicker.dart';
import 'top_anchored_scroll_view.dart';

/// "What's happening" — one running column of everything Serval sees and
/// hears, across every camera.
///
/// The column exists so the video never has to be covered: alerts, speech and
/// scene descriptions all land here instead of on top of a feed. It keeps a
/// full transcript rather than the last line only, which is better for
/// scanning back through the day at the cost of a second place to look.
///
/// Every row is a way into the footage behind it: clicking one opens its camera
/// a few seconds before the thing it describes. Talking back and pan/tilt live
/// on the single-camera view, so a row routes you there rather than letting you
/// speak into a feed you are not watching — where the same feed is waiting,
/// scoped to that camera.
class ActivityColumn extends StatelessWidget {
  const ActivityColumn({
    super.key,
    required this.items,
    required this.filter,
    required this.facets,
    this.cameras = const [],
    this.onFilterChanged,
    this.onOpenCamera,
    this.collapsed = false,
    this.onCollapsedChanged,
    this.range,
    this.horizon,
  });

  /// The window the rows were scoped to, for the empty state to name.
  ///
  /// Null is a caller with no scrubber behind it — the sample repository's
  /// composed rows, and the widget tests — where "nothing yet" is the whole
  /// truth and there is no window to blame.
  final TimelineRange? range;

  /// How far back the feed can speak for. See [ServalRepository.feedHorizon].
  final DateTime? horizon;

  /// The rows to draw — already filtered. What [facets] counts is the pool they
  /// were drawn from, which is why both are passed rather than derived here.
  final List<ActivityItem> items;

  final ActivityFilter filter;
  final ActivityFacets facets;

  /// Every camera on the wall, for the *Which camera* facet.
  final List<Camera> cameras;

  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// Where clicking a row should land: the camera, and the instant to seek its recording to —
  /// null for a row recent enough that the live view is what you meant. See [replayStartFor].
  final void Function(String cameraId, DateTime? at)? onOpenCamera;

  final bool collapsed;

  /// Null leaves the column permanently open and draws no chevron.
  final ValueChanged<bool>? onCollapsedChanged;

  @override
  Widget build(BuildContext context) => ActivitySurface(
    width: Serval.activityColumnWidth,
    filter: filter,
    facets: facets,
    shown: items.length,
    cameras: cameras,
    onFilterChanged: onFilterChanged,
    collapsed: collapsed,
    onCollapsedChanged: onCollapsedChanged,
    children: [
      Expanded(
        child: WallActivityFeed(
          items: items,
          cameras: cameras,
          filter: filter,
          range: range,
          horizon: horizon,
          onOpenCamera: onOpenCamera,
          onFilterChanged: onFilterChanged,
        ),
      ),
    ],
  );
}

/// The wall's feed, with the three answers that are the wall's rather than the
/// feed's: which cameras keep nothing, how far back the window reaches, and
/// what an empty one should say about it.
///
/// Extracted so the column and the phone's sheet draw the same feed from the
/// same wiring. Duplicating it is how the two would come to disagree about
/// which rows lead anywhere.
class WallActivityFeed extends StatelessWidget {
  const WallActivityFeed({
    super.key,
    required this.items,
    required this.filter,
    this.cameras = const [],
    this.range,
    this.horizon,
    this.onOpenCamera,
    this.onFilterChanged,
  });

  final List<ActivityItem> items;
  final ActivityFilter filter;
  final List<Camera> cameras;
  final TimelineRange? range;
  final DateTime? horizon;
  final void Function(String cameraId, DateTime? at)? onOpenCamera;
  final ValueChanged<ActivityFilter>? onFilterChanged;

  @override
  Widget build(BuildContext context) => ActivityFeed(
    items: items,
    // The horizon, but only where [range] actually reaches past it — see
    // [_FeedHorizon] for why it is conditional rather than always drawn.
    horizon: horizonInView(range, horizon),
    onOpen: onOpenCamera == null
        ? null
        : (item, at) => onOpenCamera!(item.cameraId, at),
    // A camera that keeps nothing has no footage behind any of its rows, so they lead
    // nowhere — the same gate the scrubber puts on its own track.
    unrecorded: {
      for (final camera in cameras)
        if (!camera.records) camera.id,
    },
    empty: ActivityEmptyState(
      filter: filter,
      cameras: cameras,
      // Named rather than left at "yet", because with a range behind it an
      // empty column is a fact about the window and not about the house.
      // "Nothing heard or seen yet" over a five-minute range reads as a
      // system that has never heard anything.
      nothingAtAll: range == null
          ? 'Nothing heard or seen yet.'
          : 'Nothing heard or seen ${rangeScopeLabel(range!)}.',
      onFilterChanged: onFilterChanged,
    ),
  );
}

/// The column, its header and the filter panel that opens over it — shared by
/// the wall's column and the single-camera panel, which differ only in what
/// they stack under the header.
///
/// The panel is an overlay rather than a section pushed in above the feed
/// because the counts beside every facet describe the rows underneath: a panel
/// that shoved them out of the way would be reporting on a column nobody can
/// see. It covers the feed and not the header, so the chips and the *N of M*
/// line stay readable while it is open — which is what makes ticking a box and
/// watching the readout change one gesture rather than two.
class ActivitySurface extends StatefulWidget {
  const ActivitySurface({
    super.key,
    required this.width,
    required this.filter,
    required this.facets,
    required this.shown,
    required this.children,
    this.cameras = const [],
    this.onFilterChanged,
    this.collapsed = false,
    this.onCollapsedChanged,
  });

  final double width;

  final ActivityFilter filter;
  final ActivityFacets facets;

  /// How many rows survived the filter, for the *N of the M events in view*
  /// readout. Not `facets.total`, which is the M.
  final int shown;

  /// What goes under the header. One `Expanded` feed on the wall; the panel
  /// adds its *Still listening…* strip above and Serval's summary below.
  final List<Widget> children;

  final List<Camera> cameras;
  final ValueChanged<ActivityFilter>? onFilterChanged;
  final bool collapsed;
  final ValueChanged<bool>? onCollapsedChanged;

  @override
  State<ActivitySurface> createState() => _ActivitySurfaceState();
}

class _ActivitySurfaceState extends State<ActivitySurface> {
  /// Owned here rather than by the field, so the caret survives the rebuild
  /// every keystroke causes — the query goes up to the screen, comes back down
  /// as a new filter, and rebuilds this whole subtree on the way.
  final _search = TextEditingController();

  bool _panelOpen = false;

  @override
  void didUpdateWidget(ActivitySurface oldWidget) {
    super.didUpdateWidget(oldWidget);

    // *Clear* is the one thing that empties the query from outside the field.
    if (widget.filter.query != _search.text) _search.text = widget.filter.query;
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  /// Nothing in a collapsed column can be tapped, and a panel left open behind
  /// the rail would be waiting there when it reopens.
  void _close() => setState(() => _panelOpen = false);

  @override
  Widget build(BuildContext context) {
    if (widget.collapsed && _panelOpen) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _close();
      });
    }

    return _CollapsiblePanel(
      width: widget.width,
      collapsed: widget.collapsed,
      onCollapsedChanged: widget.onCollapsedChanged,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          ActivityHeader(
            filter: widget.filter,
            facets: widget.facets,
            shown: widget.shown,
            cameras: widget.cameras,
            search: _search,
            onFilterChanged: widget.onFilterChanged,
            onOpenFilters: widget.onFilterChanged == null
                ? null
                : () => setState(() => _panelOpen = !_panelOpen),
            onCollapse: widget.onCollapsedChanged == null
                ? null
                : () => widget.onCollapsedChanged!(true),
          ),
          Expanded(
            child: Stack(
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: widget.children,
                ),
                if (_panelOpen && widget.onFilterChanged != null)
                  Positioned(
                    left: 14,
                    right: 14,
                    top: 0,
                    bottom: 14,
                    child: ActivityFilterPanel(
                      filter: widget.filter,
                      facets: widget.facets,
                      cameras: widget.cameras,
                      onChanged: widget.onFilterChanged!,
                      onDone: _close,
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The shell both right-hand panels sit in: their full width, or the rail they
/// collapse to.
///
/// Here rather than in either panel because the two want identical behaviour
/// and differ only in how wide they are when open — the same reason
/// [ActivityFeed] is shared. [width] is a parameter rather than a token read
/// because the wall's column and the single-camera panel are 4px apart, and
/// unifying them is not this widget's decision to make.
class _CollapsiblePanel extends StatelessWidget {
  const _CollapsiblePanel({
    required this.width,
    required this.collapsed,
    required this.onCollapsedChanged,
    required this.child,
  });

  /// How wide the panel is when it is open.
  final double width;

  final bool collapsed;
  final ValueChanged<bool>? onCollapsedChanged;
  final Widget child;

  static const _duration = Duration(milliseconds: 160);

  @override
  Widget build(BuildContext context) {
    final open = collapsed && onCollapsedChanged != null
        ? Serval.activityRailWidth
        : width;

    return AnimatedContainer(
      duration: _duration,
      curve: Curves.easeOut,
      width: open,
      decoration: BoxDecoration(
        color: Serval.rail,
        border: Border(left: BorderSide(color: Serval.hairline)),
      ),
      // The contents are laid out at their settled width for the whole
      // animation and clipped to the box, rather than being re-laid-out at each
      // width the box passes through. An alert card's two buttons do not fit in
      // 40px and would throw for the length of the transition — and re-flowing
      // the entire feed on every frame to show it mid-squeeze is work spent on
      // something nobody watches. Right-anchored, so the panel reads as a
      // curtain drawn across contents that stay where they are.
      child: ClipRect(
        child: OverflowBox(
          alignment: Alignment.centerRight,
          minWidth: open,
          maxWidth: open,
          child: collapsed && onCollapsedChanged != null
              ? _CollapsedRail(onExpand: () => onCollapsedChanged!(false))
              : child,
        ),
      ),
    );
  }
}

/// What is left of a panel that has been collapsed: the way back into it.
class _CollapsedRail extends StatelessWidget {
  const _CollapsedRail({required this.onExpand});

  final VoidCallback onExpand;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 16),
    child: Align(
      alignment: Alignment.topCenter,
      // The caret points the way the panel will come back from.
      child: NocturneButton.icon(
        icon: PhosphorIconsRegular.caretLeft,
        variant: NocturneButtonVariant.ghost,
        height: 28,
        onPressed: onExpand,
      ),
    ),
  );
}

/// The rows themselves, split into *Right now* and *Earlier today*.
///
/// Separate from [ActivityColumn] because the single-camera panel shows the same
/// feed scoped to one camera, and supplies its own chrome around it. Extracted
/// rather than duplicated: a second rendering of a row would drift from this
/// one, and the drift would be invisible until the two were on screen together.
class ActivityFeed extends StatelessWidget {
  const ActivityFeed({
    super.key,
    required this.items,
    this.onOpen,
    this.unrecorded = const {},
    this.showCamera = true,
    this.padding = const EdgeInsets.fromLTRB(12, 0, 12, 16),
    this.empty,
    this.horizon,
    this.inFocus,
  });

  /// Whether a row's moment is part of whatever the screen is currently about.
  ///
  /// Null is the ordinary case: every row is equally in view. Set while a clip is being trimmed,
  /// where the rows inside the range are the ones the clip will contain and the rest are context —
  /// so those dim rather than disappear. Filtering them out instead would remove exactly the rows
  /// you need to see to decide where the range should end.
  ///
  /// A predicate rather than a range, so the feed keeps knowing nothing about clips.
  final bool Function(DateTime at)? inFocus;

  /// How many rows are drawn at once.
  ///
  /// The column lays out eagerly — see [TopAnchoredScrollView], which needs an
  /// exact `maxScrollExtent` to hold your place as rows arrive — so every row in
  /// the list is built whether or not it is on screen, and collapsing the panel
  /// throws those elements away and rebuilds all of them on the next expand.
  /// That is affordable for a hundred rows and not for a thousand.
  ///
  /// **This bounds the drawing and nothing else.** The pool behind it is whole,
  /// and everything counted off it — the facet counts, the alert tally, the
  /// *N of M* line — is counted over the pool rather than over what fits here.
  /// A number in the filter panel that described only the visible slice would be
  /// useless for deciding what to filter *to*, which is the one thing the panel
  /// is for.
  static const maxRows = 100;

  final List<ActivityItem> items;

  /// Where the read stopped, when it stopped short of the window on screen.
  /// Null is a feed that reaches as far back as the bar does — see
  /// [_FeedHorizon]. Already tested against the range by the caller.
  final DateTime? horizon;

  /// Where a row leads: the row itself, and the instant its footage should start at — null for one
  /// recent enough that the live view is what you meant. See [replayStartFor], which decides both.
  ///
  /// One callback for two different destinations. The wall routes to the camera the row names; the
  /// single-camera panel seeks the picture already on screen and throws the row away. What they
  /// must not differ on is *when*, which is why the instant is computed here rather than by either
  /// of them.
  ///
  /// Null leaves every row inert.
  final void Function(ActivityItem item, DateTime? at)? onOpen;

  /// Cameras that keep nothing. Their rows still say what happened — that is what the feed is for —
  /// but there is no footage to open, so they are not tap targets.
  final Set<String> unrecorded;

  /// False on the single-camera panel, where every row is this camera and
  /// naming it on each one is six words of noise. What the name leaves behind is
  /// [ActivityItem.speaker] where the row has one.
  final bool showCamera;

  final EdgeInsets padding;

  /// What to say when nothing has happened. Null renders nothing at all.
  final Widget? empty;

  @override
  Widget build(BuildContext context) {
    if (items.isEmpty) return empty ?? const SizedBox.shrink();

    // Newest first, so the cut is always off the oldest end — the rows nearest
    // the top of the column are the ones you came to read.
    final drawn = items.length > maxRows ? items.take(maxRows).toList() : items;

    final recent = drawn.where((i) => i.isRecent).toList();
    final earlier = drawn.where((i) => !i.isRecent).toList();

    return TopAnchoredScrollView(
      // The feed is newest-first, so this holds your place as rows arrive
      // above. The head is where it changes, and the id is already a stable
      // identity.
      //
      // Counted off what is drawn rather than off `items`: this exists to
      // measure the list's own height, and rows past the cut have none.
      revision: (drawn.length, drawn.first.id),
      padding: padding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (recent.isNotEmpty) ...[
            const SectionKicker(
              'Right now',
              padding: EdgeInsets.fromLTRB(6, 10, 6, 6),
            ),
            for (final item in recent) _row(item),
          ],
          if (earlier.isNotEmpty) ...[
            const SectionKicker(
              'Earlier today',
              padding: EdgeInsets.fromLTRB(6, 14, 6, 6),
            ),
            for (final item in earlier) _row(item),
          ],
          // Last, under everything, because these are statements about where
          // the list ends rather than events in it.
          //
          // One or the other, never both. A capped list has not reached the far
          // edge of the window, so what the read did or did not manage down
          // there is not yet the reader's problem — the cut is.
          if (items.length > drawn.length)
            _FeedCapped(drawn: drawn.length, matching: items.length)
          else if (horizon case final horizon?)
            _FeedHorizon(horizon: horizon, now: DateTime.now()),
        ],
      ),
    );
  }

  Widget _row(ActivityItem item) {
    // Read at build rather than at tap: a row that was current when it arrived is not current ten
    // minutes later, and the surface it draws — clickable or not — has to say which it is now.
    final onTap = onOpen == null || unrecorded.contains(item.cameraId)
        ? null
        : () => onOpen!(item, replayStartFor(item.at));

    final row = item.isAlert
        ? _AlertCard(item: item, showCamera: showCamera, onTap: onTap)
        : _ActivityRow(item: item, showCamera: showCamera, onTap: onTap);

    // Still tappable while dimmed — dimming says "not in the clip", and tapping one of these is
    // how you make it be.
    return inFocus == null || inFocus!(item.at)
        ? row
        : Opacity(opacity: 0.4, child: row);
  }
}

/// The title, the search field, the *Filter* button and the readout of what is
/// currently filtered — drawn by both the wall's column and the single-camera
/// panel.
///
/// This was a four-segment control, and the replacement splits its job in
/// three. Search takes the long tail nothing can enumerate: a word somebody
/// said, a name, a plate. The panel behind the button takes the facets the
/// telemetry already carries. And the chips take the readout — what is filtered
/// stays on the surface, small and removable one at a time, so a column that has
/// been narrowed can never be mistaken for a quiet house.
class ActivityHeader extends StatelessWidget {
  const ActivityHeader({
    super.key,
    required this.filter,
    required this.facets,
    required this.shown,
    required this.search,
    this.cameras = const [],
    this.onFilterChanged,
    this.onOpenFilters,
    this.onCollapse,
  });

  final ActivityFilter filter;
  final ActivityFacets facets;

  /// How many rows survived the filter — the N in *N of the M events in view*.
  final int shown;

  /// The search field's text, owned by [ActivitySurface] so it outlives the
  /// rebuild each keystroke causes.
  final TextEditingController search;

  final List<Camera> cameras;
  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// Null draws the *Filter* button inert, for a header with no panel behind
  /// it.
  final VoidCallback? onOpenFilters;

  /// Null draws no chevron, for a panel that cannot be collapsed.
  final VoidCallback? onCollapse;

  /// What the chips call each camera. Built from the wall's own list, so a
  /// chip says *Front door* rather than `front-door`.
  Map<String, String> get _cameraNames => {
    for (final camera in cameras) camera.id: camera.name,
  };

  @override
  Widget build(BuildContext context) {
    final chips = filter.chips(_cameraNames);

    return Padding(
      padding: const EdgeInsets.fromLTRB(18, 16, 18, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // The chevron shares the title's line rather than taking one of its
          // own: the heading is one short phrase and leaves the room, and the
          // search field below already has the width to itself.
          Row(
            children: [
              Expanded(
                child: Text(
                  "What's happening",
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontHeading,
                    fontSize: 15,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                  ),
                ),
              ),
              // No tooltip on the chevron: an arrow pointing at the edge the
              // panel leaves towards, and back the other way to bring it in, is
              // the whole of what there is to say, and a label spelling it out
              // reads as though the glyph did not manage it.
              if (onCollapse != null)
                NocturneButton.icon(
                  icon: PhosphorIconsRegular.caretRight,
                  variant: NocturneButtonVariant.ghost,
                  height: 28,
                  onPressed: onCollapse,
                ),
            ],
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: ActivitySearchField(
                  controller: search,
                  onChanged: onFilterChanged == null
                      ? (_) {}
                      : (value) =>
                            onFilterChanged!(filter.copyWith(query: value)),
                ),
              ),
              const SizedBox(width: 8),
              ActivityFilterButton(
                count: filter.facetCount,
                onTap: onOpenFilters ?? () {},
              ),
            ],
          ),
          if (chips.isNotEmpty) ...[
            const SizedBox(height: 10),
            Wrap(
              spacing: 4,
              runSpacing: 4,
              children: [
                for (final chip in chips)
                  ActivityFilterChip(
                    label: chip.label,
                    onRemove: onFilterChanged == null
                        ? () {}
                        : () => onFilterChanged!(chip.without),
                  ),
              ],
            ),
          ],
          // Drawn only when something is filtered. At rest the column is
          // everything, and a line saying "64 of the 64 events in view" would
          // be arithmetic in place of a fact.
          if (!filter.isEmpty) ...[
            const SizedBox(height: 10),
            Row(
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Expanded(
                  child: Text(
                    eventsInView(shown, facets.total),
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11.5,
                      color: Nocturne.mix(Nocturne.text, 50),
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                // Everything, search included — the one control that puts the
                // column back the way it was.
                ActivityLink(
                  'Clear',
                  fontSize: 11.5,
                  onTap: onFilterChanged == null
                      ? null
                      : () => onFilterChanged!(ActivityFilter.none),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// What the column says when the filter leaves nothing.
///
/// Three different things, because they are three different facts: nothing has
/// happened, nothing matches the facets, or nothing matches what was typed. The
/// last one is worth explaining — Serval only keeps words it heard, so a person
/// it saw but nobody named will never answer a search, and a reader who does
/// not know that reads an empty column as a broken one.
class ActivityEmptyState extends StatelessWidget {
  const ActivityEmptyState({
    super.key,
    required this.filter,
    required this.nothingAtAll,
    this.cameras = const [],
    this.onFilterChanged,
  });

  final ActivityFilter filter;

  /// What to say when the column is unfiltered and still empty — which is not
  /// a filter problem and must not be dressed as one.
  final String nothingAtAll;

  final List<Camera> cameras;
  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// Which cameras the search was scoped to, named. Empty on the single-camera
  /// panel, whose scope is the screen rather than the filter.
  List<String> get _scope => [
    for (final camera in cameras)
      if (filter.cameraIds.contains(camera.id)) camera.name,
  ];

  String get _headline {
    if (filter.isEmpty) return nothingAtAll;

    final query = filter.query.trim();
    if (query.isEmpty) return 'Nothing in view matches this filter.';

    final scope = _scope;
    return scope.isEmpty
        ? 'Nothing matched “$query”.'
        : 'Nothing on ${_list(scope)} matched “$query”.';
  }

  /// "Back yard", "Back yard and Kitchen", "Driveway, Back yard and Kitchen".
  static String _list(List<String> names) => switch (names.length) {
    1 => names.single,
    2 => '${names.first} and ${names.last}',
    _ => '${names.sublist(0, names.length - 1).join(', ')} and ${names.last}',
  };

  @override
  Widget build(BuildContext context) {
    final searching = filter.query.trim().isNotEmpty;
    final scoped = searching && _scope.isNotEmpty;

    return Padding(
      padding: const EdgeInsets.fromLTRB(18, 24, 18, 24),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 14),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(Nocturne.radiusMd),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              _headline,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.4,
                color: Nocturne.mix(Nocturne.text, 72),
              ),
            ),
            if (searching) ...[
              const SizedBox(height: 7),
              Text(
                'Serval only keeps words it heard — a person it saw but '
                'nobody named won’t match a search.',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  height: 1.45,
                  color: Nocturne.mix(Nocturne.text, 45),
                ),
              ),
            ],
            // Widens the search rather than clearing it: the words are what you
            // came for, and the camera is the guess most likely to have been
            // the wrong one.
            if (scoped && onFilterChanged != null) ...[
              const SizedBox(height: 9),
              NocturneButton(
                label: 'Search every camera',
                variant: NocturneButtonVariant.primary,
                height: 28,
                fontSize: 12,
                horizontalPadding: 11,
                onPressed: () =>
                    onFilterChanged!(filter.copyWith(cameraIds: const {})),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// [horizon], but only where [range] reaches past it — otherwise null.
///
/// Shared by the column and the single-camera panel so the two cannot disagree
/// about when the feed is worth apologising for.
DateTime? horizonInView(TimelineRange? range, DateTime? horizon) {
  if (range == null || horizon == null) return null;

  return range.startAt(DateTime.now()).isBefore(horizon) ? horizon : null;
}

/// The line under the last row when there was more that matched than is drawn.
///
/// Points at the filter, because that is the way out: the pool behind this is
/// whole and every count in the panel is taken off it, so narrowing to a camera
/// or a kind really does reach what is past the cut. A reader who is told only
/// "there is more" has been given a fact and no move to make.
class _FeedCapped extends StatelessWidget {
  const _FeedCapped({required this.drawn, required this.matching});

  final int drawn;
  final int matching;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(6, 14, 6, 4),
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(Nocturne.radiusMd),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Showing the newest $drawn of $matching.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.4,
              color: Nocturne.mix(Nocturne.text, 72),
            ),
          ),
          const SizedBox(height: 7),
          Text(
            'Filter to reach the rest — the counts in the panel are for all '
            '$matching, not just these.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ],
      ),
    ),
  );
}

/// The line under the last row when the feed stops before the bar does.
///
/// A column that simply ends reads as a house that went quiet, and that reading
/// is indistinguishable on screen from the true one — the read ran out of depth.
/// The whole point of scoping the feed to the range is that the bar and the
/// column describe the same window; where they cannot, saying so is what keeps
/// that promise honest rather than merely usually true.
///
/// Drawn only when the range actually reaches past the horizon. A busy morning
/// that truncated at noon says nothing at all while the bar is set to an hour,
/// because over that hour the column is complete and a warning would be about a
/// window nobody is looking at.
class _FeedHorizon extends StatelessWidget {
  const _FeedHorizon({required this.horizon, required this.now});

  final DateTime horizon;
  final DateTime now;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(6, 14, 6, 4),
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(Nocturne.radiusMd),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Nothing older than ${activityTimeLabel(horizon, now: now)} '
            'is loaded.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.4,
              color: Nocturne.mix(Nocturne.text, 72),
            ),
          ),
          const SizedBox(height: 7),
          Text(
            'The timeline reaches further back than this — there were more '
            'events here than one read returns.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ],
      ),
    ),
  );
}

/// Something that wants a decision. The only tinted row in the column, and the
/// only one carrying an action.
///
/// No *Open camera* button: the card's own surface does that, as every other
/// row's does, and a labelled button pointing at it would be pointing at itself.
class _AlertCard extends StatelessWidget {
  const _AlertCard({required this.item, required this.showCamera, this.onTap});

  final ActivityItem item;
  final bool showCamera;

  /// Open the footage behind this row. Null where there is none. See [ActivityFeed.onOpen].
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => _RowSurface(
    onTap: onTap,
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 11),
    color: Nocturne.mix(Serval.alert, 10),
    hoverColor: Nocturne.mix(Serval.alert, 17),
    border: Border.all(color: Nocturne.mix(Serval.alert, 32)),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _RowIcon(icon: item.icon, alert: true),
        const SizedBox(width: 11),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _RowHeading(item: item, showCamera: showCamera),
              const SizedBox(height: 5),
              _RowBody(item: item, alert: true),
            ],
          ),
        ),
      ],
    ),
  );
}

class _ActivityRow extends StatelessWidget {
  const _ActivityRow({
    required this.item,
    required this.showCamera,
    this.onTap,
  });

  final ActivityItem item;
  final bool showCamera;

  /// Open the footage behind this row. Null where there is none. See [ActivityFeed.onOpen].
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => _RowSurface(
    onTap: onTap,
    padding: const EdgeInsets.all(10),
    hoverColor: Nocturne.mix(Nocturne.text, 5),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _RowIcon(icon: item.icon, speech: item.isSpeech),
        const SizedBox(width: 11),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _RowHeading(item: item, showCamera: showCamera),
              const SizedBox(height: 4),
              _RowBody(item: item),
            ],
          ),
        ),
      ],
    ),
  );
}

/// The ground under a row: its tint, its corners, and — where there is footage behind it — the
/// gesture that opens it.
///
/// Both card shapes sit on this rather than each growing its own hover state, because the two are
/// the same target drawn twice. A row that lit up on the wall and stayed flat on the camera's own
/// panel would be the same click looking like two different features.
///
/// A row with nowhere to go draws no fill and defers the cursor. It reads as text, which is what it
/// is: the feed still says what happened on a camera that keeps nothing, there is just no footage
/// to go and see.
class _RowSurface extends StatefulWidget {
  const _RowSurface({
    required this.padding,
    required this.child,
    this.onTap,
    this.color,
    this.hoverColor,
    this.border,
  });

  final EdgeInsets padding;
  final Widget child;
  final VoidCallback? onTap;

  /// The resting fill. Null is the plain row, which has none until you point at it.
  final Color? color;
  final Color? hoverColor;
  final BoxBorder? border;

  @override
  State<_RowSurface> createState() => _RowSurfaceState();
}

class _RowSurfaceState extends State<_RowSurface> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final surface = Container(
      padding: widget.padding,
      decoration: BoxDecoration(
        color: _hovered ? widget.hoverColor ?? widget.color : widget.color,
        borderRadius: BorderRadius.circular(Nocturne.radiusMd),
        border: widget.border,
      ),
      child: widget.child,
    );

    if (widget.onTap == null) return surface;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        // The whole card, including the gaps between its lines — a row you can only open by
        // hitting the words is a target that moves with the sentence.
        behavior: HitTestBehavior.opaque,
        child: surface,
      ),
    );
  }
}

/// What a row says, under its heading — quoted speech behind the accent rule, or
/// plain narration.
///
/// One widget for both callers, because two copies is the drift this file's own
/// header warns about: the day speech grows a speaker bubble and a face, one
/// would get it and the other would go on drawing a bare sentence.
///
/// [alert] only picks the ink. An alerting row cannot currently *be* speech —
/// `is_alert` is set from sounds and detections and from nothing else — so the
/// speech arm is unreached from `_AlertCard` today. It is written anyway,
/// because the alternative on the day an alerting utterance exists is a row with
/// no rule, no speaker and no face, and nobody would notice which until the two
/// were side by side.
class _RowBody extends StatelessWidget {
  const _RowBody({required this.item, this.alert = false});

  final ActivityItem item;
  final bool alert;

  @override
  Widget build(BuildContext context) {
    // One figure per row kind, shared by both callers so the two cannot drift.
    final opacity = alert ? 88.0 : (item.isSpeech ? 80.0 : 72.0);

    if (!item.isSpeech) return _text(item.text, opacity);

    return Container(
      padding: const EdgeInsets.only(left: 9),
      decoration: BoxDecoration(
        border: Border(
          left: BorderSide(color: Nocturne.mix(Nocturne.accent, 60), width: 2),
        ),
      ),
      child: item.turns.isEmpty
          ? _SpeechLine(
              text: item.text,
              speakerNumber: item.speakerNumber,
              emotion: item.emotion,
              opacity: opacity,
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (var i = 0; i < item.turns.length; i++) ...[
                  if (i > 0) const SizedBox(height: 5),
                  _SpeechLine(
                    text: item.turns[i].text,
                    speakerNumber: item.turns[i].speakerNumber,
                    emotion: item.turns[i].emotion,
                    opacity: opacity,
                  ),
                ],
              ],
            ),
    );
  }

  static Widget _text(String value, double opacity) => Text(
    value,
    style: TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 13,
      height: 1.45,
      color: Nocturne.mix(Nocturne.text, opacity),
    ),
  );
}

/// One quoted line: who said it, what they said, and how it sounded.
///
/// The [Expanded] in the middle is the whole reason this is a `Row` rather than
/// a `Wrap` or a trailing `WidgetSpan` — it is what pins the face to the right
/// edge whatever the sentence's length, so a one-word line and a wrapping
/// paragraph put their glyph in the same place.
class _SpeechLine extends StatelessWidget {
  const _SpeechLine({
    required this.text,
    required this.opacity,
    this.speakerNumber,
    this.emotion,
  });

  final String text;
  final double opacity;
  final int? speakerNumber;
  final ActivityEmotion? emotion;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      if (speakerNumber case final number?) ...[
        // Nudged down rather than flush with the text box: 13px copy at height
        // 1.45 is a ~19px line, and a 15px bubble hung from the top of that box
        // sits visibly above the words it belongs to.
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: _SpeakerBubble(number),
        ),
        const SizedBox(width: 7),
      ],
      Expanded(
        child: Text(
          text,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            height: 1.45,
            color: Nocturne.mix(Nocturne.text, opacity),
          ),
        ),
      ),
      if (emotion case final emotion?) ...[
        const SizedBox(width: 7),
        Padding(
          padding: const EdgeInsets.only(top: 1.5),
          child: PhosphorIcon(
            emotionGlyph(emotion),
            // Bigger than the timestamp beside it, and coloured, which is what
            // this first shipped without. A face is not a word: letterforms
            // survive at a glance because their shapes are already known, while
            // these glyphs are a filled disc whose whole meaning is a few
            // knocked-out pixels of eye and mouth. Drawn grey at 13px, the angry
            // face and the fearful one were the same blob — measured on a real
            // feed, not guessed. A mark nobody can read is not restraint, it is
            // absence that still takes up room.
            size: 16,
            // One flat colour per emotion — see [emotionColor]. The hue tells
            // them apart at a glance and the face confirms which, which is also
            // what keeps it working for a reader who cannot separate two of the
            // hues.
            color: emotionColor(emotion),
          ),
        ),
      ],
    ],
  );
}

/// Which voice said it — the design's ①, drawn rather than typed.
///
/// U+2460 is in neither Inter nor JetBrainsMono, so a literal circled numeral
/// would be tofu in the goldens and on most machines. The design specifies an
/// appearance, not a codepoint.
///
/// Accent rather than a hue of its own. `serval_tokens.dart` is explicit that
/// the design adds exactly three chromatic roles, used "as a line, a dot and a
/// tint, never a flood" — and a speech row already wears the accent twice, on
/// its 26px glyph square and on the rule beside the quote. A third quiet use
/// reads as part of the quotation; a per-speaker palette would be inventing
/// colour to encode an index that resets every conversation.
class _SpeakerBubble extends StatelessWidget {
  const _SpeakerBubble(this.number);

  final int number;

  @override
  Widget build(BuildContext context) => Container(
    // Stadium, not a circle: a fixed circle clips at ten speakers, and the
    // minimum keeps single digits perfectly round while letting `10` widen.
    constraints: const BoxConstraints(minWidth: 15, minHeight: 15),
    padding: const EdgeInsets.symmetric(horizontal: 3),
    alignment: Alignment.center,
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.accent, 18),
      borderRadius: BorderRadius.circular(8),
    ),
    child: Text(
      '$number',
      // Mono because a speaker index is a telemetry label, which is the role
      // this face already has — and because Inter's `1` at this size is
      // ambiguous where JetBrainsMono's is not.
      style: const TextStyle(
        fontFamily: Nocturne.fontMono,
        fontSize: 9.5,
        height: 1,
        fontWeight: Nocturne.headingWeight,
        color: Nocturne.accent300,
      ),
    ),
  );
}

class _RowHeading extends StatelessWidget {
  const _RowHeading({required this.item, required this.showCamera});

  final ActivityItem item;
  final bool showCamera;

  /// What leads the heading, before the time.
  ///
  /// The wall names the camera, because the column runs across all of them. The
  /// single-camera panel has no such question to answer, so the slot goes to
  /// [ActivityItem.speaker] — and on a scene or a sound, which have no speaker,
  /// the heading is the time alone.
  ///
  /// That slot no longer carries an *identity*. The bubble on the quote does,
  /// on both screens, which is what freed this one to say where the voice was
  /// or how many there were — the questions that have an answer on every row
  /// rather than only inside a conversation.
  String? get _lead => showCamera ? item.cameraName : item.speaker;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.baseline,
    textBaseline: TextBaseline.alphabetic,
    children: [
      if (_lead case final lead?) ...[
        Flexible(
          child: Text(
            lead,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 13,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
        ),
        const SizedBox(width: 7),
      ],
      Text(
        item.timeLabel,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 45),
        ),
      ),
    ],
  );
}

/// The 26px tinted square that leads every row. Speech takes the accent,
/// alerts take the attention hue, and everything else sits on neutral.
class _RowIcon extends StatelessWidget {
  const _RowIcon({required this.icon, this.alert = false, this.speech = false});

  final ActivityIcon icon;
  final bool alert;
  final bool speech;

  @override
  Widget build(BuildContext context) {
    final (background, foreground) = alert
        ? (Nocturne.mix(Serval.alert, 20), Serval.alert)
        : speech
        ? (Nocturne.mix(Nocturne.accent, 16), Nocturne.accent400)
        : (Nocturne.mix(Nocturne.text, 7), Nocturne.mix(Nocturne.text, 65));

    return Container(
      width: 26,
      height: 26,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(6),
      ),
      child: PhosphorIcon(activityGlyph(icon), size: 14, color: foreground),
    );
  }
}
