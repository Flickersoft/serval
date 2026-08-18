import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/scheduler.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../models/activity.dart';
import '../models/camera.dart';
import '../models/system_stats.dart';
import '../models/timeline.dart';
import '../models/wall_layout.dart';
import '../playback/wall_replay_controller.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/activity_column.dart';
import '../widgets/activity_sheet.dart';
import '../widgets/camera_tile.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/frame_size.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/paired_rows.dart';
import '../widgets/resize_handle.dart';
import '../widgets/roster_widgets.dart';
import '../widgets/storage_bar.dart';
import '../widgets/timeline_scrubber.dart';
import '../widgets/replay_transport.dart';

/// The live wall.
///
/// Tiles keep their own size and shape, and under *Rearrange* can be dragged to
/// move and dragged by the corner to resize. Everything Serval sees and hears
/// runs down one activity column so the video stays uncovered.
class WallScreen extends ConsumerStatefulWidget {
  const WallScreen({super.key, required this.onOpenCamera});

  final void Function(String cameraId, DateTime? at) onOpenCamera;

  @override
  ConsumerState<WallScreen> createState() => _WallScreenState();
}

class _WallScreenState extends ConsumerState<WallScreen> {
  ActivityFilter _filter = ActivityFilter.none;
  bool _rearranging = false;

  TimelineRange _range = TimelineRange.hour;

  /// Vitals warnings put aside for this session.
  ///
  /// For the session and no longer, and deliberately not persisted: a volume that is 94% full will
  /// still be 94% full in five minutes, so a strip that could be silenced for good would be
  /// silenced once and then be worth nothing. A critical one cannot be dismissed at all — see
  /// [_vitalsAlert].
  final _dismissedAlerts = <VitalsAlertKind>{};

  /// Read once: the override is installed by `ServalApp` for the life of the app, and this screen
  /// subscribes to it imperatively rather than through `ref.watch` — the working layout below has
  /// to survive notifications rather than be rebuilt by them.
  late final ServalRepository _repository = ref.read(repositoryProvider);

  /// The master clock every tile follows while replaying. Built with the screen rather than on
  /// demand — it holds no player and starts no timer until something seeks.
  late final WallReplayController _replay = WallReplayController(
    repository: _repository,
  );

  /// Where the compact tray is settled, so the wall under it knows how much room it has.
  ///
  /// Only the phone layout builds a tray at all; on a desktop this is a controller nothing ever
  /// reports to, which costs one `ChangeNotifier` and keeps the tray's height out of `build`.
  final _tray = ActivitySheetController();

  /// The arrangement being looked at, and edited.
  ///
  /// Held here rather than read from the repository per build, because a drag
  /// has to have something to mutate. Seeded and reconciled by [_reconcile].
  List<TileLayout> _layout = const [];

  /// The camera set [_layout] was last reconciled against. Null means never.
  Set<String>? _seededFor;

  /// Whether [_layout] was seeded from an arrangement the Server actually gave us, rather than from
  /// the default pack standing in for one that had not arrived yet.
  ///
  /// The latch [_reconcile] turns on, and it never turns off — from here on the working copy is the
  /// user's arrangement, and re-seeding would throw away whatever they have done to it since.
  bool _seededFromPreferences = false;

  /// What shape each camera's picture turned out to be, learned from the frames
  /// themselves — see [_MeasuredFrame].
  ///
  /// Only the compact wall reads it. A desktop tile's shape comes from its span
  /// on the grid, and a camera that disagrees with its cell is contained inside
  /// it; below the breakpoint there is no grid to disagree with.
  final _frameAspects = <String, double>{};

  /// Filed only when it is news. A camera sends its shape once a second and
  /// keeps sending the same one, and rebuilding the wall for that would undo
  /// the whole reason frames ride a per-tile notifier.
  void _rememberAspect(String cameraId, double aspect) {
    if ((_frameAspects[cameraId] ?? 0) == aspect) return;
    setState(() => _frameAspects[cameraId] = aspect);
  }

  /// The slices this screen's *own* build reads, as opposed to the ones its children watch for
  /// themselves.
  ///
  /// **The feed is not among them, deliberately.** The column and the tray subscribe to it where
  /// they draw it, so a detection arriving — twice a second, per camera with something in frame —
  /// no longer reaches up here and relays out every tile on the wall. That one absence is the
  /// whole point of the list.
  ///
  /// What is left divides in two. The first three are the arrangement: which cameras there are, how
  /// they are laid out, and what each has recorded. The last two are read during [build] and drawn
  /// in the chrome — the vitals strip, and the bell's unread dot.
  ///
  /// Those last two could be scoped to the widgets that draw them, and are not. Both are rare —
  /// vitals on the five-second sweep, an unread count on a doorbell — and `registryChanges` already
  /// fires on that same sweep, so wrapping either would buy no rebuild back and cost the header a
  /// nesting level. Worth revisiting only if something here starts moving at the rate the feed
  /// does.
  late final List<Listenable> _slices = [
    _repository.registryChanges,
    _repository.preferenceChanges,
    _repository.timelineChanges,
    _repository.vitalsChanges,
    _repository.alertChanges,
  ];

  @override
  void initState() {
    super.initState();
    for (final slice in _slices) {
      slice.addListener(_onArrangementChanged);
    }
    _replay.addListener(_onReplayChanged);
    _reconcile();
    _syncReplay();
  }

  @override
  void dispose() {
    for (final slice in _slices) {
      slice.removeListener(_onArrangementChanged);
    }
    _replay.removeListener(_onReplayChanged);
    _replay.dispose();
    _tray.dispose();
    super.dispose();
  }

  void _onReplayChanged() {
    if (mounted) setState(() {});
  }

  /// Hands the controller the cameras on the wall and what each has recorded.
  ///
  /// Called wherever either could have changed rather than from `build`: `update` can dispose a
  /// removed camera's player and notify, and a notification raised during a build is a build that
  /// schedules another one.
  void _syncReplay() => _replay.update(
    {
      for (final camera in _repository.cameras())
        camera.id: _repository.timelineFor(camera.id, _range),
    },
    labels: {
      for (final camera in _repository.cameras()) camera.id: camera.name,
    },
  );

  /// Repaints on any of [_slices], and folds a changed camera set into the
  /// arrangement being edited when the cameras themselves changed.
  ///
  /// The *reconcile* is keyed on the camera ids because [_applyLayout] saves,
  /// and saving notifies — re-seeding on each notify would throw away the very
  /// edit that caused it. That is the whole job of [_seededFor].
  ///
  /// Until the arrangement itself has landed, though, the camera ids are the
  /// wrong question: preferences arrive on a notification with no camera-set
  /// change behind them, and there is no edit to protect yet anyway. So the
  /// short circuit waits on [_seededFromPreferences] as well — which is also
  /// what lets `_watchPreferences`' retry reach a wall that is already up.
  ///
  /// The *rebuild* is keyed on nothing, and still must not be — but it is now
  /// reached by three slices rather than by everything the repository holds.
  /// What it redraws is each tile's online and recording state and the vitals
  /// strip, all of which are read during [build]. The activity column used to
  /// be on that list, and is not any more: it subscribes where it is drawn, so
  /// an arriving utterance no longer relays out the wall to add one row.
  void _onArrangementChanged() {
    final ids = [for (final camera in _repository.cameras()) camera.id];
    _syncReplay();
    if (_seededFromPreferences && setEquals(ids.toSet(), _seededFor)) {
      setState(() {});
      return;
    }
    setState(_reconcile);
  }

  /// Seeds the working layout from the saved arrangement, or folds a changed
  /// camera set into the copy already being edited.
  ///
  /// **What ends the seeding is preferences arriving, not the cameras.** Both
  /// reads are in flight at once and the registry is the one that lands first —
  /// `LiveServalRepository.start` notifies from `_loadRegistry` and only then
  /// applies the fetched preferences — so the first notification this screen
  /// gets carries a full camera set and a `wallLayout()` still standing in with
  /// the default pack. Keying the latch on the camera set, as this used to,
  /// took that stand-in for the arrangement and never looked again: defaults
  /// drawn over a saved layout for the whole session, on every interactive
  /// sign-in, with only a browser reload putting it right.
  ///
  /// Worse, it did not stay cosmetic. `preferencesKnown` unlocks the rearrange
  /// control the moment the document lands, while the screen is still showing
  /// the stand-in, and there is no save step — see [_applyLayout]. One nudged
  /// tile and the default pack became the stored truth.
  ///
  /// So: seed while there is no arrangement to protect — [_seededFor] null or
  /// empty — *or* while what we seeded from was a stand-in, and latch on the
  /// first seed taken from a document the Server actually gave us. No guard is
  /// needed against re-seeding under a drag: `canRearrange` is
  /// `preferencesKnown`, so the latch has already closed before anything can be
  /// dragged.
  ///
  /// Reconciling `_layout` rather than re-reading the repository is what keeps
  /// an arrangement alive when a camera appears or disappears mid-session.
  void _reconcile() {
    final ids = [for (final camera in _repository.cameras()) camera.id];
    final known = _repository.preferencesKnown;

    final seeding =
        _seededFor == null || _seededFor!.isEmpty || !_seededFromPreferences;

    _layout = seeding
        ? List.of(_repository.wallLayout())
        : WallGrid.reconcile(_layout, ids);
    _seededFor = ids.toSet();
    _seededFromPreferences = _seededFromPreferences || (seeding && known);
  }

  /// The one vitals warning the wall shows, or null when there is nothing to say.
  ///
  /// **One, not all.** The wall is for watching cameras; a stack of strips across the top of it
  /// would be a settings page wearing the wrong screen. The most severe outstanding warning is
  /// enough to send somebody to `/settings/server`, which is where the rest of them are.
  ///
  /// A critical alert is never dismissable and so never checked against [_dismissedAlerts]: at
  /// under 5% free, recording is about to start failing, and silencing that is not a decision the
  /// wall should offer.
  VitalsAlert? get _vitalsAlert {
    VitalsAlert? best;

    for (final alert
        in _repository.systemStats()?.alerts ?? const <VitalsAlert>[]) {
      if (alert.isCritical) return alert;
      if (_dismissedAlerts.contains(alert.kind)) continue;
      best ??= alert;
    }

    return best;
  }

  /// The only save path: every accepted move and every accepted resize.
  ///
  /// There is no save step, and deliberately so — this state is disposed the
  /// moment you open a camera, settings, or sign out, and a save deferred to
  /// `dispose` would notify listeners during unmount.
  void _applyLayout(List<TileLayout> next) {
    setState(() => _layout = next);

    // List.of: the repository keeps the instance it is handed as its saved
    // layout, and this screen must not still be holding a reference into it.
    unawaited(_repository.saveWallLayout(List.of(next)));
  }

  @override
  Widget build(BuildContext context) {
    final repository = _repository;

    // Unfiltered: what the column draws is the filter over this, and what its
    // facet panel counts is this itself.
    //
    // Scoped to the same range as the scrubber under the wall, so what the
    // track covers is what the column lists. The range button is the one
    // control naming the window, and it names it for both.
    List<ActivityItem> poolAt(DateTime? asOf) =>
        repository.activityFor(asOf: asOf, range: _range).toList();

    if (isCompact(context)) return _buildCompact(repository, poolAt);

    // The rail is drawn by `ServalShell` — this screen is what goes to the right of it.
    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Row(
        children: [
          Expanded(
            child: Column(
              children: [
                _WallHeader(
                  rearranging: _rearranging,
                  onRearrangingChanged: (v) => setState(() => _rearranging = v),
                  replaying: _replay.replaying,
                  canRearrange: repository.preferencesKnown,
                  empty: repository.cameras().isEmpty,
                ),

                // A sibling of the header, not part of it — and that is the whole placement
                // decision. [_WallHeader] holds its height constant so that nothing appearing
                // inside it jogs the wall down, and a strip mounted in there would do exactly
                // that. Out here it shifts the grid once, when something is genuinely wrong, and
                // unlike the rearrange subtitle it does not toggle back and forth.
                if (_vitalsAlert case final alert?)
                  VitalsAlertStrip(
                    message: alert.message,
                    critical: alert.isCritical,
                    action: NocturneButton(
                      label: 'Open settings',
                      variant: NocturneButtonVariant.ghost,
                      onPressed: () => context.go('/settings/server'),
                    ),
                    onDismiss: alert.isCritical
                        ? null
                        : () =>
                              setState(() => _dismissedAlerts.add(alert.kind)),
                  ),

                Expanded(child: _wallAndActivity(repository, poolAt)),
              ],
            ),
          ),
        ],
      ),
    );
  }

  /// The wall on a phone: the tiles in one stack, and the feed as a sheet over
  /// them.
  ///
  /// Three things go, and each because there is no room to aim at them rather
  /// than to save space. The rail is already gone below
  /// [Serval.compactWidth], so the gear rides the bar. *Rearrange* goes because
  /// a stack of full-width tiles has no spans to drag — [_CompactWall] reads the
  /// saved arrangement in order and draws every tile the same. And the scrubber
  /// goes with it: replay is a single-camera act, and a 412px track under six
  /// cameras is a control nobody can land on.
  Widget _buildCompact(
    ServalRepository repository,
    List<ActivityItem> Function(DateTime?) poolAt,
  ) {
    final cameras = repository.cameras();
    // Cameras not known to be down, which is what this line has always counted and is why it is
    // `!= offline` rather than `== online`. Counting only the ones with a fresh frame would put
    // this at zero every time the phone comes back from the background — the wall's flash, in a
    // different typeface.
    final live = cameras
        .where((camera) => camera.connection != CameraConnection.offline)
        .length;
    final order = [
      for (final tile in WallGrid.readingOrder(_layout)) tile.cameraId,
    ];

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      // The bar is inside the stack rather than above it, because the filter
      // dims and covers it too. Round 11 draws that deliberately: at this height
      // the sheet is the screen's subject, and a lit bar over a dimmed wall
      // reads as a panel that only half opened.
      child: LayoutBuilder(
        builder: (context, constraints) => Stack(
          children: [
            Column(
              children: [
                CompactAppBar(
                  title: 'Home',
                  // Suppressed on an empty wall: "0 of 0 cameras live" is
                  // arithmetic where having no cameras at all is the fact.
                  subtitle: cameras.isEmpty
                      ? null
                      : '$live of ${cameras.length} '
                            '${cameras.length == 1 ? 'camera' : 'cameras'} live',
                  actions: [
                    // Where the rail's other destinations go on a phone: beside the gear, since
                    // the wall is home and there is no tab bar under it to carry them. The bell
                    // keeps its dot here for the same reason it has one on the rail — this is the
                    // only place a phone learns there is something waiting without being told.
                    CompactBarAction(
                      icon: PhosphorIconsRegular.bell,
                      tooltip: 'Alerts',
                      // `_repository`, not a fresh `ref.watch`: this screen already listens to it
                      // (see initState), which is what makes the dot move when an alert arrives.
                      badge: _repository.unreadAlerts > 0,
                      onPressed: () => context.go('/alerts'),
                    ),
                    CompactBarAction(
                      icon: PhosphorIconsRegular.filmStrip,
                      tooltip: 'Saved clips',
                      onPressed: () => context.go('/clips'),
                    ),
                    CompactBarAction(
                      icon: PhosphorIconsRegular.gearSix,
                      tooltip: 'Settings',
                      onPressed: () => context.go('/settings'),
                    ),
                  ],
                ),
                if (_vitalsAlert case final alert?)
                  VitalsAlertStrip(
                    message: alert.message,
                    critical: alert.isCritical,
                    action: NocturneButton(
                      label: 'Open settings',
                      variant: NocturneButtonVariant.ghost,
                      onPressed: () => context.go('/settings/server'),
                    ),
                    onDismiss: alert.isCritical
                        ? null
                        : () =>
                              setState(() => _dismissedAlerts.add(alert.kind)),
                  ),
                Expanded(
                  // Only for the tray's height, and only the settled one: the wall keeps its
                  // layout while the tray moves, and what changes is how much room the tiles are
                  // given to scroll into behind it.
                  child: cameras.isEmpty
                      ? const Padding(
                          padding: EdgeInsets.fromLTRB(12, 12, 12, 0),
                          child: _NoCameras(),
                        )
                      : ListenableBuilder(
                          listenable: _tray,
                          builder: (context, _) => _CompactWall(
                            layout: _layout,
                            repository: repository,
                            replay: _replay,
                            onTapCamera: _openTile,
                            aspects: _frameAspects,
                            onAspect: _rememberAspect,
                            trayHeight: _tray.height,
                          ),
                        ),
                ),
              ],
            ),
            ValueListenableBuilder<DateTime?>(
              valueListenable: _replay.playheadSeconds,
              builder: (context, asOf, _) => Consumer(
                // The tray's subscription to the feed, so the tiles above it keep their frames and
                // their layout while the column under it fills up.
                builder: (context, ref, _) {
                  ref.watch(activityRevisionProvider);

                  final pool = poolAt(asOf);
                  final items = _filter.apply(pool);

                  return ActivitySheet(
                    controller: _tray,
                    filter: _filter,
                    facets: ActivityFacets.of(
                      pool,
                      alertsOnly: _filter.alertsOnly,
                    ),
                    shown: items.length,
                    cameras: cameras,
                    // No head, and so no peek detent: the wall's tray carries
                    // nothing but the feed, and a third height with nothing in it
                    // to show would be a place to put the tray for no reason.
                    body: (context, _) => WallActivityFeed(
                      items: items,
                      cameras: cameras,
                      filter: _filter,
                      range: _range,
                      horizon: repository.feedHorizon(),
                      onOpenCamera: _open,
                      onFilterChanged: (f) => setState(() => _filter = f),
                    ),
                    // The rhythm rule, but never so little that raising the sheet
                    // was not worth the gesture. A portrait camera at the top of
                    // the wall is taller than the room above the resting sheet,
                    // so "one camera whole overhead" cannot hold and the honest
                    // fallback is a feed you can read.
                    raisedHeight: math.max(
                      constraints.maxHeight -
                          _CompactWall.raisedSheetTop(
                            context,
                            order: order,
                            aspects: _frameAspects,
                          ),
                      constraints.maxHeight * Serval.activitySheetRaisedShare,
                    ),
                    onFilterChanged: (f) => setState(() => _filter = f),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }

  /// The wall and its feed, side by side.
  Widget _wallAndActivity(
    ServalRepository repository,
    List<ActivityItem> Function(DateTime?) poolAt,
  ) {
    // A registry with nothing in it is the first thing a new install shows, and an empty grid
    // over an empty timeline says only that something is broken. The wall explains itself
    // instead, and points at the one page that fixes it.
    final empty = repository.cameras().isEmpty;

    final wall = Padding(
      padding: const EdgeInsets.fromLTRB(22, 18, 18, 22),
      child: Column(
        children: [
          Expanded(
            child: empty
                ? const _NoCameras()
                : _TileGrid(
                    layout: _layout,
                    repository: repository,
                    rearranging: _rearranging,
                    replay: _replay,
                    onTapCamera: _openTile,
                    onLayoutChanged: _applyLayout,
                  ),
          ),
          // Always on, exactly as the single-camera view has it. There is no
          // replay *mode* to enter: the day is on screen, and clicking it is
          // what starts playback. The exception is a wall with no cameras, where the bar spans
          // nothing and its range control chooses between empty days.
          if (!empty) ...[
            const SizedBox(height: 14),
            _WallScrubber(
              replay: _replay,
              range: _range,
              onRangeChanged: _setRange,
              // Any one recorded camera is enough for the bar to mean something. It reads
              // "nothing is kept" only when that is true of the whole wall.
              records: _repository.cameras().any((camera) => camera.records),
            ),
          ],
        ],
      ),
    );

    // Collapsed-ness lives on the repository rather than in this State: it is
    // shared with the single-camera view, and a preference you have to restate
    // on every screen is the app forgetting what you just told it.
    final activity = ValueListenableBuilder<bool>(
      valueListenable: repository.activityPanelCollapsed,
      // Nested rather than folded into the screen's own rebuild: the playhead is
      // a listenable precisely so that the tiles and the grid are not relaid out
      // as it moves, and the column is the one thing that has to follow it.
      builder: (context, collapsed, _) => ValueListenableBuilder<DateTime?>(
        valueListenable: _replay.playheadSeconds,
        builder: (context, asOf, _) => Consumer(
          // And the same for the desktop column: it is the thing the feed is drawn in, so it is
          // the thing that hears about the feed.
          builder: (context, ref, _) {
            ref.watch(activityRevisionProvider);

            final pool = poolAt(asOf);

            return ActivityColumn(
              range: _range,
              horizon: repository.feedHorizon(),
              items: _filter.apply(pool),
              filter: _filter,
              facets: ActivityFacets.of(pool, alertsOnly: _filter.alertsOnly),
              cameras: repository.cameras(),
              onFilterChanged: (f) => setState(() => _filter = f),
              onOpenCamera: _open,
              collapsed: collapsed,
              onCollapsedChanged: repository.setActivityPanelCollapsed,
            );
          },
        ),
      ),
    );

    return Row(
      children: [
        Expanded(child: wall),
        activity,
      ],
    );
  }

  /// Clicking a row in the feed goes to that camera, at the instant the row already backed up for
  /// you — see [replayStartFor], which is where the lead-in lives.
  ///
  /// The lead-in belongs to the row rather than to this handover, and [_openTile] below is why:
  /// it asks to open at an instant too, and backing that one up by five seconds would land it
  /// short of the frame it was taken from.
  void _open(String cameraId, [DateTime? at]) =>
      widget.onOpenCamera(cameraId, at);

  /// Tapping a tile hands that camera over at whatever the wall was showing.
  ///
  /// Replaying the wall and then tapping a tile is asking to see *this* camera at *this* moment,
  /// not to leave replay — arriving live would drop the instant being looked at, and getting back
  /// to it would mean finding it again on a different scrubber. The playhead is null while the
  /// wall is live, which is exactly when opening live is what was meant.
  void _openTile(String cameraId) =>
      widget.onOpenCamera(cameraId, _replay.playhead.value);

  void _setRange(TimelineRange range) {
    setState(() => _range = range);
    _syncReplay();
  }
}

/// The wall's timeline: the union of every camera's day, a lane for each of them, and the
/// transport that drives all of it.
/// What the wall shows before a single camera has been registered.
///
/// A [ConsumerWidget] for the one thing it needs from outside: every camera write is Admin-only
/// on the Server, so below that role this offers no button. Naming an action the Server will
/// refuse is worse than the blank grid it replaces — it sends a viewer to a page they cannot act
/// on, having told them they could.
class _NoCameras extends ConsumerWidget {
  const _NoCameras();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final canEdit = ref.watch(isAdminProvider);
    return EmptyRoster(
      icon: PhosphorIconsRegular.videoCamera,
      title: 'No cameras yet',
      body: canEdit
          ? 'Add one under Settings → Cameras — paste its stream URL and Serval starts '
                'pulling it straight away, with nothing to restart.'
          : 'No cameras have been added yet. An administrator can add them under '
                'Settings → Cameras.',
      action: canEdit
          ? NocturneButton(
              label: 'Add a camera',
              icon: PhosphorIconsRegular.plus,
              variant: NocturneButtonVariant.primary,
              // The path, not '/settings', which resolves to a different screen either side of
              // the compact breakpoint.
              onPressed: () => context.go('/settings/cameras'),
            )
          : null,
    );
  }
}

class _WallScrubber extends StatelessWidget {
  const _WallScrubber({
    required this.replay,
    required this.range,
    required this.onRangeChanged,
    required this.records,
  });

  final WallReplayController replay;
  final TimelineRange range;
  final ValueChanged<TimelineRange> onRangeChanged;

  /// Whether any camera on the wall is recorded. See [TimelineScrubber.records].
  final bool records;

  @override
  Widget build(BuildContext context) {
    return TimelineScrubber(
      window: replay.timeline,
      // Per-camera bars only once there is a camera-by-camera question to answer. While live the
      // merge says the thing you actually want — was anything recording, did anything happen —
      // in a fraction of the height, and the wall keeps the rest.
      lanes: replay.replaying ? replay.lanes : const [],
      // One bar over every camera, so the empty-track sentence names the wall rather than a
      // camera the reader would have to go looking for.
      scope: TimelineScope.wall,
      range: range,
      records: records,
      onRangeChanged: onRangeChanged,
      live: !replay.replaying,
      playhead: replay.playhead,
      onSeek: (at) => unawaited(replay.seekTo(at)),
      onScrub: replay.scrubTo,
      onBackToLive: () => unawaited(replay.backToLive()),
      transport: !replay.replaying
          ? null
          : ReplayTransport(
              compact: isCompact(context),
              playing: replay.playing,
              rate: replay.rate,
              rates: WallReplayController.rates,
              onPlayPause: () =>
                  replay.playing ? replay.pause() : replay.play(),
              onRateChanged: (rate) => unawaited(replay.setRate(rate)),
            ),
    );
  }
}

class _WallHeader extends StatelessWidget {
  const _WallHeader({
    required this.rearranging,
    required this.onRearrangingChanged,
    required this.replaying,
    required this.canRearrange,
    required this.empty,
  });

  final bool rearranging;
  final ValueChanged<bool> onRearrangingChanged;
  final bool replaying;

  /// Whether the registry has any cameras in it at all, which the subtitle and the rearrange
  /// control both answer to: there is no arrangement to make out of nothing.
  final bool empty;

  /// Whether an arrangement can be changed at all, which is false while this account's stored one
  /// has not been read — see `ServalRepository.preferencesKnown`. What is on screen then is a
  /// default pack standing in for it, and an edit made against that stand-in is what would be
  /// stored, so the mode is closed rather than the write being dropped underneath it.
  final bool canRearrange;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        // Expanded, not Flexible: it is what pushes the button to the far end
        // of the header rather than leaving it against the title, where it read
        // as part of the heading. Both lines already ellipsize.
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Home · live view',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: 17,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(height: 2),
              // One line either way: a subtitle that appeared only while
              // rearranging would change the header's height and jog the whole
              // wall down every time the mode is toggled.
              Text(
                rearranging
                    ? 'Drag the grip at a tile’s top-left to move it. '
                          'Drag the corner to resize.'
                    // Said here rather than in a strip above the grid: this line holds its height
                    // whatever it reads, and a banner that appeared on a failed read and vanished
                    // on the retry would jog the whole wall twice for a state that usually lasts a
                    // second.
                    : !canRearrange
                    ? 'Still loading your saved arrangement — this is the default '
                          'until it arrives.'
                    // Below the loading branch on purpose: an account whose arrangement is still
                    // in flight has not been shown to have no cameras, and flashing this at
                    // somebody who has six is the worse of the two wrong answers.
                    : empty
                    ? 'No cameras yet — add one under Settings → Cameras.'
                    : replaying
                    ? 'Every camera at once, at the same moment. '
                          'Drag the timeline to go there.'
                    : 'Every camera at once. Tap a tile to open it.',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(width: 16),
        // One control, and no save — an arrangement is written as it is made.
        // Glyph only, and ghost weight until it is on: arranging the wall is
        // something you do once, so the affordance should not compete with the
        // cameras for attention. The accent outline while rearranging is all
        // that carries the mode, so it stays.
        Tooltip(
          message: empty
              ? 'Nothing to arrange yet'
              : !canRearrange
              ? 'Waiting for your saved arrangement'
              : rearranging
              ? 'Done rearranging'
              : 'Rearrange the wall',
          child: NocturneButton.icon(
            icon: rearranging
                ? PhosphorIconsRegular.check
                : PhosphorIconsRegular.arrowsOutCardinal,
            variant: rearranging
                ? NocturneButtonVariant.primary
                : NocturneButtonVariant.ghost,
            onPressed: canRearrange && !empty
                ? () => onRearrangingChanged(!rearranging)
                : null,
          ),
        ),
      ],
    ),
  );
}

/// The wall on a phone: every camera at the shape it sends, one under another.
///
/// The grid stops being a grid here. Twenty-four columns scaled to 412px puts a
/// standard tile at 88px wide with the gaps taking more room than the cells, so
/// what arrives is six thumbnails nobody can read rather than a small wall. A
/// tile takes the width instead and stands at exactly 16:9, which is what the
/// camera sends.
///
/// The saved arrangement is not discarded, only its spans: [WallGrid.readingOrder]
/// keeps the sequence somebody chose on a desktop, and every tile is drawn the
/// same size regardless of what it was given there.
class _CompactWall extends StatelessWidget {
  const _CompactWall({
    required this.layout,
    required this.repository,
    required this.replay,
    required this.onTapCamera,
    required this.aspects,
    required this.onAspect,
    this.trayHeight = Serval.activitySheetResting,
  });

  static const _gap = 10.0;

  /// The three edges that do not move. The fourth is [_bottom], which the tray decides.
  static const _padding = EdgeInsets.fromLTRB(12, 12, 12, 0);

  final List<TileLayout> layout;
  final ServalRepository repository;
  final WallReplayController replay;
  final ValueChanged<String> onTapCamera;

  /// How tall the tray is at the detent it has **settled** on.
  final double trayHeight;

  /// What the tiles are asked to keep clear at the bottom, which is what the tray is covering.
  ///
  /// It follows the tray down and not up. Down is the ask: a stowed tray gives back 184px of
  /// screen, and a wall that went on reserving room for a sheet that is no longer there would
  /// hold the last tile above a strip of nothing. Up is refused because raising the tray is an
  /// act of reading the feed rather than of rearranging the wall — growing this then would shove
  /// the tiles about behind a sheet that is covering them anyway, which is the same reason the
  /// figure does not follow the live drag.
  double get _bottom => math.min(trayHeight, Serval.activitySheetResting);

  /// What shape each camera's picture has turned out to be, by camera id.
  ///
  /// A camera that has not sent a frame yet is absent, and gets 16:9 — the
  /// shape most of them are, and the one the placeholder underneath is drawn
  /// for.
  final Map<String, double> aspects;

  /// Reports a shape the wall did not know about. Held by the screen rather
  /// than by each tile because the sheet's raised detent is measured against
  /// the first row, and a tile cannot tell the sheet how tall it turned out.
  final void Function(String cameraId, double aspect) onAspect;

  /// How many tiles share a row.
  ///
  /// Two from [kPairedMinWidth] up, which is the figure everything else in the
  /// App pairs at. It is also what saves a phone held sideways: at 892x412 a
  /// single full-width tile would be 501px tall in a 356px space, and there is
  /// no useful way to show a camera taller than the screen.
  ///
  /// Off the window's width for the reason [isCompact] documents.
  static int columnsFor(BuildContext context) =>
      MediaQuery.sizeOf(context).width >= kPairedMinWidth ? 2 : 1;

  /// One tile's width at this size.
  static double tileWidth(BuildContext context) {
    final columns = columnsFor(context);
    return (MediaQuery.sizeOf(context).width -
            _padding.horizontal -
            _gap * (columns - 1)) /
        columns;
  }

  /// Where the raised sheet's top edge belongs, measured from the top of the
  /// screen: under the bar and one whole row of tiles, with the next row peeking
  /// behind it.
  ///
  /// Derived rather than pinned at the design's 340, because "one camera whole
  /// overhead and the next one peeking" is the thing round 11 is claiming, and
  /// it has to stay true at two columns, on a screen of a different height, and
  /// over a row that is as tall as its tallest camera.
  static double raisedSheetTop(
    BuildContext context, {
    required List<String> order,
    required Map<String, double> aspects,
  }) {
    final columns = columnsFor(context);
    final width = tileWidth(context);

    var row = 0.0;
    for (final id in order.take(columns)) {
      final height = width / (aspects[id] ?? Serval.pictureAspect);
      if (height > row) row = height;
    }

    // Nothing to peek at behind a wall that is one row deep — the sheet may as
    // well have the rest of the screen.
    final peek = order.length > columns ? _gap + Serval.activitySheetPeek : 0.0;

    return CompactAppBar.height + _padding.top + row + peek;
  }

  @override
  Widget build(BuildContext context) {
    final columns = columnsFor(context);

    // Nothing on this screen can start a replay, but a window dragged narrow
    // mid-replay must not leave the tiles showing now while the sheet is still
    // reading the playhead. Same gate as the grid's: the mode alone is not an
    // instant, and tiles that answered it would all read NO FOOTAGE.
    final replaying = replay.replaying ? replay : null;

    final tiles = [
      for (final tile in WallGrid.readingOrder(layout))
        if (repository.cameraById(tile.cameraId) case final camera?)
          PairedItem(
            // The camera's own shape, not the wall's. On a desktop a tile's
            // shape comes from its span on the grid, so a doorbell's portrait
            // frame is contained inside a 16:9 cell and the leftover painted.
            // Here there is no grid: a tile is a row to itself and the width is
            // already decided, so the only thing left to decide the height with
            // is the picture. Assuming 16:9 is what left a vertical camera as a
            // narrow strip down the middle of a letterbox.
            AspectRatio(
              aspectRatio: aspects[camera.id] ?? Serval.pictureAspect,
              child: _MeasuredFrame(
                frames: repository.frameNotifier(camera.id),
                onAspect: (aspect) => onAspect(camera.id, aspect),
                // Outside the tile, because an offline one draws no gesture of
                // its own and still has to open its camera.
                child: GestureDetector(
                  onTap: () => onTapCamera(camera.id),
                  child: CameraTile(
                    compact: true,
                    camera: camera,
                    frames: repository.frameNotifier(camera.id),
                    replaying: replaying != null,
                    replay: replaying != null && replaying.hasFootage(camera.id)
                        ? replaying.sourceFor(camera.id)!.player!.buildView()
                        : null,
                  ),
                ),
              ),
            ),
          ),
    ];

    // On the same 240ms `easeOutCubic` the tray reaches a detent on, and inside the scroll view
    // rather than around it: this is room the tiles scroll *into*, not a frame drawn around them.
    // A wall already scrolled to its end follows the shrinking extent down, which is the tiles
    // taking back the screen the tray gave up.
    return TweenAnimationBuilder<double>(
      tween: Tween(end: _bottom),
      duration: const Duration(milliseconds: 240),
      curve: Curves.easeOutCubic,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final (index, row) in pairedRows(
            items: tiles,
            paired: columns == 2,
            gap: _gap,
          ).indexed) ...[if (index > 0) const SizedBox(height: _gap), row],
        ],
      ),
      builder: (context, bottom, child) => SingleChildScrollView(
        padding: _padding.copyWith(bottom: bottom),
        child: child,
      ),
    );
  }
}

/// Passes [child] through untouched and reports the shape of the picture behind
/// it.
///
/// Nothing else in the App knows how a camera is proportioned: the Server
/// publishes no resolution — `Camera.resolutionLabel` is a stub and says so —
/// and the live players that do learn one are not what a wall tile draws. What
/// the wall has is the frame itself, and the decoded frame knows its own size.
///
/// Resolving the same bytes the tile is already drawing is a cache hit rather
/// than a second decode: `Image.memory` and `resolveImageSize` both key on the
/// identical `Uint8List`. That is also why only a *new* buffer is measured —
/// re-resolving every frame at 1 fps per camera would ask the cache the same
/// question all day.
class _MeasuredFrame extends StatefulWidget {
  const _MeasuredFrame({
    required this.frames,
    required this.onAspect,
    required this.child,
  });

  final ValueListenable<Uint8List?>? frames;
  final ValueChanged<double> onAspect;
  final Widget child;

  @override
  State<_MeasuredFrame> createState() => _MeasuredFrameState();
}

class _MeasuredFrameState extends State<_MeasuredFrame> {
  /// The buffer already asked about, so an unchanged frame is not re-resolved.
  Uint8List? _measured;

  @override
  void initState() {
    super.initState();
    widget.frames?.addListener(_onFrame);
    // The first frame may already be in hand, and reporting a shape during the
    // build that mounted this would be setting state on an ancestor mid-build.
    WidgetsBinding.instance.addPostFrameCallback((_) => _onFrame());
  }

  @override
  void didUpdateWidget(_MeasuredFrame old) {
    super.didUpdateWidget(old);
    if (old.frames == widget.frames) return;

    old.frames?.removeListener(_onFrame);
    widget.frames?.addListener(_onFrame);
    _onFrame();
  }

  @override
  void dispose() {
    widget.frames?.removeListener(_onFrame);
    super.dispose();
  }

  void _onFrame() {
    final bytes = widget.frames?.value;
    if (bytes == null || identical(bytes, _measured) || !mounted) return;
    _measured = bytes;

    resolveImageSize(MemoryImage(bytes)).then((size) {
      if (mounted && size != null) widget.onAspect(size.aspectRatio);
    });
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// The wall's measurements for one frame.
///
/// Kept as a value the state can hold onto, because the gesture callbacks have
/// to turn a pixel delta into a grid coordinate long after the `LayoutBuilder`
/// that measured it has returned.
class _Metrics {
  const _Metrics({
    required this.cellWidth,
    required this.rowHeight,
    required this.rows,
  });

  final double cellWidth;
  final double rowHeight;

  /// Including the spare row at the bottom.
  final int rows;

  double get stepX => cellWidth + _TileGrid._gap;
  double get stepY => rowHeight + _TileGrid._gap;

  Rect rectOf(TileLayout tile) => Rect.fromLTWH(
    tile.column * stepX,
    tile.row * stepY,
    tile.columnSpan * cellWidth + (tile.columnSpan - 1) * _TileGrid._gap,
    tile.rowSpan * rowHeight + (tile.rowSpan - 1) * _TileGrid._gap,
  );
}

/// Lays the tiles out on a twelve-column grid, honouring each tile's own span,
/// and — while rearranging — moves and resizes them.
///
/// A plain `GridView` cannot express a tile that is two cells tall next to one
/// that is one, so the grid is measured once and each tile positioned from its
/// [TileLayout]. Rows are unbounded and the wall scrolls, which is what lets it
/// hold however many cameras the Server has rather than the twelve a fixed grid
/// had room for.
///
/// Both gestures live here rather than on [CameraTile] for two reasons: a tile
/// knows nothing about cell sizes, so only the grid can turn a pixel delta into
/// a span; and an offline tile draws no header and no stack, yet still has to
/// be draggable.
class _TileGrid extends StatefulWidget {
  const _TileGrid({
    required this.layout,
    required this.repository,
    required this.rearranging,
    required this.replay,
    required this.onTapCamera,
    required this.onLayoutChanged,
  });

  static const _gap = 12.0;

  /// How close to the viewport edge a drag has to get before the wall scrolls
  /// itself, and how far it scrolls per frame.
  static const _autoScrollMargin = 60.0;
  static const _autoScrollSpeed = 9.0;

  /// The grab area around the 15px handle and 16px grip glyphs.
  static const _handleTarget = 28.0;

  /// Where that grab area sits relative to the tile's top-left, so it lands
  /// over the grip `_TileHeader` draws at 12 horizontal, 11 vertical padding.
  static const _gripInset = 4.0;

  final List<TileLayout> layout;
  final ServalRepository repository;
  final bool rearranging;

  /// The master clock, or null while the wall is live.
  final WallReplayController? replay;

  final ValueChanged<String> onTapCamera;
  final ValueChanged<List<TileLayout>> onLayoutChanged;

  @override
  State<_TileGrid> createState() => _TileGridState();
}

class _TileGridState extends State<_TileGrid>
    with SingleTickerProviderStateMixin {
  final _scroll = ScrollController();
  final _viewportKey = GlobalKey();

  /// Built in [initState] rather than lazily: a `late final` ticker that no
  /// drag ever started would be created by `dispose` disposing it, and
  /// `createTicker` looks up an inherited widget the element no longer has.
  late final Ticker _autoScroll;

  /// Whichever tile the pointer is over — it is the one that shows the corner
  /// handle. One at a time: a corner mark on every tile is six advertisements
  /// for one feature.
  String? _hovered;

  String? _dragging;
  Offset _dragBy = Offset.zero;

  String? _resizing;
  Offset _resizeBy = Offset.zero;

  /// Where the pointer is, for the auto-scroll edge test. Global, because the
  /// viewport it is measured against slides underneath it.
  Offset _pointer = Offset.zero;

  /// Set by [build]. Null before the first layout.
  _Metrics? _metrics;

  @override
  void initState() {
    super.initState();
    _autoScroll = createTicker(_onAutoScrollTick);
  }

  @override
  void didUpdateWidget(_TileGrid old) {
    super.didUpdateWidget(old);
    if (!widget.rearranging && (_dragging != null || _resizing != null)) {
      _cancel();
    }
  }

  @override
  void dispose() {
    _autoScroll.dispose();
    _scroll.dispose();
    super.dispose();
  }

  TileLayout? _tile(String? cameraId) {
    if (cameraId == null) return null;
    for (final tile in widget.layout) {
      if (tile.cameraId == cameraId) return tile;
    }
    return null;
  }

  // ------------------------------------------------------------------ gestures

  Drag _startMove(String cameraId, Offset globalPosition) {
    _pointer = globalPosition;
    // Whatever was in flight is abandoned rather than committed. A multi-drag
    // recognizer tracks every pointer that goes down on it, and a gesture whose
    // end never arrived — a pointer lost off the edge of the window — would
    // otherwise leave the ticker running for the next drag to start a second
    // time. Refusing the new gesture instead would trade that exception for a
    // wall that has quietly stopped accepting drags.
    setState(() {
      _resetGesture();
      _dragging = cameraId;
    });
    _autoScroll.start();

    return _CallbackDrag(
      onMove: (details) {
        _pointer = details.globalPosition;
        setState(() => _dragBy += details.delta);
      },
      onEnd: _commit,
      onCancel: _cancel,
    );
  }

  Drag _startResize(String cameraId) {
    // Takes over from anything in flight, for the reason [_startMove] gives.
    setState(() {
      _resetGesture();
      _resizing = cameraId;
    });

    return _CallbackDrag(
      onMove: (details) => setState(() => _resizeBy += details.delta),
      onEnd: _commit,
      onCancel: _cancel,
    );
  }

  /// Drops whatever gesture is in flight on the floor, uncommitted.
  ///
  /// Not a `setState` of its own: every caller is either already inside one or
  /// about to install a new gesture in the same frame.
  void _resetGesture() {
    if (_autoScroll.isActive) _autoScroll.stop();
    _dragging = null;
    _resizing = null;
    _dragBy = Offset.zero;
    _resizeBy = Offset.zero;
  }

  void _cancel() => setState(_resetGesture);

  void _commit() {
    final next = _candidate();
    _cancel();
    // Null means the gesture was refused, and the tile has already snapped
    // home. Nothing is written, and nothing was lost.
    if (next != null) widget.onLayoutChanged(next);
  }

  /// Scrolls the wall when a drag reaches the top or bottom of the viewport.
  ///
  /// Without this a tile cannot be dragged from the bottom of a tall wall to
  /// the top at all — the pointer runs out of screen first. The scrolled
  /// distance is folded into [_dragBy] because a pan reports pointer movement
  /// only, and content sliding under a still pointer is movement the gesture
  /// never hears about.
  void _onAutoScrollTick(Duration _) {
    final box = _viewportKey.currentContext?.findRenderObject() as RenderBox?;
    if (box == null || !_scroll.hasClients) return;

    final y = box.globalToLocal(_pointer).dy;
    final by = y < _TileGrid._autoScrollMargin
        ? -_TileGrid._autoScrollSpeed
        : y > box.size.height - _TileGrid._autoScrollMargin
        ? _TileGrid._autoScrollSpeed
        : 0.0;
    if (by == 0) return;

    final to = (_scroll.offset + by).clamp(
      0.0,
      _scroll.position.maxScrollExtent,
    );
    final moved = to - _scroll.offset;
    if (moved == 0) return;

    _scroll.jumpTo(to);
    setState(() => _dragBy += Offset(0, moved));
  }

  // ----------------------------------------------------------------- candidate

  /// Where a dragged tile would land: snapped from the tile's own top-left
  /// rather than from the pointer, so a six-wide tile goes where it looks like
  /// it is going.
  (int, int) _dropAt(TileLayout tile, _Metrics m) {
    final at = m.rectOf(tile).shift(_dragBy);
    final (column, row) = WallGrid.clamp(
      (at.left / m.stepX).round(),
      (at.top / m.stepY).round(),
      tile.columnSpan,
      tile.rowSpan,
    );
    // No further down than the first empty row, so a tile released low on a
    // short wall cannot open a canyon of empty rows behind it.
    //
    // Bounded by where the tile's *top* may sit, not by where its bottom lands:
    // clamping the bottom instead stops a two-row tile a whole row short of the
    // one-row tiles, and two heroes side by side could then never be stacked —
    // the row below them is the first empty row, and that is exactly the row
    // the bottom-clamp forbade.
    final last = WallGrid.lastDropRow(widget.layout);
    return (column, row > last ? last : row);
  }

  /// The spans a resize is asking for, inverting the width formula.
  (int, int) _sizeTo(TileLayout tile, _Metrics m) {
    final at = m.rectOf(tile);
    int span(double px, double cell) =>
        ((px + _TileGrid._gap) / (cell + _TileGrid._gap)).round();
    return (
      span(at.width + _resizeBy.dx, m.cellWidth),
      span(at.height + _resizeBy.dy, m.rowHeight),
    );
  }

  /// What the gesture in flight would commit to, or null if it would be
  /// refused. Recomputed rather than remembered, so the highlight on screen can
  /// never disagree with what the drop actually does.
  List<TileLayout>? _candidate() {
    final m = _metrics;
    final tile = _tile(_dragging ?? _resizing);
    if (m == null || tile == null) return null;

    if (_dragging != null) {
      final (column, row) = _dropAt(tile, m);
      return WallGrid.move(
        widget.layout,
        tile.cameraId,
        column: column,
        row: row,
      );
    }

    final (columnSpan, rowSpan) = _sizeTo(tile, m);
    return WallGrid.resize(
      widget.layout,
      tile.cameraId,
      columnSpan: columnSpan,
      rowSpan: rowSpan,
    );
  }

  /// Whether the outline should read as accepted.
  ///
  /// A gesture asking for exactly what is already there is refused by
  /// [WallGrid] — there is nothing for it to return — but holding a tile over
  /// its own place is not a refusal, and painting it red would say it was.
  bool _wouldTake(TileLayout tile, _Metrics m) {
    if (_dragging != null) {
      final (column, row) = _dropAt(tile, m);
      if (column == tile.column && row == tile.row) return true;
    } else {
      final (columnSpan, rowSpan) = _sizeTo(tile, m);
      if (columnSpan == tile.columnSpan && rowSpan == tile.rowSpan) return true;
    }
    return _candidate() != null;
  }

  /// The outline drawn where the gesture would land it.
  Rect? _candidateRect(TileLayout tile, _Metrics m) {
    if (_dragging != null) {
      final (column, row) = _dropAt(tile, m);
      return m.rectOf(tile.copyWith(column: column, row: row));
    }
    final (columnSpan, rowSpan) = _sizeTo(tile, m);
    return m.rectOf(tile.copyWith(columnSpan: columnSpan, rowSpan: rowSpan));
  }

  // --------------------------------------------------------------------- build

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final cellWidth =
          (constraints.maxWidth - _TileGrid._gap * (WallGrid.columns - 1)) /
          WallGrid.columns;

      // A window narrow enough to make a cell vanish would make the snap maths
      // divide by zero and hand back garbage spans. Nothing sensible can be
      // drawn at that size anyway.
      if (cellWidth <= 0) return const SizedBox.shrink();

      final m = _metrics = _Metrics(
        cellWidth: cellWidth,
        rowHeight: WallGrid.rowHeightFor(cellWidth, _TileGrid._gap),
        rows: WallGrid.rowsFor(widget.layout),
      );

      final active = _tile(_dragging ?? _resizing);
      final candidateRect = active == null ? null : _candidateRect(active, m);
      // Never while moving: the handle is drawn at the tile's committed
      // position, so during a drag it would sit as a stray corner mark over the
      // slot the tile has already left.
      final handleFor = widget.rearranging && _dragging == null
          ? _tile(_resizing ?? _hovered)
          : null;

      // The scroll view sizes to its content, and a wall shorter than the
      // window would then be centred by the enclosing row rather than starting
      // under the header. Filling the slot is what keeps the first row at the
      // top and the spare row's empty space at the bottom, where it belongs.
      /// Where a tile is actually drawn: at rest on the grid, or following the
      /// pointer if it is the one in flight.
      Rect drawnRect(TileLayout tile) {
        if (tile.cameraId != active?.cameraId) return m.rectOf(tile);
        return _dragging != null
            ? m.rectOf(tile).shift(_dragBy)
            : _stretched(m.rectOf(tile), _resizeBy);
      }

      return SizedBox(
        height: constraints.maxHeight,
        child: SingleChildScrollView(
          key: _viewportKey,
          controller: _scroll,
          child: SizedBox(
            height: m.rows * m.stepY - _TileGrid._gap,
            child: Stack(
              children: [
                for (final tile in widget.layout)
                  if (tile.cameraId != active?.cameraId)
                    if (widget.repository.cameraById(tile.cameraId)
                        case final camera?)
                      _tileAt(tile, m.rectOf(tile), camera),

                // The tile in flight paints above the ones at rest and follows
                // the pointer raw; the outline over it is what says where it
                // will actually land.
                if (active != null)
                  if (widget.repository.cameraById(active.cameraId)
                      case final camera?)
                    _tileAt(active, drawnRect(active), camera, lifted: true),

                // Above the lifted tile, not under it. The two rects coincide
                // whenever a drag is near its snap point, and an outline drawn
                // underneath is then invisible — which loses exactly the frame
                // where a refusal most needs to be legible.
                if (candidateRect != null)
                  Positioned.fromRect(
                    key: const ValueKey('drop-target'),
                    rect: candidateRect,
                    child: IgnorePointer(
                      child: _DropTarget(valid: _wouldTake(active!, m)),
                    ),
                  ),

                // The grips last, so every one of them — including the grip on
                // the tile being dragged — is above every tile.
                if (widget.rearranging)
                  for (final tile in widget.layout)
                    _gripAt(drawnRect(tile), tile),

                // At the drawn rect, not the committed one, so the corner stays
                // under the pointer that is stretching it.
                if (handleFor != null)
                  _handleAt(drawnRect(handleFor), handleFor),
                //
                // Every child above is keyed. The drop target appears and the
                // active tile changes place mid-gesture, so the number of
                // children before the grips changes as soon as a drag begins —
                // and unkeyed, Flutter would re-match the grips by index, tear
                // down the recognizer holding the very drag in flight, and the
                // tile would sit still under the pointer.
              ],
            ),
          ),
        ),
      );
    },
  );

  /// A resize follows the pointer from the bottom-right, never smaller than a
  /// grabbable square so the tile cannot invert under the corner.
  static Rect _stretched(Rect at, Offset by) => Rect.fromLTWH(
    at.left,
    at.top,
    (at.width + by.dx).clamp(_TileGrid._handleTarget, double.infinity),
    (at.height + by.dy).clamp(_TileGrid._handleTarget, double.infinity),
  );

  Widget _tileAt(
    TileLayout tile,
    Rect rect,
    Camera camera, {
    bool lifted = false,
  }) {
    // Not merely `replay != null`. Opening replay puts the scrubber on screen and nothing else:
    // until a playhead has been placed there is no instant to show, and tiles that answered the
    // mode rather than the playhead would all read NO FOOTAGE the moment you opened it.
    final replay = widget.replay?.replaying ?? false ? widget.replay : null;

    final child = CameraTile(
      camera: camera,
      frames: widget.repository.frameNotifier(camera.id),
      rearranging: widget.rearranging,
      dragging: lifted,
      replaying: replay != null,
      // The surface belongs to the player and outlives the widget tree, so a window reopening
      // mid-replay does not tear it down and flash the tile. Null is the gap: this camera
      // recorded nothing at the playhead, and the tile says so in words.
      replay: replay != null && replay.hasFootage(camera.id)
          ? replay.sourceFor(camera.id)!.player!.buildView()
          : null,
      // Rearranging suppresses the tap outright rather than leaning on drag
      // slop to tell the two apart: the mode has already been declared, and one
      // interpretation of a pointer per mode is far easier to reason about.
      onTap: widget.rearranging ? null : () => widget.onTapCamera(camera.id),
    );

    return Positioned.fromRect(
      // Keyed by camera: the tile in flight changes its position among the
      // stack's children mid-gesture, and unkeyed Flutter would re-match them
      // by index — swapping their hover state and, worse, leaving one camera's
      // last frame painted under another camera's name.
      key: ValueKey(tile.cameraId),
      rect: rect,
      child: MouseRegion(
        onEnter: (_) => setState(() => _hovered = tile.cameraId),
        onExit: (_) {
          if (_hovered == tile.cameraId) setState(() => _hovered = null);
        },
        // The tile itself is not a drag surface: moving is the grip's job and
        // resizing the corner's. So the body of a tile does nothing at all
        // while rearranging, and opens the camera when settled.
        child: widget.rearranging
            ? child
            : GestureDetector(
                onTap: () => widget.onTapCamera(camera.id),
                child: child,
              ),
      ),
    );
  }

  /// The grip at a tile's top-left — the one place a move can start.
  ///
  /// Placed by the grid over the glyph `CameraTile` draws in its header, for
  /// the same reason the corner handle is: only the grid knows a tile's rect,
  /// and an offline tile has no header of its own to hang a gesture from.
  Widget _gripAt(Rect at, TileLayout tile) => Positioned(
    // Keyed, and it matters: this is what holds the drag recognizer.
    key: ValueKey('grip-${tile.cameraId}'),
    left: at.left + _TileGrid._gripInset,
    top: at.top + _TileGrid._gripInset,
    width: _TileGrid._handleTarget,
    height: _TileGrid._handleTarget,
    child: MouseRegion(
      cursor: SystemMouseCursors.move,
      // Not opaque, for the same reason the corner handle is not: an opaque
      // region here would exit the tile's own and take `_hovered` with it.
      opaque: false,
      child: RawGestureDetector(
        behavior: HitTestBehavior.opaque,
        // An immediate multi-drag rather than a pan: it claims the pointer on
        // the first movement, where a pan waits for its larger slop and would
        // lose the arena to the scroll view on any drag with a vertical
        // component. This is what `Draggable` uses, and for exactly that reason.
        gestures: {
          ImmediateMultiDragGestureRecognizer:
              GestureRecognizerFactoryWithHandlers<
                ImmediateMultiDragGestureRecognizer
              >(
                ImmediateMultiDragGestureRecognizer.new,
                (instance) =>
                    instance.onStart = (position) =>
                        _startMove(tile.cameraId, position),
              ),
        },
      ),
    ),
  );

  Widget _handleAt(Rect at, TileLayout tile) => Positioned(
    // Keyed for the same reason the grip is: it holds the resize recognizer.
    key: ValueKey('handle-${tile.cameraId}'),
    left: at.right - _TileGrid._handleTarget - 2,
    top: at.bottom - _TileGrid._handleTarget - 2,
    width: _TileGrid._handleTarget,
    height: _TileGrid._handleTarget,
    child: MouseRegion(
      cursor: SystemMouseCursors.resizeDownRight,
      // Not opaque, or pointing at the corner would exit the tile's own region,
      // clear `_hovered`, and unmount the very handle being pointed at — the
      // press would then land on the tile underneath and move it instead of
      // resizing it. The handle is still the topmost region, so its cursor wins.
      opaque: false,
      // A later child of the stack, so a pointer that lands on the corner never
      // reaches the tile's own recognizer underneath it.
      child: RawGestureDetector(
        behavior: HitTestBehavior.opaque,
        gestures: {
          ImmediateMultiDragGestureRecognizer:
              GestureRecognizerFactoryWithHandlers<
                ImmediateMultiDragGestureRecognizer
              >(
                ImmediateMultiDragGestureRecognizer.new,
                (instance) =>
                    instance.onStart = (_) => _startResize(tile.cameraId),
              ),
        },
        child: Align(
          alignment: Alignment.bottomRight,
          child: ResizeHandle(active: _resizing == tile.cameraId),
        ),
      ),
    ),
  );
}

/// A [Drag] made of three callbacks — what the multi-drag recognizers want back
/// and what Flutter does not otherwise provide.
class _CallbackDrag extends Drag {
  _CallbackDrag({
    required this.onMove,
    required this.onEnd,
    required this.onCancel,
  });

  final ValueChanged<DragUpdateDetails> onMove;
  final VoidCallback onEnd;
  final VoidCallback onCancel;

  @override
  void update(DragUpdateDetails details) => onMove(details);

  @override
  void end(DragEndDetails details) => onEnd();

  @override
  void cancel() => onCancel();
}

/// Where a tile in flight would land: an accent ring when the drop will be
/// taken, a recording-hued one when it will be refused.
class _DropTarget extends StatelessWidget {
  const _DropTarget({required this.valid});

  final bool valid;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(Nocturne.radiusMd),
      // Two pixels, not the system's usual hairline: this ring is read against
      // moving video rather than against a panel.
      border: Border.all(
        width: 2,
        color: valid ? Nocturne.accent400 : Serval.recording,
      ),
      color: valid
          ? Nocturne.mix(Nocturne.accent, 10)
          : Nocturne.mix(Serval.recording, 10),
    ),
  );
}
