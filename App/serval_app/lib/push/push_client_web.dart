import 'dart:async';
import 'dart:convert';
import 'dart:js_interop';
// For `has`, which is the only way to ask whether a browser implements something before touching
// it — reading an absent member through the typed bindings throws rather than answering null.
import 'dart:js_interop_unsafe';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'push_client.dart';

/// Where the VAPID key this browser subscribed with is remembered.
///
/// `localStorage` rather than anything grander: it is not a secret — it is the public half of a key
/// pair the server hands to anyone who asks — and it has to survive exactly as long as the
/// browser's own subscription does, which is what `localStorage` is scoped to.
const _keyStorageKey = 'serval.push.vapidKey';

/// The service worker's URL, relative so it inherits the page's base href and lands at scope "/".
///
/// Scope is what matters here rather than the filename: a worker registered from a subdirectory
/// would only control that subdirectory, and a push has no page to be relative to.
const _serviceWorkerUrl = 'sw.js';

bool get isSupported {
  // Both halves are needed and they are gated separately — Safari had one without the other for
  // years. Both are also withheld outside a secure context, which is why this is the single check
  // the screen makes rather than asking about HTTPS itself.
  if (!web.window.isSecureContext) {
    return false;
  }

  final navigator = web.window.navigator;
  return navigator.has('serviceWorker') && web.window.has('PushManager');
}

/// The message `sw.js` posts when a notification is tapped and this tab is already open.
const _navigateMessage = 'serval:navigate';

/// Where `sw.js` leaves a tapped notification's destination. See the comment above it there for why
/// the message alone is not enough and why this is a cache rather than anything more obvious.
const _pendingCache = 'serval-nav';
const _pendingKey = '/__serval/pending-nav';

/// The most recent tap this browser has already acted on.
///
/// In `localStorage` because it has to outlive the page: the two routes a destination arrives by
/// belong to different loads of the App, and this is what stops the second one reopening what the
/// first already opened.
const _lastNavIdKey = 'serval.push.lastNavId';

/// How old a recorded destination may be and still be worth opening.
///
/// Belt and braces to the read-and-delete in [_takePending]: if a delete ever fails, this is what
/// keeps a tap from an abandoned session out of the next launch.
const _pendingMaxAge = Duration(minutes: 2);

void onNavigate(void Function(String route) handler) {
  if (!isSupported) {
    return;
  }

  final container = web.window.navigator.serviceWorker;

  container.addEventListener(
    'message',
    (web.MessageEvent event) {
      if (event.data.dartify() case {
        'type': _navigateMessage,
        'url': final String url,
        'id': final num id,
      }) {
        _accept(id.toInt(), url, handler);
      }
    }.toJS,
  );

  // `addEventListener` does not start the container's message queue — only assigning `onmessage`
  // does — so anything the worker posted before this point would sit in it unread. Costs nothing
  // when the queue is already running, which it is once the document has finished loading.
  container.startMessages();

  // The listener above only ever hears a tap that arrived while this page was running. A tap that
  // *woke* the App posts into a page that has not built its first frame, let alone this listener,
  // so the destination is also collected from where `sw.js` recorded it: once now, for that case,
  // and again on coming back to the front, for a tap answered while the App was in the background.
  unawaited(_takePending(handler));

  web.document.addEventListener(
    'visibilitychange',
    (web.Event _) {
      if (web.document.visibilityState == 'visible') {
        unawaited(_takePending(handler));
      }
    }.toJS,
  );
}

/// Reads the destination `sw.js` recorded, clearing it on the way past.
///
/// The delete happens before the handler runs rather than after, so a navigation that throws — or a
/// page closed halfway through one — cannot leave an entry behind for the next launch to open.
Future<void> _takePending(void Function(String route) handler) async {
  try {
    final cache = await web.window.caches.open(_pendingCache).toDart;
    final recorded = await cache.match(_pendingKey.toJS).toDart;
    if (recorded == null) {
      return;
    }

    await cache.delete(_pendingKey.toJS).toDart;

    final body = (await recorded.text().toDart).toDart;
    if (jsonDecode(body) case {'url': final String url, 'id': final num id}) {
      final tappedAt = DateTime.fromMillisecondsSinceEpoch(id.toInt());
      if (DateTime.now().difference(tappedAt) > _pendingMaxAge) {
        return;
      }

      _accept(id.toInt(), url, handler);
    }
  } catch (_) {
    // A browser that refuses the Cache API leaves the message as the only route, which is where
    // this started. Nothing here is worth failing a launch over.
  }
}

/// Acts on one tap, once.
///
/// Both routes in carry the same id, either may arrive first, and both may arrive. Remembering the
/// highest id acted on is what makes the second delivery of a tap do nothing while a genuinely
/// newer tap still opens — which is the whole reason a tapped alert cannot reopen itself in a loop.
void _accept(int id, String url, void Function(String route) handler) {
  final storage = web.window.localStorage;
  if (id <= (int.tryParse(storage.getItem(_lastNavIdKey) ?? '') ?? 0)) {
    return;
  }

  storage.setItem(_lastNavIdKey, '$id');

  // Already there — which happens when both routes deliver the same tap, and when the tap names the
  // screen that is open. Recorded as answered above; navigating again would only push a second copy
  // of this screen onto the history stack for the back button to walk through.
  final here = '${web.window.location.pathname}${web.window.location.search}';
  if (here == url) {
    return;
  }

  handler(url);
}

PushPermission get permission => switch (web.Notification.permission) {
  'granted' => PushPermission.granted,
  'denied' => PushPermission.denied,
  _ => PushPermission.prompt,
};

String? get subscribedKey => web.window.localStorage.getItem(_keyStorageKey);

Future<PushSubscriptionInfo?> current() async {
  if (!isSupported) {
    return null;
  }

  final registration = await _existingRegistration();
  if (registration == null) {
    return null;
  }

  final subscription = await registration.pushManager.getSubscription().toDart;
  return subscription == null ? null : _describe(subscription);
}

Future<PushSubscriptionInfo?> subscribe(String vapidPublicKey) async {
  if (!isSupported) {
    return null;
  }

  // Asked before the worker is registered, so a refusal costs nothing. requestPermission resolves
  // immediately with the existing answer when it has been asked before, so this is also the
  // no-prompt path for somebody who already said yes.
  final granted = (await web.Notification.requestPermission().toDart).toDart;
  if (granted != 'granted') {
    return null;
  }

  final registration = await web.window.navigator.serviceWorker
      .register(_serviceWorkerUrl.toJS)
      .toDart;

  // register() resolves as soon as the registration exists, which can be before the worker is
  // active. Subscribing against one that is still installing fails, so wait for the container to
  // report itself ready rather than for our own promise.
  await web.window.navigator.serviceWorker.ready.toDart;

  // An existing subscription made against a different key is unusable — every message sent to it is
  // rejected, silently — so it is discarded rather than reused. This is the path that repairs a
  // deployment whose key pair has changed.
  final existing = await registration.pushManager.getSubscription().toDart;
  if (existing != null && subscribedKey != vapidPublicKey) {
    await existing.unsubscribe().toDart;
  }

  final subscription = await registration.pushManager
      .subscribe(
        web.PushSubscriptionOptionsInit(
          // Required to be true by every browser that implements this. A subscription that may
          // push without showing anything is a tracking channel, and asking for one is refused.
          userVisibleOnly: true,
          applicationServerKey: _decodeBase64Url(vapidPublicKey).toJS,
        ),
      )
      .toDart;

  web.window.localStorage.setItem(_keyStorageKey, vapidPublicKey);
  return _describe(subscription);
}

Future<void> unsubscribe() async {
  if (!isSupported) {
    return;
  }

  web.window.localStorage.removeItem(_keyStorageKey);

  final registration = await _existingRegistration();
  final subscription = await registration?.pushManager.getSubscription().toDart;
  await subscription?.unsubscribe().toDart;
}

/// The registration if there already is one, without creating one.
///
/// `getRegistration` rather than `ready`, which never completes when nothing has been registered —
/// awaiting it on a browser that has never subscribed would hang the screen that is only asking
/// whether it should draw an "on" or an "off" state.
Future<web.ServiceWorkerRegistration?> _existingRegistration() async =>
    await web.window.navigator.serviceWorker
        .getRegistration(_serviceWorkerUrl)
        .toDart;

PushSubscriptionInfo _describe(web.PushSubscription subscription) =>
    PushSubscriptionInfo(
      endpoint: subscription.endpoint,
      p256dh: _encodeBase64Url(subscription.getKey('p256dh')),
      auth: _encodeBase64Url(subscription.getKey('auth')),
    );

/// base64url without padding, which is how every Web Push field is spelled and what the Server's
/// `PushEndpoints.Validate` checks the length of after decoding.
String _encodeBase64Url(JSArrayBuffer? buffer) {
  if (buffer == null) {
    return '';
  }

  final bytes = buffer.toDart.asUint8List();
  final base64 = web.window.btoa(String.fromCharCodes(bytes));

  return base64.replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

/// The reverse, for handing the VAPID public key to `subscribe`, which wants raw bytes.
Uint8List _decodeBase64Url(String value) {
  final padded = value
      .replaceAll('-', '+')
      .replaceAll('_', '/')
      .padRight(value.length + ((4 - value.length % 4) % 4), '=');

  final decoded = web.window.atob(padded);
  return Uint8List.fromList([
    for (var i = 0; i < decoded.length; i++) decoded.codeUnitAt(i),
  ]);
}
