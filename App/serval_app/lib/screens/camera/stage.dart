part of '../camera_screen.dart';

class _VideoStage extends StatelessWidget {
  const _VideoStage({
    required this.repository,
    required this.camera,
    required this.replay,
    required this.liveVideoSize,
    required this.pictureZoom,
    required this.caption,
    required this.theirAudio,
    required this.subtitles,
    required this.allDetections,
    required this.talking,
    required this.micStage,
    required this.zoom,
    required this.onTheirAudioChanged,
    required this.onToggleSubtitles,
    required this.onToggleAllDetections,
    required this.onTalkChanged,
    required this.onVolumeChanged,
    required this.onPtzMove,
    required this.onPtzStop,
    required this.onPtzHome,
    required this.onPtzPreset,
    required this.onZoomChanged,
    required this.ptz,
    this.clip,
    this.onPlayClip,
  });

  /// The range being trimmed, when the screen is a trimmer.
  ///
  /// Its presence replaces the stage's usual chrome. Pan, tilt and talk-back all act on the camera
  /// *now*, and none of them means anything while the picture is a frame from an hour ago being
  /// used to set an end.
  final ClipSelection? clip;

  final VoidCallback? onPlayClip;

  final ServalRepository repository;
  final Camera camera;
  final ReplayController replay;

  /// The live picture's dimensions, for [_PictureAligned]. Written by [WebRtcView] below and read
  /// beside it, which is why it is owned by the screen rather than by either.
  final ValueNotifier<Size?> liveVideoSize;

  /// How far the picture is magnified, written by [PictureZoom] inside [_StageVideo] and read by
  /// the pill above it — siblings again, for the same reason.
  final ValueNotifier<double> pictureZoom;
  final String? caption;
  final bool theirAudio;
  final bool subtitles;

  /// Whether this camera is drawing every detection it stored rather than only the alerting ones.
  /// Unlike [subtitles] this is held by the repository and follows the account, because it is a
  /// statement about a camera rather than about the window you are looking at it through.
  final bool allDetections;
  final bool talking;

  /// Where the live session's microphone is, written by [WebRtcView] below and read by the talk
  /// button in the control row — siblings again, like [liveVideoSize].
  final ValueNotifier<MicStage> micStage;

  final ZoomPosition zoom;
  final ValueChanged<bool> onTheirAudioChanged;
  final VoidCallback onToggleSubtitles;
  final VoidCallback onToggleAllDetections;
  final ValueChanged<bool> onTalkChanged;
  final ValueChanged<double> onVolumeChanged;

  final ValueChanged<PtzDirection> onPtzMove;
  final VoidCallback onPtzStop;
  final VoidCallback onPtzHome;
  final ValueChanged<PtzPreset> onPtzPreset;
  final ValueChanged<double> onZoomChanged;

  /// What the camera says its PTZ can do. The controls are drawn from this, never from the
  /// camera's `ptzConfigured` flag — that says only that there is an endpoint to ask.
  final PtzProbe ptz;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: Serval.tile,
      borderRadius: BorderRadius.circular(9),
      border: Border.all(color: Serval.panelBorder),
    ),
    child: ClipRRect(
      borderRadius: BorderRadius.circular(9),
      child: Stack(
        fit: StackFit.expand,
        children: [
          // The same picture the phone draws, at desktop size. Everything below this is chrome
          // that the phone puts somewhere else — which is the whole difference between the two.
          _StageVideo(
            repository: repository,
            camera: camera,
            replay: replay,
            liveVideoSize: liveVideoSize,
            pictureZoom: pictureZoom,
            allDetections: allDetections,
            talking: talking,
            theirAudio: theirAudio,
            micStage: micStage,
          ),
          if (clip case final clip?) ...[
            Positioned(left: 14, top: 14, child: _ClipEndPill(selection: clip)),
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: _ClipStageControls(
                selection: clip,
                theirAudio: theirAudio,
                onTheirAudioChanged: onTheirAudioChanged,
                onPlay: onPlayClip,
              ),
            ),
          ] else ...[
            Positioned(
              left: 14,
              top: 14,
              // The seam the repository's `pictureTakenAt` describes, driven by the replay
              // position rather than by the repository — it ticks ~4x/s, and notification on that
              // interface means the structure changed. The pill's own timer stops itself the moment
              // this stops being null.
              child: ValueListenableBuilder<DateTime?>(
                valueListenable: replay.playhead,
                builder: (context, playhead, _) => _StatusPills(
                  camera: camera,
                  replaying: replay.replaying,
                  pictureTakenAt:
                      playhead ?? repository.pictureTakenAt(camera.id),
                  pictureZoom: pictureZoom,
                ),
              ),
            ),
            // Drawn from what the camera reported, never from "an ONVIF URL is set". That flag is
            // wrong on the live NVR for both of its cameras: it would give a fixed bullet a pad and
            // a zoom slider it has neither of, and a pan/tilt camera with a fixed lens a zoom
            // slider that does nothing.
            Positioned(
              right: 14,
              top: 14,
              child: _PtzOverlay(
                probe: ptz,
                onMove: onPtzMove,
                onStop: onPtzStop,
                onHome: onPtzHome,
                onPreset: onPtzPreset,
                zoom: zoom,
                onZoomChanged: onZoomChanged,
              ),
            ),
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: _VideoControls(
                allDetections: allDetections,
                onToggleAllDetections: onToggleAllDetections,
                camera: camera,
                caption: subtitles ? caption : null,
                theirAudio: theirAudio,
                subtitles: subtitles,
                replaying: replay.replaying,
                onTheirAudioChanged: onTheirAudioChanged,
                onToggleSubtitles: onToggleSubtitles,
                onTalkChanged: onTalkChanged,
                micStage: micStage,
                volume: repository.playbackVolumeFor(camera.id),
                onVolumeChanged: onVolumeChanged,
              ),
            ),
          ],
        ],
      ),
    ),
  );
}

/// Which end the picture is showing, and when it is.
///
/// The whole trimmer rests on this: you set the start by *watching* it, so the picture has to say
/// which end it is following or a frame is just a frame.
class _ClipEndPill extends StatelessWidget {
  const _ClipEndPill({required this.selection});

  final ClipSelection selection;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    spacing: 8,
    children: [
      Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: Serval.panel.withValues(alpha: 0.78),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: Nocturne.mix(Nocturne.accent, 50)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          spacing: 7,
          children: [
            Icon(
              selection.active == ClipEnd.start
                  ? PhosphorIconsRegular.arrowLineLeft
                  : PhosphorIconsRegular.arrowLineRight,
              size: 13,
              color: Nocturne.accent300,
            ),
            Text(
              selection.active == ClipEnd.start
                  ? 'The start of your clip'
                  : 'The end of your clip',
              style: const TextStyle(
                fontSize: 11.5,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.accent300,
              ),
            ),
          ],
        ),
      ),
      Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
        decoration: BoxDecoration(
          color: Serval.panel.withValues(alpha: 0.72),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
        ),
        child: Text(
          stampLabel(selection.activeAt),
          style: monoStyle(
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 60),
          ),
        ),
      ),
    ],
  );
}

/// What the stage offers while a range is being chosen: play it, hear it, and the one instruction
/// that makes the mode legible.
class _ClipStageControls extends StatelessWidget {
  const _ClipStageControls({
    required this.selection,
    required this.theirAudio,
    required this.onTheirAudioChanged,
    this.onPlay,
  });

  final ClipSelection selection;
  final bool theirAudio;
  final ValueChanged<bool> onTheirAudioChanged;
  final VoidCallback? onPlay;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(16, 52, 16, 16),
    decoration: BoxDecoration(
      gradient: LinearGradient(
        begin: Alignment.bottomCenter,
        end: Alignment.topCenter,
        colors: [
          Serval.overlay.withValues(alpha: 0.8),
          const Color(0x00000000),
        ],
      ),
    ),
    child: Row(
      spacing: 10,
      children: [
        NocturneButton(
          label: 'Play the ${clipSpokenLabel(selection.span)}',
          icon: PhosphorIconsFill.play,
          variant: NocturneButtonVariant.primary,
          height: 40,
          fontSize: 13.5,
          horizontalPadding: 18,
          borderRadius: 20,
          onPressed: onPlay,
        ),
        NocturneButton(
          label: theirAudio ? 'Sound on' : 'Sound off',
          icon: theirAudio
              ? PhosphorIconsRegular.speakerSimpleHigh
              : PhosphorIconsRegular.speakerSimpleX,
          height: 40,
          fontSize: 13,
          horizontalPadding: 15,
          borderRadius: 20,
          onPressed: () => onTheirAudioChanged(!theirAudio),
        ),
        const Spacer(),

        // Flexible, so a narrow stage loses the end of the hint rather than the whole row
        // overflowing — the two buttons are the controls and must keep their room.
        Flexible(
          child: Row(
            mainAxisSize: MainAxisSize.min,
            spacing: 8,
            children: [
              Icon(
                PhosphorIconsRegular.handGrabbing,
                size: 15,
                color: Nocturne.mix(Nocturne.text, 40),
              ),
              Flexible(
                child: Text(
                  'Hold an end and the picture follows it',
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 55),
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

class _StatusPills extends StatefulWidget {
  const _StatusPills({
    required this.camera,
    required this.pictureTakenAt,
    required this.pictureZoom,
    this.replaying = false,
  });

  final Camera camera;

  /// When the picture on screen was taken, or null while it is live.
  final DateTime? pictureTakenAt;

  /// How far the picture is magnified. Said out loud whenever it is not 1, because a picture that
  /// somebody zoomed and walked away from is indistinguishable from the whole scene, and the whole
  /// scene is what anyone reading a camera assumes they are looking at.
  final ValueListenable<double> pictureZoom;

  final bool replaying;

  @override
  State<_StatusPills> createState() => _StatusPillsState();
}

class _StatusPillsState extends State<_StatusPills> {
  Timer? _tick;
  DateTime _now = DateTime.now();

  @override
  void initState() {
    super.initState();
    _syncTick();
  }

  @override
  void didUpdateWidget(_StatusPills old) {
    super.didUpdateWidget(old);
    _syncTick();
  }

  /// Only a live picture needs a ticking clock, and only it can afford one: nothing else on this
  /// screen rebuilds every second. Dragging back into the recording supplies a fixed time, and a
  /// fixed time is fixed — so the timer stops, and starts again on the way back to live.
  void _syncTick() {
    final wantsTick = widget.pictureTakenAt == null;
    if (wantsTick == (_tick != null)) return;

    _tick?.cancel();
    _tick = wantsTick
        ? Timer.periodic(
            const Duration(seconds: 1),
            (_) => setState(() => _now = DateTime.now()),
          )
        : null;
  }

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Row(
    children: [
      // Wrapped alone rather than lifted, so a pinch does not put the ticking clock beside it
      // through a rebuild on every pointer sample.
      ValueListenableBuilder<double>(
        valueListenable: widget.pictureZoom,
        // A tenth of a turn is under the noise of a pinch and reads as a flickering digit; a
        // picture within that of the whole scene is the whole scene.
        builder: (context, scale, _) => scale < 1.05
            ? const SizedBox.shrink()
            : Padding(
                padding: const EdgeInsets.only(right: 8),
                child: Pill.outline('${scale.toStringAsFixed(1)}×'),
              ),
      ),
      // The camera is still recording while you replay, but a REC dot is a claim about the
      // picture on screen, and that picture is an hour old.
      if (widget.replaying)
        Pill.outline('Replay')
      else if (widget.camera.isRecording)
        Pill.recording(onVideo: true)
      // Said rather than left blank. A camera that keeps nothing has no REC dot and neither does
      // one whose recorder has died, and the gap between those two looks identical.
      else if (!widget.camera.records)
        Pill.outline('Not kept'),
      const SizedBox(width: 8),
      Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.bg, 70),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
        ),
        child: Text(
          stampLabel(widget.pictureTakenAt ?? _now),
          style: monoStyle(
            fontSize: 11,
            color: Nocturne.mix(Nocturne.text, 60),
          ),
        ),
      ),
    ],
  );
}

/// The bottom gradient and everything in it: the caption Serval is hearing
/// right now, and the row you act from.
class _VideoControls extends StatelessWidget {
  const _VideoControls({
    required this.camera,
    required this.caption,
    required this.theirAudio,
    required this.subtitles,
    required this.allDetections,
    required this.replaying,
    required this.onTheirAudioChanged,
    required this.onToggleSubtitles,
    required this.onToggleAllDetections,
    required this.onTalkChanged,
    required this.micStage,
    required this.onVolumeChanged,
    required this.volume,
  });

  final Camera camera;
  final String? caption;
  final bool theirAudio;
  final bool subtitles;
  final bool allDetections;
  final VoidCallback onToggleAllDetections;

  /// Passed down as a listenable rather than a value so a drag rebuilds the pill alone — these
  /// controls sit over the video and re-laying them out per pointer sample would be visible.
  final ValueListenable<double> volume;
  final ValueChanged<double> onVolumeChanged;

  /// Talking is a live act. The WebRTC session that would carry the microphone is gone while you
  /// replay, so the button is disabled rather than left to fail silently.
  final bool replaying;
  final ValueChanged<bool> onTheirAudioChanged;
  final VoidCallback onToggleSubtitles;
  final ValueChanged<bool> onTalkChanged;

  /// See [_VideoStage.micStage].
  final ValueNotifier<MicStage> micStage;

  @override
  Widget build(BuildContext context) => Stack(
    children: [
      // Paint only — see the same scrim in [_ImmersiveControls]. A decoration that hit-tests its
      // rectangle would put the bottom of every desktop stage out of reach of a scroll-to-zoom.
      const Positioned.fill(
        child: IgnorePointer(
          child: DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(
                begin: Alignment.bottomCenter,
                end: Alignment.topCenter,
                colors: [Color(0xD1000000), Color(0x00000000)],
              ),
            ),
          ),
        ),
      ),
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 60, 16, 16),
        child: Column(
          children: [
            if (caption != null) ...[
              _Caption(text: caption!),
              const SizedBox(height: 12),
            ],
            // Wrapped rather than one Row: at the design's width the talk
            // controls and the ready-made replies sit on one line, and below
            // it the replies drop to a second line instead of being clipped.
            // The full width is needed for `spaceBetween` to mean anything.
            // The replies are commented out below, so today this wraps one child.
            SizedBox(
              width: double.infinity,
              child: Wrap(
                alignment: WrapAlignment.spaceBetween,
                crossAxisAlignment: WrapCrossAlignment.center,
                spacing: 16,
                runSpacing: 12,
                children: [
                  // A Wrap rather than a Row, for the reason the comment above gives about the
                  // replies: four controls and a talk button do not fit every width this stage is
                  // given, and the narrow case should drop a control to a second line rather than
                  // clip it. Identical to a Row wherever they do fit, which is the design's width.
                  Wrap(
                    crossAxisAlignment: WrapCrossAlignment.center,
                    spacing: 10,
                    runSpacing: 10,
                    children: [
                      HoldToTalkButton(
                        // The camera has to opt into the audio backchannel
                        // before go2rtc will even probe it — and there has to be
                        // a live session for the microphone to ride.
                        //
                        // And the browser has to be willing to hand over a
                        // microphone at all: `getUserMedia` is secure-context
                        // only, so over plain HTTP there is nothing to hand the
                        // session's sender however hard the button is pressed.
                        // That one is structural and no amount of retrying fixes
                        // it, so it is named here rather than left to `mic` —
                        // which reports the refusals that *are* worth retrying.
                        enabled:
                            camera.twoWayAudio && !replaying && isSecureContext,
                        // Only when the origin is the *binding* constraint. A camera with no
                        // backchannel, or one being replayed, would not talk over HTTPS either, and
                        // blaming the scheme for that would send someone off configuring TLS to fix
                        // something TLS does not fix.
                        disabledReason:
                            camera.twoWayAudio && !replaying && !isSecureContext
                            ? 'Talk-back needs HTTPS'
                            : null,
                        mic: micStage,
                        // No request goes out here. The sending half of the audio is already in
                        // the WebRTC session; the first hold opens the microphone and hands it to
                        // that sender, and every hold after only enables it — so speech reaches
                        // the camera's backchannel over the connection the video is already on.
                        onTalkStart: () => onTalkChanged(true),
                        onTalkEnd: () => onTalkChanged(false),
                      ),
                      // Mute lives on this pill's speaker glyph. See [VolumeControl].
                      VolumeControl(
                        volume: volume,
                        onChanged: onVolumeChanged,
                        muted: !theirAudio,
                        onMutedChanged: (muted) => onTheirAudioChanged(!muted),
                      ),
                      VideoToggle(
                        label: 'Subtitles',
                        icon: PhosphorIconsRegular.chatCenteredText,
                        active: subtitles,
                        onTap: onToggleSubtitles,
                      ),
                      // Reads "everything the detector saw", not "alert me about more". It draws the
                      // episodes that are stored and normally silent, which is the question you ask
                      // of a camera that is reporting something you cannot see.
                      VideoToggle(
                        label: 'All detections',
                        icon: PhosphorIconsRegular.boundingBox,
                        active: allDetections,
                        onTap: onToggleAllDetections,
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    ],
  );
}

/// The screen while it is a trimmer: what is selected, and how far the track is zoomed in.
///
/// A pair rather than two fields on the state because they are only ever meaningful together —
/// there is no zoom without a selection to zoom around, and the mode is exactly "both of these
/// exist". Null is not in clip mode.
