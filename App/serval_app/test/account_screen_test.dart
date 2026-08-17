import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/screens/account_screen.dart';
import 'package:serval_app/theme/app_theme.dart';

/// The page that finally reaches `PUT /api/auth/password`.
///
/// What is worth pinning here is not the layout but the two rules that make the form safe: the
/// current password is required before anything can be sent, and the *sign out everywhere* switch
/// starts on. Both are the opposite of what a careless implementation would do — the Server's own
/// default for `signOutAllSessions` is false, and this page overrides it deliberately.
class _RecordingRepository extends SampleServalRepository {
  _RecordingRepository();

  String? currentPassword;
  String? newPassword;
  bool? signOutAll;
  int calls = 0;

  @override
  Future<void> changeOwnPassword({
    required String currentPassword,
    required String newPassword,
    bool signOutAllSessions = false,
  }) async {
    calls++;
    this.currentPassword = currentPassword;
    this.newPassword = newPassword;
    signOutAll = signOutAllSessions;
  }
}

void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    // Taller than a real screen on purpose. The page is a ListView, so a short surface never
    // builds *Sessions* at all and a finder for it reports it missing rather than off-screen —
    // and what these tests are about is the form's rules, not where it scrolls.
    view.physicalSize = const Size(1440, 1800);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  Widget harness(_RecordingRepository repository) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: const Scaffold(body: AccountScreen()),
    ),
  );

  /// The box under a given label.
  ///
  /// `NocturneField` draws its label as a *sibling* of the box inside its own Column, not as an
  /// ancestor of it — so this walks up to that Column and back down, which is the only way to tell
  /// three identically-shaped password boxes apart.
  Future<void> type(WidgetTester tester, String label, String value) async {
    final field = find.descendant(
      of: find
          .ancestor(of: find.text(label), matching: find.byType(Column))
          .first,
      matching: find.byType(EditableText),
    );
    expect(field, findsOneWidget, reason: 'no box found under “$label”');

    await tester.enterText(field, value);
    await tester.pump();
  }

  testWidgets('the three sections are on the page', (tester) async {
    await tester.pumpWidget(harness(_RecordingRepository()));
    await tester.pumpAndSettle();

    expect(find.text('Profile'), findsOneWidget);
    expect(find.text('Password'), findsOneWidget);
    expect(find.text('Sessions'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  /// The reason the route asks for it at all: being signed in proves the session was opened once,
  /// not that whoever is holding it now knows the password. A form that let the field stay empty
  /// would be asking the Server to enforce what the screen should never have offered.
  testWidgets('nothing is sent without the current password', (tester) async {
    final repository = _RecordingRepository();
    await tester.pumpWidget(harness(repository));
    await tester.pumpAndSettle();

    await type(tester, 'New password', 'a-long-enough-one');
    await type(tester, 'Confirm new password', 'a-long-enough-one');

    await tester.tap(find.text('Change password'));
    await tester.pumpAndSettle();

    expect(repository.calls, 0);
  });

  testWidgets('two different new passwords are refused, and say so', (
    tester,
  ) async {
    final repository = _RecordingRepository();
    await tester.pumpWidget(harness(repository));
    await tester.pumpAndSettle();

    await type(tester, 'Current password', 'the-old-one');
    await type(tester, 'New password', 'a-long-enough-one');
    await type(tester, 'Confirm new password', 'a-different-one');
    await tester.pumpAndSettle();

    expect(find.text('The two new passwords differ.'), findsOneWidget);

    await tester.tap(find.text('Change password'));
    await tester.pumpAndSettle();

    expect(repository.calls, 0);
  });

  /// The Server defaults `signOutAllSessions` to false. This page defaults it to **true**, because
  /// the common reason to change a password is that somebody may know the old one, and leaving
  /// their other sessions alive is what would make the change pointless.
  testWidgets('a change signs out everywhere unless told otherwise', (
    tester,
  ) async {
    final repository = _RecordingRepository();
    await tester.pumpWidget(harness(repository));
    await tester.pumpAndSettle();

    await type(tester, 'Current password', 'the-old-one');
    await type(tester, 'New password', 'a-long-enough-one');
    await type(tester, 'Confirm new password', 'a-long-enough-one');
    await tester.pumpAndSettle();

    await tester.tap(find.text('Change password'));
    await tester.pumpAndSettle();

    expect(repository.calls, 1);
    expect(repository.currentPassword, 'the-old-one');
    expect(repository.newPassword, 'a-long-enough-one');
    expect(repository.signOutAll, isTrue);
    expect(find.text('Password changed.'), findsOneWidget);
  });

  testWidgets('a password shorter than the Server accepts never leaves', (
    tester,
  ) async {
    final repository = _RecordingRepository();
    await tester.pumpWidget(harness(repository));
    await tester.pumpAndSettle();

    await type(tester, 'Current password', 'the-old-one');
    await type(tester, 'New password', 'short');
    await type(tester, 'Confirm new password', 'short');
    await tester.pumpAndSettle();

    expect(
      find.text('The new password must be at least 8 characters.'),
      findsOneWidget,
    );

    await tester.tap(find.text('Change password'));
    await tester.pumpAndSettle();

    expect(repository.calls, 0);
  });
}
