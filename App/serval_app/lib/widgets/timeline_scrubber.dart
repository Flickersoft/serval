import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/activity.dart';
import '../models/timeline.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'timeline_range_panel.dart';

/// The day, under the video: where footage exists, where something happened,
/// and where "now" is — and the control that drags the stage back into the
/// recording.
///
/// The window comes from the repository, which merges the Server's
/// `GET /api/cameras/{id}/coverage` for the footage with the telemetry reads
/// for the marks. This widget's whole job on the way back out is turning an x
/// into an instant; deciding what is playable at that instant, and opening it,
/// belongs to the screen's replay controller.
/// How many cameras a scrubber's timeline spans.
///
/// Only the copy depends on it. A timeline with nothing recorded under it has to say so, and the
/// sentence that does it names its subject — "this camera" is right under one camera and wrong
/// under the wall, where the same bar stands for every camera at once.
enum TimelineScope { camera, wall }

class TimelineScrubber extends StatefulWidget {
  const TimelineScrubber({
    super.key,
    required this.window,
    required this.range,
    this.onRangeChanged,
    this.live = true,
    this.playhead,
    this.onSeek,
    this.onScrub,
    this.onBackToLive,
    this.lanes = const [],
    this.transport,
    this.dense = false,
    this.records = true,
    this.scope = TimelineScope.camera,
  });

  /// What this timeline covers, which is what the "nothing is kept" line has to name.
  ///
  /// Defaulted to [TimelineScope.camera] because that is what most of the App puts a scrubber
  /// under; the wall is the one caller spanning more than one camera.
  final TimelineScope scope;

  /// Whether anything on this timeline is being recorded at all.
  ///
  /// False and the bar is empty for a reason no amount of waiting will change, so it says so
  /// instead of inviting a drag. An empty track reads as "a quiet day" — which is the one thing it
  /// is not, and the misreading costs someone a real hunt through the wrong day for footage that
  /// was never written.
  final bool records;

  /// Drops the header's sentence and tightens the gap, for the one place this
  /// sits over the picture rather than under it: a phone in landscape, where the
  /// bar is the only way back into the recording and there is no room to explain
  /// itself. The dot, the range control and the transport all stay — they are
  /// the controls; the sentence was the caption.
  final bool dense;

  final TimelineWindow window;
  final TimelineRange range;
  final ValueChanged<TimelineRange>? onRangeChanged;

  /// One row per camera, in the order the wall shows them. Empty for a single camera, which gets
  /// the merged track instead.
  ///
  /// With more than one they *replace* the track rather than sitting under it. A merged track
  /// answers "was anything recording", which is the right question for the playhead and the wrong
  /// one for everything else: the moment there are two cameras, what you want off a timeline is
  /// which of them saw the thing. Drawn as hairlines beneath a full-height track they were too
  /// thin to read and too close together to tell apart — two of them looked like one smeared bar.
  final List<TimelineLane> lanes;

  /// Whether the lanes are drawn instead of the merged track.
  bool get _stacked => lanes.length > 1;

  /// Play, pause and speed, when something is driving the playhead rather than following it. Sits
  /// in the header, where "Back to live" already is.
  final Widget? transport;

  /// False once you have dragged back into the recording.
  final bool live;

  /// Where the picture on the stage is coming from, while replaying.
  ///
  /// A listenable rather than a value because it ticks about four times a
  /// second: routed through the screen it would relayout the transcript panel
  /// every 250 ms, so only the playhead's own subtree rebuilds.
  final ValueListenable<DateTime?>? playhead;

  /// A tap, or the end of a drag — go and play from here.
  final ValueChanged<DateTime>? onSeek;

  /// Mid-drag. Fires per pointer sample, so the controller only turns this into
  /// a real seek when the instant already falls inside the open playlist.
  final ValueChanged<DateTime>? onScrub;

  final VoidCallback? onBackToLive;

  static const _trackHeight = 44.0;

  /// How tall each camera's bar is, for a wall of [count] of them.
  ///
  /// Shrinks rather than scrolls, because the whole value of the stack is taking it in at a
  /// glance. Two cameras get a bar you could aim a pointer at; eight get something nearer the old
  /// hairline, but eight of them form a legible block in a way two never did.
  static double _laneHeight(int count) => switch (count) {
    <= 3 => 20.0,
    <= 6 => 14.0,
    _ => 12.0,
  };

  static const _laneGap = 4.0;

  /// The column the camera names sit in, to the left of every bar.
  ///
  /// Fixed, and the same for every row, which is the whole point: laid over the bars instead, a
  /// name covered the first stretch of its own camera's day, and covered a different amount of it
  /// per camera — so the bars appeared to start in different places. A gutter costs this much
  /// timeline and takes nothing away from it.
  static const _laneGutter = 88.0;

  /// The row of tick labels under the stack. The merged track carries its own; the bars have no
  /// room for any, so the axis becomes a row of its own.
  static const _rulerHeight = 16.0;

  /// The track's own left and right inset, shared by the tick labels and the
  /// hover readout so they line up with each other.
  static const _labelInset = 10.0;

  @override
  State<TimelineScrubber> createState() => _TimelineScrubberState();
}

class _TimelineScrubberState extends State<TimelineScrubber> {
  /// Where the pointer is during a drag. Drawn instead of the playhead, so the
  /// line tracks the finger rather than lagging behind the decoder.
  DateTime? _dragAt;

  /// Where the pointer is hovering, for the readout above the track.
  DateTime? _hoverAt;

  /// Held here because this is what rebuilds on every pointer sample, and because the merged track
  /// and the lanes under it must derive their blocks the same way — see [_TrackGeometry].
  final _geometry = _GeometryCache();

  /// Seeking needs somewhere to seek *to*. A camera that records nothing has an empty track for
  /// good, so the gestures come off it entirely rather than leaving a bar that accepts a drag and
  /// answers with nothing.
  bool get _interactive =>
      widget.records && (widget.onSeek != null || widget.onScrub != null);

  /// Where the timeline starts. The gutter is not part of it, so a click beside a camera's name
  /// means the left edge of the window rather than some time before it.
  double get _inset => widget._stacked ? TimelineScrubber._laneGutter : 0.0;

  DateTime _timeAtLocal(double dx, double width) {
    final usable = width - _inset;
    if (usable <= 0) return widget.window.from;
    return widget.window.timeAt(((dx - _inset) / usable).clamp(0.0, 1.0));
  }

  /// The end of a drag, however it ended: drop the drag line and go and play from where it was.
  void _commitDrag() {
    final at = _dragAt;
    setState(() => _dragAt = null);
    if (at != null) widget.onSeek?.call(at);
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      _Header(
        live: widget.live,
        range: widget.range,
        playhead: widget.playhead,
        onRangeChanged: widget.onRangeChanged,
        onBackToLive: widget.onBackToLive,
        transport: widget.transport,
        dense: widget.dense,
        records: widget.records,
        scope: widget.scope,
      ),
      SizedBox(height: widget.dense ? 6 : 8),
      LayoutBuilder(
        builder: (context, constraints) {
          final width = constraints.maxWidth;

          return MouseRegion(
            cursor: _interactive ? SystemMouseCursors.click : MouseCursor.defer,
            onHover: (event) => setState(
              () => _hoverAt = _timeAtLocal(event.localPosition.dx, width),
            ),
            onExit: (_) => setState(() => _hoverAt = null),
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTapUp: widget.onSeek == null
                  ? null
                  : (details) => widget.onSeek!(
                      _timeAtLocal(details.localPosition.dx, width),
                    ),
              onHorizontalDragStart: !_interactive
                  ? null
                  : (details) => setState(
                      () => _dragAt = _timeAtLocal(
                        details.localPosition.dx,
                        width,
                      ),
                    ),
              onHorizontalDragUpdate: !_interactive
                  ? null
                  : (details) {
                      final at = _timeAtLocal(details.localPosition.dx, width);
                      setState(() => _dragAt = at);
                      widget.onScrub?.call(at);
                    },
              onHorizontalDragEnd: !_interactive ? null : (_) => _commitDrag(),
              // A cancel is the pointer being taken away mid-drag — off the edge of the window,
              // a touch the system claimed. It commits like a release rather than discarding:
              // the line was dragged somewhere on purpose, and leaving it stranded there with
              // nothing behind it is the one outcome that reads as broken.
              onHorizontalDragCancel: !_interactive ? null : _commitDrag,
              child: widget._stacked
                  ? _LaneBoard(
                      lanes: widget.lanes,
                      window: widget.window,
                      width: width,
                      playhead: widget.playhead,
                      dragAt: _dragAt,
                      hoverAt: _hoverAt,
                      endsNow: widget.range.live,
                      geometry: _geometry,
                    )
                  : SizedBox(
                      height: TimelineScrubber._trackHeight,
                      child: _Track(
                        window: widget.window,
                        width: width,
                        playhead: widget.playhead,
                        dragAt: _dragAt,
                        hoverAt: _hoverAt,
                        endsNow: widget.range.live,
                        geometry: _geometry,
                      ),
                    ),
            ),
          );
        },
      ),
    ],
  );
}

class _Header extends StatelessWidget {
  const _Header({
    required this.live,
    required this.range,
    required this.playhead,
    required this.onRangeChanged,
    required this.onBackToLive,
    this.transport,
    this.dense = false,
    this.records = true,
    this.scope = TimelineScope.camera,
  });

  final bool live;
  final TimelineRange range;
  final ValueListenable<DateTime?>? playhead;
  final ValueChanged<TimelineRange>? onRangeChanged;
  final VoidCallback? onBackToLive;
  final Widget? transport;

  /// See [TimelineScrubber.dense].
  final bool dense;

  /// See [TimelineScrubber.records].
  final bool records;

  /// See [TimelineScrubber.scope].
  final TimelineScope scope;

  @override
  Widget build(BuildContext context) {
    // A phone, and not the landscape overlay — which is compact by width and has 892px to lay
    // the row out in.
    final compact = isCompact(context) && !dense;

    // Replaying on a phone the row is over its width before the range control is reached: the
    // status, the playhead, the transport and *Back to live* are ~418px of children that may not
    // give, in 380px of row, and the one flexible child is the control that then scales to a
    // sliver. So the controls take a line of their own. Every label survives that way — the two
    // one-line shapes that fit both cost the status word *and* the label under *Back to live*,
    // which is the way out of replay and the last thing on this screen to leave unnamed.
    if (compact && !live) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              _dot,
              const SizedBox(width: 6),
              _status,
              const SizedBox(width: 10),
              _PlayheadLabel(playhead: playhead),
              const Spacer(),
              rangeControl,
            ],
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              ?transport,
              const Spacer(),
              // Real text leaves this row about a hundred pixels of slack, so the scale never
              // bites. It is here for the same reason the one-line form has it: the transport is
              // four segments and a button and cannot give, so if anything ever has to — a text
              // scale turned up, a longer word for this in another language — it is the label
              // rather than the row.
              if (onBackToLive != null)
                Flexible(
                  child: FittedBox(
                    fit: BoxFit.scaleDown,
                    alignment: Alignment.centerRight,
                    child: _backToLive,
                  ),
                ),
            ],
          ),
        ],
      );
    }

    return Row(
      children: [
        _dot,
        const SizedBox(width: 6),
        _status,
        const SizedBox(width: 10),
        if (live && !dense)
          // The sentence takes the whole gap, so the range control lands against the right edge
          // and the two states put it in the same place. Expanded rather than Flexible for exactly
          // that reason: two flex children — a loose caption and the control's own — split the
          // free space between them, the caption takes only as much of its half as the words need,
          // and the control then right-aligns inside a half-width box, stopping short of the edge
          // by whatever the sentence left over. It also still gives way first, which is what a row
          // carrying a control and a caption should do.
          Expanded(
            child: Text(
              // A chosen period is not "today", and the track no longer ends at now — so the copy
              // names the window instead of inviting you to drag back into a day you are not
              // looking at. With nothing recorded there is nothing to invite at all, and the bar
              // has to say why it is empty — left as a caption about dragging, an empty track
              // reads as a quiet day rather than as a camera that keeps nothing.
              !records
                  ? switch (scope) {
                      TimelineScope.camera =>
                        'Nothing is kept for this camera — there is no footage to replay',
                      TimelineScope.wall =>
                        'Nothing is being recorded — there is no footage to replay',
                    }
                  : range.live
                  ? 'Drag back to replay today'
                  : 'Drag to replay '
                        '${periodLabel(range.startAt(DateTime.now()), range.endingAt!)}',
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),
          )
        else if (!live)
          // Kept in the dense form too: where the playhead is *is* the readout, and dropping it
          // would leave a bar being dragged with nothing saying where to.
          _PlayheadLabel(playhead: playhead),
        if (!live && transport != null) ...[
          const SizedBox(width: 14),
          transport!,
        ],
        if (!live && onBackToLive != null) ...[
          const SizedBox(width: 12),
          _backToLive,
        ],
        // Whichever of the two takes the gap, the control ends up against the right edge.
        //
        // Live, the sentence above is the flexible one and this sits at its natural width after
        // it. Replaying there is no sentence — the playhead, the transport and *Back to live* are
        // all controls and none of them may give — so the gap belongs to this, and with it the job
        // of giving way: at 1440 with the activity column open that row wants about twenty pixels
        // more than it has, and `scaleDown` only bites when it does.
        if (live && !dense)
          rangeControl
        else
          Expanded(
            child: Align(
              alignment: Alignment.centerRight,
              child: FittedBox(
                fit: BoxFit.scaleDown,
                alignment: Alignment.centerRight,
                child: rangeControl,
              ),
            ),
          ),
      ],
    );
  }

  /// Grey while nothing is kept: the red dot is the mark of a recorder running, and a camera's
  /// picture is live without any of it being written down.
  Widget get _dot => Container(
    width: 6,
    height: 6,
    decoration: BoxDecoration(
      color: live && records ? Serval.recording : Nocturne.neutral600,
      shape: BoxShape.circle,
    ),
  );

  Widget get _status => Text(
    live ? 'Live' : 'Replaying',
    style: const TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 12.5,
      fontWeight: Nocturne.headingWeight,
      color: Nocturne.text,
    ),
  );

  Widget get _backToLive => NocturneButton(
    label: 'Back to live',
    icon: PhosphorIconsRegular.broadcast,
    variant: NocturneButtonVariant.ghost,
    horizontalPadding: 0,
    onPressed: onBackToLive,
  );

  /// One button naming what is on the track, which opens the panel that changes it.
  ///
  /// One button rather than a row of spans with *Custom* on the end: that shape says the presets
  /// are the real answers and a date is the exception, which is backwards once a camera has days
  /// behind it, and it costs four segments of header to say. A button says what you are looking at.
  Widget get rangeControl => Builder(
    builder: (context) => NocturneButton(
      label: rangeButtonLabel(range, DateTime.now()),
      icon: PhosphorIconsRegular.calendarBlank,
      trailingIcon: PhosphorIconsRegular.caretUp,
      variant: NocturneButtonVariant.primary,
      height: 32,
      fontSize: 12.5,
      horizontalPadding: 12,
      onPressed: onRangeChanged == null ? null : () => _open(context),
    ),
  );

  void _open(BuildContext context) {
    // The button itself is the anchor, so the panel opens against the edge it was summoned from
    // rather than in the middle of the screen.
    final anchor = context.findRenderObject();
    if (anchor is! RenderBox) return;

    unawaited(
      showTimelineRangePanel(
        context: context,
        anchor: anchor,
        now: DateTime.now(),
        range: range,
        onChanged: onRangeChanged!,
        onBackToLive: live ? null : onBackToLive,
      ),
    );
  }
}

/// How far back the stage is, in the activity column's own words — "4 min ago",
/// "4:12 pm". Reused rather than reformatted so the two read the same.
class _PlayheadLabel extends StatelessWidget {
  const _PlayheadLabel({required this.playhead});

  final ValueListenable<DateTime?>? playhead;

  @override
  Widget build(BuildContext context) {
    final style = TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 12.5,
      color: Nocturne.mix(Nocturne.text, 45),
    );

    // Nothing rather than a second "Replaying": the status word is already to the left, and this
    // slot exists to say *when*, which without a playhead there is no answer to.
    if (playhead == null) return const SizedBox.shrink();

    return ValueListenableBuilder<DateTime?>(
      valueListenable: playhead!,
      builder: (context, at, _) =>
          Text(at == null ? '' : activityTimeLabel(at), style: style),
    );
  }
}

class _Track extends StatelessWidget {
  const _Track({
    required this.window,
    required this.width,
    required this.playhead,
    required this.dragAt,
    required this.hoverAt,
    required this.endsNow,
    required this.geometry,
  });

  final TimelineWindow window;
  final double width;
  final ValueListenable<DateTime?>? playhead;
  final DateTime? dragAt;
  final DateTime? hoverAt;

  /// Whether the right edge is *now* rather than the end of a period someone asked for. The
  /// recording hue and the word only belong on an edge that is really the present.
  final bool endsNow;

  final _GeometryCache geometry;

  @override
  Widget build(BuildContext context) {
    final track = geometry.of(window, width);

    // One layer per kind of thing that happened, each merged among its own so a run is as wide as
    // that kind went on, and each cut clear of the layers over it so a pixel is painted once.
    final layers = track.layers;

    // The playhead is a sibling of the track rather than a child of it, because it is the one
    // thing here that draws outside the rounded rectangle — everything else is of the day and
    // belongs within it.
    return Stack(
      clipBehavior: Clip.none,
      fit: StackFit.expand,
      children: [
        DecoratedBox(
          decoration: BoxDecoration(
            color: Nocturne.mix(Nocturne.text, 5),
            borderRadius: BorderRadius.circular(7),
            border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
          ),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(7),
            child: Stack(
              children: [
                // Where footage exists. A ground rather than the accent: a working
                // NVR has recorded all day, and the default state must not compete
                // with the marks that say something happened. Nothing while loading,
                // because an empty track means "not known yet", which is a different
                // statement from "nothing was recorded".
                if (!window.loading)
                  for (final span in window.coverage)
                    Positioned(
                      left: (window.positionOf(span.from) * width).clamp(
                        0.0,
                        width,
                      ),
                      top: 0,
                      bottom: 0,
                      width: track.spanWidth(span),
                      // 8%, against the track's own 5%. One clear step up — enough that a hole
                      // reads as a hole at a glance — and well below the marks, which have to
                      // stay the brightest thing on a bar that is otherwise solid all day.
                      child: ColoredBox(color: Nocturne.mix(Nocturne.text, 8)),
                    ),
                // The shape of the day, in the colours of what made it: what was
                // heard, what was seen, who spoke, and what the vision model was
                // asked to describe. The layers no longer overlap by the time they
                // arrive here, so this is a fill rather than a stack — which pixel
                // belongs to which kind was decided in [_TrackGeometry.layers].
                for (final layer in layers)
                  for (final block in layer.blocks)
                    Positioned(
                      left: block.left,
                      top: 0,
                      bottom: 0,
                      width: block.width,
                      child: ColoredBox(color: layer.colour),
                    ),
                // Now, at the right edge. The one place the recording hue appears on
                // the track — and only while that edge really is now: on a period someone asked
                // for it is the end of the period, and the live hue there is a claim about the
                // present.
                if (endsNow)
                  const Positioned(
                    right: 0,
                    top: 0,
                    bottom: 0,
                    width: 2,
                    child: ColoredBox(color: Serval.recording),
                  ),
                _Ticks(window: window, width: width, endsNow: endsNow),
                if (hoverAt != null && dragAt == null)
                  _HoverReadout(
                    window: window,
                    width: width,
                    at: hoverAt!,
                    layers: layers,
                  ),
              ],
            ),
          ),
        ),
        _Playhead(
          window: window,
          width: width,
          playhead: playhead,
          dragAt: dragAt,
        ),
      ],
    );
  }
}

/// The wall's timeline: one bar per camera, and an axis under them.
///
/// This is what a wall gets instead of the merged track. The merge is still computed — the
/// playhead snaps against it, because "is there footage here" is a question about the wall rather
/// than about any one camera — but it is no longer drawn, since every span in it is already on one
/// of these bars and drawing it again cost the height that made the bars readable.
class _LaneBoard extends StatelessWidget {
  const _LaneBoard({
    required this.lanes,
    required this.window,
    required this.width,
    required this.playhead,
    required this.dragAt,
    required this.hoverAt,
    required this.endsNow,
    required this.geometry,
  });

  final List<TimelineLane> lanes;

  /// The merge, for the axis and the *now* edge. Not drawn as a band.
  final TimelineWindow window;

  final double width;
  final ValueListenable<DateTime?>? playhead;
  final DateTime? dragAt;
  final DateTime? hoverAt;

  /// See [_Track.endsNow].
  final bool endsNow;

  /// One cache for every lane, so no bar can derive its blocks by different rules than its
  /// neighbours — the same argument [_TrackGeometry] makes for being a type at all.
  final _GeometryCache geometry;

  /// How wide the bars themselves are, once the names have had their column.
  double get _trackWidth =>
      (width - TimelineScrubber._laneGutter).clamp(0.0, width);

  @override
  Widget build(BuildContext context) {
    final height = TimelineScrubber._laneHeight(lanes.length);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Stack(
          children: [
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                for (final (index, lane) in lanes.indexed) ...[
                  if (index > 0)
                    const SizedBox(height: TimelineScrubber._laneGap),
                  SizedBox(
                    height: height,
                    // Stretch, not the default centre: a bar is a Stack of positioned children
                    // alone, so under a loose height constraint it has nothing to size itself
                    // from and collapses to a hairline.
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        SizedBox(
                          width: TimelineScrubber._laneGutter,
                          child: Padding(
                            padding: const EdgeInsets.only(right: 8),
                            child: Align(
                              alignment: Alignment.centerLeft,
                              child: Text(
                                lane.label,
                                overflow: TextOverflow.ellipsis,
                                maxLines: 1,
                                style: monoStyle(
                                  fontSize: 8.5,
                                  color: Nocturne.mix(Nocturne.text, 50),
                                ),
                              ),
                            ),
                          ),
                        ),
                        Expanded(
                          child: _Lane(
                            lane: lane,
                            width: _trackWidth,
                            playhead: playhead,
                            dragAt: dragAt,
                            geometry: geometry,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ],
            ),
            // Now, down the right-hand edge of every bar at once — the one place the recording hue
            // appears here, and the only mark that belongs to the wall rather than to a camera.
            // Absent on a chosen period, whose right edge is the end of the period.
            if (endsNow)
              const Positioned(
                right: 0,
                top: 0,
                bottom: 0,
                width: 2,
                child: ColoredBox(color: Serval.recording),
              ),
          ],
        ),
        // Inset by the same gutter, which is what keeps a tick under the minute it names.
        Padding(
          padding: const EdgeInsets.only(left: TimelineScrubber._laneGutter),
          child: SizedBox(
            height: TimelineScrubber._rulerHeight,
            child: _Ruler(
              window: window,
              width: _trackWidth,
              hoverAt: hoverAt,
              endsNow: endsNow,
            ),
          ),
        ),
      ],
    );
  }
}

/// The axis under the bars: what time it is across the window, or — while the pointer is over it —
/// what time is under the pointer.
///
/// One or the other, never both. They would land in the same few pixels and read as one smudged
/// label, and while you are aiming a click the only number that matters is the one you are aiming
/// at.
class _Ruler extends StatelessWidget {
  const _Ruler({
    required this.window,
    required this.width,
    required this.hoverAt,
    required this.endsNow,
  });

  final TimelineWindow window;
  final double width;
  final DateTime? hoverAt;

  /// See [_Track.endsNow].
  final bool endsNow;

  @override
  Widget build(BuildContext context) {
    final style = monoStyle(
      fontSize: 9.5,
      color: Nocturne.mix(Nocturne.text, 35),
    );

    if (hoverAt case final at?) {
      final position = window.positionOf(at).clamp(0.0, 1.0);
      return Stack(
        children: [
          Positioned(
            left: (position * width - 30).clamp(0.0, width - 60),
            top: 3,
            width: 60,
            child: Text(
              clockLabel(at),
              textAlign: TextAlign.center,
              style: monoStyle(
                fontSize: 9.5,
                color: Nocturne.mix(Nocturne.text, 60),
              ),
            ),
          ),
        ],
      );
    }

    final usable = width - TimelineScrubber._labelInset * 2;

    return Stack(
      children: [
        for (final (at, label) in timelineTicks(window.from, window.to))
          if (window.positionOf(at) case final position
              when position < 1 - _Ticks._nowGuard)
            Positioned(
              left: TimelineScrubber._labelInset + position * usable,
              top: 3,
              child: Text(label, style: style),
            ),
        Positioned(
          right: TimelineScrubber._labelInset,
          top: 3,
          child: Text(timelineEndLabel(window.to, live: endsNow), style: style),
        ),
      ],
    );
  }
}

/// One camera's own day, as a bar.
///
/// The same geometry as the merged track and none of its furniture: no ticks, no hover readout, no
/// knob on the playhead, and not even its own name — that sits in the board's gutter, so that
/// every bar can start at the same x whatever its camera is called. It answers one question — did
/// *this* camera have footage here, and did anything happen in it.
class _Lane extends StatelessWidget {
  const _Lane({
    required this.lane,
    required this.width,
    required this.playhead,
    required this.dragAt,
    required this.geometry,
  });

  final TimelineLane lane;

  /// The bar's own width, which is the widget's width less the name column. Passed in rather than
  /// measured because every mark on it is positioned against this number, and a bar that laid
  /// itself out against a different width than the axis below it would put its marks under the
  /// wrong minute.
  final double width;

  final ValueListenable<DateTime?>? playhead;
  final DateTime? dragAt;

  /// See [_LaneBoard.geometry].
  final _GeometryCache geometry;

  TimelineWindow get window => lane.window;

  @override
  Widget build(BuildContext context) {
    final track = geometry.of(window, width);
    final layers = track.layers;

    return DecoratedBox(
      decoration: BoxDecoration(
        color: Nocturne.mix(Nocturne.text, 5),
        borderRadius: BorderRadius.circular(3),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
      ),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(3),
        child: Stack(
          children: [
            if (!window.loading)
              for (final span in window.coverage)
                Positioned(
                  left: (window.positionOf(span.from) * width).clamp(
                    0.0,
                    width,
                  ),
                  top: 0,
                  bottom: 0,
                  width: track.spanWidth(span),
                  child: ColoredBox(color: Nocturne.mix(Nocturne.text, 8)),
                ),
            for (final layer in layers)
              for (final block in layer.blocks)
                Positioned(
                  left: block.left,
                  top: 0,
                  bottom: 0,
                  width: block.width,
                  child: ColoredBox(color: layer.colour),
                ),
            _LanePlayhead(
              window: window,
              width: width,
              playhead: playhead,
              dragAt: dragAt,
            ),
          ],
        ),
      ),
    );
  }
}

/// The playhead's continuation through a lane: the line without the glow or the knob.
///
/// Every lane draws its own rather than one line being drawn over the stack, because the lanes are
/// separate rounded strips with gaps between them — a single line across the column would paint
/// the gaps too and read as a scratch down the widget rather than as a playhead.
class _LanePlayhead extends StatelessWidget {
  const _LanePlayhead({
    required this.window,
    required this.width,
    required this.playhead,
    required this.dragAt,
  });

  final TimelineWindow window;
  final double width;
  final ValueListenable<DateTime?>? playhead;
  final DateTime? dragAt;

  @override
  Widget build(BuildContext context) {
    if (dragAt != null) return _line(dragAt!);
    if (playhead == null) return const SizedBox.shrink();

    return ValueListenableBuilder<DateTime?>(
      valueListenable: playhead!,
      builder: (context, at, _) =>
          at == null ? const SizedBox.shrink() : _line(at),
    );
  }

  Widget _line(DateTime at) {
    final position = window.positionOf(at);
    if (position < 0 || position > 1) return const SizedBox.shrink();

    return Positioned(
      left: (position * width).clamp(0.0, width - 2),
      top: 0,
      bottom: 0,
      width: 2,
      child: const ColoredBox(color: Nocturne.accent300),
    );
  }
}

/// The geometry each bar is currently drawn from, kept across rebuilds.
///
/// The scrubber calls `setState` on every pointer sample — a hover readout and a drag line both
/// have to follow the finger — and each of those rebuilds the track and, on a wall, every lane
/// under it. Deriving a bar's layers is six filtered passes over its marks plus a union and a
/// difference apiece, and none of it depends on where the pointer is.
///
/// Keyed on the window by identity, which is exactly what it should be: `timelineFor` hands back
/// the same [TimelineWindow] object until something it is built from moves, so identity here means
/// "nothing has changed" rather than merely "nobody has rebuilt". A record key gives that for
/// free — [TimelineWindow] declares no `==`, so it falls back to identity, while the width beside
/// it compares by value.
///
/// Bounded rather than cleared on change: a resize mints a key per width and a wall has a bar per
/// camera, so a cap is the one rule that covers both without having to know which happened.
class _GeometryCache {
  /// A wall of twelve bars plus its merged track, with room to cross a resize without thrashing.
  static const _entries = 16;

  final _held = <(TimelineWindow, double), _TrackGeometry>{};

  _TrackGeometry of(TimelineWindow window, double width) {
    // Remove-and-reinsert keeps the map in recency order, so evicting from the
    // front drops the least recently used entry rather than the oldest insert.
    final key = (window, width);
    final geometry = _held.remove(key) ?? _TrackGeometry(window, width);
    _held[key] = geometry;

    while (_held.length > _entries) {
      _held.remove(_held.keys.first);
    }

    return geometry;
  }
}

/// The track's arithmetic: where an instant lands, and how the marks around it merge into
/// something drawable.
///
/// Held apart from the widget because the main track is no longer the only thing drawing it. A
/// wall of cameras puts a thin lane under the track for each one, and a lane that merged its marks
/// by different rules than the track above it would disagree with it about where the day was busy
/// — visibly, since the two are stacked and share an x axis.
class _TrackGeometry {
  _TrackGeometry(this.window, this.width);

  final TimelineWindow window;
  final double width;

  /// A span this thin still has to be visible. At 24 hours across ~700 px one
  /// pixel is about two minutes, so a short run recovered after an outage would
  /// otherwise round away to nothing and read as no footage at all.
  static const minSpanWidth = 1.0;

  /// The narrowest an alert may be drawn. Wide enough to see and to aim a pointer at, and no
  /// wider — every pixel over the event's own length is track the alert does not own.
  static const minAlertWidth = 3.0;

  /// The marks, merged into what can actually be drawn.
  ///
  /// A mark is an instant, and the track is about a thousand pixels wide — so over twelve hours a
  /// pixel is around forty seconds, and a camera that saw a car pull in produces a burst of scene
  /// descriptions that all land within a few pixels of each other. Drawn one by one that reads as
  /// a picket fence: a wall of identical hairlines that says "something happened" everywhere and
  /// therefore nowhere.
  ///
  /// Merging neighbours into one wider block turns a burst into a block whose width is how long the
  /// activity went on, leaves an isolated event a tick, and gives the eye the shape of the day
  /// rather than its sampling rate — the same argument the Server makes for coverage spans.
  ///
  /// **Called once per layer**, which is what keeps severity and category out of the merge. Marks
  /// merged only against their own layer say where *that kind* of thing went on; one pass carrying
  /// every kind lets the widest claim win the whole run — a camera with speech all evening chains
  /// into a single block, and one person in it paints hours of track as an alert, or a burst of
  /// scene descriptions swallows the sound in the middle of it and paints the stretch slate.
  ///
  /// [spanned] is the difference between the two, and it follows what the layer is *for*. The band
  /// is scanned, so its marks are fixed-size ticks legible at any range. An alert is aimed at, so
  /// it is drawn across the time it actually covers: at one hour across a thousand pixels the 12 px
  /// tick ceiling is over half a minute, which paints a ten-second visit orange across thirty and
  /// lands the playhead after the person has gone, on footage with no box and no way to tell that
  /// from a broken overlay.
  ///
  /// [marks] must be ascending by `at`, which [TimelineWindow.marks] guarantees. Only the block
  /// last opened is compared against, so a mark out of order does not open a block of its own — it
  /// merges into one already past it and is lost.
  List<_MarkBlock> blocks(
    Iterable<TimelineMark> marks, {
    bool spanned = false,
  }) {
    /// Two marks closer than this are the same moment as far as the track can show.
    const gap = 2.0;

    final blocks = <_MarkBlock>[];

    for (final mark in marks) {
      final position = window.positionOf(mark.at);
      if (position < 0 || position > 1) continue;

      final left = position * width;
      final right = left + (spanned ? spannedWidth(mark) : tickWidth(mark));

      final last = blocks.isEmpty ? null : blocks.last;
      if (last != null && left <= last.right + gap) {
        last.right = right > last.right ? right : last.right;
        continue;
      }

      blocks.add(_MarkBlock(left: left, right: right));
    }

    return blocks;
  }

  /// A mark as a tick: legible first, and the same size at 1 h as at 24 h.
  double tickWidth(TimelineMark mark) =>
      (3 + mark.ran.inSeconds).clamp(3, 12).toDouble();

  /// A mark across the time it actually covers.
  ///
  /// Floored so an instant cannot vanish, which is the one place this still overstates: below
  /// [minAlertWidth] the floor is wider than the event, and at 24 h that floor is minutes. It
  /// cannot be helped — an event thinner than a pixel has to round up to something or be invisible
  /// — but it means the guarantee is only ever "the alert starts here", never "and ends there".
  double spannedWidth(TimelineMark mark) {
    final span = window.span.inMicroseconds;
    if (span <= 0) return minAlertWidth;

    final pixels = mark.ran.inMicroseconds / span * width;
    return pixels.clamp(minAlertWidth, width);
  }

  /// Every layer of the track, in paint order, each already cut clear of the ones over it.
  ///
  /// The order is a priority: whichever kind is higher up this list owns a pixel the two share.
  /// Alerts first, because they are the reason to look at the bar at all. Then sounds over
  /// objects, since a sound is the one reading the picture cannot give you afterwards. Scenes
  /// last — a scene describes the detection that triggered it and lands on the same instant, so
  /// cutting it away under the objects drops paint that was saying the same thing twice.
  ///
  /// Ties are what this is for, and they are common: one arrival routinely produces a detection
  /// and a scene, and a conversation with a dog barking over it produces an utterance and a sound.
  ///
  /// A mark belongs to exactly one layer — an alert is in its alert layer and in no band. The two
  /// size their marks differently, so a mark in both is drawn twice at two different widths: an
  /// alert shorter than the tick ceiling leaves a stub of band sticking out past its own orange,
  /// claiming ordinary activity for time where the only thing that happened was the alert.
  ///
  /// Derived once per geometry. Six filtered passes over the marks plus a union and a difference
  /// each is not something to repeat for a pointer that moved — see [_GeometryCache].
  late final List<_MarkLayer> layers = _layers();

  List<_MarkLayer> _layers() {
    final layers = <_MarkLayer>[];

    // Every pixel any higher layer has claimed, merged. Cutting against this once per layer is
    // what holds the guarantee — a pixel is painted exactly once, so a fill that is alpha rather
    // than an opaque blend still means one thing.
    var covered = const <_MarkBlock>[];

    for (final (of, alert) in _paintOrder) {
      final blocks = this.blocks(
        window.marks.where(
          (mark) =>
              mark.of == of && (mark.kind == TimelineMarkKind.alert) == alert,
        ),
        spanned: alert,
      );
      if (blocks.isEmpty) continue;

      layers.add(
        _MarkLayer(
          colour: Nocturne.mix(
            Serval.markHue(of, alert: alert),
            alert ? 75 : 55,
          ),
          label: alert ? '${of.label} · alert' : of.label,
          blocks: without(blocks, covered),
        ),
      );

      covered = union(covered, blocks);
    }

    return layers;
  }

  /// [a] and [b] as one sorted, internally merged list.
  ///
  /// Both arrive in that shape and neither is modified, which is what lets this walk them once.
  /// Touching blocks come out as one, so the result is a set of pixels rather than a history of
  /// how they were claimed — which is all [without] can use.
  List<_MarkBlock> union(List<_MarkBlock> a, List<_MarkBlock> b) {
    final merged = <_MarkBlock>[];
    var i = 0;
    var j = 0;

    while (i < a.length || j < b.length) {
      final next = j >= b.length || (i < a.length && a[i].left <= b[j].left)
          ? a[i++]
          : b[j++];

      final last = merged.isEmpty ? null : merged.last;
      if (last != null && next.left <= last.right) {
        if (next.right > last.right) last.right = next.right;
        continue;
      }

      merged.add(_MarkBlock(left: next.left, right: next.right));
    }

    return merged;
  }

  /// [blocks], with every stretch [holes] covers taken out of them.
  ///
  /// Both layers fill with [Nocturne.mix], which is alpha rather than an opaque blend — so an
  /// alert drawn straight over the band would come out a different orange from one drawn over
  /// bare track, and the same alert would read as two colours depending on how busy the camera
  /// was around it. Cutting the band out from under the alerts means every pixel of the track is
  /// painted exactly once and a colour means one thing.
  ///
  /// Both lists arrive sorted and internally merged, which is what lets this walk them once.
  List<_MarkBlock> without(List<_MarkBlock> blocks, List<_MarkBlock> holes) {
    final kept = <_MarkBlock>[];

    for (final block in blocks) {
      double left = block.left;

      for (final hole in holes) {
        if (hole.right <= left) continue;
        if (hole.left >= block.right) break;
        if (hole.left > left) {
          kept.add(_MarkBlock(left: left, right: hole.left));
        }
        left = hole.right;
      }

      if (left < block.right) {
        kept.add(_MarkBlock(left: left, right: block.right));
      }
    }

    return kept;
  }

  double spanWidth(CoverageSpan span) {
    final left = (window.positionOf(span.from) * width).clamp(0.0, width);
    final right = (window.positionOf(span.to) * width).clamp(0.0, width);
    return (right - left).clamp(minSpanWidth, width);
  }
}

/// One run of activity, in track pixels — see [_TrackGeometry.blocks].
///
/// Carries no kind: the layer it was built for is what colours it.
class _MarkBlock {
  _MarkBlock({required this.left, required this.right});

  final double left;
  double right;

  double get width => right - left;
}

/// One kind of thing that happened, everywhere it happened, in one colour.
///
/// [blocks] are already cut clear of every layer above this one, so a track is drawn by walking
/// the layers in order and filling each block — no layer needs to know what the others did.
class _MarkLayer {
  const _MarkLayer({
    required this.colour,
    required this.label,
    required this.blocks,
  });

  final Color colour;

  /// What this colour means, for the hover readout — six hues is more than a bar 44 px tall can
  /// explain on its own, so the thing already following the cursor says it in words.
  final String label;

  final List<_MarkBlock> blocks;
}

/// The layers of the track, highest priority first — see [_TrackGeometry.layers].
///
/// Alerts exist only for the two kinds that carry `is_alert`, which is why this is six entries and
/// not eight: a scene or an utterance can never be severe, so the layers are never built.
const _paintOrder = <(ActivityKind, bool)>[
  (ActivityKind.sounds, true),
  (ActivityKind.objects, true),
  (ActivityKind.sounds, false),
  (ActivityKind.objects, false),
  (ActivityKind.speech, false),
  (ActivityKind.scenes, false),
];

class _Ticks extends StatelessWidget {
  const _Ticks({
    required this.window,
    required this.width,
    required this.endsNow,
  });

  final TimelineWindow window;
  final double width;

  /// See [_Track.endsNow].
  final bool endsNow;

  /// Ticks nearer the right edge than this are dropped: "now" is written there,
  /// and two labels in the same few pixels read as one smudged one.
  static const _nowGuard = 0.08;

  @override
  Widget build(BuildContext context) {
    final style = monoStyle(
      fontSize: 9.5,
      color: Nocturne.mix(Nocturne.text, 35),
    );
    final usable = width - TimelineScrubber._labelInset * 2;

    return Stack(
      children: [
        for (final (at, label) in timelineTicks(window.from, window.to))
          if (window.positionOf(at) case final position
              when position < 1 - _nowGuard)
            Positioned(
              left: TimelineScrubber._labelInset + position * usable,
              bottom: 5,
              child: Text(label, style: style),
            ),
        Positioned(
          right: TimelineScrubber._labelInset,
          bottom: 5,
          child: Text(timelineEndLabel(window.to, live: endsNow), style: style),
        ),
      ],
    );
  }
}

/// Where the picture on the stage is coming from.
///
/// A line and a glow, which is the whole of Nocturne's rule for the accent —
/// filling the played region would be the flood the system forbids, and would
/// also fight the coverage band underneath for the same pixels.
class _Playhead extends StatelessWidget {
  const _Playhead({
    required this.window,
    required this.width,
    required this.playhead,
    required this.dragAt,
  });

  final TimelineWindow window;
  final double width;
  final ValueListenable<DateTime?>? playhead;
  final DateTime? dragAt;

  @override
  Widget build(BuildContext context) {
    // The drag wins while it lasts: the finger is ahead of the decoder, and a
    // line that lagged behind the pointer would feel broken.
    if (dragAt != null) return _line(dragAt!);
    if (playhead == null) return const SizedBox.shrink();

    return ValueListenableBuilder<DateTime?>(
      valueListenable: playhead!,
      builder: (context, at, _) =>
          at == null ? const SizedBox.shrink() : _line(at),
    );
  }

  /// How far the line rises above the track, and the knob above the line.
  ///
  /// The knob is a sibling of the line rather than a child of it: the line is two pixels wide, and
  /// a knob inside it is offered two pixels to be nine in.
  static const _rise = 7.0;
  static const _knob = 9.0;

  Widget _line(DateTime at) {
    final position = window.positionOf(at);
    if (position < 0 || position > 1) return const SizedBox.shrink();

    final x = (position * width).clamp(0.0, width - 2);

    return Positioned.fill(
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Positioned(
            left: x,
            // Above the track rather than inside it. On a day with a lot of activity the marks
            // are lavender too, and a line that began where they begin was one of them; breaking
            // the top edge is what makes it read as *where you are* instead.
            top: -_rise,
            bottom: 0,
            width: 2,
            child: DecoratedBox(
              decoration: BoxDecoration(
                color: Nocturne.accent300,
                // The glow, then a hairline of the tile colour over it. Flutter paints the list
                // back-to-front, so the outline lands last, against the line itself — it is what
                // holds the line apart from a mark it happens to be crossing.
                boxShadow: [
                  BoxShadow(
                    color: Nocturne.mix(Nocturne.accent, 55),
                    blurRadius: 10,
                  ),
                  BoxShadow(
                    color: Nocturne.mix(Serval.tile, 72),
                    spreadRadius: 1,
                  ),
                ],
              ),
            ),
          ),
          Positioned(
            left: x + 1 - _knob / 2,
            top: -_rise - 4,
            width: _knob,
            height: _knob,
            child: Container(
              decoration: BoxDecoration(
                color: Serval.tile,
                shape: BoxShape.circle,
                border: Border.all(color: Nocturne.accent300, width: 1.5),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The instant under the cursor, and what is drawn there — so a click can be aimed before it is
/// made, and so the track's colours explain themselves.
///
/// The bar has six of them and 44 px of height, which is nowhere near enough room for a legend.
/// This already follows the cursor, so it is where the mapping is taught: point at a band and it
/// tells you what that colour meant, until you no longer need telling.
///
/// Only on hover, so it is a desktop reading. Touch falls back to the colour alone, which is the
/// whole of what the bar said before.
class _HoverReadout extends StatelessWidget {
  const _HoverReadout({
    required this.window,
    required this.width,
    required this.at,
    required this.layers,
  });

  final TimelineWindow window;
  final double width;
  final DateTime at;
  final List<_MarkLayer> layers;

  /// What is painted at [x], or null over bare track.
  ///
  /// The layers do not overlap, so the first hit is the only hit.
  String? _labelAt(double x) {
    for (final layer in layers) {
      for (final block in layer.blocks) {
        if (x >= block.left && x < block.right) return layer.label;
      }
    }

    return null;
  }

  @override
  Widget build(BuildContext context) {
    final position = window.positionOf(at).clamp(0.0, 1.0);
    final label = _labelAt(position * width);

    // Wide enough for the longest of them — *Scene descriptions · alert* cannot happen, but
    // *Objects seen · alert* can — and centred on the cursor until an edge stops it.
    const readoutWidth = 130.0;

    return Positioned(
      left: (position * width - readoutWidth / 2).clamp(
        0.0,
        (width - readoutWidth).clamp(0.0, double.infinity),
      ),
      top: 4,
      width: readoutWidth,
      child: Text(
        label == null ? clockLabel(at) : '${clockLabel(at)} · $label',
        textAlign: TextAlign.center,
        overflow: TextOverflow.ellipsis,
        style: monoStyle(fontSize: 9.5, color: Nocturne.mix(Nocturne.text, 60)),
      ),
    );
  }
}
