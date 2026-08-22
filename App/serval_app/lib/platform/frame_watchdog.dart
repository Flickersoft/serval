// A browser that stops drawing the App, behind the same conditional-import shape
// `secure_context.dart` and `push_client.dart` use. `dart.library.io` is true under the VM —
// including `flutter test` — so the stub is what the test binary compiles, and no widget test ever
// reloads a page.
import 'frame_watchdog_stub.dart'
    if (dart.library.js_interop) 'frame_watchdog_web.dart'
    if (dart.library.io) 'frame_watchdog_stub.dart'
    as platform;

/// Watches for the App coming back from the background unable to paint, and recovers it.
///
/// Flutter's frame pipeline on web is latched in two places, and both latches are cleared only
/// from inside the `requestAnimationFrame` callback that a scheduled frame is waiting on —
/// `FrameService._isFrameScheduled` in the engine, `SchedulerBinding._hasScheduledFrame` in the
/// framework. Neither is cleared by anything else, and both are consulted before a new frame is
/// asked for: `scheduleFrame()` returns early while a frame is believed to be outstanding.
///
/// The web engine's own note on the first of the two says it plainly — *"if this value is stuck in
/// `true` state, there will be no way to schedule new frames and the app will freeze"*.
///
/// A phone puts that state within easy reach. This App is animating whenever it is in front of
/// somebody — six live tiles and a feed — so there is almost always a frame in flight at the moment
/// it is sent to the background. `requestAnimationFrame` does not run for a hidden page, and a
/// backgrounded PWA that Android freezes or whose renderer is torn down and restored can come back
/// without ever delivering that callback. The latches then never clear, `scheduleFrame` is a
/// permanent no-op, and nothing else in the framework recovers it.
///
/// What that looks like is *not* a blank screen: the canvas keeps the last frame it painted, so the
/// App sits there showing the screen it was on, ignoring every tap. Everything that is not painting
/// carries on — timers, sockets, and the routing a tapped notification does — which is why a tap
/// that lands here appears to have opened nothing.
///
/// This does not try to unwedge the pipeline, because the engine's latch is not reachable from
/// here. It reloads, which is the same thing the only available workaround does — closing the App
/// and opening it again — minus the person having to know that.
///
/// [route] is where to reload *to*, and it must be the router's own answer rather than the address
/// bar's. `Router` reports a navigation to the browser from a post-frame callback, so an App that
/// cannot paint never writes the new address — a tapped notification routed while wedged leaves
/// `location` still naming the screen it was already on, and reloading that would land back on
/// exactly the screen this is trying to get somebody off. `GoRouter.go` sets the route information
/// provider's value synchronously, which is why that is the thing to ask.
///
/// A no-op off the web, so there is no platform branch at the call site.
void watchFrames(String Function() route) => platform.watchFrames(route);
