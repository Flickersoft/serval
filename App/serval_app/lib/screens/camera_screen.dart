import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart'
    show
        PopupMenuButton,
        PopupMenuItem,
        PopupMenuPosition,
        Tooltip,
        showModalBottomSheet;
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/activity.dart';
import '../media/media_saver.dart';
import '../models/camera.dart';
import '../models/clip_selection.dart';
import '../models/ptz.dart';
import '../models/saved_clip.dart';
import '../platform/secure_context.dart';
import '../models/timeline.dart';
import '../playback/microphone_gate.dart';
import '../playback/playback_volume.dart';
import '../playback/replay_controller.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/clip_trimmer.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/save_clip_dialog.dart';
import '../widgets/picture_aligned.dart';
import '../widgets/picture_zoom.dart';
import '../widgets/pill.dart';
import '../widgets/ptz_pad.dart';
import '../widgets/replay_stage_view.dart';
import '../widgets/talk_controls.dart';
import '../widgets/tile_placeholder.dart';
import '../widgets/activity_panel.dart';
import '../widgets/activity_sheet.dart';
import '../widgets/timeline_scrubber.dart';
import '../widgets/replay_transport.dart';
import '../widgets/volume_control.dart';
import '../widgets/webrtc_view.dart';

part 'camera/chrome.dart';
part 'camera/overlays.dart';
part 'camera/save.dart';
part 'camera/stage.dart';

/// The picture's band on the phone layout.
///
/// The one thing on that layout whose *height* is the design decision rather than a consequence of
/// one, and the band itself is private — so it is named here for the test that measures it.
const pictureBandKey = Key('camera-picture-band');

/// One camera, up close.
///
/// This is the only screen that talks back or drives pan/tilt. The wall
/// deliberately carries neither, so you always speak from the feed you are
/// actually watching.
class CameraScreen extends ConsumerStatefulWidget {
  const CameraScreen({
    super.key,
    required this.camera,
    required this.onBack,
    this.onOpenSettings,
    this.openAt,
  });

  final Camera camera;
  final VoidCallback onBack;

  /// The gear in the top bar — the registry, opened on this camera. Null leaves it disabled,
  /// which is what it was before anything was behind it.
  final VoidCallback? onOpenSettings;

  /// An instant to open the recording at, from a row in the feed. Null opens live.
  final DateTime? openAt;

  @override
  ConsumerState<CameraScreen> createState() => _CameraScreenState();
}

class _CameraScreenState extends ConsumerState<CameraScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  // An hour, not the design's twelve: the thing you have come to find is nearly always in the
  // last few minutes, and at 12 h that is a few pixels of track. Widened in [initState] when
  // arriving from a feed row older than that, since a track that does not reach the instant asked
  // for cannot be seeked to it.
  late TimelineRange _range = rangeForOpenAt(widget.openAt);

  /// The instant to seek to once there is coverage to seek within, or null.
  ///
  /// Held rather than acted on at once because the timeline arrives asynchronously: `timelineFor`
  /// answers the first read with an empty window and fetches behind it, and a seek into an empty
  /// window is answered "nothing was recorded here" — which would be a lie told to everyone
  /// arriving from the feed.
  late DateTime? _pendingOpen = widget.openAt;

  // Held here rather than in the panel for the same reason the wall holds its own: changing it
  // re-queries the repository, and the query is made where the repository is.
  ActivityFilter _activityFilter = ActivityFilter.none;

  /// Whether this screen draws every stored episode or only the alerting ones.
  ///
  /// A troubleshooting instrument for the camera in front of you, so it lives on the route and
  /// dies with it: leaving the screen puts it back to the alerts-only view everybody shares. It
  /// is deliberately not a preference — a switch you have to remember turning on is one you will
  /// find still on a week later, wondering why the driveway is full of boxes.
  ///
  /// Read by this screen alone. The wall asks the repository the same questions without it, so
  /// nothing here can change what that screen shows.
  bool _allDetections = false;

  /// Where the phone's tray is, and how tall it is there.
  ///
  /// Owned by the screen rather than by the tray because the picture behind it is laid out
  /// against the same figures — and because clicking a row has to be able to put the tray down.
  /// Not remembered anywhere: where the tray is left is what you are doing with this camera for a
  /// minute, not a standing preference about panels. The desktop's chevron writes one of those,
  /// through [ServalRepository.activityPanelCollapsed], and a phone borrowing that bit would have
  /// a glance at a doorbell rearranging the wall you go back to.
  final _tray = ActivitySheetController();

  bool _theirAudio = true;
  bool _subtitles = true;
  bool _talking = false;

  /// The picture asked for the whole screen, from the frame's expand corner or from *Move*.
  ///
  /// A mode rather than a reflow: the camera is horizontal, so a phone
  /// turned on its side takes the frame from 412x232 to nearly four times that, and pan, tilt and
  /// zoom belong there at full size. Turning the phone enters it on its own — see
  /// [isPhoneLandscape] — and this is the same mode reached deliberately, so leaving it is one
  /// press of the same corner.
  bool _expanded = false;

  /// What the zoom control reads, and whether the camera or we put it there.
  ///
  /// Starts reckoned at mid-travel and is replaced by the camera's own answer as soon as
  /// `GET /ptz/status` lands — see [ZoomPosition]. A camera that reports no position keeps the
  /// reckoned value for the life of the screen, and the control drops its readout to say so.
  ZoomPosition _zoom = ZoomPosition.unknown;
  Timer? _zoomStop;

  /// Re-reads the camera's position once a drag has settled and the lens has stopped moving.
  ///
  /// Separate from [_zoomStop] and deliberately later than it: a status read issued while the
  /// camera is still travelling answers with where it was passing through, which would snap the
  /// knob backwards and then need correcting again.
  Timer? _zoomResync;

  /// What the two save buttons are doing, and what to say about it. One at a time each — the
  /// button goes inert while its own job runs, so a second press cannot start a second export.
  _SaveJob? _snapshotJob;
  _SaveJob? _clipJob;
  Timer? _clearSaved;

  /// The range being trimmed, or null when the screen is doing its ordinary job.
  ///
  /// *Save clip* does not open a dialog asking for two times — it turns this screen into a
  /// trimmer, so the mode lives here rather than in a route or a modal. Null is the whole
  /// difference between the two states.
  _ClipMode? _clipMode;

  /// Polls a save the Server accepted but has not finished writing.
  Timer? _clipPoll;

  /// Live or replaying, and from when. Owned here rather than by the repository because the
  /// position ticks about four times a second and the repository's notifications mean the
  /// structure changed.
  late final ReplayController _replay = ReplayController(
    repository: _repository,
    cameraId: widget.camera.id,
  );

  /// The live picture's dimensions, published by [WebRtcView] and read by whatever is drawn over
  /// it. Owned here because those two are siblings in the stage's stack rather than nested, and
  /// because it has to outlive a view that is torn down on every camera switch.
  ///
  /// Replay does not use it — a [VodPlayer] reports its own — so it is null the whole time the
  /// stage is showing recorded footage.
  final _liveVideoSize = ValueNotifier<Size?>(null);

  /// How far the picture is magnified, published by [PictureZoom] and read by the pill that says
  /// so. Owned here for the same reason [_liveVideoSize] is, and it survives a switch between live
  /// and replay because both are the same camera pointing the same way.
  final _pictureZoom = ValueNotifier<double>(1);

  /// Where the live session's microphone is, published by [WebRtcView] and read by *Hold to talk*.
  /// Owned here for the reason [_liveVideoSize] is, and more so: on a phone the button is not even
  /// in the same half of the screen as the picture whose session holds the device.
  final _micStage = ValueNotifier<MicStage>(MicStage.closed);

  @override
  void initState() {
    super.initState();

    // Seed the controller with this camera's stored level, and follow it afterwards. The controller
    // has no player yet, so this only records the level it will apply to the first window it opens —
    // which is exactly what is wanted, since replay may never be entered at all. Seeding also means
    // the first window is already lifted, rather than the first fifteen minutes of a quiet camera
    // playing at the recording's own level.
    _applyStoredVolume();
    _volume.addListener(_applyStoredVolume);

    // Arrived from a row in the feed. The coverage it has to be seeked within is fetched behind
    // the first read, so this waits on the repository rather than on a rebuild — `build` here is
    // driven by the replay controller, and a seek that waits for a rebuild that waits for the
    // seek is a screen that opens live and stays there.
    if (_pendingOpen != null) {
      // The timeline slice and not the repository: what this is waiting for is coverage, which
      // arrives with the window. On the whole-repository notification it also woke for every
      // detection heartbeat, and re-read a timeline it already knew was still loading.
      _repository.timelineChanges.addListener(_openPending);
      _openPending();
    }

    // Gated on the camera record's own flag rather than on the probe, because it is the same
    // condition the Server guards the route with and it is known synchronously — waiting for the
    // probe would cost a rebuild to learn something a fixed camera already answers.
    if (widget.camera.ptzConfigured) unawaited(_readZoomPosition());
  }

  /// Replaces the reckoned position with the camera's own, where it has one.
  ///
  /// Silent on null: a camera that does not report a position is the fallback case, not an error,
  /// and the control already says what it is showing. Guarded on `mounted` because this outlives a
  /// screen closed during the round trip.
  Future<void> _readZoomPosition() async {
    final measured = await _repository.ptzZoomPosition(widget.camera.id);
    if (!mounted || measured == null) return;
    setState(() => _zoom = measured);
  }

  /// This camera's volume, as the slider's position. Kept by the repository, per camera and per
  /// machine, and stable for this id — so the listener attached in [initState] is attached to the
  /// same notifier the pill drags.
  late final ValueListenable<double> _volume = _repository.playbackVolumeFor(
    widget.camera.id,
  );

  /// The level this camera treats as silence, off the record it opened with.
  ///
  /// `late final` because nothing changes it while the screen is up: it is set from the camera's own
  /// settings, against a live meter, which is somewhere else entirely.
  late final double? _gateRms = widget.camera.playbackGateRms;

  /// Pushes the stored position at replay, as the volume and the lift it splits into.
  ///
  /// Only replay needs telling. The live view follows the same notifier through the builder around
  /// `WebRtcView`, which is what keeps a drag from relaying out the whole screen.
  void _applyStoredVolume() {
    final level = playbackFromTravel(_volume.value);
    _replay.setVolume(level.volume);
    _replay.setGain(level.db, _gateRms);
  }

  /// Seeks to the instant this screen was opened at, once the track can answer for it.
  ///
  /// Runs once: the first read that comes back with coverage wins, clears the request and drops
  /// the listener. Until then a seek would be answered "nothing was recorded here", which is a
  /// lie told to everyone arriving from the feed.
  void _openPending() {
    final at = _pendingOpen;
    if (at == null) return;

    final timeline = _repository.timelineFor(widget.camera.id, _range);
    if (timeline.loading || timeline.coverage.isEmpty) return;

    _pendingOpen = null;
    _repository.timelineChanges.removeListener(_openPending);

    // After the frame: this can be reached from a notification raised during a build, and seeking
    // notifies in its turn.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      // Exactly [at]. Whoever asked for this screen decided what instant it should open on — a
      // feed row backs its own off by a few seconds because a detection is stamped part-way into
      // what caused it, and a tile handed over from the wall means the frame it was showing.
      if (mounted) unawaited(_replay.seekTo(at, timeline));
    });
  }

  /// Clicking a row in the feed takes the picture to its moment.
  ///
  /// The wall's column routes to a camera; here you are already on it, so a click is a seek and
  /// the screen does not change. The row hands over an instant already backed off by a few
  /// seconds — see [replayStartFor] — or null for one close enough to now that its footage is the
  /// live view, which is the one case that goes forward rather than back.
  ///
  /// The tray comes down with it. Clicking a row is asking to see what it names, and the tray it
  /// was clicked in may be over the picture that answers.
  void _openRow(DateTime? at, TimelineWindow timeline) {
    if (_tray.detent == SheetDetent.raised) _tray.goTo(SheetDetent.resting);

    unawaited(at == null ? _replay.backToLive() : _replay.seekTo(at, timeline));
  }

  @override
  void dispose() {
    _volume.removeListener(_applyStoredVolume);
    // Only still attached if the coverage never arrived, but a listener into a disposed State is
    // the kind of thing that only shows up as a crash on a slow network.
    if (_pendingOpen != null) {
      _repository.timelineChanges.removeListener(_openPending);
    }
    _zoomStop?.cancel();
    _zoomResync?.cancel();
    _clearSaved?.cancel();
    // The save itself is the Server's and carries on regardless — this only stops asking about it.
    _clipPoll?.cancel();
    _replay.dispose();
    _liveVideoSize.dispose();
    _pictureZoom.dispose();
    _micStage.dispose();
    _tray.dispose();
    super.dispose();
  }

  /// The pad reports a direction on press and on every repeat; the Server's ONVIF auto-stop is
  /// about a second, and the pad repeats well inside it.
  void _onPtzMove(PtzDirection direction) => _repository.ptzMove(
    widget.camera.id,
    pan: direction.pan,
    tilt: direction.tilt,
  );

  /// A drag on the zoom track, driven whichever way the camera can be driven.
  ///
  /// Two shapes, decided on what the camera reported. A camera with an absolute zoom space is sent
  /// to the position under the finger, which is what the track has always looked like it did. One
  /// without takes velocities only, so the drag becomes "zoom in/out at a steady rate while it is
  /// moving", stopped a beat after it settles — the same press-and-hold shape the pad uses.
  ///
  /// Both then re-read the camera. The knob moves to the finger immediately either way, because a
  /// control that waits for a SOAP round trip before it responds feels broken; the read that
  /// follows is what makes the position true rather than merely requested.
  void _onZoomChanged(double zoom) {
    final absolute = switch (_repository.ptzProbeFor(widget.camera.id)) {
      PtzKnown(absoluteZoom: true) => true,
      _ => false,
    };

    final direction = zoom > _zoom.value ? 1.0 : -1.0;
    setState(() => _zoom = _zoom.withValue(zoom));

    if (absolute) {
      _repository.ptzZoomTo(widget.camera.id, zoom);
    } else {
      _repository.ptzMove(widget.camera.id, zoom: direction * 0.5);
      _zoomStop?.cancel();
      _zoomStop = Timer(
        const Duration(milliseconds: 350),
        () => _repository.ptzStop(widget.camera.id),
      );
    }

    // Late enough that the lens has arrived: the velocity path stops at 350ms and a camera told to
    // go somewhere absolutely still has to travel there. Asking sooner reads a position it is
    // passing through, which snaps the knob backwards and then needs correcting again.
    _zoomResync?.cancel();
    _zoomResync = Timer(
      const Duration(milliseconds: 1200),
      () => unawaited(_readZoomPosition()),
    );
  }

  /// "Their audio" means the live peer connection's remote track while live, and the replay
  /// player's volume while replaying. Without this, muting would silently stop meaning anything
  /// the moment you dragged back.
  void _setTheirAudio(bool on) {
    if (on == _theirAudio) return;
    setState(() => _theirAudio = on);

    // Unconditionally, not only while replaying. The controller re-applies its mute to every
    // window it opens, so muting while live has to be recorded now — otherwise dragging back into
    // replay plays the camera's audio while the speaker glyph reads muted.
    _replay.setMuted(!_theirAudio);
  }

  /// Saves the camera's latest still.
  ///
  /// Live only, and refused with the reason while replaying: `snapshot.jpg` is the newest frame
  /// and nothing extracts one from the archive, so this would save a different moment than the
  /// one on screen — which the file itself would never reveal.
  Future<void> _onSnapshot() async {
    if (_snapshotJob is _SaveWorking) return;

    if (_replay.replaying) {
      _finish(
        snapshot: true,
        job: const _SaveFailed(
          'Snapshot saves the live picture. Go back to live to use it.',
        ),
      );
      return;
    }

    if (!_repository.canSaveMedia) {
      _finish(
        snapshot: true,
        job: const _SaveFailed('This build has no Server to save from.'),
      );
      return;
    }

    setState(() => _snapshotJob = const _SaveWorking(0));
    try {
      final saved = await _repository.saveSnapshot(widget.camera.id);
      _finish(snapshot: true, job: _SaveDone(saved));
    } on Object catch (e) {
      _finish(snapshot: true, job: _SaveFailed('$e'));
    }
  }

  /// Turns the screen into a trimmer, opened on the window around what is on screen.
  ///
  /// The range opens already selected rather than empty, so the common case — *that, what just
  /// happened* — is chosen the moment the mode appears and everything after this is adjustment.
  ///
  /// Symmetric around a playhead, because you scrub *to* a thing rather than past it; entirely
  /// backwards from the live edge, because the future has not been recorded.
  Future<void> _onSaveClip(TimelineWindow timeline) async {
    if (_clipMode != null || _clipJob is _SaveWorking) return;

    final replaying = _replay.replaying;
    final anchor = replaying ? _replay.playhead.value : DateTime.now();
    if (anchor == null) return;

    // The trimmer works in segments, so it needs the unmerged index rather than the coverage the
    // scrubber draws. Asked for around the anchor and no wider: an hour of a four-second camera is
    // 900 rows, which is affordable, and a day is not.
    final segments = await _repository.segmentsFor(
      widget.camera.id,
      from: anchor.subtract(const Duration(minutes: 45)),
      to: anchor.add(const Duration(minutes: 45)),
    );

    final selection = ClipSelection.around(
      anchor,
      segments: segments,
      before: replaying
          ? const Duration(seconds: 30)
          : const Duration(seconds: 60),
      after: replaying ? const Duration(seconds: 30) : Duration.zero,
      max: _clipMax,
    );

    if (selection == null) {
      // The same sentence replay uses for the same condition, so there is one wording for
      // "there is no footage here" rather than two.
      _finish(
        snapshot: false,
        job: const _SaveFailed('Nothing was recorded here.'),
      );
      return;
    }

    if (!mounted) return;
    setState(() {
      _clipJob = null;
      _clipMode = _ClipMode(
        selection: selection,
        zoom: TrimZoom.forSpan(selection.span),
      );
    });

    // The picture follows the end being held, so entering the mode moves it to the end that is
    // live — otherwise the first thing the trimmer shows is a frame from neither end of it.
    _followClipEnd(selection, timeline);
  }

  void _onClipSelectionChanged(
    ClipSelection selection,
    TimelineWindow timeline,
  ) {
    final mode = _clipMode;
    if (mode == null) return;

    final movedEnd =
        mode.selection.activeAt != selection.activeAt ||
        mode.selection.active != selection.active;

    setState(() {
      _clipMode = mode.copyWith(
        selection: selection,
        // A selection dragged out past what the near track can show widens it rather than running
        // off the end, where a handle cannot be reached.
        zoom: selection.span * 1.5 > mode.zoom.span ? TrimZoom.far : mode.zoom,
      );
    });

    if (movedEnd) _followClipEnd(selection, timeline);
  }

  /// Seeks the picture to whichever end is being held — 12a's *hold an end and the picture follows
  /// it*, which is the whole reason the range is set by watching rather than by typing.
  void _followClipEnd(ClipSelection selection, TimelineWindow timeline) {
    if (!widget.camera.records) return;
    _replay.scrubTo(selection.activeAt, timeline);
  }

  void _exitClipMode() {
    if (!mounted) return;
    setState(() => _clipMode = null);
  }

  /// Plays the range from its start, so a chosen clip can be watched before it is kept.
  ///
  /// Seeks and plays rather than opening anything: the replay controller is already showing this
  /// camera at one end of the range, and moving it to the other end is the whole of "play it".
  void _playClip(ClipSelection selection, TimelineWindow timeline) {
    _replay.seekTo(selection.from, timeline);
    if (!_replay.playing) _replay.play();
  }

  /// Whether an instant falls inside the range being trimmed.
  ///
  /// What decides which feed rows are lit. Anything spoken or seen inside the clip is part of it;
  /// everything else is the context you are choosing against.
  bool _isInClip(DateTime at) {
    final selection = _clipMode?.selection;
    if (selection == null) return true;

    return !at.isBefore(selection.from) && !at.isAfter(selection.to);
  }

  /// Snaps the clip around a row somebody clicked — 12a's *click a line to snap the clip around it*.
  ///
  /// Extends rather than replaces: a range that already covers the row is left alone, so clicking
  /// something already in the clip does not shrink the clip down to it. Otherwise whichever end is
  /// on the wrong side of the row moves out to take it in.
  void _snapClipAround(DateTime? at, TimelineWindow timeline) {
    final mode = _clipMode;
    if (mode == null || at == null) return;

    final selection = mode.selection;
    if (_isInClip(at)) return;

    final grown = at.isBefore(selection.from)
        ? selection.snapTo(at, selection.to, max: _clipMax)
        : selection.snapTo(selection.from, at, max: _clipMax);

    _onClipSelectionChanged(grown, timeline);
  }

  /// The event under the playhead, for 12c's *Whole event*.
  ///
  /// What Serval saw rather than what the scrubber was showing — and taken from the marks the
  /// track is already drawing, so the thing it snaps to is one the user can see.
  TimelineMark? _eventUnder(ClipSelection selection, TimelineWindow timeline) {
    TimelineMark? best;
    var nearest = const Duration(days: 1);

    for (final mark in timeline.marks) {
      if (mark.ran <= Duration.zero) continue;

      final distance = (mark.at.difference(selection.activeAt)).abs();
      if (distance < nearest) {
        nearest = distance;
        best = mark;
      }
    }

    return nearest <= const Duration(minutes: 2) ? best : null;
  }

  /// The dialog, and then whatever it was closed with.
  Future<void> _onConfirmClip(TimelineWindow timeline) async {
    final mode = _clipMode;
    if (mode == null) return;

    final compact = isCompact(context);

    final choice = compact
        ? await showModalBottomSheet<SaveClipChoice>(
            context: context,
            backgroundColor: const Color(0x00000000),
            barrierColor: const Color(0x9E0A0B14),
            isScrollControlled: true,
            builder: (context) => Padding(
              padding: EdgeInsets.only(
                bottom: MediaQuery.viewInsetsOf(context).bottom,
              ),
              child: _saveClipDialog(mode, compact: true),
            ),
          )
        : await showNocturneDialog<SaveClipChoice>(
            context: context,
            builder: (context) => _saveClipDialog(mode, compact: false),
          );

    if (choice == null || !mounted) return;

    _exitClipMode();

    // Checked here rather than before the dialog: the dialog is a decision about the clip, which
    // is worth making — and showing — even on a build with nothing behind it. The honest answer
    // belongs in the status line when the press cannot land, the same posture the two save buttons
    // have always taken.
    if (!_repository.canSaveMedia) {
      _finish(
        snapshot: false,
        job: const _SaveFailed('This build has no Server to save from.'),
      );
      return;
    }

    switch (choice.destination) {
      case ClipDestination.library:
        await _keepClip(mode.selection, choice.name);
      case ClipDestination.download:
        await _downloadClip(mode.selection);
      case ClipDestination.share:
        await _shareClipRange(mode.selection, choice.name);
    }
  }

  Widget _saveClipDialog(
    _ClipMode mode, {
    required bool compact,
  }) => SaveClipDialog(
    cameraName: widget.camera.name,
    from: mode.selection.from,
    to: mode.selection.to,
    estimatedBytes: _estimatedBytes(mode.selection),
    // Serval's own read on the window, which is what 12b means by the transcript earning its keep
    // twice — the name it suggests is the sentence the feed already wrote.
    suggestedName:
        _repository
            .summaryFor(
              widget.camera.id,
              asOf: mode.selection.to,
              range: _range,
            )
            ?.text
            .split('.')
            .first
            .trim() ??
        '',
    canShare: _repository.canShare,
    compact: compact,
  );

  /// Keeps the clip on the Server, following the job it starts.
  Future<void> _keepClip(ClipSelection selection, String name) async {
    setState(() => _clipJob = const _SaveWorking(0));

    try {
      final clip = await _repository.keepClip(
        cameraId: widget.camera.id,
        from: selection.from,
        to: selection.to,
        name: name,
      );

      _watchClip(clip, selection.span);
    } on Object catch (e) {
      _finish(snapshot: false, job: _SaveFailed('$e'));
    }
  }

  /// Polls until the Server has written it.
  ///
  /// A poll rather than a socket because this is one clip for one person for a few tens of
  /// seconds, and the events socket carries what happened in front of the cameras rather than what
  /// this session asked for.
  void _watchClip(SavedClip clip, Duration asked) {
    _clipPoll?.cancel();
    _clipPoll = Timer.periodic(const Duration(seconds: 1), (timer) async {
      if (!mounted) {
        timer.cancel();
        return;
      }

      try {
        final progress = await _repository.clipProgress(clip.id);

        if (progress.failed) {
          timer.cancel();
          _finish(
            snapshot: false,
            job: _SaveFailed(progress.error ?? 'The clip could not be saved.'),
          );
          return;
        }

        if (progress.done) {
          timer.cancel();
          _finish(
            snapshot: false,
            job: _SaveDone(
              SavedMedia(
                fileName: clip.name,
                location: 'Saved clips',
                bytes: progress.sizeBytes ?? progress.bytesWritten,
              ),
              asked: asked,
            ),
          );
          return;
        }

        if (mounted && _clipJob is _SaveWorking) {
          setState(() => _clipJob = _SaveWorking(progress.bytesWritten));
        }
      } on Object catch (e) {
        timer.cancel();
        _finish(snapshot: false, job: _SaveFailed('$e'));
      }
    });
  }

  /// Straight to this device, keeping nothing — what *Save clip* has always done.
  Future<void> _downloadClip(ClipSelection selection) async {
    setState(() => _clipJob = const _SaveWorking(0));

    try {
      final saved = await _repository.saveClip(
        widget.camera.id,
        from: selection.from,
        to: selection.to,
        onBytes: (bytes) {
          if (mounted && _clipJob is _SaveWorking) {
            setState(() => _clipJob = _SaveWorking(bytes));
          }
        },
      );
      _finish(snapshot: false, job: _SaveDone(saved, asked: selection.span));
    } on Object catch (e) {
      _finish(snapshot: false, job: _SaveFailed('$e'));
    }
  }

  /// Sharing a range needs a file, and the only file is a kept one.
  ///
  /// So this keeps it first and then shares it. That is not a workaround — a shared clip that
  /// vanished from the library would be a link to something nobody can find again.
  Future<void> _shareClipRange(ClipSelection selection, String name) async {
    setState(() => _clipJob = const _SaveWorking(0));

    try {
      final clip = await _repository.keepClip(
        cameraId: widget.camera.id,
        from: selection.from,
        to: selection.to,
        name: name,
      );

      while (mounted) {
        final progress = await _repository.clipProgress(clip.id);

        if (progress.failed) {
          _finish(
            snapshot: false,
            job: _SaveFailed(progress.error ?? 'The clip could not be saved.'),
          );
          return;
        }

        if (progress.done) break;

        if (mounted && _clipJob is _SaveWorking) {
          setState(() => _clipJob = _SaveWorking(progress.bytesWritten));
        }
        await Future<void>.delayed(const Duration(seconds: 1));
      }

      if (!mounted) return;
      await _repository.shareSavedClip(clip.id);

      _finish(
        snapshot: false,
        job: _SaveDone(
          SavedMedia(fileName: clip.name, location: 'Saved clips'),
          asked: selection.span,
        ),
      );
    } on Object catch (e) {
      _finish(snapshot: false, job: _SaveFailed('$e'));
    }
  }

  /// Roughly what the file will weigh, from how much footage it covers.
  ///
  /// A reckoning rather than a measurement, and the dialog says "or so" for that reason: the exact
  /// figure does not exist until ffmpeg has written it, and the segment index carries durations
  /// rather than byte counts.
  int _estimatedBytes(ClipSelection selection) =>
      (selection.span.inMilliseconds / 1000 * _assumedBytesPerSecond).round();

  /// STUB: ~12 Mbps, which is what the design's own 84 MB for 55 seconds works out at — a rate
  /// reasoned from the mock rather than from this camera, so it is wrong by whatever this camera's
  /// bitrate differs by. `RecordingSegment` indexes durations and no byte count, but the segments
  /// covering a range are known and on disk: summing their sizes would weigh the clip for real,
  /// since a clip is a copy of them.
  static const _assumedBytesPerSecond = 1500 * 1024;

  /// The longest clip the Server will accept. Mirrors `Serval:Media:ClipMaxMinutes`.
  static const _clipMax = Duration(minutes: 30);

  /// Success clears itself after a few seconds; a failure stays until the next attempt, because it
  /// is the only place the reason is written down.
  void _finish({required bool snapshot, required _SaveJob job}) {
    if (!mounted) return;
    setState(() => snapshot ? _snapshotJob = job : _clipJob = job);

    _clearSaved?.cancel();
    if (job is _SaveDone) {
      _clearSaved = Timer(const Duration(seconds: 6), () {
        if (!mounted) return;
        setState(() => snapshot ? _snapshotJob = null : _clipJob = null);
      });
    }
  }

  /// Writes to the repository only. The replay controller and the live view both follow the
  /// stored level — one through the listener above, the other through a builder around
  /// `WebRtcView` — so setting it here as well would be a second path saying the same thing.
  void _onVolumeChanged(double travel) =>
      _repository.setPlaybackVolume(widget.camera.id, travel);

  /// Flips this screen between alerts only and everything stored.
  void _toggleAllDetections() =>
      setState(() => _allDetections = !_allDetections);

  @override
  Widget build(BuildContext context) {
    final camera = widget.camera;
    final repository = _repository;

    // The one slice watched from the top of this screen, and it earns that: coverage arriving
    // changes what the screen can do rather than only what it says — whether a row can be seeked
    // to, whether *Save clip* has a recording to cut from — and [timeline] below is read once here
    // and closed over by every control that acts on it. It lands once per window, not twice a
    // second, which is the whole reason it is not on `activityChanges`.
    ref.watch(timelineRevisionProvider);

    // Only mode changes rebuild this — entering or leaving replay, a failure. The playhead does
    // not come through here; it rides its own ValueListenable down to the two things that draw it.
    return ListenableBuilder(
      listenable: _replay,
      builder: (context, _) {
        final replaying = _replay.replaying;
        final timeline = repository.timelineFor(
          camera.id,
          _range,
          includeAllDetections: _allDetections,
        );

        // Turning the phone enters the immersive mode on its own; the frame's corner and *Move*
        // enter it deliberately. One flag either way, so leaving is always the same press.
        //
        // The chosen form is gated on the width as well as on the flag: a window dragged back out
        // to a desktop has the room for the whole screen again, and a full-bleed picture there is
        // a mode nobody asked to still be in.
        final compact = isCompact(context);
        final immersive = isPhoneLandscape(context) || (_expanded && compact);

        // The tray belongs to the phone's layout and to no other. Left raised through a rotation
        // or a window dragged out to a desktop, it would be a height remembered for a screen that
        // never had one — and it comes back the next time the window is narrow enough to draw it.
        if (!compact || immersive) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (mounted) _tray.goTo(SheetDetent.resting);
          });
        }

        if (immersive) {
          return _buildImmersive(camera, timeline, replaying: replaying);
        }

        if (compact) {
          return _buildCompact(camera, timeline, replaying: replaying);
        }

        return DecoratedBox(
          decoration: const BoxDecoration(color: Serval.panel),
          child: Column(
            children: [
              _TopBar(
                camera: camera,
                onBack: widget.onBack,
                onOpenSettings: widget.onOpenSettings,
                snapshotJob: _snapshotJob,
                clipJob: _clipJob,
                choosingClip: _clipMode != null,
                onSnapshot: _onSnapshot,
                onSaveClip: () => _onSaveClip(timeline),
              ),
              Expanded(
                child: Row(
                  children: [
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(20, 18, 18, 16),
                        child: Column(
                          children: [
                            Expanded(
                              child: _VideoStage(
                                repository: repository,
                                camera: camera,
                                replay: _replay,
                                liveVideoSize: _liveVideoSize,
                                pictureZoom: _pictureZoom,
                                // The caption has no such second reading — it is a claim about
                                // *now* and nothing else, so over old footage it would be a lie.
                                caption: replaying
                                    ? null
                                    : repository.liveCaptionFor(camera.id),
                                theirAudio: _theirAudio,
                                subtitles: _subtitles,
                                allDetections: _allDetections,
                                talking: _talking,
                                micStage: _micStage,
                                zoom: _zoom,
                                onTheirAudioChanged: _setTheirAudio,
                                onVolumeChanged: _onVolumeChanged,
                                onToggleSubtitles: () =>
                                    setState(() => _subtitles = !_subtitles),
                                onToggleAllDetections: _toggleAllDetections,
                                onTalkChanged: (talking) =>
                                    setState(() => _talking = talking),
                                onPtzMove: _onPtzMove,
                                onPtzStop: () => repository.ptzStop(camera.id),
                                onPtzHome: () => repository.ptzHome(camera.id),
                                onPtzPreset: (preset) => repository.ptzPreset(
                                  camera.id,
                                  preset.token,
                                ),
                                onZoomChanged: _onZoomChanged,
                                ptz: repository.ptzProbeFor(camera.id),
                                clip: _clipMode?.selection,
                                onPlayClip: _clipMode == null
                                    ? null
                                    : () => _playClip(
                                        _clipMode!.selection,
                                        timeline,
                                      ),
                              ),
                            ),
                            const SizedBox(height: 14),

                            // The timeline you were scrubbing *becomes* the trim track. Swapped
                            // rather than stacked, because a trimmer with a scrubber under it has
                            // two things that answer to a drag and no way to tell which one you
                            // meant.
                            if (_clipMode case final mode?)
                              ClipTrimmer(
                                selection: mode.selection,
                                zoom: mode.zoom,
                                window: mode.window,
                                marks: timeline.marks,
                                max: _clipMax,
                                saving: _clipJob is _SaveWorking,
                                onChanged: (s) =>
                                    _onClipSelectionChanged(s, timeline),
                                onZoomChanged: (z) => setState(
                                  () => _clipMode = mode.copyWith(zoom: z),
                                ),
                                onCancel: _exitClipMode,
                                onSave: () => _onConfirmClip(timeline),
                              )
                            else
                              TimelineScrubber(
                                window: timeline,
                                range: _range,
                                records: camera.records,
                                onRangeChanged: (r) =>
                                    setState(() => _range = r),
                                live: !replaying,
                                playhead: _replay.playhead,
                                onSeek: (at) => _replay.seekTo(at, timeline),
                                onScrub: (at) => _replay.scrubTo(at, timeline),
                                onBackToLive: _replay.backToLive,
                                // The same control the wall has, from the same constants: a rate
                                // that meant something different depending on which screen you
                                // were on would be worse than not offering one.
                                transport: !replaying
                                    ? null
                                    : ReplayTransport(
                                        playing: _replay.playing,
                                        rate: _replay.rate,
                                        rates: ReplayController.rates,
                                        onPlayPause: () => _replay.playing
                                            ? _replay.pause()
                                            : _replay.play(),
                                        onRateChanged: (rate) =>
                                            unawaited(_replay.setRate(rate)),
                                      ),
                              ),
                          ],
                        ),
                      ),
                    ),
                    ValueListenableBuilder<bool>(
                      valueListenable: repository.activityPanelCollapsed,
                      // Nested on the playhead for the same reason the wall does it: the panel has
                      // to follow the picture, and nothing else on this screen should be relaid
                      // out because it moved.
                      builder: (context, collapsed, _) => ValueListenableBuilder<DateTime?>(
                        valueListenable: _replay.playheadSeconds,
                        builder: (context, asOf, _) => Consumer(
                          // Where the feed is watched from: the panel that draws it, and not the
                          // screen around it. A detection arriving is 2/second per camera with
                          // something in frame, and the picture, the transport and the controls
                          // have nothing to say about any of them.
                          builder: (context, ref, _) {
                            ref.watch(activityRevisionProvider);

                            // The wall's own feed, scoped to this camera. Its facets answer what
                            // happened, what was seen or heard, and whether it was an alert — the
                            // same questions they answer there. "Which camera" is already settled
                            // by being on this screen, so the panel drops that section, and the
                            // counts here are this camera's rather than the house's.
                            final pool = repository.activityFor(
                              cameraId: camera.id,
                              asOf: asOf,
                              range: _range,
                              includeAllDetections: _allDetections,
                            );

                            return ActivityPanel(
                              collapsed: collapsed,
                              range: _range,
                              horizon: repository.feedHorizon(
                                cameraId: camera.id,
                              ),
                              onCollapsedChanged:
                                  repository.setActivityPanelCollapsed,
                              summary: repository.summaryFor(
                                camera.id,
                                asOf: asOf,
                                range: _range,
                              ),
                              activity: _activityFilter.apply(pool),
                              filter: _activityFilter,
                              facets: ActivityFacets.of(
                                pool,
                                alertsOnly: _activityFilter.alertsOnly,
                              ),
                              onFilterChanged: (f) =>
                                  setState(() => _activityFilter = f),
                              // While trimming, a row is not somewhere to seek to — it is a thing to
                              // put *in* the clip. The rows outside the range dim, and clicking one
                              // snaps the range around it rather than moving the picture away from
                              // the end being held.
                              inFocus: _clipMode == null ? null : _isInClip,
                              onSeek: !camera.records
                                  ? null
                                  : _clipMode != null
                                  ? (at) => _snapClipAround(at, timeline)
                                  : (at) => _openRow(at, timeline),
                              // "Still listening…" is a claim that more transcript is coming, and it
                              // defaulted to true — so it showed on a camera with audio AI off, which
                              // will never produce another turn, and over hour-old footage. Both are
                              // real fields, so this needs no stub.
                              listening: camera.aiAudio && !replaying,
                            );
                          },
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  // ----------------------------------------------------------------- design 8a

  /// One camera on a phone: the picture, and a tray over it carrying everything else.
  ///
  /// The camera sends 16:9 and a portrait phone is 9:19.5, so the frame can only ever be a strip —
  /// which settles the layout rather than constraining it. Nothing is drawn on the picture but the
  /// detection boxes and the two things that date the frame, so at this size the scene stays
  /// legible, and everything the desktop floats over the video rides the tray instead.
  ///
  /// **Nothing here divides a budget.** The picture is laid out against the tray's *settled*
  /// height and the tray takes what it needs from what it measures, so there is no figure standing
  /// in for the controls and nothing that has to be given up when the arithmetic comes out short.
  /// A row of buttons that comes and goes, a save that writes a line, a transport row that arrives
  /// with replay — each of those used to move every other thing on the screen.
  Widget _buildCompact(
    Camera camera,
    TimelineWindow timeline, {
    required bool replaying,
  }) {
    final repository = _repository;
    final talkBar = _talkBar(camera, replaying: replaying)
        ? _TalkBar.height
        : 0.0;

    final picture = _PictureBand(
      repository: repository,
      camera: camera,
      replay: _replay,
      liveVideoSize: _liveVideoSize,
      pictureZoom: _pictureZoom,
      allDetections: _allDetections,
      talking: _talking,
      theirAudio: _theirAudio,
      micStage: _micStage,
      onExpand: () => setState(() => _expanded = true),
      clip: _clipMode?.selection,
      onPlayClip: _clipMode == null
          ? null
          : () => _playClip(_clipMode!.selection, timeline),
    );

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // *Next* rather than *Save*, because there is one more screen. The X rather than an
          // arrow, because leaving discards a range rather than going back anywhere.
          if (_clipMode != null)
            CompactAppBar(
              title: 'Choose a clip',
              subtitle:
                  '${camera.name} · ${dayLabel(_clipMode!.selection.from)}',
              onBack: _exitClipMode,
              backIcon: PhosphorIconsRegular.x,
              backTooltip: 'Stop choosing',
              actions: [
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: NocturneButton(
                    label: 'Next',
                    variant: NocturneButtonVariant.primary,
                    height: 36,
                    fontSize: 14,
                    horizontalPadding: 14,
                    onPressed: _clipJob is _SaveWorking
                        ? null
                        : () => _onConfirmClip(timeline),
                  ),
                ),
              ],
            )
          else
            CompactAppBar(
              title: camera.name,
              onBack: widget.onBack,
              // Fixed wording, the same reason the wide bar gives: an alert-labelled sound says a
              // camera needs attention, but the class name is not a sentence.
              trailing: camera.needsAttention
                  ? Pill.solid("Someone's here")
                  : null,
              actions: [
                _CameraOverflow(
                  onSettings: widget.onOpenSettings,
                  snapshotJob: _snapshotJob,
                  clipJob: _clipJob,
                ),
              ],
            ),

          // Trimming is a mode rather than a state of this screen, and it wants the room under the
          // picture for one thing. A tray there would carry a feed's head — a title and a search
          // field — over controls that are not a feed, and offer to narrow a list nobody is
          // reading. So the trimmer keeps the plain column, which is what it always drew.
          if (_clipMode != null) ...[
            SizedBox(
              key: pictureBandKey,
              height: MediaQuery.sizeOf(context).width / Serval.pictureAspect,
              child: picture,
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 13, 16, 12),
              child: _compactTrimmer(timeline),
            ),
            Expanded(
              child: SingleChildScrollView(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                child: _compactTrimmer(timeline).buildCompactControls(),
              ),
            ),
          ] else
            Expanded(
              child: LayoutBuilder(
                builder: (context, constraints) => ListenableBuilder(
                  listenable: _tray,
                  builder: (context, _) => Stack(
                    children: [
                      // The picture takes everything the tray is not covering, and the scene keeps
                      // its shape inside it — 16:9 letterboxed into a taller box, because a phone
                      // held upright cannot make a landscape picture bigger without cropping it,
                      // and cropping a camera is not a way to see more of it. What the extra
                      // height buys is room for a pinch: the zoom is clipped to this box, and
                      // twice the box is twice the zoomed frame you can actually look at. Pushing
                      // the tray down is how you ask for that room — and a camera sending a
                      // portrait scene wants all of it, which is what the stowed detent is.
                      //
                      // Against the settled detent, and animated on the same curve the tray uses
                      // to reach it. Following the live drag would re-box the video on every frame
                      // of a gesture and re-anchor a pinch in the middle of one.
                      AnimatedPositioned(
                        duration: const Duration(milliseconds: 240),
                        curve: Curves.easeOutCubic,
                        left: 0,
                        right: 0,
                        top: 0,
                        height: _pictureHeight(
                          context,
                          constraints.maxHeight,
                          bottomInset: talkBar,
                        ),
                        child: SizedBox(key: pictureBandKey, child: picture),
                      ),

                      // *Hold to talk* as a pill pinned at the bottom, the one place a thumb
                      // reaches without shifting grip. Talking to someone at the door is what this
                      // screen is opened to do on a phone, so here it is the lowest thing on it,
                      // and the tray stops above it rather than covering it at any height. Drawn
                      // dead on the cameras that cannot, it was the tallest control on the screen
                      // for a button that could never fire.
                      if (talkBar > 0)
                        Positioned(
                          left: 0,
                          right: 0,
                          bottom: 0,
                          child: _TalkBar(
                            camera: camera,
                            onTalkChanged: (talking) =>
                                setState(() => _talking = talking),
                            micStage: _micStage,
                          ),
                        ),

                      Positioned.fill(
                        child: _compactTray(
                          repository,
                          camera,
                          timeline,
                          replaying: replaying,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }

  /// Whether this camera and this moment get the talk bar, which is also what the tray stops
  /// above.
  ///
  /// Replaying takes it away: a push-to-talk over three-day-old footage speaks into the present
  /// while the picture is somewhere else entirely, and the transport wants the room.
  bool _talkBar(Camera camera, {required bool replaying}) =>
      camera.twoWayAudio && !replaying;

  /// How tall the picture is, given where the tray is settled.
  ///
  /// Exactly what the tray leaves, so the picture's own bottom edge — and the corner that gives it
  /// the screen — is always the last thing above the tray rather than the first thing behind it.
  /// The talk bar counts as tray: it is what the tray stops above, so the picture stops there too.
  ///
  /// Never less than the band. At the top of its travel the tray is covering the picture outright,
  /// and squeezing the video into the sliver behind it is work nobody can see the result of.
  double _pictureHeight(
    BuildContext context,
    double region, {
    required double bottomInset,
  }) => math.max(
    MediaQuery.sizeOf(context).width / Serval.pictureAspect,
    region - bottomInset - _tray.height,
  );

  /// Everything that is not the picture: the controls the camera is worked with, and the feed
  /// under them.
  ///
  /// The tray rather than a column under the picture, and the same tray the wall uses. What it
  /// carries above the feed is what its peek measures — see [ActivitySheet.head] — so *Audio*,
  /// *Move*, the two saves and the timeline are readable at every height that shows any of the
  /// feed, and pushing the tray past them to [SheetDetent.stowed] is what gives the picture the
  /// screen outright.
  ///
  /// The filter rides up with it, dimming the picture rather than covering it, which is the same
  /// bargain the wall's filter detent strikes by leaving 96px of wall behind it.
  Widget _compactTray(
    ServalRepository repository,
    Camera camera,
    TimelineWindow timeline, {
    required bool replaying,
  }) {
    // Handed to the builder rather than rebuilt inside it. The playhead ticks once a second, and
    // the row of buttons and the track it would drag through a rebuild have nothing to say about
    // where the playhead is — only the feed and the filter's counts do.
    final chrome = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _ActionRow(
          audioOn: _theirAudio,
          onAudio: () => _setTheirAudio(!_theirAudio),
          // *Move* is the way into landscape rather than a pad squeezed onto a 412px strip: that
          // is where the design puts pan and tilt, at full size over the picture.
          canMove: switch (repository.ptzProbeFor(camera.id)) {
            PtzKnown(any: true) => true,
            _ => false,
          },
          onMove: () => setState(() => _expanded = true),
          snapshotJob: _snapshotJob,
          onSnapshot: _onSnapshot,
          clipJob: _clipJob,
          onSaveClip: () => _onSaveClip(timeline),
        ),

        // The one place either job's outcome is written down on this layout — the buttons that
        // carry it on the desktop are glyphs here and have nowhere to put a sentence. It arrives
        // and leaves without moving anything: the tray measures its own head, so a line appearing
        // here grows the tray rather than taking the room from the feed.
        if (_snapshotJob != null || _clipJob != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 10, 16, 0),
            child: _SaveStatus(snapshot: _snapshotJob, clip: _clipJob),
          ),

        Padding(
          padding: const EdgeInsets.fromLTRB(16, 13, 16, 12),
          child: Consumer(
            // The track's marks are the feed, drawn along a line instead of down a column, so this
            // is a feed reader like the tray under it and subscribes like one. Re-reading rather
            // than closing over the window from the screen's build: `timelineFor` memoises on the
            // same revision this fires with and hands back the *identical* object when nothing
            // moved, which the scrubber checks for to skip re-deriving its layers. So a heartbeat
            // that changed no mark costs this an equality test and nothing else.
            builder: (context, ref, _) {
              ref.watch(activityRevisionProvider);

              final window = repository.timelineFor(
                camera.id,
                _range,
                includeAllDetections: _allDetections,
              );

              return TimelineScrubber(
                window: window,
                range: _range,
                records: camera.records,
                onRangeChanged: (r) => setState(() => _range = r),
                live: !replaying,
                playhead: _replay.playhead,
                onSeek: (at) => _replay.seekTo(at, window),
                onScrub: (at) => _replay.scrubTo(at, window),
                onBackToLive: _replay.backToLive,
                transport: !replaying
                    ? null
                    : ReplayTransport(
                        compact: true,
                        playing: _replay.playing,
                        rate: _replay.rate,
                        rates: ReplayController.rates,
                        onPlayPause: () =>
                            _replay.playing ? _replay.pause() : _replay.play(),
                        onRateChanged: (rate) =>
                            unawaited(_replay.setRate(rate)),
                      ),
              );
            },
          ),
        ),
      ],
    );

    return ValueListenableBuilder<DateTime?>(
      valueListenable: _replay.playheadSeconds,
      child: chrome,
      builder: (context, asOf, chrome) => Consumer(
        // The tray's own subscription to the feed, so the picture above it, the talk bar below it
        // and the whole screen behind it are not rebuilt by a detection heartbeat. What is left
        // inside is the tray and what it draws, which is everything that has an opinion about the
        // pool and nothing that does not.
        builder: (context, ref, _) {
          ref.watch(activityRevisionProvider);

          // One query for both the feed and the filter over it: the counts beside every facet are
          // a report on the rows underneath, and two reads of the repository are two chances for
          // them to disagree about what the tray is holding.
          final pool = _repository.activityFor(
            cameraId: camera.id,
            asOf: asOf,
            range: _range,
            includeAllDetections: _allDetections,
          );
          final activity = _activityFilter.apply(pool);

          return ActivitySheet(
            controller: _tray,
            head: chrome,
            filter: _activityFilter,
            facets: ActivityFacets.of(
              pool,
              alertsOnly: _activityFilter.alertsOnly,
            ),
            shown: activity.length,
            // No *Which camera* facet: the question is settled by being on this screen, and a
            // section offering the one camera you are already looking at would be a filter that can
            // only ever mean "yes".
            onFilterChanged: (f) => setState(() => _activityFilter = f),
            // The tray may take the room outright here, and the floor the wall keeps would be a
            // strip of picture nobody asked for. You opened this camera; the picture is behind the
            // tray and a drag, a tap on the handle or Back all bring it straight back.
            topInset: 0,
            floor: 0,
            bottomInset: _talkBar(camera, replaying: replaying)
                ? _TalkBar.height
                : 0.0,
            body: (context, detent) => CameraActivity(
              activity: activity,
              filter: _activityFilter,
              range: _range,
              horizon: _repository.feedHorizon(cameraId: camera.id),
              onFilterChanged: (f) => setState(() => _activityFilter = f),
              onSeek: camera.records ? (at) => _openRow(at, timeline) : null,
              // Both are pinned rather than scrolled, so both cost the rows underneath the room
              // they take — and both are asked about the *settled* detent rather than the live
              // height, so neither appears and vanishes under a moving finger. The summary is a
              // paragraph, and waits for the height that has room to read one.
              summary: detent == SheetDetent.raised
                  ? _repository.summaryFor(camera.id, asOf: asOf, range: _range)
                  : null,
              // Nothing at the two heights where none of the feed is on the screen: a meter
              // nobody can see is a subscription nobody asked for.
              listening:
                  camera.aiAudio &&
                  !replaying &&
                  detent != SheetDetent.peek &&
                  detent != SheetDetent.stowed,
            ),
          );
        },
      ),
    );
  }

  /// The phone's trimmer, built once and used twice: as the fixed track, and for the controls that
  /// scroll under it.
  ///
  /// One instance rather than two so the two halves cannot disagree about which end is live — the
  /// lit field and the lit handle are the same fact, and building them separately is how they stop
  /// being.
  ClipTrimmer _compactTrimmer(TimelineWindow timeline) {
    final mode = _clipMode!;
    final event = _eventUnder(mode.selection, timeline);

    return ClipTrimmer(
      selection: mode.selection,
      zoom: mode.zoom,
      window: mode.window,
      marks: timeline.marks,
      compact: true,
      max: _clipMax,
      saving: _clipJob is _SaveWorking,
      onChanged: (s) => _onClipSelectionChanged(s, timeline),
      onZoomChanged: (z) => setState(() => _clipMode = mode.copyWith(zoom: z)),
      onCancel: _exitClipMode,
      onSave: () => _onConfirmClip(timeline),
      onWholeEvent: event == null
          ? null
          : () => _onClipSelectionChanged(
              mode.selection.snapTo(
                event.at,
                event.at.add(event.ran),
                max: _clipMax,
              ),
              timeline,
            ),
    );
  }

  // ---------------------------------------------------------- turn the phone

  /// The picture with the screen to itself, and the controls floating on it.
  ///
  /// Landscape is a real mode rather than a reflow: the frame goes from 412x232 to nearly four
  /// times that, and this is where pan, tilt and zoom belong at full size. The transcript does not
  /// come along — it is reading, and reading happens in portrait — but the timeline does, because
  /// it is the only way back into the recording and losing it would strand you in live.
  Widget _buildImmersive(
    Camera camera,
    TimelineWindow timeline, {
    required bool replaying,
  }) {
    final size = MediaQuery.sizeOf(context);

    // *Expand* means landscape, not "the picture but taller". A phone that has actually been
    // turned is already landscape and the stage is drawn upright into it; a phone still held
    // upright gets the stage laid out at the long edge and turned a quarter, which is what every
    // video player does when you tap full screen without rotating. The alternative — a 16:9 frame
    // letterboxed into 9:19.5 — is a smaller picture than the band it came from.
    final upright = size.height > size.width;

    final stage = _immersiveStage(
      camera,
      timeline,
      replaying: replaying,
      // Whenever the mode was *chosen*, and on any shape of window — a landscape one is exactly
      // where somebody who pressed the corner ends up, and hiding the way out there strands them
      // in a mode they asked for with nothing to press. Only the mode nobody chose has no way
      // out to offer: a phone that arrived here by being turned leaves by being turned back.
      collapsible: _expanded,
    );

    if (!upright) return stage;

    return ColoredBox(
      color: Serval.tile,
      child: Center(
        child: RotatedBox(
          quarterTurns: 1,
          child: SizedBox(width: size.height, height: size.width, child: stage),
        ),
      ),
    );
  }

  Widget _immersiveStage(
    Camera camera,
    TimelineWindow timeline, {
    required bool replaying,
    required bool collapsible,
  }) {
    final repository = _repository;

    return ColoredBox(
      color: Serval.tile,
      child: Stack(
        fit: StackFit.expand,
        children: [
          _StageVideo(
            repository: repository,
            camera: camera,
            replay: _replay,
            liveVideoSize: _liveVideoSize,
            pictureZoom: _pictureZoom,
            allDetections: _allDetections,
            talking: _talking,
            theirAudio: _theirAudio,
            micStage: _micStage,
          ),

          Positioned(
            left: 16,
            top: 14,
            // Clear of the corner control when there is one, so a long camera name runs out of
            // room rather than under it.
            right: collapsible ? 68 : 16,
            child: Row(
              children: [
                _RoundOverlayButton(
                  icon: PhosphorIconsRegular.arrowLeft,
                  tooltip: 'All cameras',
                  onPressed: widget.onBack,
                ),
                const SizedBox(width: 10),
                Flexible(
                  child: Text(
                    camera.name,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontFamily: Nocturne.fontHeading,
                      fontSize: 15,
                      fontWeight: Nocturne.headingWeight,
                      color: Nocturne.text,
                      shadows: [
                        Shadow(blurRadius: 6, color: Color(0xB3000000)),
                      ],
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                ValueListenableBuilder<DateTime?>(
                  valueListenable: _replay.playhead,
                  builder: (context, playhead, _) => _StatusPills(
                    camera: camera,
                    replaying: _replay.replaying,
                    pictureTakenAt:
                        playhead ?? repository.pictureTakenAt(camera.id),
                    pictureZoom: _pictureZoom,
                  ),
                ),
              ],
            ),
          ),

          // The top-right corner, which is where a full-screen video is left. Not the bottom-right
          // one it was entered from: down there is the control row.
          if (collapsible)
            Positioned(
              right: 16,
              top: 14,
              child: _RoundOverlayButton(
                icon: PhosphorIconsRegular.cornersIn,
                tooltip: 'Leave full screen',
                onPressed: () => setState(() => _expanded = false),
              ),
            ),

          // Where pan, tilt and zoom belong: full size, over the video, with the whole picture
          // underneath to aim by.
          Positioned(
            right: 16,
            top: 58,
            child: _PtzOverlay(
              probe: repository.ptzProbeFor(camera.id),
              onMove: _onPtzMove,
              onStop: () => repository.ptzStop(camera.id),
              onHome: () => repository.ptzHome(camera.id),
              onPreset: (preset) =>
                  repository.ptzPreset(camera.id, preset.token),
              zoom: _zoom,
              onZoomChanged: _onZoomChanged,
            ),
          ),

          Positioned(
            left: 0,
            right: 0,
            bottom: 0,
            child: _ImmersiveControls(
              camera: camera,
              replaying: replaying,
              caption: _subtitles && !replaying
                  ? repository.liveCaptionFor(camera.id)
                  : null,
              subtitles: _subtitles,
              allDetections: _allDetections,
              theirAudio: _theirAudio,
              timeline: timeline,
              range: _range,
              replay: _replay,
              onRangeChanged: (r) => setState(() => _range = r),
              onTheirAudioChanged: _setTheirAudio,
              onToggleSubtitles: () => setState(() => _subtitles = !_subtitles),
              onToggleAllDetections: _toggleAllDetections,
              onTalkChanged: (talking) => setState(() => _talking = talking),
              micStage: _micStage,
              onSnapshot: _onSnapshot,
              snapshotJob: _snapshotJob,
            ),
          ),
        ],
      ),
    );
  }
}
