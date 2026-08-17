// Accounts — design 3b. The screen is 745 lines with twelve `setState` sites, and the only
// destructive controls in the app (remove an account, set someone's password) sit behind them.
//
// What is pinned here is the loading/error/data fork the provider owns, and the selection, which
// is derived rather than written back into state after every re-fetch.
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_models.dart';
import 'package:serval_app/data/auth/user_account.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/screens/users_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/user_editor_form.dart';

/// A repository whose account list the test drives: it can hang, fail, or answer.
class _UsersRepository extends SampleServalRepository {
  _UsersRepository();

  /// Completed by the test, so the loading state is observable rather than a race.
  Completer<List<UserAccount>>? gate;
  Object? failure;
  List<UserAccount> accounts = [
    UserAccount(
      username: 'kim',
      displayName: 'Kim',
      role: Role.admin,
      createdAt: DateTime.utc(2024, 1, 12),
    ),
    UserAccount(
      username: 'sam',
      displayName: 'Sam',
      role: Role.viewer,
      createdAt: DateTime.utc(2024, 3, 4),
    ),
  ];

  int reads = 0;

  @override
  Future<List<UserAccount>> listUsers() async {
    reads++;
    if (gate != null) return gate!.future;
    if (failure != null) throw failure!;
    return accounts;
  }

  @override
  Future<void> deleteUser(String username) async {
    accounts = [
      for (final a in accounts)
        if (a.username != username) a,
    ];
  }
}

void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1440, 900);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  Future<ProviderContainer> pump(
    WidgetTester tester,
    _UsersRepository repository,
  ) async {
    final container = ProviderContainer(
      overrides: [repositoryProvider.overrideWithValue(repository)],
    );
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: buildServalTheme(),
          home: const Scaffold(body: UsersScreen()),
        ),
      ),
    );
    return container;
  }

  testWidgets('says so while the first read is in flight', (tester) async {
    final repository = _UsersRepository()..gate = Completer();
    await pump(tester, repository);
    await tester.pump();

    // A nullable-list-means-loading sentinel cannot tell "still reading" from "read fine, no
    // accounts", and an empty Server then sits on the loading copy forever.
    expect(find.text('Loading accounts…'), findsOneWidget);

    repository.gate!.complete(repository.accounts);
    await tester.pumpAndSettle();

    expect(find.text('Loading accounts…'), findsNothing);
    expect(find.text('Kim'), findsWidgets);
  });

  testWidgets('a failed read offers a retry, and the retry re-reads', (
    tester,
  ) async {
    final repository = _UsersRepository()..failure = StateError('no server');
    await pump(tester, repository);
    await tester.pumpAndSettle();

    expect(find.text('Try again'), findsOneWidget);
    expect(repository.reads, 1);

    // Recover, then retry: the button must actually re-run the read rather than only clearing the
    // error, which is the failure mode of a hand-rolled retry that just nulls its error field.
    repository.failure = null;
    await tester.tap(find.text('Try again'));
    await tester.pumpAndSettle();

    expect(repository.reads, 2);
    expect(find.text('Try again'), findsNothing);
    expect(find.text('Kim'), findsWidgets);
  });

  testWidgets('opens on the first account', (tester) async {
    final repository = _UsersRepository();
    await pump(tester, repository);
    await tester.pumpAndSettle();

    expect(find.text('Kim'), findsWidgets);
    expect(find.text('Sam'), findsWidgets);
    // The editor is showing someone, rather than the empty-state copy.
    expect(find.text('No accounts yet'), findsNothing);
  });

  testWidgets('a deleted account does not strand the editor on it', (
    tester,
  ) async {
    // The case the derived selection exists for. A selection written back after each re-fetch
    // holds a deleted account the moment one write is missed, and its *Remove* button then keeps
    // offering to delete it again.
    final repository = _UsersRepository();
    final container = await pump(tester, repository);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Sam').last);
    await tester.pumpAndSettle();

    await repository.deleteUser('sam');
    container.invalidate(usersProvider);
    await tester.pumpAndSettle();

    expect(find.text('Sam'), findsNothing);

    // Not merely "Kim is somewhere on screen" — she is in the left-hand list either way. The
    // editor must have *moved* to her, rather than falling through to the empty state with a
    // roster that plainly is not empty.
    expect(find.text('No accounts yet'), findsNothing);
    expect(
      tester
          .widget<UserEditorForm>(find.byType(UserEditorForm))
          .account
          ?.username,
      'kim',
    );
  });

  testWidgets('an empty roster reads as empty rather than as loading', (
    tester,
  ) async {
    final repository = _UsersRepository()..accounts = [];
    await pump(tester, repository);
    await tester.pumpAndSettle();

    expect(find.text('Loading accounts…'), findsNothing);
    expect(find.text('No accounts yet'), findsOneWidget);
  });
}
