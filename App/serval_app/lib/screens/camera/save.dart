part of '../camera_screen.dart';

class _ClipMode {
  const _ClipMode({
    required this.selection,
    required this.zoom,
    required this.window,
  });

  /// Opens with the window centred on the range, which is the one moment centring is right.
  factory _ClipMode.opening({required ClipSelection selection}) {
    final zoom = TrimZoom.forSpan(selection.span);

    return _ClipMode(
      selection: selection,
      zoom: zoom,
      window: zoom.windowFor(selection.from, selection.to),
    );
  }

  final ClipSelection selection;
  final TrimZoom zoom;

  /// The slice of time the track draws.
  ///
  /// Held as state rather than recomputed from the selection, and that is the whole point. A window
  /// centred on the range moves *both* ends on screen whenever either one is dragged — grab the end
  /// handle, the centre shifts by half of what you moved, and the start handle slides the other way
  /// under a finger that never touched it. So the window stays where it is and only pans when a
  /// handle actually reaches its edge.
  final CoverageSpan window;

  _ClipMode copyWith({
    ClipSelection? selection,
    TrimZoom? zoom,
    CoverageSpan? window,
  }) => _ClipMode(
    selection: selection ?? this.selection,
    zoom: zoom ?? this.zoom,
    window: window ?? this.window,
  );

  /// The window shifted only as far as it must to keep [at] on the track.
  CoverageSpan windowKeeping(DateTime at) => window.duration == zoom.span
      ? zoom.windowKeeping(window, at)
      // A window of the wrong width can only mean the zoom changed without the track following.
      : zoom.windowFor(selection.from, selection.to);

  /// The same trim at a different zoom, with the track resized to match.
  ///
  /// Both halves matter: changing [zoom] alone leaves [window] at its old width and the track does
  /// not move, which is the *Wider* control appearing to do nothing.
  _ClipMode zoomedTo(TrimZoom next) => copyWith(
    zoom: next,
    window: next.windowFor(selection.from, selection.to),
  );
}

/// What a *Snapshot* or *Save clip* press is doing.
sealed class _SaveJob {
  const _SaveJob();
}

/// In flight. [bytes] is what has arrived, and is zero on the web — `package:http`'s browser
/// client buffers the whole body before yielding any of it, so there is nothing to count until
/// there is everything.
class _SaveWorking extends _SaveJob {
  const _SaveWorking(this.bytes);

  final int bytes;
}

class _SaveDone extends _SaveJob {
  const _SaveDone(this.saved, {this.asked});

  final SavedMedia saved;

  /// The window that was requested, so a truncated clip can be reported as a shortfall rather
  /// than as a plain success.
  final Duration? asked;
}

class _SaveFailed extends _SaveJob {
  const _SaveFailed(this.reason);

  final String reason;
}

String _megabytes(int bytes) =>
    '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';

/// The line under the top bar that says what happened.
///
/// A pill and a sentence rather than a `SnackBar`: Material's is a filled surface carrying a
/// ripple, which is the flood Nocturne forbids — and it would cover the video it is reporting on.
class _SaveStatus extends StatelessWidget {
  const _SaveStatus({this.snapshot, this.clip, this.castProblem});

  final _SaveJob? snapshot;
  final _SaveJob? clip;

  /// Why the last cast attempt failed, if one did. Shares this line because it is the same
  /// question — what happened to the thing I just pressed — and a second status line would
  /// compete with this one for the same space.
  final String? castProblem;

  @override
  Widget build(BuildContext context) {
    // A cast failure outranks a finished save: it is the newer news, and the save's own outcome
    // has already been read by the time somebody presses Cast.
    if (castProblem case final problem?) {
      return Text(
        problem,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Serval.alert,
        ),
      );
    }

    final job = clip is _SaveWorking || clip is _SaveFailed
        ? clip
        : (snapshot ?? clip);
    if (job == null || job is _SaveWorking) return const SizedBox.shrink();

    final (text, alert) = switch (job) {
      _SaveFailed(:final reason) => (reason, true),
      _SaveDone(:final saved, :final asked) => (_describe(saved, asked), false),
      _ => ('', false),
    };

    if (text.isEmpty) return const SizedBox.shrink();

    return Text(
      text,
      overflow: TextOverflow.ellipsis,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12.5,
        color: alert ? Serval.alert : Nocturne.mix(Nocturne.text, 60),
      ),
    );
  }

  /// Names the file, and says plainly when the clip is shorter than the window asked for.
  ///
  /// The shortfall is measured from what the Server reported it wrote, not guessed from the
  /// file's size — a clip cut at an ffmpeg restart is a different thing from a camera that was
  /// off, and only the Server can tell them apart.
  static String _describe(SavedMedia saved, Duration? asked) {
    final where = saved.location == null ? '' : ' to ${saved.location}';

    if (saved.truncatedTo case final covered? when asked != null) {
      return 'Saved ${covered.inSeconds} s of the ${asked.inSeconds} s asked for'
          ' — the recording restarted partway through.';
    }

    return 'Saved ${saved.fileName}$where';
  }
}

/// The pan/tilt/zoom controls, drawn from what the camera said it can do.
///
/// Four states, and each draws something different on purpose:
///
///  * probing — nothing. A control that materialises a moment later reads as a glitch, and the
///    probe usually lands before the WebRTC session does.
///  * not configured — nothing, and no message. The settings screen already explains it.
///  * unknown — no controls, and the Server's own words. Hiding it would be indistinguishable
///    from "this camera has no pan/tilt", which is a different fact.
///  * known — exactly the axes reported, and nothing else.
