// What the App makes of the Google Home status, and what the section draws from it.
//
// The section is the only place an operator finds out *why* the integration is not working, so
// the tests here are mostly about it saying the right thing in each of the three states rather
// than about layout — the goldens hold the layout.
//
// Nothing here writes configuration, because nothing can: every Serval:GoogleHome:* key is
// environment-only. See Docs/google-home.md.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/google_home.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/google_home_section.dart';

void main() {
  group('status', () {
    test('reads a live integration', () {
      final status = GoogleHomeStatus.fromJson({
        'effective': true,
        'blocker': 'None',
        'reason': null,
        'publicBaseUrl': 'https://serval.example.com',
        'homeGraphKeyConfigured': true,
      });

      expect(status.effective, isTrue);
      expect(status.reason, isNull);
      expect(status.publicBaseUrl, 'https://serval.example.com');
      expect(status.homeGraphKeyConfigured, isTrue);
    });

    // A Server older or newer than this build, or one that answered oddly. Absent means off,
    // which is the safe reading: claiming the integration is live when the payload did not say so
    // would send somebody looking for a networking fault that does not exist.
    test('an empty payload reads as switched off', () {
      final status = GoogleHomeStatus.fromJson(const {});

      expect(status.effective, isFalse);
      expect(status.homeGraphKeyConfigured, isFalse);
      expect(status.reason, isNull);
    });

    /// **The bug this file exists for.** `blocker` first shipped as an unattributed C# enum, which
    /// System.Text.Json writes as its ordinal — so the App received `"blocker": 1`, the cast to
    /// String threw, the exception escaped into an unawaited future, and the whole card silently
    /// vanished from the Server status page. It looked exactly like the feature had never been
    /// built.
    ///
    /// The Server now sends a name and a test pins that. This is the second line of defence: no
    /// payload shape may take the card off the screen.
    test('an integer blocker does not throw', () {
      final status = GoogleHomeStatus.fromJson(const {
        'effective': false,
        'blocker': 1,
        'reason': 'Serval:GoogleHome:Enabled is false.',
        'publicBaseUrl': null,
        'homeGraphKeyConfigured': false,
      });

      expect(status.effective, isFalse);
      expect(status.blocker, '1');
      expect(status.reason, 'Serval:GoogleHome:Enabled is false.');
    });

    /// Nothing in the payload may be load-bearing enough to throw on. A Server one version ahead
    /// or behind must degrade, not blank the page.
    test('wrong types anywhere do not throw', () {
      final status = GoogleHomeStatus.fromJson(const {
        'effective': 'yes',
        'blocker': ['odd'],
        'reason': 42,
        'publicBaseUrl': 7,
        'homeGraphKeyConfigured': 1,
      });

      // A non-bool is not true — the safe reading, since claiming the integration is live when the
      // payload did not say so sends somebody hunting a networking fault that does not exist.
      expect(status.effective, isFalse);
      expect(status.homeGraphKeyConfigured, isFalse);
      expect(status.reason, '42');
    });

    /// A deployment that never turned this on gets no card at all — the state almost every
    /// deployment is permanently in, and one where there is nothing to diagnose.
    test('a switched-off deployment is recognised', () {
      expect(
        GoogleHomeStatus.fromJson(const {
          'effective': false,
          'blocker': 'disabled',
        }).switchedOff,
        isTrue,
      );

      // Case-insensitive: the Server sends camelCase, but nothing should hinge on that holding.
      expect(
        GoogleHomeStatus.fromJson(const {'blocker': 'Disabled'}).switchedOff,
        isTrue,
      );

      // Every other blocker means somebody is part-way through setting it up, which is exactly
      // when the card has something worth saying.
      for (final blocker in const [
        'none',
        'webRtcDisabled',
        'publicBaseUrlInvalid',
        'projectIdMissing',
        'clientIdMissing',
        'clientSecretMissing',
      ]) {
        expect(
          GoogleHomeStatus.fromJson({'blocker': blocker}).switchedOff,
          isFalse,
          reason: blocker,
        );
      }
    });

    test('a link tolerates wrong types too', () {
      final link = GoogleHomeLink.fromJson(const {
        'agentUserId': 12345,
        'linkedAt': 0,
        'lastFulfillmentAt': null,
        'lastSyncAt': null,
      });

      expect(link.agentUserId, '12345');
      expect(link.linkedAt, isNull);
    });

    test('a link keeps the times it was given, and null where it has none', () {
      final link = GoogleHomeLink.fromJson({
        'agentUserId': '0f8fad5b',
        'linkedAt': '2026-08-03T09:40:00Z',
        'lastFulfillmentAt': null,
        'lastSyncAt': null,
      });

      expect(link.agentUserId, '0f8fad5b');
      expect(link.linkedAt, isNotNull);
      // Never called since linking — the state the card exists to distinguish from a working
      // link, and one a default-to-now would erase.
      expect(link.lastFulfillmentAt, isNull);
      expect(link.lastSyncAt, isNull);
    });
  });

  group('section', () {
    Future<void> pump(
      WidgetTester tester, {
      required GoogleHomeStatus? status,
      List<GoogleHomeLink> links = const [],
      void Function(GoogleHomeLink)? onUnlink,
      String? error,
    }) => tester.pumpWidget(
      MaterialApp(
        theme: buildServalTheme(),
        home: Scaffold(
          body: SingleChildScrollView(
            child: GoogleHomeSection(
              status: status,
              links: links,
              onUnlink: onUnlink,
              error: error,
            ),
          ),
        ),
      ),
    );

    const off = GoogleHomeStatus(
      effective: false,
      blocker: 'ClientIdMissing',
      reason:
          'Serval:GoogleHome:ClientId is not set, so account linking rejects every request.',
      publicBaseUrl: null,
      homeGraphKeyConfigured: false,
      castReceiverConfigured: false,
    );

    const live = GoogleHomeStatus(
      effective: true,
      blocker: 'None',
      reason: null,
      publicBaseUrl: 'https://serval.example.com',
      homeGraphKeyConfigured: true,
      castReceiverConfigured: true,
    );

    /// The whole point of the card: the Server's own sentence, rendered as given. Mapping the
    /// blocker to local text here would put the App a release behind the Server on the first
    /// condition anyone adds.
    testWidgets('shows the Server’s reason verbatim', (tester) async {
      await pump(tester, status: off);

      expect(find.textContaining('Serval:GoogleHome:ClientId'), findsOneWidget);
      expect(find.text('Not active'), findsOneWidget);
    });

    // Not a fault, so it must not read as one — the overwhelming majority of deployments are here
    // and nothing is wrong with them.
    testWidgets('a closed integration shows no address and no link prompt', (
      tester,
    ) async {
      await pump(tester, status: off);

      expect(find.textContaining('Public address'), findsNothing);
      expect(find.textContaining('No Google account has linked'), findsNothing);
    });

    /// Three states, not two. "On but nobody has linked" is a real step in the middle of the
    /// runbook, and collapsing it into "on" leaves somebody waiting for cameras that will never
    /// arrive.
    testWidgets('live with nobody linked says so', (tester) async {
      await pump(tester, status: live);

      expect(find.text('Ready — no account linked'), findsOneWidget);
      expect(
        find.textContaining('No Google account has linked'),
        findsOneWidget,
      );
      expect(find.text('https://serval.example.com'), findsOneWidget);
    });

    testWidgets('a linked account reports when Google last called', (
      tester,
    ) async {
      await pump(
        tester,
        status: live,
        links: [
          GoogleHomeLink(
            agentUserId: 'agent-1',
            linkedAt: DateTime(2026, 8, 3),
            lastFulfillmentAt: null,
            lastSyncAt: null,
          ),
        ],
      );

      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Google has not called since linking'), findsOneWidget);
    });

    /// A Viewer reads this page and does not act on it, so the action is absent rather than
    /// present-and-inert — there is no 403 to explain if the button was never offered.
    testWidgets('no unlink handler means no unlink button', (tester) async {
      await pump(
        tester,
        status: live,
        links: [
          GoogleHomeLink(
            agentUserId: 'agent-1',
            linkedAt: DateTime(2026, 8, 3),
            lastFulfillmentAt: DateTime(2026, 8, 8),
            lastSyncAt: null,
          ),
        ],
      );

      expect(find.text('Unlink'), findsNothing);
    });

    testWidgets('unlinking reports the account it was asked about', (
      tester,
    ) async {
      GoogleHomeLink? unlinked;

      await pump(
        tester,
        status: live,
        links: [
          GoogleHomeLink(
            agentUserId: 'agent-1',
            linkedAt: DateTime(2026, 8, 3),
            lastFulfillmentAt: DateTime(2026, 8, 8),
            lastSyncAt: null,
          ),
        ],
        onUnlink: (link) => unlinked = link,
      );

      await tester.tap(find.text('Unlink'));
      await tester.pump();

      expect(unlinked?.agentUserId, 'agent-1');
    });

    testWidgets('an error is shown alongside the state, not instead of it', (
      tester,
    ) async {
      await pump(
        tester,
        status: live,
        error: 'The server refused the request.',
      );

      expect(find.text('The server refused the request.'), findsOneWidget);
      expect(find.text('Ready — no account linked'), findsOneWidget);
    });

    /// With no status at all the card still draws, and says the status is unavailable rather than
    /// disappearing or claiming the integration is off. Those are different situations: one is a
    /// deployment that has not turned this on, the other is a question that failed — and reading
    /// the second as the first sends somebody to check configuration that is fine.
    testWidgets('a failed read still draws the card', (tester) async {
      await pump(
        tester,
        status: null,
        error: 'Could not read the Google Home status.',
      );

      expect(find.text('Google Home'), findsOneWidget);
      expect(find.text('Status unavailable'), findsOneWidget);
      expect(
        find.text('Could not read the Google Home status.'),
        findsOneWidget,
      );
      expect(find.text('Not active'), findsNothing);
    });

    /// Without a HomeGraph key the integration works and the device list goes stale — a real
    /// difference an operator has to be told about, since the symptom appears days later as a
    /// renamed camera Google never heard about.
    testWidgets('says when camera changes will not reach Google', (
      tester,
    ) async {
      await pump(
        tester,
        status: const GoogleHomeStatus(
          effective: true,
          blocker: 'None',
          reason: null,
          publicBaseUrl: 'https://serval.example.com',
          homeGraphKeyConfigured: false,
          castReceiverConfigured: false,
        ),
      );

      expect(find.textContaining('Not pushed'), findsOneWidget);
    });

    /// The receiver row says what the operator gets, in both directions.
    ///
    /// Unset is a working deployment, so the row cannot read as a fault — but it does have to say
    /// there is no Cast button, because nothing else does and its absence otherwise looks like one.
    /// Google will not put a camera on a television by voice whatever is configured here, so the
    /// button is the only route to one.
    testWidgets('says whether a television can be cast to at all', (
      tester,
    ) async {
      await pump(tester, status: live);
      expect(find.textContaining('Cast from a camera screen'), findsOneWidget);

      await pump(
        tester,
        status: const GoogleHomeStatus(
          effective: true,
          blocker: 'None',
          reason: null,
          publicBaseUrl: 'https://serval.example.com',
          homeGraphKeyConfigured: true,
          castReceiverConfigured: false,
        ),
      );
      expect(
        find.textContaining('No Cast receiver registered'),
        findsOneWidget,
      );
    });
  });
}
