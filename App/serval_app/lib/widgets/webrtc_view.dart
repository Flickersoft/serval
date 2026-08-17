import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../data/serval_repository.dart';
import '../models/camera.dart';
import '../playback/microphone_gate.dart';
import '../playback/webrtc_session.dart';
import '../theme/serval_tokens.dart';
import 'tile_placeholder.dart';

/// The focused camera's live video.
///
/// HLS is the Server's recorder and its archive; it is also multi-second latency, which is fine
/// for watching and useless for *interacting*. Pan/tilt and talk-back both need to see the
/// result immediately, so this view is WebRTC — see [WebRtcSession], which owns the signalling
/// and the tracks. This widget renders the session and forwards its knobs.
///
/// **Talk-back rides this same session.** There is no second signalling path and no talk-back
/// endpoint: for a camera with `twoWayAudio` set, the offer declares a *sending* audio m-line from
/// the start, but leaves it empty. The microphone is opened on the first press and handed to that
/// sender with `replaceTrack`, which changes no SDP — so talk-back still never renegotiates, and
/// the operating system's microphone indicator stays dark until someone actually speaks.
///
/// When any of that fails — WebRTC disabled, go2rtc unreachable, the camera offline, no ICE path
/// — it falls back to the wall's snapshot for the same camera. A live view that cannot start
/// must not become a black rectangle.
class WebRtcView extends StatefulWidget {
  const WebRtcView({
    super.key,
    required this.repository,
    required this.camera,
    this.talking = false,
    this.theirAudio = true,
    this.volume = 1,
    this.gainDb = 0,
    this.gateRms,
    this.videoSize,
    this.micStage,
  });

  final ServalRepository repository;
  final Camera camera;

  /// Where to publish the picture's own dimensions, for whatever is drawn over it.
  ///
  /// The view is fitted inside the stage without distorting it, so unless the stage happens to
  /// share the stream's aspect ratio the picture is letterboxed within it — and a detection box in
  /// normalised coordinates means a fraction of the *picture*. Owned by the caller, because the
  /// overlay is the caller's sibling to this and not its child.
  ///
  /// Null while nothing is drawn over the view, which is every use but the single-camera stage.
  final ValueNotifier<Size?>? videoSize;

  /// Whether *Hold to talk* is held. The first time it is, the microphone is opened; after that it
  /// gates whether the held device sends.
  final bool talking;

  /// Where to publish how the microphone is doing, for the talk button to read.
  ///
  /// Owned by the caller for the same reason [videoSize] is: the button is this widget's sibling
  /// rather than its child — the phone pins it below the picture entirely — so the state has to
  /// travel up before it can come back down. Null wherever nothing is listening.
  final ValueNotifier<MicStage>? micStage;

  /// Whether the camera's own audio plays.
  final bool theirAudio;

  /// How loudly, 0..1, at or below unity. Distinct from [theirAudio]: that stops the audio arriving
  /// at all, this scales what does.
  final double volume;

  /// How far above unity to amplify, in dB, and the level the gate treats as silence.
  ///
  /// [volume] and this are the two halves one slider position splits into — `playbackFromTravel` does
  /// the splitting, and the caller passes the result rather than the position, because this widget is
  /// the only place the two are needed apart. Below unity this is 0 and the whole level is [volume];
  /// above it [volume] sits at 1 and this carries the rest.
  ///
  /// Passed in rather than read off [camera] for the same reason [volume] is: the caller holds the
  /// live value. A [Camera] reaching this widget is a snapshot taken when the screen opened, so a
  /// level changed while watching would not be on it.
  final double gainDb;
  final double? gateRms;

  @override
  State<WebRtcView> createState() => _WebRtcViewState();
}

class _WebRtcViewState extends State<WebRtcView> {
  late WebRtcSession _session;

  AppLifecycleListener? _lifecycle;

  /// When the App went away, or null while it is in front of somebody.
  DateTime? _hiddenAt;

  /// Sessions started to replace one that was lost, since the last that reached [WebRtcStage.live].
  int _restarts = 0;

  /// The restart waiting out its backoff, if any.
  Timer? _pendingRestart;

  /// Automatic restarts before this view stops trying and leaves the fallback up.
  ///
  /// There has to be a limit. Signalling is a `POST /api/cameras/{id}/webrtc` per attempt, so a
  /// camera that is genuinely gone would otherwise be an open loop against the Server for as long
  /// as somebody leaves this screen open — and after three failures in quick succession, the next
  /// one is not going to be different either. A person who wants another go leaves and comes back.
  static const _maxRestarts = 3;

  /// How long the App must have been away for a resume to rebuild a session that still claims to
  /// be connected.
  ///
  /// Matches the grace figure in `Docs/live-view.md`: an alt-tab to look at something else should
  /// not renegotiate WebRTC, and anything longer is long enough for a phone to have slept.
  static const _staleAfterHidden = Duration(seconds: 10);

  @override
  void initState() {
    super.initState();
    _session = _open();

    // This widget owns a connection, so it owns the resume — there is no plumbing to add and
    // nothing above it that would know when to act. `onShow` rather than `onResume` for the same
    // reason `main.dart` uses it: losing window focus must not disturb anything.
    _lifecycle = AppLifecycleListener(onHide: _onHidden, onShow: _onShown);
  }

  WebRtcSession _open() {
    final session = WebRtcSession(
      repository: widget.repository,
      camera: widget.camera,
      talking: widget.talking,
      theirAudio: widget.theirAudio,
      volume: widget.volume,
      gainDb: widget.gainDb,
      gateRms: widget.gateRms,
    );
    session.renderer.addListener(_publishVideoSize);
    session.stage.addListener(_onStageChanged);
    session.micStage.addListener(_publishMicStage);
    session.start();
    return session;
  }

  void _publishMicStage() => widget.micStage?.value = _session.micStage.value;

  void _onHidden() => _hiddenAt = DateTime.now();

  /// Back in front of somebody. Rebuild the session unless there is good reason to think it
  /// survived.
  ///
  /// Two conditions, because either alone leaves a hole. A session that has noticed it lost its
  /// transport is plainly worth replacing. But a phone whose radio slept has a peer connection that
  /// is dead and has *not been told* — it still reads connected, and its last frame sits on screen
  /// looking like the driveway right now. Time away is the only evidence available for that one.
  void _onShown() {
    final hiddenAt = _hiddenAt;
    _hiddenAt = null;

    final away = hiddenAt == null
        ? Duration.zero
        : DateTime.now().difference(hiddenAt);
    if (_session.isHealthy && away < _staleAfterHidden) return;

    // A resume is a person asking for this, not a failure — so it does not spend the budget and it
    // does not wait out a backoff.
    _restarts = 0;
    _restart();
  }

  /// A session that has lost its connection cannot be revived — go2rtc's signalling is one
  /// request/response, so there is nowhere to renegotiate to. Everything here is a new session.
  void _onStageChanged() {
    switch (_session.stage.value) {
      case WebRtcStage.live:
        // Reaching the picture is what proves the last restart worked, and the only thing that
        // should refill the budget. Counting attempts instead would let a session that connects
        // and drops every two seconds retry forever.
        _restarts = 0;
        _pendingRestart?.cancel();
        _pendingRestart = null;

      // Back to connecting on a session that has already reported losing its transport — the
      // settle timer in `WebRtcSession` giving up. A view that has only just opened is also
      // `connecting`, which is why this asks the session rather than the stage alone.
      case WebRtcStage.connecting when !_session.isHealthy:
        _scheduleRestart();

      case WebRtcStage.failed:
        _scheduleRestart();

      case WebRtcStage.connecting:
        break;
    }
  }

  void _scheduleRestart() {
    if (_pendingRestart != null) return;
    if (_restarts >= _maxRestarts) return;

    // Widening, so a camera that is down is asked about less and less often rather than at a fixed
    // rate. Short at first because most of what this recovers from is momentary.
    final wait = Duration(seconds: 2 << _restarts);
    _restarts++;

    _pendingRestart = Timer(wait, () {
      _pendingRestart = null;
      if (mounted) _restart();
    });
  }

  /// Swaps in a new session for the one on screen.
  ///
  /// `setState` because the build binds to the session's own [WebRtcSession.stage] notifier, and
  /// the new session has a different one.
  void _restart() {
    final old = _session;
    old.renderer.removeListener(_publishVideoSize);
    old.stage.removeListener(_onStageChanged);
    old.micStage.removeListener(_publishMicStage);

    // Both belong to the picture that is going away. A size left behind would have the overlay lay
    // detection boxes out to a stream that no longer exists; a microphone left behind would have
    // the talk button claim one is open after the session holding it has gone — and the new
    // session's own first notification only arrives on a press, so nothing else would correct it.
    widget.videoSize?.value = null;
    widget.micStage?.value = MicStage.closed;

    setState(() => _session = _open());
    unawaited(old.dispose());
  }

  /// The renderer is a `ValueNotifier<RTCVideoValue>`, so this is every change in the picture's
  /// dimensions — including the one on reconnect to a camera at a different resolution.
  ///
  /// Rotation is folded in rather than reported alongside: at 90 or 270 the displayed picture is
  /// the stream's dimensions swapped, and a consumer laying out to this aspect wants what is on
  /// screen rather than what was encoded.
  void _publishVideoSize() {
    final target = widget.videoSize;
    if (target == null) return;

    final value = _session.renderer.value;
    if (value.width <= 0 || value.height <= 0) {
      target.value = null;
      return;
    }

    final turned = value.rotation == 90 || value.rotation == 270;
    target.value = turned
        ? Size(value.height, value.width)
        : Size(value.width, value.height);
  }

  @override
  void didUpdateWidget(WebRtcView oldWidget) {
    super.didUpdateWidget(oldWidget);

    // A session is for one camera, once — switching cameras is a new session.
    if (oldWidget.camera.id != widget.camera.id) {
      // A fresh camera gets a fresh budget: three failures on the last one say nothing about this
      // one, and the pending restart they earned belongs to a session that is about to go.
      _restarts = 0;
      _pendingRestart?.cancel();
      _pendingRestart = null;
      _restart();
      return;
    }

    if (oldWidget.talking != widget.talking) {
      _session.setTalking(widget.talking);
    }
    if (oldWidget.theirAudio != widget.theirAudio) {
      _session.setTheirAudio(widget.theirAudio);
    }

    // The gain is watched alongside the volume, not instead of it. One slider position produces both
    // — see `playbackFromTravel` — but which of the two moves depends on which side of unity the drag
    // is on, and a drag that crosses unity changes one while the other sits still.
    if (oldWidget.volume != widget.volume ||
        oldWidget.gainDb != widget.gainDb ||
        oldWidget.gateRms != widget.gateRms) {
      _session.setLevel(
        volume: widget.volume,
        gainDb: widget.gainDb,
        gateRms: widget.gateRms,
      );
    }
  }

  @override
  void dispose() {
    _lifecycle?.dispose();
    _pendingRestart?.cancel();
    _session.renderer.removeListener(_publishVideoSize);
    _session.stage.removeListener(_onStageChanged);
    _session.micStage.removeListener(_publishMicStage);
    // The notifiers outlive this view — they belong to the stage — so leaving a size behind would
    // have the overlay keep laying out to a camera that is no longer on screen, and leaving a
    // microphone behind would have the button claim one is open after the session that held it
    // has gone.
    widget.videoSize?.value = null;
    widget.micStage?.value = MicStage.closed;
    _session.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<WebRtcStage>(
    valueListenable: _session.stage,
    builder: (context, stage, _) => switch (stage) {
      WebRtcStage.live => RTCVideoView(
        _session.renderer,
        objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain,
      ),
      WebRtcStage.connecting => StageFallback(
        repository: widget.repository,
        camera: widget.camera,
        message: 'Connecting…',
      ),
      WebRtcStage.failed => StageFallback(
        repository: widget.repository,
        camera: widget.camera,
        message: 'Live view unavailable',
        detail: _session.failure,
      ),
    },
  );
}

/// What the single-camera stage paints when there is no picture — a live view that has not
/// connected, or a replay that could not open.
///
/// Underneath the design's placeholder it shows the wall's own snapshot of this camera, which is
/// a real frame from a second ago rather than nothing at all — so a live view that failed still
/// tells you whether anyone is at the door.
class StageFallback extends StatelessWidget {
  const StageFallback({
    super.key,
    required this.repository,
    required this.camera,
    required this.message,
    this.detail,
  });

  final ServalRepository repository;
  final Camera camera;
  final String message;
  final String? detail;

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: [
      TilePlaceholderView(placeholder: camera.placeholder),
      ValueListenableBuilder<Uint8List?>(
        valueListenable: repository.frameNotifier(camera.id),
        builder: (context, frame, _) => frame == null
            ? const SizedBox.shrink()
            // Contained, and the bars given the video ground rather than the stripe underneath, so
            // they read as the edge of the picture instead of as the placeholder showing through a
            // frame that is genuinely this camera's.
            : ColoredBox(
                color: Serval.tile,
                child: Image.memory(
                  frame,
                  fit: BoxFit.contain,
                  gaplessPlayback: true,
                ),
              ),
      ),
      LivePlaceholderView(
        camera: camera,
        label: message.toUpperCase(),
        detail: detail,
      ),
    ],
  );
}
