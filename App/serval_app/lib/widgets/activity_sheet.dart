import 'package:flutter/physics.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import '../models/activity.dart';
import '../models/camera.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'activity_column.dart';
import 'activity_filter_panel.dart';

/// "What's happening" on a phone: a tray over the picture rather than a column
/// beside it.
///
/// A 376px column at 412px wide is the whole screen, and a fixed band costs a third of the phone
/// whether or not you are reading it. The tray floats, so what it covers keeps its full height and
/// gives room up only while the feed is being read.
///
/// **The detents are what the tray leaves behind it**, not fractions of the screen.
/// [SheetDetent.stowed] is the floor of the travel: the handle and one line saying what is behind
/// it, and nothing else on any screen that has a tray. [peek] is whatever [head] measures — the
/// controls the tray carries — so the picture under it is as big as it can be while the camera is
/// still operable. [SheetDetent.resting] adds [restingHeight] of feed to that: the search field
/// under a thumb with an event or two below it. [SheetDetent.raised] is the ceiling, where the
/// feed is the subject.
///
/// **The filter is the tray.** On a desktop the panel floats inside the column over the feed; at
/// this width that would be a column inside a column. So it rises with the tray, dims what is
/// behind it, and puts *Done* in a bar the thumb already reaches — the same bar that repeats the
/// count, so what you chose is visible before you dismiss it rather than after.
class ActivitySheet extends StatefulWidget {
  const ActivitySheet({
    super.key,
    required this.filter,
    required this.facets,
    required this.shown,
    required this.body,
    this.head,
    this.cameras = const [],
    this.onFilterChanged,
    this.controller,
    this.restingHeight = Serval.activitySheetResting,
    this.stowedHeight = Serval.activitySheetStowed,
    this.raisedHeight,
    this.topInset = Serval.activitySheetFilterTop,
    this.floor = Serval.activitySheetFloor,
    this.bottomInset = 0,
  });

  final ActivityFilter filter;

  /// What the pool the rows were drawn from contains, for the counts beside every facet. Passed
  /// rather than derived because [shown] counts what survived the filter and this counts what it
  /// was applied to.
  final ActivityFacets facets;

  /// How many rows [body] is drawing, for the readout in the head.
  final int shown;

  /// The rows, and whatever is pinned above or below them. Given the detent so that anything it
  /// only has room for at one height can ask — and asked about the **settled** detent rather than
  /// the live height, because a strip that appears and disappears under a moving finger is worse
  /// than one that is not there at all.
  final Widget Function(BuildContext context, SheetDetent detent) body;

  /// Controls carried above the feed's own head, and the thing [SheetDetent.peek] is measured
  /// from. Null on a screen whose tray is only a feed.
  final Widget? head;

  /// Every camera on the wall, for the *Which camera* facet. Empty on a screen already showing
  /// one, where that facet could only ever mean "yes".
  final List<Camera> cameras;

  final ValueChanged<ActivityFilter>? onFilterChanged;

  /// Drives the tray from outside, and reports where it came to rest.
  final ActivitySheetController? controller;

  /// How much feed [SheetDetent.resting] shows — measured from [peek], so the same figure means
  /// the same thing whether or not the tray is carrying controls.
  final double restingHeight;

  /// What is left of the tray at [SheetDetent.stowed] — the handle, and the line under it that
  /// names the feed. Not measured like [peek] is: this one is a bar the tray draws itself, so the
  /// figure and the thing are the same size by construction.
  final double stowedHeight;

  /// What [SheetDetent.raised] rises to. Null takes the whole room the tray has, which is what a
  /// caller with nothing behind it worth protecting wants.
  final double? raisedHeight;

  /// How much of what is behind the tray the filter leaves uncovered.
  final double topInset;

  /// What a tray must always leave behind it, below which it stops being over anything.
  final double floor;

  /// A strip at the bottom the tray never reaches over — the phone's *Hold to talk*, which is the
  /// one control that has to be under a thumb whatever else is happening.
  final double bottomInset;

  @override
  State<ActivitySheet> createState() => _ActivitySheetState();
}

/// The bar the tray is reduced to at [SheetDetent.stowed].
///
/// It says the same words as the head it covers, so a finder for those words matches twice at that
/// height and neither match tells you which one is on the screen. Named here for the tests that
/// have to ask.
const activityStowBarKey = Key('activity-stow-bar');

/// Where the tray can come to rest, lowest first.
///
/// [SheetDetent.filtering] is not reachable by dragging — it is where the *Filter* button puts the
/// tray and where *Done* takes it from. [SheetDetent.peek] exists only where there is a [head] to
/// measure it against; [SheetDetent.stowed] exists everywhere, because every tray is over
/// something somebody may want the whole screen for.
enum SheetDetent { stowed, peek, resting, raised, filtering }

/// Drives the tray from outside, and reports where it came to rest.
///
/// Two-way: the tray writes the detent it settles at, and [goTo] moves it. That is what lets a
/// screen put the tray down in answer to something the tray itself did — clicking a row asks to
/// see the footage, and footage behind a raised tray is not being seen.
///
/// It also carries the resolved [detents], so that what the tray is covering can be laid out
/// against the height it is **settled** on rather than the one it is passing through. A picture
/// that followed the live drag would re-box the video forty times a second and re-anchor a pinch
/// in the middle of one.
class ActivitySheetController extends ChangeNotifier {
  SheetDetent _detent = SheetDetent.resting;
  SheetDetents _detents = const SheetDetents(
    stowed: 0,
    resting: 0,
    raised: 0,
    filtering: 0,
  );

  SheetDetent get detent => _detent;
  SheetDetents get detents => _detents;

  /// How tall the tray is at the detent it has settled on, or is on its way to.
  double get height => _detents[_detent];

  /// Move the tray. It listens, and answers by settling there.
  void goTo(SheetDetent detent) => _report(detent: detent);

  /// Written by the tray: where it went, and what the room it has works out to.
  void _report({SheetDetent? detent, SheetDetents? detents}) {
    final moved = detent != null && detent != _detent;
    final resized = detents != null && detents != _detents;
    if (!moved && !resized) return;

    if (detent != null) _detent = detent;
    if (detents != null) _detents = detents;
    notifyListeners();
  }
}

/// The heights, resolved against the room actually available.
///
/// A value class so the arithmetic can be checked without pumping a frame, and so a rotation is a
/// recomputation rather than a special case. Everything is clamped in order: the tray always
/// leaves its floor behind, each detent is never below the one under it, and none of them passes
/// the ceiling.
class SheetDetents {
  const SheetDetents({
    required this.stowed,
    this.peek,
    required this.resting,
    required this.raised,
    required this.filtering,
  });

  factory SheetDetents.of({
    required double body,
    required double resting,
    double stowed = Serval.activitySheetStowed,
    double? peek,
    double? raised,
    double topInset = Serval.activitySheetFilterTop,
    double floor = Serval.activitySheetFloor,
  }) {
    final ceiling = (body - topInset).clamp(0.0, body);
    final away = stowed.clamp(0.0, ceiling);
    final low = peek?.clamp(away, ceiling <= away ? away : ceiling);

    // The bottom of the travel, which is [peek] where there is one and [stowed] where there is
    // not — everything above is stacked on it and nothing may fall below it.
    final bottom = low ?? away;
    // Measured from [peek] rather than from nothing: `restingHeight` is how much *feed* the
    // detent shows, and a tray carrying controls has to clear them first for that to be true.
    // From nothing where there is no head, so a tray that carries only a feed shows the figure
    // asked for rather than the figure plus a bar.
    final base = low ?? 0.0;

    final rest = (base + resting).clamp(
      bottom,
      (body - floor).clamp(bottom, body),
    );
    final rise = (raised ?? ceiling).clamp(
      rest,
      ceiling <= rest ? rest : ceiling,
    );

    return SheetDetents(
      stowed: away,
      peek: low,
      resting: rest,
      raised: rise,
      filtering: ceiling < rise ? rise : ceiling,
    );
  }

  final double stowed;

  /// Null where there is no head to measure it from, which is what makes this a three-detent tray.
  final double? peek;
  final double resting;
  final double raised;
  final double filtering;

  /// The detents a drag may come to rest at, lowest first.
  List<SheetDetent> get draggable => [
    SheetDetent.stowed,
    if (peek != null) SheetDetent.peek,
    SheetDetent.resting,
    SheetDetent.raised,
  ];

  /// The tallest the tray can ever be, and so the height its contents are laid out at.
  double get ceiling => filtering;

  double operator [](SheetDetent detent) => switch (detent) {
    SheetDetent.stowed => stowed,
    SheetDetent.peek => peek ?? resting,
    SheetDetent.resting => resting,
    SheetDetent.raised => raised,
    SheetDetent.filtering => filtering,
  };

  /// Which draggable detent [height] is nearest to.
  SheetDetent nearest(double height) {
    var best = SheetDetent.resting;
    var gap = double.infinity;

    for (final detent in draggable) {
      final distance = (this[detent] - height).abs();
      if (distance < gap) {
        gap = distance;
        best = detent;
      }
    }

    return best;
  }

  /// The next detent past [height] in the direction thrown — so a fling steps one place rather
  /// than crossing the whole tray however hard it was meant.
  SheetDetent beyond(double height, {required bool up}) {
    final order = up ? draggable : draggable.reversed.toList();

    for (final detent in order) {
      final at = this[detent];
      // A pixel of slack, so a fling begun from a settled detent leaves it rather than
      // re-targeting the height it is already sitting at.
      if (up ? at > height + 1 : at < height - 1) return detent;
    }

    return order.last;
  }

  @override
  bool operator ==(Object other) =>
      other is SheetDetents &&
      other.stowed == stowed &&
      other.peek == peek &&
      other.resting == resting &&
      other.raised == raised &&
      other.filtering == filtering;

  @override
  int get hashCode => Object.hash(stowed, peek, resting, raised, filtering);
}

class _ActivitySheetState extends State<ActivitySheet>
    with SingleTickerProviderStateMixin {
  /// The tray's height in pixels, as an animation.
  ///
  /// Unbounded rather than a 0–1 drive: the value *is* the height, so a window that changes size
  /// mid-gesture recomputes its detents and re-targets rather than reinterpreting a fraction
  /// against a box that moved.
  late final AnimationController _height = AnimationController.unbounded(
    vsync: this,
    value: widget.restingHeight,
  );

  /// Owned here rather than by the field, so the caret survives the rebuild every keystroke
  /// causes — the query goes up to the screen, comes back down as a new filter, and rebuilds this
  /// whole subtree on the way.
  final _search = TextEditingController();

  /// Holds focus while the filter is up, so Escape reaches it.
  ///
  /// Compact is a phone most of the time and a narrow browser window the rest, and in the second
  /// one Escape is what a covering surface is expected to answer to. Taking focus costs nothing:
  /// the head — and with it the search field — is not in the tree while the filter is up.
  final _escape = FocusNode(debugLabel: 'activity filter');

  SheetDetent _detent = SheetDetent.resting;

  /// Whether a finger is on the tray.
  ///
  /// Tracked rather than inferred, because the obvious inference is wrong: a drag moves the tray by
  /// assigning [_height]`.value`, and that setter stops the controller — so `isAnimating` is false
  /// for the whole of a gesture, and the re-target in [build] cannot tell a dragged tray from a
  /// settled one without being told. See the guard there.
  ///
  /// A plain field and not `setState`: it decides whether a correction runs, and draws nothing.
  bool _dragging = false;

  /// Where *Done* goes back to. The detent you opened the filter from, which is the least
  /// surprising place to be returned to.
  SheetDetent _cameFrom = SheetDetent.resting;

  /// Whether the panel is in the tree at all.
  ///
  /// Kept up through the close so the panel does not blink out a frame before the tray finishes
  /// coming down, and taken out afterwards so a filter left open is never waiting behind a
  /// resting tray.
  bool _filterMounted = false;

  /// What [ActivitySheet.head] laid out to, which is what [SheetDetent.peek] is.
  ///
  /// Measured rather than totted up from the heights of the controls inside it: the row of
  /// buttons comes and goes, a save writes a line under it, and the scrubber grows a second row
  /// the moment replay starts. A figure standing in for all that is wrong in exactly the states
  /// somebody is in when they notice.
  double? _headHeight;

  SheetDetents _detents = const SheetDetents(
    stowed: 0,
    resting: 0,
    raised: 0,
    filtering: 0,
  );

  bool get _filtering => _detent == SheetDetent.filtering;

  @override
  void initState() {
    super.initState();
    widget.controller?.addListener(_onControllerChanged);
  }

  @override
  void didUpdateWidget(ActivitySheet old) {
    super.didUpdateWidget(old);

    if (old.controller != widget.controller) {
      old.controller?.removeListener(_onControllerChanged);
      widget.controller?.addListener(_onControllerChanged);
    }

    // A tray that stops carrying a head stops having a peek to sit at.
    if (old.head != null && widget.head == null) {
      _headHeight = null;
      if (_detent == SheetDetent.peek) _goTo(SheetDetent.resting);
    }

    // *Clear* is the one thing that empties the query from outside the field.
    if (widget.filter.query != _search.text) _search.text = widget.filter.query;
  }

  @override
  void dispose() {
    widget.controller?.removeListener(_onControllerChanged);
    _height.dispose();
    _search.dispose();
    _escape.dispose();
    super.dispose();
  }

  void _onControllerChanged() {
    final wanted = widget.controller!.detent;
    if (wanted != _detent) _goTo(wanted);
  }

  /// The head's height, arriving after the layout that produced it.
  ///
  /// A frame late by construction — it is read during build and written during layout — so the
  /// tray corrects on the next frame rather than in the one where the head changed. That is
  /// imperceptible at any height and exact at every one, which is the trade a figure written down
  /// in advance gets the wrong way round.
  /// The build this schedules resolves the detents against the new figure, and the re-target that
  /// follows it is the one in [build] — which is the only place the resolved heights exist.
  void _onHeadMeasured(double height) {
    if (!mounted || _headHeight == height) return;
    setState(() => _headHeight = height);
  }

  void _goTo(SheetDetent detent, {double velocity = 0}) {
    if (detent == SheetDetent.filtering) {
      _filterMounted = true;
      _escape.requestFocus();
    } else if (_filtering) {
      // Unmounted on arrival rather than now — see [_filterMounted].
      _height.addStatusListener(_unmountFilter);
    }

    setState(() => _detent = detent);
    widget.controller?._report(detent: detent);

    final target = _detents[detent];

    if (velocity == 0) {
      _height.animateTo(
        target,
        duration: const Duration(milliseconds: 240),
        curve: Curves.easeOutCubic,
      );
      return;
    }

    // Seeded from the gesture, so a fling reads as one rather than as the same easing however
    // hard it was thrown.
    _height
        .animateWith(
          SpringSimulation(
            SpringDescription.withDampingRatio(
              mass: 1,
              stiffness: 500,
              ratio: 1.1,
            ),
            _height.value,
            target,
            velocity,
          ),
        )
        // A spring stops when it is close enough, which leaves the tray a fraction of a pixel
        // off. The detent is a figure the wall pads against and the tests measure, so it has to
        // be the figure and not nearly it.
        .then((_) {
          if (mounted && !_height.isAnimating && _detent == detent) {
            _height.value = target;
          }
        });
  }

  void _unmountFilter(AnimationStatus status) {
    if (status != AnimationStatus.completed &&
        status != AnimationStatus.dismissed) {
      return;
    }
    _height.removeStatusListener(_unmountFilter);
    if (mounted && !_filtering) setState(() => _filterMounted = false);
  }

  void _onDragStart(DragStartDetails details) => _dragging = true;

  /// Cleared here as well as in [_onDragEnd], because a recognizer that loses the arena after the
  /// pointer went down cancels without ever having started or ended. A flag left set there would
  /// switch the re-target off for good.
  void _onDragCancel() => _dragging = false;

  void _onDragUpdate(DragUpdateDetails details) {
    // The two ends the drag may reach. While filtering, down is the way out — dragging the tray
    // away is the same gesture as *Done*.
    final low = _filtering
        ? _detents[_cameFrom]
        : _detents[_detents.draggable.first];
    final high = _filtering ? _detents.filtering : _detents.raised;

    _height.value = (_height.value - details.delta.dy).clamp(low, high);
  }

  void _onDragEnd(DragEndDetails details) {
    // Before the `_goTo` below rather than after, and safe either way: `_goTo` starts the animation
    // synchronously, so `isAnimating` has taken over as the thing holding the re-target off by the
    // time any frame is built.
    _dragging = false;

    // Metres per second at the pointer; the controller wants pixels per millisecond, and up the
    // screen is a decreasing dy but a growing height.
    final velocity = -(details.primaryVelocity ?? 0);

    if (_filtering) {
      final open = velocity.abs() >= 400
          ? velocity > 0
          : _height.value > (_detents.filtering + _detents[_cameFrom]) / 2;

      _goTo(
        open ? SheetDetent.filtering : _cameFrom,
        velocity: velocity / 1000,
      );
      return;
    }

    final detent = velocity.abs() >= 400
        ? _detents.beyond(_height.value, up: velocity > 0)
        : _detents.nearest(_height.value);

    _goTo(detent, velocity: velocity / 1000);
  }

  /// A tap on the grabber advances the tray, and comes back down from the top.
  ///
  /// One place at a time, so a stowed tray on a camera comes back to the controls before it comes
  /// back to the feed — the same order the drag passes through, and the reason a tap is a way out
  /// of [SheetDetent.stowed] that needs no aim.
  void _toggle() => _goTo(switch (_detent) {
    SheetDetent.stowed =>
      _detents.peek == null ? SheetDetent.resting : SheetDetent.peek,
    SheetDetent.peek => SheetDetent.resting,
    SheetDetent.resting => SheetDetent.raised,
    _ => SheetDetent.resting,
  });

  void _openFilter() {
    _cameFrom = _filtering ? _cameFrom : _detent;
    _goTo(SheetDetent.filtering);
  }

  void _closeFilter() => _goTo(_cameFrom);

  /// Back puts the tray down before it leaves the screen.
  ///
  /// A tray that is not a route gets nothing for free here, and on web this is the browser's own
  /// back button. Filtering is a mode and unwinds first; a raised tray is covering the thing the
  /// screen is for, and giving that back is a better answer to *back* than leaving.
  bool get _popTakesAStep =>
      _filtering || _detent == SheetDetent.raised && widget.head != null;

  void _pop() {
    if (_filtering) {
      _closeFilter();
    } else {
      _goTo(SheetDetent.resting);
    }
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final body = (constraints.maxHeight - widget.bottomInset).clamp(
        0.0,
        constraints.maxHeight,
      );

      _detents = SheetDetents.of(
        body: body,
        resting: widget.restingHeight,
        stowed: widget.stowedHeight,
        peek: _headHeight == null ? null : _headHeight! + _grabberHeight,
        raised: widget.raisedHeight,
        topInset: widget.topInset,
        floor: widget.floor,
      );

      // A window resized under a settled tray re-targets rather than being left at a height that
      // is no longer any detent, and whatever is laid out against the tray hears the new figures.
      // Both deferred, because this runs during layout.
      //
      // Never under a finger. A drag is the third way the tray moves — after an animation and a
      // resize — and it is the one that leaves no trace on the controller: `_onDragUpdate` assigns
      // `_height.value`, which stops the controller, so `isAnimating` is false and the height is
      // off its detent by exactly the distance dragged. Both halves of the old test therefore read
      // a gesture in progress as a tray to be put back, and a rebuild arriving mid-drag — an alert,
      // a detection, a playhead tick, a rotation — snapped it home under the finger a frame after
      // every move. [_dragging] is what tells the two apart.
      final controller = widget.controller;
      final settled = _detents[_detent];

      // Gated on there being a controller at all: a tray built without one compared `null` against
      // resolved detents, which is unequal forever, and so re-targeted on every rebuild it ever had
      // rather than on the resizes this is for.
      final resized = controller != null && controller.detents != _detents;

      if (!_dragging &&
          (resized || !_height.isAnimating && _height.value != settled)) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted || _dragging) return;
          controller?._report(detents: _detents);
          if (!_height.isAnimating) _height.value = _detents[_detent];
        });
      }

      return PopScope(
        canPop: !_popTakesAStep,
        onPopInvokedWithResult: (didPop, _) {
          if (!didPop && _popTakesAStep) _pop();
        },
        child: Focus(
          focusNode: _escape,
          onKeyEvent: (node, event) {
            if (!_filtering ||
                event is! KeyDownEvent ||
                event.logicalKey != LogicalKeyboardKey.escape) {
              return KeyEventResult.ignored;
            }
            _closeFilter();
            return KeyEventResult.handled;
          },
          // The Stack is inside the builder because `Positioned` has to be a Stack's own child;
          // the tray's subtree rides through as `child` and is not rebuilt per frame.
          child: AnimatedBuilder(
            animation: _height,
            builder: (context, child) => Stack(
              children: [
                // What is behind goes back under the filter, and tapping it is a way out. Nothing
                // at all at the draggable detents: the tray is chrome there, and what it covers
                // stays live.
                Positioned.fill(
                  child: IgnorePointer(
                    ignoring: !_filtering,
                    child: GestureDetector(
                      onTap: _filtering ? _closeFilter : null,
                      child: AnimatedOpacity(
                        opacity: _filtering ? 1 : 0,
                        duration: const Duration(milliseconds: 240),
                        child: const ColoredBox(color: Color(0x990A0B14)),
                      ),
                    ),
                  ),
                ),
                Positioned(
                  left: 0,
                  right: 0,
                  bottom: widget.bottomInset,
                  height: _height.value.clamp(0.0, body),
                  child: child!,
                ),
              ],
            ),
            child: _sheet(),
          ),
        ),
      );
    },
  );

  /// The tray's contents, laid out at the tallest it can ever be and clipped by the box it
  /// happens to be in.
  ///
  /// Nothing re-flows as the tray moves — dragging reveals rows that are already laid out rather
  /// than re-measuring the feed forty times a second. It is also what makes [SheetDetent.peek]
  /// possible at all: at that height the feed's own head does not fit, and a `Column` asked to
  /// fit it would overflow rather than be cut off.
  Widget _sheet() => ClipRRect(
    // Bigger than `Nocturne.radiusLg`, which is a card's corner. This is an edge of the screen
    // folding back, and it is the only one in the app.
    borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
    child: DecoratedBox(
      decoration: BoxDecoration(
        color: _filtering ? Serval.overlay : Serval.rail,
        border: Border(
          top: BorderSide(
            color: Nocturne.mix(Nocturne.text, _filtering ? 14 : 12),
          ),
        ),
        // The one upward-lit surface in the App. Nocturne's elevation is all cast downward, which
        // over a tray would light what it covers.
        boxShadow: const [
          BoxShadow(
            color: Color(0x8C000000),
            blurRadius: 44,
            offset: Offset(0, -18),
          ),
        ],
      ),
      child: ClipRect(
        child: OverflowBox(
          alignment: Alignment.topCenter,
          minHeight: _detents.ceiling,
          maxHeight: _detents.ceiling,
          child: Stack(
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _grabber(),
                  Expanded(child: widget.body(context, _detent)),
                ],
              ),
              // Over the head rather than instead of it, and only while the tray is low enough
              // for there to be nothing else to see. Swapping the two around would re-flow the
              // tray mid-drag, and putting this line in the column would mean "What's happening"
              // sat above the camera's own controls at every height — a title over the wrong
              // thing to save a widget.
              Positioned(
                top: _grabberHeight,
                left: 0,
                right: 0,
                height: (_detents.stowed - _grabberHeight).clamp(
                  0.0,
                  _detents.ceiling,
                ),
                child: _stowBar(),
              ),
              // Over the feed rather than in place of it, so the rows keep their scroll position
              // across a filter session — and so the panel's own ground is the only thing between
              // you and them.
              if (_filterMounted && widget.onFilterChanged != null)
                Positioned.fill(
                  child: ActivityFilterPanel(
                    compact: true,
                    filter: widget.filter,
                    facets: widget.facets,
                    cameras: widget.cameras,
                    shown: widget.shown,
                    onChanged: widget.onFilterChanged!,
                    onDone: _closeFilter,
                  ),
                ),
            ],
          ),
        ),
      ),
    ),
  );

  /// The pull handle's own zone. 18px is well under the 44 the rest of the compact design aims
  /// at, which is why the drag reaches down over everything under it as well.
  static const _grabberHeight = 18.0;

  /// How far off [SheetDetent.stowed] the bar has faded out entirely.
  ///
  /// Short, and shorter than the gap to the detent above on every screen: the bar is standing in
  /// for the head behind it, and the moment there is enough tray to read the head in, the stand-in
  /// should be gone rather than ghosting over it.
  static const _stowFade = 40.0;

  /// What the tray says for itself when it has been pushed all the way down.
  ///
  /// Over the head rather than in the column with it, and its face leaves the tree the moment it
  /// is invisible — so the controls it covers are hittable at every height that shows them, and
  /// "What's happening" is in the tree once wherever the real one is on the screen.
  ///
  /// It takes the same drags and the same tap the handle does. At this height the handle is a
  /// quarter of what the tray is, and a target that small is not the only way back.
  ///
  /// **The detector outlives the face it sits under.** A recognizer that leaves the tree mid-drag
  /// is disposed, and disposing the one that won the gesture cancels it — so taking the whole bar
  /// out at the end of the fade killed every drag up from stowed exactly [_stowFade] in, with the
  /// search field half revealed, and the finger had to be lifted and put down to carry on. What
  /// comes and goes is the face; what stays is this, hidden from hit tests instead of removed.
  Widget _stowBar() => AnimatedBuilder(
    animation: _height,
    builder: (context, _) {
      final opacity =
          1 - ((_height.value - _detents.stowed) / _stowFade).clamp(0.0, 1.0);

      return IgnorePointer(
        ignoring: opacity <= 0,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: _toggle,
          onVerticalDragStart: _onDragStart,
          onVerticalDragUpdate: _onDragUpdate,
          onVerticalDragEnd: _onDragEnd,
          onVerticalDragCancel: _onDragCancel,
          child: opacity <= 0
              ? const SizedBox.expand()
              : Opacity(
                  key: activityStowBarKey,
                  opacity: opacity,
                  // Opaque, so what it is standing in for is revealed by the fade rather than
                  // showing through it.
                  child: ColoredBox(
                    color: Serval.rail,
                    child: Center(
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 16),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.baseline,
                          textBaseline: TextBaseline.alphabetic,
                          children: [
                            const Text(
                              'What’s happening',
                              style: TextStyle(
                                fontFamily: Nocturne.fontHeading,
                                fontSize: 16,
                                fontWeight: Nocturne.headingWeight,
                                color: Nocturne.text,
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Text(
                                activityReadout(
                                  widget.filter,
                                  widget.shown,
                                  widget.facets.total,
                                ),
                                textAlign: TextAlign.right,
                                overflow: TextOverflow.ellipsis,
                                style: TextStyle(
                                  fontFamily: Nocturne.fontBody,
                                  fontSize: 12.5,
                                  color: Nocturne.mix(Nocturne.text, 50),
                                ),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
        ),
      );
    },
  );

  /// The handle, the controls the tray carries, and the feed's head — one drag target.
  ///
  /// The drag stops above the feed itself, where a downward swipe has to be a scroll. It does
  /// **not** stop above the scrubber: a track that takes horizontal drags and a tray that takes
  /// vertical ones are two recognizers in one arena, and the direction of the gesture is what
  /// decides between them.
  ///
  /// The **tap** covers less. 18px is well under the 44 the rest of the compact design aims at, so
  /// it reaches over the feed's own head as well — but never over [ActivitySheet.head], where it
  /// would be a second meaning for pressing *Snapshot*. The search field and *Filter* are their
  /// own targets and win the arena, being deeper.
  Widget _grabber() => GestureDetector(
    behavior: HitTestBehavior.opaque,
    onVerticalDragStart: _onDragStart,
    onVerticalDragUpdate: _onDragUpdate,
    onVerticalDragEnd: _onDragEnd,
    onVerticalDragCancel: _onDragCancel,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _tappable(
          SizedBox(
            height: _grabberHeight,
            child: Center(
              child: Container(
                width: 38,
                height: 4,
                decoration: BoxDecoration(
                  color: Nocturne.mix(Nocturne.text, 28),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
          ),
        ),
        if (!_filtering) ...[
          if (widget.head case final head?)
            _MeasureHeight(onChanged: _onHeadMeasured, child: head),
          _tappable(
            _SheetHead(
              filter: widget.filter,
              facets: widget.facets,
              shown: widget.shown,
              cameras: widget.cameras,
              search: _search,
              onFilterChanged: widget.onFilterChanged,
              onOpenFilters: widget.onFilterChanged == null
                  ? null
                  : _openFilter,
            ),
          ),
        ],
      ],
    ),
  );

  Widget _tappable(Widget child) => GestureDetector(
    behavior: HitTestBehavior.opaque,
    onTap: _filtering ? null : _toggle,
    child: child,
  );
}

/// Reports what its child laid out to.
///
/// The report is scheduled after the frame rather than raised during it, because the figure is
/// read during build and a notification out of layout would be a rebuild inside a layout.
class _MeasureHeight extends SingleChildRenderObjectWidget {
  const _MeasureHeight({required this.onChanged, required Widget super.child});

  final ValueChanged<double> onChanged;

  @override
  RenderObject createRenderObject(BuildContext context) =>
      _RenderMeasureHeight(onChanged);

  @override
  void updateRenderObject(
    BuildContext context,
    _RenderMeasureHeight renderObject,
  ) => renderObject.onChanged = onChanged;
}

class _RenderMeasureHeight extends RenderProxyBox {
  _RenderMeasureHeight(this.onChanged);

  ValueChanged<double> onChanged;
  double? _reported;

  @override
  void performLayout() {
    super.performLayout();

    if (size.height == _reported) return;
    _reported = size.height;

    final height = size.height;
    WidgetsBinding.instance.addPostFrameCallback((_) => onChanged(height));
  }
}

/// What the tray is holding, in one line.
///
/// Unlike the desktop header this says something at rest, because at rest one event is visible and
/// the reader cannot see what the list is. "Everything, as it happens" is the fact that replaces
/// the arithmetic of "64 of the 64".
///
/// Shared with the stowed bar rather than written twice: the bar is standing in for this line, and
/// a stand-in that could say something else would be a second answer to the same question.
String activityReadout(ActivityFilter filter, int shown, int total) =>
    filter.isEmpty ? 'Everything, as it happens' : eventsInView(shown, total);

/// The fixed head above the feed: what the tray is, what it is holding, and the two controls that
/// narrow it.
///
/// Not [ActivityHeader] with a flag. The four controls are the same ones, but the arrangement is
/// not: the readout comes up beside the title instead of sitting under the chips, it says
/// something at rest where the desktop says nothing, and *Clear* moves to the end of the chip row.
/// That is a different layout, and a layout behind a boolean is a switch.
///
/// The chips are here rather than in the feed because the tray can be pushed down: this is the
/// lowest row that stays on screen above [SheetDetent.peek], so it is the only place a narrowed
/// column can announce itself.
class _SheetHead extends StatelessWidget {
  const _SheetHead({
    required this.filter,
    required this.facets,
    required this.shown,
    required this.search,
    this.cameras = const [],
    this.onFilterChanged,
    this.onOpenFilters,
  });

  final ActivityFilter filter;
  final ActivityFacets facets;
  final int shown;
  final TextEditingController search;
  final List<Camera> cameras;
  final ValueChanged<ActivityFilter>? onFilterChanged;
  final VoidCallback? onOpenFilters;

  Map<String, String> get _cameraNames => {
    for (final camera in cameras) camera.id: camera.name,
  };

  @override
  Widget build(BuildContext context) {
    final chips = filter.chips(_cameraNames);

    return Container(
      padding: const EdgeInsets.fromLTRB(16, 2, 16, 12),
      decoration: BoxDecoration(
        border: Border(
          // Only once the chips are there. Two lines is a head; three is a
          // block, and a block wants an edge under it.
          bottom: chips.isEmpty
              ? BorderSide.none
              : BorderSide(color: Serval.hairline),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              const Text(
                'What’s happening',
                style: TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: 16,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  activityReadout(filter, shown, facets.total),
                  textAlign: TextAlign.right,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 50),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: ActivitySearchField(
                  compact: true,
                  controller: search,
                  onChanged: onFilterChanged == null
                      ? (_) {}
                      : (value) =>
                            onFilterChanged!(filter.copyWith(query: value)),
                ),
              ),
              const SizedBox(width: 8),
              ActivityFilterButton(
                compact: true,
                count: filter.facetCount,
                onTap: onOpenFilters ?? () {},
              ),
            ],
          ),
          if (chips.isNotEmpty) ...[
            const SizedBox(height: 10),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                for (final chip in chips)
                  ActivityFilterChip(
                    compact: true,
                    label: chip.label,
                    onRemove: onFilterChanged == null
                        ? () {}
                        : () => onFilterChanged!(chip.without),
                  ),
                // Last in the wrap rather than on a line of its own: everything
                // here is one thing the filter is doing, and *Clear* is the one
                // that undoes all of them.
                ActivityLink(
                  'Clear',
                  fontSize: 12.5,
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
