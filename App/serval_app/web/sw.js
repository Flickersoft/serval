'use strict';

// Serval's service worker. Its whole job is to exist while the tab does not: a push arrives when
// nothing of the App is running, so showing the notification and deciding where a tap goes has to
// happen here rather than in Dart.
//
// Registered by lib/push/push_client_web.dart, not by index.html, so it appears at the moment
// somebody turns notifications on rather than on every page load. Flutter's own worker is switched
// off at build time (--pwa-strategy=none in the Dockerfile) because there is one registration slot
// at scope "/" and this is what belongs in it.

// Payloads are composed by AlertNotifier.Compose on the server and are deliberately small: a title,
// a time, and URLs for everything else.
self.addEventListener('push', (event) => {
  if (!event.data) {
    return;
  }

  let payload;
  try {
    payload = event.data.json();
  } catch (e) {
    // Anything that is not our JSON is not ours to show. Silently ignoring it is better than a
    // notification reading "undefined" on somebody's lock screen.
    return;
  }

  const options = {
    body: payload.body || '',
    icon: 'icons/Icon-192.png',
    badge: 'icons/Icon-192.png',
    // One notification per camera: a camera alerting three times while a phone is in a pocket
    // should replace its own notification rather than stack three. The server sets the same value
    // as the push Topic so the collapsing also happens upstream, before delivery.
    tag: payload.camera_id || 'serval',
    // A quiet push updates the card without interrupting anybody — both flags, because renotify
    // governs only whether a *replacement* alerts, and silent governs whether anything does.
    // An absent flag is a loud one, which is what a server that does not set it means and what
    // every push before this one meant. iOS ignores renotify, so a quiet push there replaces the
    // card silently either way.
    renotify: !payload.quiet,
    silent: payload.quiet === true,
    timestamp: payload.at ? Date.parse(payload.at) : Date.now(),
    data: { url: payload.url || '/alerts' },
  };

  // An empty image is the test notification, which has no camera behind it. Passing '' would ask
  // the browser to fetch the page itself as a picture.
  if (payload.image) {
    options.image = payload.image;
  }

  event.waitUntil(self.registration.showNotification(payload.title || 'Serval', options));
});

// Where a tapped notification's destination is left for the App to collect.
//
// The message below is the fast path, and it is enough only for a window already running the App.
// A tap that *wakes* the App posts into a page whose listener does not exist yet — main.dart.js and
// the first frame are still ahead of it — and a message dispatched to no listener is gone, which is
// what left somebody looking at whatever screen the browser restored.
//
// The Cache API rather than anything nicer: a worker cannot see localStorage, and this worker is
// stopped seconds after the click, so it cannot hold the destination in memory either. The key is
// never fetched — the handler below passes every request through — so it is only ever a name.
const PENDING_CACHE = 'serval-nav';
const PENDING_KEY = '/__serval/pending-nav';

async function recordPending(url, id) {
  const cache = await caches.open(PENDING_CACHE);
  await cache.put(new Request(PENDING_KEY), new Response(JSON.stringify({ url: url, id: id })));
}

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const url = (event.notification.data && event.notification.data.url) || '/alerts';

  // Names this tap for the whole handoff. The App may hear about it by either route below, or by
  // both, and acts on an id once — see `_accept` in push_client_web.dart. It is `Date.now()` so
  // that it also says how old the destination is, which is what lets a tap nobody ever came back
  // for expire rather than open an alert days later.
  const id = Date.now();

  event.waitUntil((async () => {
    // First, because everything after it can fail or be skipped.
    try {
      await recordPending(url, id);
    } catch (e) {
      // Storage refused. That leaves the message below as the only route, which is where this
      // started — not a reason to drop the tap.
    }

    let clients = [];
    try {
      clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    } catch (e) {
      clients = [];
    }

    // Posted before focus(), and to every window rather than only the one about to be focused:
    // focus() rejects in more states than it is documented to, and an unhandled rejection here
    // would take the rest of this handler — the openWindow fallback included — down with it.
    for (const client of clients) {
      if ('postMessage' in client) {
        client.postMessage({ type: 'serval:navigate', url: url, id: id });
      }
    }

    // Prefer a tab that is already open. Opening a second one would leave somebody with two copies
    // of a live-video app competing for the same streams, and the running tab can route itself
    // there far faster than a cold start can.
    for (const client of clients) {
      if (!('focus' in client)) {
        continue;
      }

      try {
        await client.focus();
        return;
      } catch (e) {
        // Try the next window, and open a new one if none of them can be brought forward.
      }
    }

    if (self.clients.openWindow) {
      try {
        await self.clients.openWindow(url);
      } catch (e) {
        // Nothing further to try. The destination is recorded, so whatever does come to the front
        // still lands on the alert.
      }
    }
  })());
});

// Take over as soon as we are installed rather than waiting for every tab to close. A worker that
// only starts working after the next full reload would make "turn notifications on" appear to fail.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

// Present so the page counts as installable — iOS Safari only delivers push to a site added to the
// Home Screen, and that gate checks for a fetch handler. Deliberately a passthrough: caching is
// Flutter's old worker's job and it was switched off on purpose, since every screen here needs the
// Server anyway.
self.addEventListener('fetch', () => {});
