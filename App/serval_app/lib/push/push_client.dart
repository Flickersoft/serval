// The browser's push machinery, behind the same conditional-import shape `secure_context.dart` and
// `vod_player.dart` use. `dart.library.io` is true under the VM — including `flutter test` — so the
// stub is what the test binary compiles, and the widget tests never touch a service worker.
import 'package:flutter/foundation.dart';

import 'push_client_stub.dart'
    if (dart.library.js_interop) 'push_client_web.dart'
    if (dart.library.io) 'push_client_stub.dart'
    as platform;

/// What a browser will currently do if asked to notify.
enum PushPermission {
  /// Never asked. Asking is allowed, and shows the browser's own prompt.
  prompt,

  /// Allowed. Subscribing will work.
  granted,

  /// Refused. **Nothing in a page can undo this** — the browser deliberately gives no way to
  /// re-prompt, so the only path back is the site settings in the browser's own UI. The screen has
  /// to say so rather than offering a button that cannot work.
  denied,
}

/// One browser's push subscription, in the shape `POST /api/push/subscriptions` wants.
class PushSubscriptionInfo {
  const PushSubscriptionInfo({
    required this.endpoint,
    required this.p256dh,
    required this.auth,
  });

  /// Where the browser's push service wants messages delivered. Vendor-specific and long.
  final String endpoint;

  /// The browser's public key, base64url. The server encrypts to it.
  final String p256dh;

  /// This subscription's auth secret, base64url.
  final String auth;
}

/// Talking to the browser about notifications.
///
/// Every method is a no-op returning the unsupported answer off the web, which keeps the screen
/// from having to ask what platform it is on — it asks [isSupported] and draws accordingly.
///
/// **All of this needs a secure context.** Service workers and `PushManager` are both withheld on
/// a plain-HTTP origin, and unlike the failures listed on `secure_context.dart` this one is total:
/// there is no degraded mode. [isSupported] answers false there, and the screen explains why.
abstract final class PushClient {
  /// The browser's three answers, stood in for. Null — the default — asks the platform.
  ///
  /// `flutter test` runs on the VM, where the stub answers *no push at all*. That is the honest
  /// answer for a native build and a useless one for a capture meant to be held beside a design of
  /// the working state, and it leaves four of the notifications screen's five states unreachable.
  /// Set it in `setUp`, clear it in `tearDown`.
  @visibleForTesting
  static ({
    bool supported,
    PushPermission permission,
    PushSubscriptionInfo? subscription,
  })?
  debugBrowser;

  /// Whether this browser has a service worker and a push manager at all.
  static bool get isSupported =>
      debugBrowser?.supported ?? platform.isSupported;

  /// What the browser will do if asked. Reads the current state without prompting.
  static PushPermission get permission =>
      debugBrowser?.permission ?? platform.permission;

  /// The subscription this browser already holds, or null.
  ///
  /// Read on every launch rather than trusted from storage, because a browser may discard or
  /// reissue a subscription whenever it likes and never tells the page it did.
  static Future<PushSubscriptionInfo?> current() async => debugBrowser != null
      ? debugBrowser!.subscription
      : await platform.current();

  /// Registers the service worker, asks permission if it has not been asked, and subscribes.
  ///
  /// Returns null if permission was refused. Throws if the browser has push but something else
  /// went wrong, since that is a real fault worth showing rather than a choice somebody made.
  static Future<PushSubscriptionInfo?> subscribe(String vapidPublicKey) =>
      platform.subscribe(vapidPublicKey);

  /// Drops this browser's subscription. Safe to call when there is none.
  static Future<void> unsubscribe() => platform.unsubscribe();

  /// Calls back with a route whenever a notification is tapped.
  ///
  /// The service worker prefers focusing a running tab over opening a second one — two copies of a
  /// live-video app competing for the same streams is not what somebody tapping a notification
  /// wants — so it hands the destination over rather than navigating. Without a listener the tab
  /// would come to the front still showing whatever it was showing, which reads as the tap having
  /// done nothing.
  ///
  /// Handing it over as a message is only reliable for a tab that is already running the app. A tap
  /// that *wakes* the app posts into a page that has not built a frame yet, and a message dispatched
  /// to no listener is gone — the browser then finishes restoring whatever screen it was last on,
  /// which is the tap appearing to do nothing in the case it matters most. So the worker also
  /// records the destination somewhere that outlives both it and that load, and this collects it at
  /// startup and whenever the app comes back to the front. One tap is acted on once however many of
  /// those routes deliver it.
  static void onNavigate(void Function(String route) handler) =>
      platform.onNavigate(handler);

  /// The VAPID key this browser last subscribed with, or null if it has never subscribed.
  ///
  /// Kept so a server whose key pair has changed can be noticed. A subscription is bound to the key
  /// it was made with, so after such a change every message is rejected — with no error the page
  /// ever sees. Comparing keys at launch is what turns that from permanent silence into one
  /// re-subscribe. See `VapidKeyStore` for why the key can change at all.
  static String? get subscribedKey => platform.subscribedKey;
}
