import 'package:flutter/widgets.dart';

import '../data/serval_repository.dart';
import '../models/camera.dart';
import '../playback/replay_controller.dart';
import 'webrtc_view.dart';

/// The stage while you are replaying: the VOD player's surface, or the same fallback the live
/// view uses when it cannot open.
///
/// Thin on purpose. The player owns its surface across window reopens — a fresh widget per reopen
/// would tear the video layer down and flash the stage every fifteen minutes — so this only picks
/// between the surface and the reason it is not there.
class ReplayStageView extends StatelessWidget {
  const ReplayStageView({
    super.key,
    required this.replay,
    required this.repository,
    required this.camera,
  });

  final ReplayController replay;
  final ServalRepository repository;
  final Camera camera;

  @override
  Widget build(BuildContext context) => ValueListenableBuilder<String?>(
    valueListenable: replay.failure,
    builder: (context, failure, _) {
      final player = replay.player;

      // A break in the recording is not a failure and must not read as one. Nothing was recorded
      // here, replay is crossing it at real time, and the plate says which of the two it is —
      // "unavailable" over an outage would send somebody looking for a fault in the player.
      if (failure == null && replay.inGap) {
        return StageFallback(
          repository: repository,
          camera: camera,
          message: 'No recording',
          detail: 'The camera was not recording at this time.',
        );
      }

      if (failure != null || player == null) {
        return StageFallback(
          repository: repository,
          camera: camera,
          message: 'Replay unavailable',
          detail: failure,
        );
      }

      // The player's own failures are separate from the controller's: the controller knows it
      // could not *find* something to play, while this is the decoder saying it could not play
      // what it was given. Without this, an mpv error would leave a surface that never paints
      // and nothing on screen to say why.
      return ValueListenableBuilder<String?>(
        valueListenable: player.failure,
        builder: (context, playerFailure, _) => playerFailure == null
            ? player.buildView()
            : StageFallback(
                repository: repository,
                camera: camera,
                message: 'Replay unavailable',
                detail: playerFailure,
              ),
      );
    },
  );
}
