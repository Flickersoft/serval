# Driving the web build in a browser

None of the suites in [testing.md](testing.md) opens a browser. This is how to put the real web
build in front of one — by hand, or under Playwright — and what a canvaskit app does differently
once you are there.

## Standing up a Server and a bundle

Put a Server and a web bundle up together on a throwaway database — never at the live NVR, which
has one real camera and a registry with no undo. You will be signing in as an Admin, so nothing
about being logged in narrows what a stray click can reach:

```bash
cd App/serval_app && flutter build web --release

cd Server/Serval.Server && \
ASPNETCORE_WEBROOT="$PWD/../../App/serval_app/build/web" \
Serval__Mongo__ConnectionString=mongodb://127.0.0.1:27017 \
Serval__Mongo__Database=serval_browsertest \
Serval__Media__Root=/tmp/serval-browsertest \
Serval__Auth__SigningKey=browsertest-signing-key-0123456789abcdef \
Serval__Auth__BootstrapAdminUsername=admin \
Serval__Auth__BootstrapAdminPassword=browsertest123 \
dotnet run
```

Then **http://127.0.0.1:5211**. Not whatever `ASPNETCORE_URLS` says: `Properties/launchSettings.json`
wins under `dotnet run`, so that variable is silently ignored — pass `--no-launch-profile` to pick
your own port. The Serval options above are read normally.

`ASPNETCORE_WEBROOT` rather than copying the bundle into `wwwroot/`, which is what the
[Dockerfile](../Server/Serval.Server/Dockerfile) does: a `wwwroot/` left behind locally would go on
being served by every later `dotnet run`, and a stale UI that looks fine is worse than none. Serving
it from the Server rather than `flutter run -d chrome` is also what the deployment does, and it puts
the app on the API's own origin — `ServalConfig.fromEnvironment` then reads the page origin, so no
`--dart-define` is needed and CORS never enters it.

There is no self-registration, so `BootstrapAdmin*` is the only way into an empty database (see
[Auth/AdminBootstrap.cs](../Server/Serval.Server/Auth/AdminBootstrap.cs)); it is ignored once any
account exists. The signing key must be 32+ characters and the password 8+, or the server refuses
to start and the account is quietly not created respectively.

The wall stays empty on a fresh database. A camera to fill it is a file-source one — see
[A camera with no camera](testing.md#a-camera-with-no-camera).

### When the thing you are checking is not the Server

A UI behaviour — a panel opening, a field selecting, a control landing where the design puts it —
needs none of the above. `ServalApp`'s default constructor is the sample repository with no auth
gate, which is what the goldens render, so a throwaway entrypoint gets the whole app in a browser
on the design's own content with no Mongo, no Server and no login:

```dart
// lib/zz_sample_entry.dart — delete it afterwards
import 'package:flutter/widgets.dart';
import 'main.dart';
import 'router/url_strategy.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  useServalUrlStrategy();
  runApp(const ServalApp());
}
```

```bash
cd App/serval_app
flutter build web --release -t lib/zz_sample_entry.dart
python3 -m http.server 8199 --bind 127.0.0.1 -d build/web
```

Two things to know. A plain static server has no SPA fallback, so **only `/` loads** — every deep
link 404s and every route has to be reached by clicking, and a stale bundle is best busted by a
reload rather than a `?v=` query, which `go_router` answers with *Page Not Found*. And this build
has no Server behind it, so anything about live video, playback or writing a setting is exactly
what it cannot tell you; for those, stand up the real thing above.

## A blank white page

**It means `main()` threw before `runApp`.** The likeliest cause is not the code but a stale
`.dart_tool/flutter_build/<hash>/web_plugin_registrant.dart` — that file is generated, and a build
directory older than the last dependency change registers only the plugins that existed then.
`flutter_secure_storage` missing from it fails at `auth.restore()` on the very first line of
`main`, as `MissingPluginException(No implementation found for method read on channel
plugins.it_nomads.com/flutter_secure_storage)`, and the screen never gets as far as the login form.
Delete the build directory and rebuild. A clean checkout and the Docker image are not affected.

That error is worth knowing by sight because the release bundle will not tell you: dart2js output
logs it as a bare minified `Error` plus a stack of mangled frames. Read the message by installing
the hook before the bundle loads —

```js
await page.addInitScript(() => {
  window.__caught = [];
  addEventListener('error', e => window.__caught.push(String(e.error)));
  addEventListener('unhandledrejection', e => window.__caught.push(String(e.reason)));
});
```

— and reading `window.__caught` after the load. A debug build (`flutter run -d web-server`) is the
other way, but it is slow to compile and the web-server device gives up little without the Dart
Debug extension.

## Playwright against a canvaskit app

The page is one canvas. There is no DOM to drive, and the usual accessibility-tree tooling does not
apply:

- **Click and type with `page.mouse` / `page.keyboard` at coordinates.** The semantic `<input>`
  elements Flutter emits are `disabled`, so neither a click nor a fill will take on them.
- **Leave the accessibility tree off.** Clicking the offscreen *Enable accessibility* placeholder
  gets you a readable tree, but from that point typing goes nowhere — the semantic inputs take the
  focus and cannot receive it. A reload clears it. Read the screen from screenshots instead.
- **Screenshots come back downscaled**, so screenshot coordinates need scaling up before they are
  mouse coordinates. Measure the ratio against `innerWidth` on the machine you are on rather than
  assuming one — a 1600x1000 viewport captured at 1389x868 (~1.15) on the box this was written on,
  and that number is a property of the display, not of the app.
- **`Enter` does not submit the login form.** Click *Sign in*.
- **`mouse.dblclick()` does not double-tap.** It fires both clicks with no gap, and Flutter's
  `DoubleTapGestureRecognizer` discards a second tap that arrives inside `kDoubleTapMinTime` (40 ms)
  — so the gesture is silently a pair of single taps, and whatever the double tap was meant to do
  simply does not happen. Send `down`/`up` twice with ~90 ms between them instead. Worth knowing
  because the failure looks exactly like a broken handler: nothing on screen moves, and there is no
  error anywhere to say the gesture was never recognised.
- **A platform view can swallow the gesture around it, and only in the browser.** An
  `HtmlElementView` — replay's `<video>`, everywhere `VodPlayer.buildView` lands — defaults to
  `PlatformViewHitTestBehavior.opaque`, which takes the hit test from the widgets wrapping it, so a
  `GestureDetector` around the picture never fires. It reads as a handler that works everywhere
  except over video: the wall's tiles opened their camera live and did nothing while replaying.
  No VM test can see it, because the conditional import in `playback/vod_player.dart` gives
  `flutter test` the media_kit backend and its plain Flutter texture instead — so a browser run is
  the only thing that covers a gesture drawn over a player.
- **A two-finger pinch needs CDP.** `page.touchscreen` is single-touch. Drive
  `Input.dispatchTouchEvent` on a `newCDPSession(page)` with two `touchPoints`, moving them apart
  over several `touchMove`s — one jump from rest does not pass the scale recognizer's slop.
- **Let screenshots keep their default name.** They land in `.playwright-mcp/`, which is gitignored;
  an explicit filename is resolved against the working directory instead and drops a loose file in
  the repo root.

Signing in, creating a user, the settings sidebar and sign-out have all been driven this way.

## Notifications need an Android emulator

A desktop tab cannot answer anything about notifications: what matters is a real notification in a
real system tray, tapped, waking a browser that may or may not still be running the app. The
emulator gives all of that, and `adb` drives it well enough to automate.

The Flutter app has no Android target — this is the **web** build, in the emulator's Chrome, which
is what an installed PWA runs anyway.

```bash
export ANDROID_HOME=$HOME/Android/Sdk
export JAVA_HOME=/opt/android-studio/jbr          # sdkmanager needs a JDK; Android Studio bundles one
export PATH=$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH

sdkmanager "system-images;android-36;google_apis_playstore;x86_64"
avdmanager create avd -n serval-push -k "system-images;android-36;google_apis_playstore;x86_64" -d pixel_7
emulator -avd serval-push -no-window -no-snapshot -no-audio -no-boot-anim -gpu swiftshader_indirect &
```

A Play Store image because Chrome ships in it and because it carries the Play Services that Web Push
needs. `avdmanager` complains `Could not load devices from …/devices.xml` and writes a working AVD
anyway. Then edit `~/.android/avd/serval-push.avd/config.ini`: `hw.gpu.enabled=yes` and
`hw.gpu.mode=swiftshader_indirect` (canvaskit needs WebGL, and software GL is what works headless),
`hw.ramSize=4096`, `hw.keyboard=yes`.

Two traps, both silent:

- **Secure context.** Service workers and `PushManager` are withheld off HTTPS, and `10.0.2.2` — the
  usual way to reach the host — is *not* a trustworthy origin, so nothing registers and the screen
  just says push is unsupported. Tunnel instead, and load **`http://localhost:5211`**, which is
  trustworthy even over HTTP:

  ```bash
  adb reverse tcp:5211 tcp:5211
  ```

- **Android 13+ notification permission.** Chrome itself must hold `POST_NOTIFICATIONS` or no web
  notification is ever posted, and nothing anywhere says so:

  ```bash
  adb shell pm grant com.android.chrome android.permission.POST_NOTIFICATIONS
  ```

Chrome's own first-run gets in the way of the first login: *Use without an account*, then a
*Save password?* bubble that must be dismissed by tapping the page — **not** with `keyevent 4`,
which navigates back instead. Type into the canvaskit login form by tapping the username field and
using `keyevent 61` (Tab) to reach the password; tapping the second field lands under the keyboard.

### Driving it

```bash
adb exec-out screencap -p > shot.png          # then look at it — the app is canvaskit
adb shell input tap X Y                       # coordinates, as with Playwright above
adb shell input keyevent 3                    # HOME: background Chrome
adb shell cmd statusbar expand-notifications  # open the shade
adb shell monkey -p com.android.chrome -c android.intent.category.LAUNCHER 1   # bring it back
adb forward tcp:9222 localabstract:chrome_devtools_remote   # CDP, for the exact answer
```

With CDP forwarded, `http://127.0.0.1:9222/json/list` names every page and service-worker target, and
`Runtime.evaluate` on `location.pathname` says where the app actually is — which beats reading a
screenshot when the question is which route won.

### Making a notification happen

Three ways, and the cheapest is enough for most questions, because the interesting code is in
`notificationclick` rather than in the `push` handler:

1. `navigator.serviceWorker.ready.then(r => r.showNotification('…', {data: {url: '/alerts/x'}}))`
   evaluated in the page. No FCM, no subscription, and it still produces a real Android notification.
   Evaluate it in the *page*, not the worker: the worker is stopped between events and only shows up
   as a target while it is running.
2. CDP `ServiceWorker.deliverPushMessage`, when the `push` handler itself is what is being changed.
3. A real push — turn *This device* on and `POST /api/push/test` with a bearer token. This works: the
   emulator reaches FCM and the server's message comes back down to it.

**What cannot be tested here is the installed PWA.** Chrome offers *Install*, but minting a WebAPK
needs a Google account on the device; without one it fails with `WebAPK service unknown_account` in
`logcat` and nothing is installed. A Chrome tab exercises the same service worker, the same
`clients.matchAll`, and the same message — it just is not a standalone window.

### The state that breaks things

A tab that is merely backgrounded, or even frozen with CDP `Page.setWebLifecycleState`, receives a
`client.postMessage` fine — Chrome queues it and delivers it on resume. The case that loses it is a
tap arriving while the app is **loading**, which is every tap that wakes a PWA the OS had killed:
the worker posts into a page that has not built a frame, and a message dispatched to no listener is
gone. Reproduce it deliberately — reload the page and tap the notification inside the boot window:

```bash
node cdp.js eval "location.reload()"
adb shell input tap 480 828        # the notification, shade already open
```

That is what the pending-navigation record in `web/sw.js` and `_takePending` in
`lib/push/push_client_web.dart` exist for; see the comments there.

## Test the insecure origin too, and know what it costs

Serving Serval over plain HTTP is supported — trying it out should not mean standing up
certificates first — but browsers withhold a handful of APIs from any page that is not a **secure
context**, and every one of them fails *silently*. This is the first list to check whenever
something works on Linux, or on `localhost`, and not on the deployment.

`localhost` and `127.0.0.1` count as secure **even over HTTP**, so the local invocation above will
not reproduce any of this. Reach the same server by its LAN address instead — that is the only way
to test what a deployment actually does. `window.isSecureContext` in the console tells you which
side you are on.

| API | Withheld on an insecure origin | What Serval does about it |
|---|---|---|
| `crypto.subtle` | `flutter_secure_storage` throws `UnsupportedError` on **every** call | `TokenStore` falls back to plain `localStorage`. On web the "secure" tier keeps its wrapping key in the same `localStorage` anyway, so this is not the downgrade it looks like. |
| `ImageDecoder` | Image decoding falls back to CanvasKit's WASM codec, which ignores a byte-offset view | `DashboardSocket.decodeFrame` hands `Image.memory` a standalone copy, never a `sublistView`. Not scheme-specific: the same codec is used by Safari **with** TLS. |
| `getUserMedia` | No microphone, ever | Talk-back cannot work. *Hold to talk* is disabled and says `Talk-back needs HTTPS` rather than sitting there inert. The only capability with no workaround. |

The failure signatures are worth recognising by sight, because none of them raises:

- **Login fails with a network-sounding error on a request that returned 200.** Secure storage
  threw on the way out. `UnsupportedError` is an `Error`, not an `Exception`, so anything catching
  only the latter mislabels it.
- **Wall tiles stay on the placeholder while frames arrive.** Count the binary WebSocket frames
  before blaming the socket — the transport is almost certainly fine and the decode is not.
- **`Playback failed: manifestLoadError`.** Not a secure-context problem at all, despite appearing
  alongside them: it is a 401. See the stream-token note below.

## Playback tokens are not just for the playlist

`hls.js` and libmpv cannot set an `Authorization` header, so the player passes
`?stream_token=` instead — read by `OnMessageReceived` on the `"StreamToken"` scheme in
`Program.cs`. The part that is easy to miss: a player resolves the playlist's relative segment
URIs against the playlist's **own URL**, and RFC 3986 drops that URL's query when it does. So the
token has to be written into every segment and `EXT-X-MAP` URI as well, which `HlsPlaylist.BuildVod`
does. Fetching `vod.m3u8` and checking it parses does **not** prove it can be played; fetch a
segment as the playlist wrote it — `integration/live_server_test.dart` does both.

## Producing the README screenshots

The screenshots in [Docs/media/](media/SOURCES.md) come from a throwaway quickstart stack showing
public-domain footage — regenerable after any UI change, with nothing personal in frame:

1. **Footage**: download the clips listed in [media/SOURCES.md](media/SOURCES.md) and transcode
   each to a camera-like H.264 MP4 (`-c:v libx264 -g 30 -pix_fmt yuv420p`, 640×360–1280×720,
   10–15 fps). Give one camera a main + sub pair so the alert preview ring is exercised.
2. **Stack**: from `deploy/`, run the quickstart compose with the AI env block enabled, the
   models directory populated (both `--profile setup` one-shots), a compose override mounting the
   footage read-only at `/footage`, and `mem_limit` raised for the vision model. A throwaway
   project name (`-p`) keeps its volumes away from anything real.
3. **Cameras**: `POST /api/cameras` one file-source camera per clip
   ([testing.md — A camera with no camera](testing.md#a-camera-with-no-camera)), `aiVision` on.
   Let it run 10+ minutes: alerts need the 120 s novelty window to pass before an arrival counts,
   and the timeline needs some depth to look real.
4. **Capture** under Playwright at a 1600×1000 viewport, driving by coordinates as described
   above. Shot list: the wall (hero), camera replay with the timeline and scene feed, alerts,
   saved clips, server settings, server vitals, and one phone-width frame (viewport 412×900).
   Convert to WebP (~quality 88 — PIL does it, GitHub renders it) before committing to
   `Docs/media/`; the seven shots together should stay well under 1 MB.
