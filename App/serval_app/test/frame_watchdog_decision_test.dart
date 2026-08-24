import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/platform/frame_watchdog_decision.dart';

/// The judgement the frame watchdog makes, tested without a browser.
///
/// The watchdog itself cannot be reached from here — the conditional import in
/// `frame_watchdog.dart` hands `flutter test` the stub — and this is the half where being wrong
/// costs something: a verdict of [FrameVerdict.reload] throws away whatever somebody was doing.
void main() {
  const deadline = Duration(seconds: 3);

  FrameVerdict judge({
    bool painted = false,
    Duration waited = deadline,
    bool visible = true,
    int attemptsLeft = 2,
  }) => judgeFrame(
    painted: painted,
    waited: waited,
    deadline: deadline,
    visible: visible,
    attemptsLeft: attemptsLeft,
  );

  test('a frame arriving is the whole answer', () {
    expect(judge(painted: true), FrameVerdict.healthy);

    // Even from a thread that took its time about it, and even from a page that has since gone
    // away: a frame was produced, so the pipeline is not latched.
    expect(
      judge(painted: true, waited: const Duration(minutes: 1), visible: false),
      FrameVerdict.healthy,
    );
  });

  test('a wait that ended on time with nothing painted is the wedge', () {
    expect(judge(), FrameVerdict.reload);
  });

  test('a page that went away mid-probe is left alone', () {
    // It never owed anybody a frame. Reloading somebody's App on this would be worse than the
    // fault the reload exists to recover from.
    expect(judge(visible: false), FrameVerdict.healthy);
  });

  test('a jammed thread is asked again rather than reloaded', () {
    expect(judge(waited: const Duration(seconds: 30)), FrameVerdict.retry);
  });

  test('a thread jammed to the last probe is left alone, not reloaded', () {
    expect(
      judge(waited: const Duration(seconds: 30), attemptsLeft: 0),
      FrameVerdict.healthy,
    );
  });

  test('overrunning the deadline a little is still the wedge', () {
    // The margin exists to tell a busy thread from an idle one, not to excuse every late timer.
    // Just over the deadline is a thread that was free within a frame or two of when it was asked.
    expect(judge(waited: const Duration(seconds: 4)), FrameVerdict.reload);
  });
}
