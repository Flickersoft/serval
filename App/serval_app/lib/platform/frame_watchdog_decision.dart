/// What one liveness probe concluded about the frame pipeline.
enum FrameVerdict {
  /// A frame was produced, or the App is not in a state this can judge. Nothing to do.
  healthy,

  /// The main thread was too busy for the answer to mean anything. Ask again.
  retry,

  /// The pipeline is latched shut, and only a reload clears it.
  reload,
}

/// How much later than its deadline a wait may end before the thread counts as jammed rather than
/// merely idle.
///
/// Generous on purpose. The cost of calling a jammed thread wedged is reloading an App that was
/// about to paint; the cost of calling a wedged thread jammed is one more probe, a heartbeat later.
/// Those are not the same price.
const _jammed = 2;

/// Reads one probe's result.
///
/// Lives apart from the browser plumbing so it can be tested: the conditional import in
/// `frame_watchdog.dart` hands `flutter test` the stub, so nothing reachable from a widget test may
/// touch `dart:js_interop`.
///
/// [waited] is the wall clock that actually passed while waiting [deadline] for a frame, and the gap
/// between the two is why this is not a single comparison. The timer ending the wait runs on the
/// same thread as the frame it waits for, so it can only fire when the event loop is free — which
/// means a wait that ended *on time* carries a second fact beyond "nothing painted": the thread was
/// free to paint and did not. A wait that ended late says only that the thread was busy, which is
/// what a resume decoding every camera's snapshot at once looks like, and a busy App is not a
/// broken one.
///
/// Deliberately not a count of animation frames. A latched pipeline is one whose
/// `requestAnimationFrame` callback never arrives, so a deadline counted in those is a deadline
/// that cannot end in the very state it exists to name.
FrameVerdict judgeFrame({
  required bool painted,
  required Duration waited,
  required Duration deadline,
  required bool visible,
  required int attemptsLeft,
}) {
  if (painted) return FrameVerdict.healthy;

  // A page that went back to the background mid-probe never owed anybody a frame. That is not the
  // fault this recovers from, and reloading somebody's App on the strength of it would be worse
  // than the fault. The next time the page is shown it is asked again.
  if (!visible) return FrameVerdict.healthy;

  if (waited > deadline * _jammed) {
    // Out of probes on a thread that has been busy throughout. Still no evidence of a latch, so
    // this leaves it alone rather than guessing; the heartbeat asks again.
    return attemptsLeft > 0 ? FrameVerdict.retry : FrameVerdict.healthy;
  }

  return FrameVerdict.reload;
}
