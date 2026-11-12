import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/models/assistants.dart';
import 'package:serval_app/screens/connect_assistant_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/assistants_section.dart';

/// The consent screen and the list of links — the two places a person decides what an AI assistant
/// may see.
///
/// What is pinned is what the person is told: who is asking, whether that is checked, where the
/// answer goes, and that a decision is sent exactly once to exactly one place.
class _ConsentRepository extends SampleServalRepository {
  _ConsentRepository({this.request, this.gone = false});

  final AssistantRequest? request;
  final bool gone;

  final answers = <bool>[];

  @override
  Future<AssistantRequest> assistantRequest(String id) async {
    if (gone) throw ServalApiException(404, 'Not found');
    return request ?? await super.assistantRequest(id);
  }

  @override
  Future<String> answerAssistantRequest(
    String id, {
    required bool approve,
  }) async {
    answers.add(approve);
    return 'https://client.example/cb?${approve ? 'code=c' : 'error=access_denied'}&iss=x';
  }
}

void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1200, 1400);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  final left = <Uri>[];

  Future<void> pump(
    WidgetTester tester,
    _ConsentRepository repository, {
    String? requestId = 'req',
  }) async {
    left.clear();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [repositoryProvider.overrideWithValue(repository)],
        child: MaterialApp(
          theme: buildServalTheme(),
          home: Scaffold(
            body: ConnectAssistantScreen(
              requestId: requestId,
              leave: (uri) async => left.add(uri),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('the consent screen', () {
    testWidgets('names a verified client by its proven domain', (tester) async {
      await pump(tester, _ConsentRepository());

      expect(find.textContaining('wants to read this Serval'), findsOneWidget);
      expect(find.text('Verified as'), findsOneWidget);
      expect(find.text('claude.ai'), findsWidgets);
      expect(find.textContaining('registered itself'), findsNothing);
    });

    testWidgets('warns about a client that registered itself', (tester) async {
      await pump(
        tester,
        _ConsentRepository(
          request: const AssistantRequest(
            clientName: 'Claude',
            verifiedHost: null,
            redirectTarget: 'evil.example',
            verified: false,
            expiresAt: null,
          ),
        ),
      );

      expect(find.text('Not verified'), findsOneWidget);
      expect(find.textContaining('registered itself'), findsOneWidget);
      expect(find.text('evil.example'), findsOneWidget);
    });

    testWidgets('allowing answers once and hands the browser over', (
      tester,
    ) async {
      final repository = _ConsentRepository();
      await pump(tester, repository);

      await tester.tap(find.text('Allow'));
      await tester.pumpAndSettle();

      expect(repository.answers, [true]);
      expect(left.single.queryParameters['code'], 'c');
      expect(find.text('Connected'), findsOneWidget);
      expect(find.text('Allow'), findsNothing);
    });

    testWidgets('denying sends the refusal back', (tester) async {
      final repository = _ConsentRepository();
      await pump(tester, repository);

      await tester.tap(find.text('Deny'));
      await tester.pumpAndSettle();

      expect(repository.answers, [false]);
      expect(left.single.queryParameters['error'], 'access_denied');
    });

    testWidgets('an expired request says so and offers nothing to approve', (
      tester,
    ) async {
      await pump(tester, _ConsentRepository(gone: true));

      expect(find.text('This request has ended'), findsOneWidget);
      expect(find.text('Allow'), findsNothing);
    });

    testWidgets('a link with no request is treated as ended', (tester) async {
      await pump(tester, _ConsentRepository(), requestId: null);

      expect(find.text('This request has ended'), findsOneWidget);
    });
  });

  group('the linked assistants section', () {
    testWidgets(
      'a closed endpoint still lists links so they can be disconnected',
      (tester) async {
        final unlinked = <String>[];
        await tester.pumpWidget(
          MaterialApp(
            theme: buildServalTheme(),
            home: Scaffold(
              body: SingleChildScrollView(
                child: AssistantsSection(
                  status: const AssistantStatus(
                    effective: false,
                    blocker: 'disabled',
                    reason: 'AI assistants are switched off.',
                    serverUrl: null,
                  ),
                  links: [
                    AssistantLink(
                      id: 'g2',
                      clientName: 'Claude',
                      verifiedHost: 'claude.ai',
                      verified: true,
                      createdAt: DateTime(2026, 11, 1),
                      lastUsedAt: DateTime(2026, 11, 2),
                    ),
                  ],
                  onUnlink: (link) => unlinked.add(link.id),
                ),
              ),
            ),
          ),
        );

        expect(find.text('AI assistants are switched off.'), findsOneWidget);
        expect(find.text('Verified as claude.ai'), findsOneWidget);
        await tester.tap(find.text('Disconnect'));
        expect(unlinked, ['g2']);
      },
    );

    testWidgets('copying the address says so, then goes back', (tester) async {
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
        SystemChannels.platform,
        (call) async => null,
      );
      addTearDown(
        () => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
          SystemChannels.platform,
          null,
        ),
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: buildServalTheme(),
          home: const Scaffold(
            body: SingleChildScrollView(
              child: AssistantsSection(
                status: AssistantStatus(
                  effective: true,
                  blocker: 'none',
                  reason: null,
                  serverUrl: 'https://serval.example/mcp',
                ),
                links: [],
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Copy'));
      await tester.pump();
      expect(find.text('Copied'), findsOneWidget);

      await tester.pump(const Duration(seconds: 3));
      expect(find.text('Copy'), findsOneWidget);
    });

    Future<void> pumpSection(WidgetTester tester, AssistantsSection section) =>
        tester.pumpWidget(
          MaterialApp(
            theme: buildServalTheme(),
            home: Scaffold(body: SingleChildScrollView(child: section)),
          ),
        );

    testWidgets('shows the address to connect with and each link', (
      tester,
    ) async {
      final unlinked = <String>[];
      await pumpSection(
        tester,
        AssistantsSection(
          status: const AssistantStatus(
            effective: true,
            blocker: 'none',
            reason: null,
            serverUrl: 'https://serval.example/mcp',
          ),
          links: [
            AssistantLink(
              id: 'g1',
              clientName: 'Gemini CLI',
              verifiedHost: null,
              verified: false,
              createdAt: DateTime(2026, 11, 1),
              lastUsedAt: null,
            ),
          ],
          onUnlink: (link) => unlinked.add(link.id),
        ),
      );

      expect(find.text('https://serval.example/mcp'), findsOneWidget);
      expect(find.text('Gemini CLI'), findsOneWidget);
      expect(find.textContaining('Unverified'), findsOneWidget);
      expect(find.textContaining('not used yet'), findsOneWidget);

      await tester.tap(find.text('Disconnect'));
      expect(unlinked, ['g1']);
    });

    testWidgets('a closed endpoint shows the Server\'s reason and no address', (
      tester,
    ) async {
      await pumpSection(
        tester,
        const AssistantsSection(
          status: AssistantStatus(
            effective: false,
            blocker: 'publicBaseUrlInvalid',
            reason: 'Serval:Mcp:PublicBaseUrl must be https.',
            serverUrl: null,
          ),
          links: [],
        ),
      );

      expect(
        find.text('Serval:Mcp:PublicBaseUrl must be https.'),
        findsOneWidget,
      );
      expect(find.text('Connector address'), findsNothing);
      expect(find.text('Nothing linked yet.'), findsNothing);
    });
  });
}
