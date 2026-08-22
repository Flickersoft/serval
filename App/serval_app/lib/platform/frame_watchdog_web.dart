import 'dart:async';
import 'dart:js_interop';

import 'package:flutter/scheduler.dart';
import 'package:web/web.dart' as web;

/// Browser animation frames the probe will sit through before deciding nothing is being painted.
///
/// Deliberately a count of frames rather than a stopwatch, because the two failures it has to tell
/// apart share a thread. A main thread merely busy — a burst of snapshots decoded on resume, a
/// WebRTC session renegotiating — delays *these* callbacks exactly as much as it delays Flutter's,
/// so waiting a fixed wall-clock second would report a slow phone as a broken one. A page that is
/// genuinely animating and has painted none of thirty frames is not busy.
const _frames = 30;

/// How long one animation frame is waited for before the probe gives up on the browser.
///
/// A page can stop animating for reasons that are nobody's fault — it went back to the background
/// mid-probe, or it never really came forward. That is not the fault this recovers from, and
/// reloading somebody's App on the strength of it would be worse than the fault. Abandoning also
/// releases [_probing], so the next time the page is shown it is asked again.
const _stall = Duration(seconds: 5);

/// Whether a probe is already running.
///
/// `visibilitychange` can fire repeatedly while one is in flight — a phone answering a call, a
/// notification shade pulled down and let go — and each of those would otherwise start a second
/// probe racing the first to reload the page.
bool _probing = false;

void watchFrames(String Function() route) {
  // The DOM event rather than `AppLifecycleListener`, and that is the point: this asks whether
  // Flutter's own machinery is still running, so it cannot be scheduled by that machinery. The
  // lifecycle listener is delivered through the engine and the framework binding, which is the
  // half under suspicion.
  web.document.addEventListener(
    'visibilitychange',
    (web.Event _) {
      if (web.document.visibilityState == 'visible') {
        unawaited(_probe(route));
      }
    }.toJS,
  );
}

/// Asks the framework for a frame and watches whether one arrives.
Future<void> _probe(String Function() route) async {
  if (_probing) return;
  _probing = true;

  try {
    var painted = false;

    // A post-frame callback rather than anything that inspects the scheduler's own flags: it is
    // set at the end of `handleDrawFrame`, so it is evidence a frame was actually produced rather
    // than evidence one was asked for — and asking is precisely what is believed to have already
    // happened. `ensureVisualUpdate` is what makes an idle App produce one; a frame with nothing
    // dirty still runs the callback.
    SchedulerBinding.instance
      ..addPostFrameCallback((_) => painted = true)
      ..ensureVisualUpdate();

    for (var frame = 0; frame < _frames; frame++) {
      if (!await _animationFrame()) return;
      if (painted) return;
    }

    // Thirty frames delivered to this file and none to Flutter. The pipeline is latched shut and
    // there is no way to open it from here.
    _reloadTo(route());
  } finally {
    _probing = false;
  }
}

/// Loads the App again at [target].
///
/// `replace` rather than `assign` where the address has to change: the entry being left is a
/// broken copy of this same App, and leaving it on the history stack would put the back button
/// one press away from returning to it.
void _reloadTo(String target) {
  final here = '${web.window.location.pathname}${web.window.location.search}';
  if (target == here) {
    web.window.location.reload();
    return;
  }

  web.window.location.replace(target);
}

/// One animation frame. False if the browser stopped delivering them instead.
///
/// Ordering matters and is in our favour: rAF callbacks run in the order they were registered, and
/// anything Flutter scheduled — including the frame [_probe] just asked for — was registered before
/// this one. So on a healthy page the frame is fully painted, post-frame callback and all, before
/// this future completes.
Future<bool> _animationFrame() {
  final frame = Completer<bool>();

  web.window.requestAnimationFrame(
    (JSNumber _) {
      if (!frame.isCompleted) frame.complete(true);
    }.toJS,
  );

  Timer(_stall, () {
    if (!frame.isCompleted) frame.complete(false);
  });

  return frame.future;
}
