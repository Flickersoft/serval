import 'dart:async';
import 'dart:js_interop';

import 'package:flutter/foundation.dart';
import 'package:flutter/scheduler.dart';
import 'package:web/web.dart' as web;

import 'frame_watchdog_decision.dart';

/// How long one probe waits for a frame before judging the wait.
///
/// Long enough that a phone still finishing a resume has painted something — the first frame back
/// carries every camera's snapshot and the whole activity column — and short enough that nobody is
/// left holding a dead App while it runs.
const _deadline = Duration(seconds: 3);

/// Probes spent on one suspicion before it is left until the next heartbeat.
const _attempts = 3;

/// How often a visible App is asked whether it is still painting.
///
/// The events below are where a wedge is *likely*, not where it is possible, and an App that is
/// wedged stays wedged whether or not anything caught the moment it happened. This is what makes
/// the recovery independent of catching the right edge: a cold load, a wedge that appears a moment
/// after a resume, and one that appears mid-session are all found within a period. It costs one
/// otherwise-idle frame per period, on an App that is already animating whenever somebody is
/// looking at it.
const _heartbeat = Duration(seconds: 20);

/// Where a reload leaves word for the next launch. See [_recordRecovery].
const _breadcrumbKey = 'serval.watchdog.recovery';

/// Whether a probe is already running.
///
/// The triggers below overlap freely — a phone answering a call, a notification shade pulled down
/// and let go, a heartbeat landing on a resume — and each of those would otherwise start a second
/// probe racing the first to reload the page.
bool _probing = false;

/// Where to reload to, held for [probeFrames] to reach. Null until [watchFrames] has run.
String Function()? _route;

void watchFrames(String Function() route) {
  _route = route;
  _reportRecovery();

  // DOM events rather than `AppLifecycleListener`, and that is the point: this asks whether
  // Flutter's own machinery is still running, so it cannot be scheduled by that machinery. The
  // lifecycle listener is delivered through the engine and the framework binding, which is the half
  // under suspicion.
  web.document.addEventListener(
    'visibilitychange',
    (web.Event _) {
      if (web.document.visibilityState == 'visible') unawaited(_probe());
    }.toJS,
  );

  // A page restored from the back/forward cache had its whole frame loop suspended and resumed, and
  // it arrives without a `visibilitychange` to say so.
  web.window.addEventListener('pageshow', ((web.Event _) => unawaited(_probe())).toJS);

  // The Page Lifecycle API's own thaw — what Chrome fires when it releases a PWA it had frozen,
  // which is the state this file exists for.
  web.document.addEventListener('resume', ((web.Event _) => unawaited(_probe())).toJS);

  Timer.periodic(_heartbeat, (_) {
    if (web.document.visibilityState == 'visible') unawaited(_probe());
  });
}

/// Asks the question now, on behalf of somebody who just did something and expects an answer.
///
/// A tapped notification is the case: it is routed through the service worker's message, which
/// arrives whether or not the App can paint, so a tap that lands on a wedged App looks exactly like
/// a tap that did nothing. Waiting out a heartbeat to find that out is most of a minute spent
/// staring at a screen that has already failed.
void probeFrames() {
  if (_route == null) return;
  unawaited(_probe());
}

/// Asks the framework for a frame and watches whether one arrives.
Future<void> _probe() async {
  final route = _route;
  if (route == null || _probing) return;
  _probing = true;

  try {
    for (var attempt = _attempts; attempt > 0; attempt--) {
      var painted = false;

      // A post-frame callback rather than anything that inspects the scheduler's own flags: it is
      // set at the end of `handleDrawFrame`, so it is evidence a frame was actually produced rather
      // than evidence one was asked for — and asking is precisely what is believed to have already
      // happened. `ensureVisualUpdate` is what makes an idle App produce one; a frame with nothing
      // dirty still runs the callback.
      final started = DateTime.now();
      SchedulerBinding.instance
        ..addPostFrameCallback((_) => painted = true)
        ..ensureVisualUpdate();

      // A timer, and nothing here waits on an animation frame. Timers are delivered to a page whose
      // frame pipeline is latched — that is the whole shape of this fault, everything but the
      // painting carrying on — so this is the one clock that still runs in the state being measured.
      await Future<void>.delayed(_deadline);

      final waited = DateTime.now().difference(started);
      final verdict = judgeFrame(
        painted: painted,
        waited: waited,
        deadline: _deadline,
        visible: web.document.visibilityState == 'visible',
        attemptsLeft: attempt - 1,
      );

      // Only what is not routine. A healthy verdict is every probe on a working App — one a
      // heartbeat, forever — and a log line per heartbeat would bury the two that mean something.
      if (verdict != FrameVerdict.healthy) {
        debugPrint(
          'FrameWatchdog: $verdict — painted=$painted '
          'waited=${waited.inMilliseconds}ms attemptsLeft=${attempt - 1}',
        );
      }

      switch (verdict) {
        case FrameVerdict.healthy:
          return;
        case FrameVerdict.retry:
          continue;
        case FrameVerdict.reload:
          final target = route();
          _recordRecovery(target, waited);
          _reloadTo(target);
          return;
      }
    }
  } finally {
    _probing = false;
  }
}

/// Leaves word that this App reloaded itself.
///
/// A recovery is invisible by design: somebody who was not holding the phone at the time sees an App
/// that works. Without a record, an App that recovered twice overnight and an App that never wedged
/// are the same observation from the outside, and telling those apart is the whole question once a
/// recovery exists at all. Read back by [_reportRecovery] on the launch the reload produces.
void _recordRecovery(String route, Duration waited) {
  try {
    web.window.localStorage.setItem(
      _breadcrumbKey,
      '${DateTime.now().toUtc().toIso8601String()} route=$route '
      'waited=${waited.inMilliseconds}ms',
    );
  } catch (_) {
    // Storage refused. A recovery nobody can read about is still a recovery, and failing the launch
    // that came out of one would be a poor trade for a diagnostic.
  }
}

void _reportRecovery() {
  try {
    final note = web.window.localStorage.getItem(_breadcrumbKey);
    if (note == null) return;

    // Cleared before it is reported, so one recovery is announced once however this launch goes on
    // to end.
    web.window.localStorage.removeItem(_breadcrumbKey);
    debugPrint('FrameWatchdog: recovered from a latched frame pipeline — $note');
  } catch (_) {
    // As above.
  }
}

/// Loads the App again at [target].
///
/// `replace` rather than `assign` where the address has to change: the entry being left is a broken
/// copy of this same App, and leaving it on the history stack would put the back button one press
/// away from returning to it.
void _reloadTo(String target) {
  final here = '${web.window.location.pathname}${web.window.location.search}';
  if (target == here) {
    web.window.location.reload();
    return;
  }

  web.window.location.replace(target);
}
