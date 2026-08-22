part of '../camera_screen.dart';

class _PtzOverlay extends StatelessWidget {
  const _PtzOverlay({
    required this.probe,
    required this.onMove,
    required this.onStop,
    required this.onHome,
    required this.onPreset,
    required this.zoom,
    required this.onZoomChanged,
  });

  final PtzProbe probe;
  final ValueChanged<PtzDirection> onMove;
  final VoidCallback onStop;
  final VoidCallback onHome;
  final ValueChanged<PtzPreset> onPreset;
  final ZoomPosition zoom;
  final ValueChanged<double> onZoomChanged;

  @override
  Widget build(BuildContext context) => switch (probe) {
    PtzProbing() || PtzNotConfigured() => const SizedBox.shrink(),
    PtzUnknown(:final reason) => _PtzUnavailable(reason: reason),
    PtzKnown(any: false) => const SizedBox.shrink(),
    final PtzKnown known => Column(
      children: [
        if (known.panTilt)
          PtzPad(
            onMove: onMove,
            onStop: onStop,
            onHome: _homeCallback(context, known),
            homeTooltip: _homeTooltip(known),
          ),
        if (known.panTilt && known.zoom) const SizedBox(height: 8),
        if (known.zoom) ZoomControl(zoom: zoom, onChanged: onZoomChanged),
      ],
    ),
  };

  /// The centre key, decided on the camera's answer and never on a guess. Sending a fixed preset
  /// token fires at nothing on a camera that stores no presets, which is the common case.
  VoidCallback? _homeCallback(BuildContext context, PtzKnown known) =>
      switch (PtzHomeAction.of(known)) {
        PtzHomeGoHome() => onHome,
        PtzHomeGoPreset(:final preset) => () => onPreset(preset),
        PtzHomeChoose(:final presets) => () => _choose(context, presets),
        PtzHomeNone() => null,
      };

  String? _homeTooltip(PtzKnown known) => switch (PtzHomeAction.of(known)) {
    PtzHomeGoHome() => 'Home position',
    PtzHomeGoPreset(:final preset) => preset.label,
    PtzHomeChoose() => 'Stored positions',
    PtzHomeNone() => null,
  };

  /// Several presets and no home position: pick, rather than recalling whichever the camera
  /// happened to list first. Getting it wrong physically turns a camera somebody is watching.
  Future<void> _choose(BuildContext context, List<PtzPreset> presets) async {
    final chosen = await showNocturneDialog<PtzPreset>(
      context: context,
      builder: (context) => NocturneDialog(
        title: 'Go to a stored position',
        width: 360,
        body: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final preset in presets)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: NocturneButton(
                  label: preset.label,
                  onPressed: () => Navigator.of(context).pop(preset),
                ),
              ),
          ],
        ),
        actions: [
          NocturneButton(
            label: 'Cancel',
            onPressed: () => Navigator.of(context).pop(),
          ),
        ],
      ),
    );

    if (chosen != null) onPreset(chosen);
  }
}

/// The camera did not answer. Says so in the Server's words rather than drawing controls that
/// might not work, or nothing at all — which would read as "this camera has no pan/tilt".
class _PtzUnavailable extends StatelessWidget {
  const _PtzUnavailable({required this.reason});

  final String reason;

  @override
  Widget build(BuildContext context) => Container(
    constraints: const BoxConstraints(maxWidth: 220),
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.bg, 78),
      borderRadius: BorderRadius.circular(9),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          'Pan and tilt unavailable',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.mix(Nocturne.text, 80),
          ),
        ),
        const SizedBox(height: 3),
        Text(
          reason,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            height: 1.4,
            color: Nocturne.mix(Nocturne.text, 50),
          ),
        ),
      ],
    ),
  );
}

/// What Serval is hearing, centered over the video.
class _Caption extends StatelessWidget {
  const _Caption({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) => ConstrainedBox(
    constraints: const BoxConstraints(maxWidth: 620),
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
      decoration: BoxDecoration(
        color: Nocturne.mix(Serval.tile, 82),
        borderRadius: BorderRadius.circular(Nocturne.radiusMd),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const PhosphorIcon(
            PhosphorIconsFill.waveform,
            size: 15,
            color: Nocturne.accent400,
          ),
          const SizedBox(width: 9),
          Flexible(
            child: Text(
              text,
              style: const TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 15,
                height: 1.4,
                color: Nocturne.text,
              ),
            ),
          ),
        ],
      ),
    ),
  );
}

// ------------------------------------------------------------------- design 8a

/// The picture as a band across the top of a phone.
///
/// Nothing is painted on it but the detection boxes and the two things that date the frame — the
/// recording pill and the clock — because at 412x232 anything else covers the scene, and seeing
/// the scene is the entire reason for opening a camera. The controls the desktop floats here live
/// under the picture instead; the one exception is the corner that gives the picture the screen.
class _PictureBand extends StatelessWidget {
  const _PictureBand({
    required this.repository,
    required this.camera,
    required this.replay,
    required this.liveVideoSize,
    required this.pictureZoom,
    required this.allDetections,
    required this.talking,
    required this.theirAudio,
    required this.micStage,
    required this.onExpand,
    this.castState = CastState.unavailable,
    this.onCast,
    this.clip,
    this.onPlayClip,
  });

  final ServalRepository repository;
  final Camera camera;
  final ReplayController replay;
  final ValueNotifier<Size?> liveVideoSize;
  final ValueNotifier<double> pictureZoom;

  /// See [_StageVideo.allDetections].
  final bool allDetections;

  /// See [_StageVideo.talking]. The button that sets it is not in this band — it is pinned to the
  /// bottom of the screen, in [_TalkBar] — but the session it acts on is, so it comes through here.
  final bool talking;
  final bool theirAudio;

  /// See [_VideoStage.micStage].
  final ValueNotifier<MicStage> micStage;

  final VoidCallback onExpand;

  /// See [_CastOverlayButton]. Drawn in the free corner: the pills have the top left and the
  /// full-screen control the bottom right.
  final CastState castState;
  final VoidCallback? onCast;

  /// The range being trimmed, when the screen is a trimmer. See [_VideoStage.clip].
  final ClipSelection? clip;

  final VoidCallback? onPlayClip;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: Serval.tile,
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Stack(
      fit: StackFit.expand,
      children: [
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
          Positioned(left: 12, top: 12, child: _ClipEndPill(selection: clip)),

          // Centred rather than in the corner, and the only control on the picture: at 412px the
          // stage has room for one thing, and the one thing is watching what you have chosen.
          Positioned(
            left: 0,
            right: 0,
            bottom: 12,
            child: Center(
              child: NocturneButton(
                label: 'Play the ${clipSpokenLabel(clip.span)}',
                icon: PhosphorIconsFill.play,
                variant: NocturneButtonVariant.primary,
                height: 40,
                fontSize: 14,
                horizontalPadding: 18,
                borderRadius: 20,
                onPressed: onPlayClip,
              ),
            ),
          ),
        ] else ...[
          Positioned(
            left: 12,
            top: 12,
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
          Positioned(
            right: 12,
            bottom: 12,
            child: _RoundOverlayButton(
              icon: PhosphorIconsRegular.cornersOut,
              tooltip: 'Fill the screen',
              onPressed: onExpand,
            ),
          ),
          if (castState != CastState.unavailable)
            Positioned(
              right: 12,
              top: 12,
              child: _CastOverlayButton(state: castState, onCast: onCast),
            ),
        ],
      ],
    ),
  );
}

/// The video layer and the boxes over it, without any of the chrome.
///
/// Split out of [_VideoStage] because the phone draws the same picture three sizes and two
/// chromes — a band, and the whole screen — and only the chrome differs. A second copy of the
/// live/replay/placeholder choice would be a second place for a camera to come up blank.
class _StageVideo extends StatelessWidget {
  const _StageVideo({
    required this.repository,
    required this.camera,
    required this.replay,
    required this.liveVideoSize,
    required this.pictureZoom,
    required this.allDetections,
    required this.talking,
    required this.theirAudio,
    required this.micStage,
  });

  final ServalRepository repository;
  final Camera camera;
  final ReplayController replay;
  final ValueNotifier<Size?> liveVideoSize;

  /// Where the pinch has got to, written here and read by the pill on the chrome above.
  final ValueNotifier<double> pictureZoom;

  /// Whether to draw every stored episode rather than only the alerting ones.
  ///
  /// The flag rather than the boxes. Both readings — live, and at the playhead — are taken below,
  /// where the playhead is known and where a subscription to the feed costs only this overlay a
  /// rebuild. Handing the live list down instead meant reading it in whatever built this, which is
  /// how a screen came to be rebuilt twice a second per camera.
  final bool allDetections;

  /// Required rather than defaulted, both of them. A picture that silently defaulted to *not
  /// talking* is how the phone's portrait layout came to draw a live *Hold to talk* whose presses
  /// reached nothing at all.
  final bool talking;
  final bool theirAudio;

  /// See [_VideoStage.micStage]. Written from in here, by the session below.
  final ValueNotifier<MicStage> micStage;

  @override
  Widget build(BuildContext context) => PictureZoom(
    // Keyed on the camera, so switching to another one starts at the whole scene rather than
    // wherever the last camera was being examined. The three chromes are separate subtrees, so a
    // move between them starts over too — which is right, since a translation measured in a
    // 412x232 band means nothing in a 892x412 one.
    key: ValueKey(camera.id),
    scale: pictureZoom,
    child: Stack(
      fit: StackFit.expand,
      children: [
        if (replay.replaying)
          ReplayStageView(
            replay: replay,
            repository: repository,
            camera: camera,
          )
        else if (repository.canStreamLive)
          // Rebuilt by the level and nothing else, which is what keeps a drag off the rest of the
          // screen. The position splits here rather than upstream because this is the only place the
          // two halves are needed apart: replay takes them through the controller instead.
          ValueListenableBuilder<double>(
            valueListenable: repository.playbackVolumeFor(camera.id),
            builder: (context, travel, _) {
              final level = playbackFromTravel(travel);
              return WebRtcView(
                repository: repository,
                camera: camera,
                talking: talking,
                theirAudio: theirAudio,
                volume: level.volume,
                gainDb: level.db,
                gateRms: camera.playbackGateRms,
                videoSize: liveVideoSize,
                micStage: micStage,
              );
            },
          )
        else
          LivePlaceholderView(camera: camera),
        ValueListenableBuilder<DateTime?>(
          valueListenable: replay.playhead,
          builder: (context, playhead, _) => Consumer(
            // Both readings of the boxes are made here, and this is the one place on the screen
            // that should be rebuilt by a detection arriving. They are the 2Hz thing — the whole
            // reason the feed has a slice of its own — and an overlay that redraws at the rate the
            // boxes move is the overlay working, not the screen thrashing.
            builder: (context, ref, _) {
              ref.watch(activityRevisionProvider);

              final boxes = playhead == null
                  ? repository.detectionsFor(
                      camera.id,
                      includeAllDetections: allDetections,
                    )
                  : repository.detectionsAt(
                      camera.id,
                      playhead,
                      includeAllDetections: allDetections,
                    );

              if (boxes.isEmpty) return const SizedBox.shrink();

              return PictureAligned(
                videoSize: replay.player?.videoSize ?? liveVideoSize,
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    for (final detection in boxes)
                      DetectionOverlay(
                        label: detection.caption,
                        rect: detection.rect,
                        isAlert: detection.isAlert,
                        isStale: detection.isStale,
                      ),
                  ],
                ),
              );
            },
          ),
        ),
      ],
    ),
  );
}

/// A glyph on a tinted round target, for the controls that sit on the picture.
///
/// The overlay's own button: [NocturneButton] draws on a panel and would disappear into video.
///
/// One shape for all of them, including the pair that enter and leave the full-screen mode — a
/// control that changes shape between the two halves of its own toggle reads as two controls.
class _RoundOverlayButton extends StatelessWidget {
  const _RoundOverlayButton({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
    this.active = false,
  });

  final PhosphorIconData icon;
  final String tooltip;
  final VoidCallback? onPressed;
  final bool active;

  @override
  Widget build(BuildContext context) => Tooltip(
    message: tooltip,
    child: Semantics(
      button: true,
      label: tooltip,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          onTap: onPressed,
          behavior: HitTestBehavior.opaque,
          child: Container(
            width: 40,
            height: 40,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: active
                  ? Nocturne.mix(Nocturne.accent, 30)
                  : const Color(0xB8161826),
              borderRadius: BorderRadius.circular(20),
              border: Border.all(
                color: active
                    ? Nocturne.mix(Nocturne.accent, 65)
                    : Nocturne.mix(Nocturne.text, 14),
              ),
            ),
            child: PhosphorIcon(
              icon,
              size: 17,
              color: active ? Nocturne.accent300 : Nocturne.text,
            ),
          ),
        ),
      ),
    ),
  );
}

/// *Cast*, in the corner of the picture, on every layout that has no room for a bar.
///
/// The same control in the same place whether the picture is a band or the whole screen, because
/// on a phone it is the same gesture — and the corner of the video is where a cast icon is looked
/// for. Filled while a session is running, which is the platform's own convention for connected and
/// the one [_ActionRow] already uses for a live toggle.
///
/// Absent, never disabled: [CastState.unavailable] means no receiver was found, or a browser with
/// no Cast support at all, and on most machines nothing is wrong — there is simply no television.
class _CastOverlayButton extends StatelessWidget {
  const _CastOverlayButton({required this.state, required this.onCast});

  final CastState state;
  final VoidCallback? onCast;

  @override
  Widget build(BuildContext context) {
    final casting = state == CastState.casting;

    return _RoundOverlayButton(
      icon: casting
          ? PhosphorIconsFill.screencast
          : PhosphorIconsRegular.screencast,
      tooltip: casting ? 'Stop casting' : 'Cast to a television',
      active: casting,
      onPressed: onCast,
    );
  }
}

/// The four things the desktop floats over the video, as a row beneath it.
///
/// *Audio* is a toggle and reads as one — lit and underlined while their sound is coming through.
/// The other three are actions. *Move* is the way into landscape rather than a pad crushed onto a
/// 412px strip: that is where the design puts pan and tilt, at full size with the whole picture
/// underneath to aim by.
class _ActionRow extends StatelessWidget {
  const _ActionRow({
    required this.audioOn,
    required this.onAudio,
    required this.canMove,
    required this.onMove,
    required this.snapshotJob,
    required this.onSnapshot,
    required this.clipJob,
    required this.onSaveClip,
  });

  final bool audioOn;
  final VoidCallback onAudio;

  /// False on a camera that reported no pan, tilt or zoom. The item stays, dimmed: a control that
  /// vanishes per camera is harder to learn than one that is plainly not available here.
  final bool canMove;
  final VoidCallback onMove;

  final _SaveJob? snapshotJob;
  final VoidCallback onSnapshot;
  final _SaveJob? clipJob;
  final VoidCallback onSaveClip;

  /// The design's row height. Fixed rather than intrinsic because the row sits in a `Column`, so
  /// the height coming in is unbounded and `stretch` — which is what lets the lit item's tint and
  /// rule reach the full height of the row — has to have something to stretch to.
  static const _height = 60.0;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: SizedBox(
      height: _height,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _ActionItem(
            icon: audioOn
                ? PhosphorIconsFill.speakerSimpleHigh
                : PhosphorIconsRegular.speakerSimpleSlash,
            label: 'Audio',
            active: audioOn,
            onTap: onAudio,
          ),
          _ActionItem(
            icon: PhosphorIconsRegular.arrowsOutCardinal,
            label: 'Move',
            onTap: canMove ? onMove : null,
          ),
          _ActionItem(
            icon: PhosphorIconsRegular.camera,
            label: snapshotJob is _SaveWorking ? 'Saving…' : 'Snapshot',
            onTap: snapshotJob is _SaveWorking ? null : onSnapshot,
          ),
          _ActionItem(
            icon: PhosphorIconsRegular.scissors,
            label: clipJob is _SaveWorking ? 'Exporting…' : 'Clip',
            onTap: clipJob is _SaveWorking ? null : onSaveClip,
          ),
        ],
      ),
    ),
  );
}

class _ActionItem extends StatelessWidget {
  const _ActionItem({
    required this.icon,
    required this.label,
    required this.onTap,
    this.active = false,
  });

  final PhosphorIconData icon;
  final String label;
  final VoidCallback? onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    final enabled = onTap != null;

    return Expanded(
      child: Semantics(
        button: true,
        enabled: enabled,
        label: label,
        child: MouseRegion(
          cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
          child: GestureDetector(
            onTap: onTap,
            behavior: HitTestBehavior.opaque,
            child: Container(
              constraints: const BoxConstraints(minHeight: 60),
              decoration: BoxDecoration(
                color: active ? Nocturne.mix(Nocturne.accent, 10) : null,
                border: active
                    ? const Border(
                        bottom: BorderSide(color: Nocturne.accent, width: 2),
                      )
                    : null,
              ),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  PhosphorIcon(
                    icon,
                    size: 18,
                    color: active
                        ? Nocturne.accent300
                        : Nocturne.mix(Nocturne.text, enabled ? 62 : 25),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    label,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11,
                      color: active
                          ? Nocturne.text
                          : Nocturne.mix(Nocturne.text, enabled ? 55 : 25),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// What the wide bar keeps beside the title and a phone has no room for.
///
/// Only the settings gear today: *Snapshot* and *Save clip* are in the action row where a thumb
/// can reach them, and the overflow is for what you visit rather than what you press.
class _CameraOverflow extends StatelessWidget {
  const _CameraOverflow({
    required this.onSettings,
    required this.snapshotJob,
    required this.clipJob,
  });

  final VoidCallback? onSettings;
  final _SaveJob? snapshotJob;
  final _SaveJob? clipJob;

  @override
  Widget build(BuildContext context) => PopupMenuButton<int>(
    color: Serval.overlay,
    position: PopupMenuPosition.under,
    tooltip: 'More',
    onSelected: (_) => onSettings?.call(),
    itemBuilder: (context) => [
      PopupMenuItem<int>(
        value: 0,
        enabled: onSettings != null,
        child: Row(
          children: [
            PhosphorIcon(
              PhosphorIconsRegular.gearSix,
              size: 18,
              color: Nocturne.mix(Nocturne.text, onSettings == null ? 35 : 85),
            ),
            const SizedBox(width: 13),
            Text(
              'Camera settings',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 14.5,
                color: Nocturne.mix(
                  Nocturne.text,
                  onSettings == null ? 35 : 85,
                ),
              ),
            ),
          ],
        ),
      ),
    ],
    child: const CompactBarAction(
      icon: PhosphorIconsRegular.dotsThreeVertical,
      tooltip: 'More',
    ),
  );
}

/// *Hold to talk*, pinned across the bottom of a phone that can talk back.
///
/// The one place a thumb reaches without shifting grip, and the thing this screen is opened to do
/// on a phone — so it is the lowest control on it. Built only where talking is possible: the
/// caller drops the whole band on a camera with no speaker and while replaying, so the one
/// disabled state left is the fixable one, and it is drawn because "Talk-back needs HTTPS" is
/// worth a reader's time in a way a dead pill on a camera that has no speaker never was.
///
/// 44px rather than the design's 56: it is still the tallest target on the screen, and the 18px
/// that buys goes to the feed, which is the thing this layout is short of.
class _TalkBar extends StatelessWidget {
  const _TalkBar({
    required this.camera,
    required this.onTalkChanged,
    required this.micStage,
  });

  /// How tall the band is, for [_CameraScreenState._belowPicture].
  static const height = 62.0;

  final Camera camera;
  final ValueChanged<bool> onTalkChanged;

  /// See [_VideoStage.micStage]. Further from its writer here than anywhere else — the picture is
  /// a band at the top of the screen and this is pinned to the bottom of it.
  final ValueNotifier<MicStage> micStage;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(16, 8, 16, 10),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(top: BorderSide(color: Serval.hairline)),
    ),
    child: SizedBox(
      height: 44,
      child: HoldToTalkButton(
        enabled: isSecureContext,
        disabledReason: isSecureContext ? null : 'Talk-back needs HTTPS',
        mic: micStage,
        height: 44,
        expand: true,
        onTalkStart: () => onTalkChanged(true),
        onTalkEnd: () => onTalkChanged(false),
      ),
    ),
  );
}

/// Everything that floats over the picture once it has the whole screen.
///
/// Three bands up from the bottom edge, in reach order: the caption, then the way back into the
/// recording, then the controls — with *Hold to talk* lowest, where the thumb already is.
class _ImmersiveControls extends StatelessWidget {
  const _ImmersiveControls({
    required this.camera,
    required this.replaying,
    required this.caption,
    required this.subtitles,
    required this.allDetections,
    required this.theirAudio,
    required this.timeline,
    required this.range,
    required this.replay,
    required this.onRangeChanged,
    required this.onTheirAudioChanged,
    required this.onToggleSubtitles,
    required this.onToggleAllDetections,
    required this.onTalkChanged,
    required this.micStage,
    required this.onSnapshot,
    required this.snapshotJob,
  });

  final Camera camera;
  final bool replaying;
  final String? caption;
  final bool subtitles;
  final bool allDetections;
  final VoidCallback onToggleAllDetections;
  final bool theirAudio;
  final TimelineWindow timeline;
  final TimelineRange range;
  final ReplayController replay;
  final ValueChanged<TimelineRange> onRangeChanged;
  final ValueChanged<bool> onTheirAudioChanged;
  final VoidCallback onToggleSubtitles;
  final ValueChanged<bool> onTalkChanged;

  /// See [_VideoStage.micStage].
  final ValueNotifier<MicStage> micStage;

  final VoidCallback onSnapshot;
  final _SaveJob? snapshotJob;

  @override
  Widget build(BuildContext context) => Stack(
    children: [
      // The scrim is paint and nothing else. A decoration hit-tests its whole rectangle, and this
      // one runs 280px up a phone held sideways — most of the picture, deaf to a pinch, for a
      // gradient whose entire job is to keep white glyphs legible over a bright scene.
      const Positioned.fill(
        child: IgnorePointer(
          child: DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(
                begin: Alignment.bottomCenter,
                end: Alignment.topCenter,
                colors: [Color(0xD9000000), Color(0x00000000)],
              ),
            ),
          ),
        ),
      ),
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 52, 16, 14),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (caption != null) ...[
              _Caption(text: caption!),
              const SizedBox(height: 11),
            ],
            // The timeline comes along, and it is the one thing here that had to: without it landscape
            // is live-only, and there is no way back into the recording short of turning the phone
            // upright. The dense form drops the sentence and keeps the controls.
            TimelineScrubber(
              window: timeline,
              range: range,
              dense: true,
              records: camera.records,
              onRangeChanged: onRangeChanged,
              live: !replaying,
              playhead: replay.playhead,
              onSeek: (at) => replay.seekTo(at, timeline),
              onScrub: (at) => replay.scrubTo(at, timeline),
              onBackToLive: replay.backToLive,
              transport: !replaying
                  ? null
                  : ReplayTransport(
                      playing: replay.playing,
                      rate: replay.rate,
                      rates: ReplayController.rates,
                      onPlayPause: () =>
                          replay.playing ? replay.pause() : replay.play(),
                      onRateChanged: (rate) => unawaited(replay.setRate(rate)),
                    ),
            ),
            const SizedBox(height: 11),
            // Bounded, because this whole bar hangs off the bottom edge with no top — so the height
            // coming in is unbounded and a row of 44px controls has to say so itself.
            SizedBox(
              height: 44,
              child: Row(
                children: [
                  HoldToTalkButton(
                    enabled:
                        camera.twoWayAudio && !replaying && isSecureContext,
                    disabledReason:
                        camera.twoWayAudio && !replaying && !isSecureContext
                        ? 'Talk-back needs HTTPS'
                        : null,
                    mic: micStage,
                    height: 44,
                    onTalkStart: () => onTalkChanged(true),
                    onTalkEnd: () => onTalkChanged(false),
                  ),
                  const SizedBox(width: 9),
                  // Their sound as a plain toggle rather than the desktop's slider, which is what the
                  // design draws here: on a phone the level is the hardware's job — the buttons on the
                  // side of it are already a volume control — and the slider costs this row 160px it
                  // does not have.
                  _RoundOverlayButton(
                    icon: theirAudio
                        ? PhosphorIconsRegular.speakerSimpleHigh
                        : PhosphorIconsRegular.speakerSimpleSlash,
                    tooltip: theirAudio ? 'Mute' : 'Unmute',
                    active: theirAudio,
                    onPressed: () => onTheirAudioChanged(!theirAudio),
                  ),
                  const SizedBox(width: 9),
                  _RoundOverlayButton(
                    icon: PhosphorIconsRegular.chatCenteredText,
                    tooltip: 'Subtitles',
                    active: subtitles,
                    onPressed: onToggleSubtitles,
                  ),
                  const SizedBox(width: 9),
                  _RoundOverlayButton(
                    icon: PhosphorIconsRegular.boundingBox,
                    tooltip: 'All detections',
                    active: allDetections,
                    onPressed: onToggleAllDetections,
                  ),
                  const SizedBox(width: 9),
                  _RoundOverlayButton(
                    icon: PhosphorIconsRegular.camera,
                    tooltip: snapshotJob is _SaveWorking
                        ? 'Saving…'
                        : 'Snapshot',
                    onPressed: snapshotJob is _SaveWorking ? null : onSnapshot,
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
