import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/dashboard_socket.dart';
import 'package:serval_app/data/events_socket.dart';
import 'package:serval_app/data/serval_config.dart';

/// `reconnectNow()` on both sockets — what the App calls when it comes back from the background.
///
/// The behaviour under test is *not waiting*. Both sockets retry on capped exponential backoff, so
/// a phone away for ten minutes is sitting on the thirty-second cap when somebody looks at it
/// again; without this the wall would hold its last frames, and read "connecting", for most of a
/// minute after the App was in front of them.
///
/// Two seams make this testable without a Server. `mintTicket` is a function, and a mint that
/// answers null makes the socket give up before it opens a channel — so the mint count is an exact
/// proxy for "it tried". Where a real connection is the point, a `dart:io` WebSocket server stands
/// in for the Server, which is the only way to reach the state where a gap exists to close.
///
/// Deliberately not tested here: the backoff's own timings. Pinning those needs `fake_async`, and
/// real-time waits at the one-second minimum are flake for no information.
void main() {
  // Never actually reached: every test on this config uses a mint that answers null, which is a
  // failed attempt and returns before a channel is opened.
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));

  /// Lets whatever the socket scheduled onto the microtask queue actually run.
  Future<void> settle() => Future<void>.delayed(Duration.zero);

  group('DashboardSocket', () {
    test('reconnecting now does not wait out the backoff', () async {
      var mints = 0;
      final socket = DashboardSocket(
        config: config,
        mintTicket: () async {
          mints++;
          return null;
        },
      );
      addTearDown(socket.close);

      // A null ticket is a failed attempt, so this schedules a retry a second out and returns.
      await socket.connect();
      expect(mints, 1);

      socket.reconnectNow();
      await settle();

      // The retry it was sitting on is a second away, and a second is the *minimum* — this is the
      // whole method.
      expect(mints, 2);
    });

    test('a mint already in flight cannot open a channel behind a reconnect', () async {
      // The guard `reconnectNow`'s safety rests on. `connect()` awaits the mint, so a slow one can
      // resolve after a teardown has already moved on; `_teardown` bumps the attempt counter so
      // that mint's ticket is dropped rather than used to open a second channel.
      final slow = Completer<String?>();
      var mints = 0;

      final socket = DashboardSocket(
        config: config,
        mintTicket: () {
          mints++;
          return mints == 1 ? slow.future : Future<String?>.value(null);
        },
      );
      addTearDown(socket.close);

      unawaited(socket.connect());
      await settle();
      expect(mints, 1);

      socket.reconnectNow();
      await settle();
      expect(mints, 2);

      // The superseded mint comes good. Nothing may come of it.
      slow.complete('a-real-looking-ticket');
      await settle();

      // How that is observed: `connect()` returns early when a channel is already open, so a third
      // mint proves nothing was left holding one.
      await socket.connect();
      expect(mints, 3);
    });

    test('reconnecting after close does nothing', () async {
      var mints = 0;
      final socket = DashboardSocket(
        config: config,
        mintTicket: () async {
          mints++;
          return null;
        },
      );

      await socket.close();
      socket.reconnectNow();
      await settle();

      expect(mints, 0);
    });
  });

  group('EventsSocket', () {
    test('reconnecting now does not wait out the backoff', () async {
      var mints = 0;
      final socket = EventsSocket(
        config: config,
        mintTicket: () async {
          mints++;
          return null;
        },
      );
      addTearDown(socket.close);

      await socket.connect();
      expect(mints, 1);

      socket.reconnectNow();
      await settle();

      expect(mints, 2);
    });

    test('reconnecting after close does nothing', () async {
      var mints = 0;
      final socket = EventsSocket(
        config: config,
        mintTicket: () async {
          mints++;
          return null;
        },
      );

      await socket.close();
      socket.reconnectNow();
      await settle();

      expect(mints, 0);
    });

    test('a reconnect after real traffic leaves a gap to close', () async {
      // The one line `EventsSocket.reconnectNow` has that the dashboard's does not, and the only
      // one whose absence is invisible: `_gapToClose` is set by `_scheduleReconnect`, not by
      // `_teardown`, so a reconnect that skips the backoff would come back silently and leave a
      // ten-minute hole in the activity column with nothing on screen to say so.
      //
      // Needs a real socket, because a gap only exists once traffic has flowed.
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(server.close);

      // One document per connection, so the client has something to mark each one as live with.
      // Its contents are beside the point — the socket counts itself connected on any message.
      const document = '''
      {
        "type": "scene",
        "schema_version": 5,
        "id": "96d97635-ead0-4c6d-bcf3-d11877f00431",
        "camera_id": "1",
        "received_at": "2026-08-01T01:02:07.972+00:00",
        "timestamp": "2026-08-01T01:01:39.493+00:00",
        "description": "A nighttime view of a residential driveway.",
        "trigger": "motion",
        "motion_score": 0.0345,
        "frame_count": 2,
        "frame_span_seconds": 1,
        "source": "server"
      }''';

      final envelope = jsonEncode({
        'camera_id': '1',
        'type': 'scene',
        'document': jsonDecode(document),
      });

      unawaited(
        server.forEach((request) async {
          final socket = await WebSocketTransformer.upgrade(request);
          socket.add(envelope);
        }),
      );

      final socket = EventsSocket(
        config: ServalConfig(
          baseUrl: Uri.parse('http://127.0.0.1:${server.port}'),
        ),
        mintTicket: () async => 'ticket',
      );
      addTearDown(socket.close);

      final reconnects = <void>[];
      socket.reconnected.listen(reconnects.add);

      final firstDocument = socket.documents.first;
      await socket.connect();
      await firstDocument;

      // Nothing to close on the first connect: the history fetch has just run, and firing here
      // would have the repository re-read a window it already has.
      expect(reconnects, isEmpty);

      final secondDocument = socket.documents.first;
      socket.reconnectNow();
      await secondDocument;
      await settle();

      expect(reconnects, hasLength(1));
    });
  });
}
