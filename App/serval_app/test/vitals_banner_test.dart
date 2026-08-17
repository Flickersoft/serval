// The wall's vitals warning strip.
//
// The wall is for watching cameras, so the bar for putting something across the top of it is high.
// Only two conditions clear it — a volume about to fill, and memory about to be killed for — and
// the rules below are what keep it from becoming wallpaper: one strip rather than a stack, the
// Server's own wording rather than the App's, dismissable so a warning somebody has already acted
// on stops nagging, and *not* dismissable when recording is about to start failing.
//
// The placement is the other thing this pins. The strip is a sibling of `_WallHeader` rather than
// part of it, because the header holds its height constant on purpose — its own comment explains
// that a subtitle appearing only while rearranging "would change the header's height and jog the
// whole wall down every time the mode is toggled". A strip inside it would do exactly that.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_repository.dart';
import 'package:serval_app/models/system_stats.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_tile.dart';
import 'package:serval_app/widgets/storage_bar.dart';

/// The sample repository with its vitals swapped out.
///
/// The sample one is deliberately healthy and alert-free — a golden that ships with a permanent
/// warning across it is a golden nobody reads — so reaching these states means overriding just
/// that one member and forwarding everything else.
class _WithAlerts extends SampleServalRepository {
  const _WithAlerts(this._alerts);

  final List<VitalsAlert> _alerts;

  @override
  SystemStats? systemStats() {
    final base = super.systemStats()!;
    return SystemStats(
      sampledAt: base.sampledAt,
      cpu: base.cpu,
      memory: base.memory,
      gpu: base.gpu,
      disk: base.disk,
      alerts: _alerts,
    );
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

  const low = VitalsAlert(
    kind: VitalsAlertKind.diskLow,
    severity: 'warning',
    message: 'Under 10% free on /media.',
  );
  const critical = VitalsAlert(
    kind: VitalsAlertKind.diskCritical,
    severity: 'critical',
    message: 'Under 5% free on /media.',
  );
  const memory = VitalsAlert(
    kind: VitalsAlertKind.memoryHigh,
    severity: 'warning',
    message: 'Serval is using 94% of the memory it is allowed.',
  );

  // The activity column's own alert card has a *Dismiss* too, so every finder below is scoped to
  // the strip rather than to the screen.
  final dismiss = find.descendant(
    of: find.byType(VitalsAlertStrip),
    matching: find.text('Dismiss'),
  );

  Widget harness(ServalRepository repository) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp.router(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      routerConfig: GoRouter(
        initialLocation: '/wall',
        routes: [
          GoRoute(
            path: '/wall',
            builder: (context, state) =>
                Scaffold(body: WallScreen(onOpenCamera: (_, _) {})),
          ),
          GoRoute(
            path: '/settings/server',
            builder: (context, state) =>
                const Scaffold(body: Center(child: Text('server settings'))),
          ),
        ],
      ),
    ),
  );

  testWidgets('a healthy server puts nothing across the wall', (tester) async {
    await tester.pumpWidget(harness(const SampleServalRepository()));
    await tester.pumpAndSettle();

    expect(find.byType(VitalsAlertStrip), findsNothing);
  });

  testWidgets('a warning appears with the Server’s own words', (tester) async {
    await tester.pumpWidget(harness(const _WithAlerts([low])));
    await tester.pumpAndSettle();

    // Verbatim. The threshold and its phrasing are the Server's, so that a future notification
    // path says the same sentence with no App running.
    expect(find.text('Under 10% free on /media.'), findsOneWidget);
    expect(find.byType(VitalsAlertStrip), findsOneWidget);
  });

  testWidgets('only the most severe one is shown, not a stack', (tester) async {
    await tester.pumpWidget(harness(const _WithAlerts([memory, critical])));
    await tester.pumpAndSettle();

    expect(find.byType(VitalsAlertStrip), findsOneWidget);
    expect(find.text('Under 5% free on /media.'), findsOneWidget);
    expect(find.textContaining('memory it is allowed'), findsNothing);
  });

  testWidgets('a warning can be put aside for the session', (tester) async {
    await tester.pumpWidget(harness(const _WithAlerts([low])));
    await tester.pumpAndSettle();

    await tester.tap(dismiss);
    await tester.pumpAndSettle();

    expect(find.byType(VitalsAlertStrip), findsNothing);
  });

  testWidgets('a critical one cannot be dismissed at all', (tester) async {
    await tester.pumpWidget(harness(const _WithAlerts([critical])));
    await tester.pumpAndSettle();

    // At under 5% free, recording is about to start failing. Silencing that is not a choice the
    // wall should offer.
    expect(find.byType(VitalsAlertStrip), findsOneWidget);
    expect(dismiss, findsNothing);
  });

  testWidgets('dismissing one warning does not hide a different one', (
    tester,
  ) async {
    await tester.pumpWidget(harness(const _WithAlerts([low, memory])));
    await tester.pumpAndSettle();

    await tester.tap(dismiss);
    await tester.pumpAndSettle();

    expect(find.textContaining('memory it is allowed'), findsOneWidget);
  });

  testWidgets('it leads to the page that explains it', (tester) async {
    await tester.pumpWidget(harness(const _WithAlerts([low])));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Open settings'));
    await tester.pumpAndSettle();

    expect(find.text('server settings'), findsOneWidget);
  });

  testWidgets('the strip pushes the grid down rather than covering it', (
    tester,
  ) async {
    // The reason it is a sibling of the header and not inside it: appearing must move the wall,
    // not overlay it or squeeze the header. Both walls still lay out cleanly, and the tiles start
    // lower with the strip present.
    await tester.pumpWidget(harness(const SampleServalRepository()));
    await tester.pumpAndSettle();
    final without = tester.getTopLeft(find.byType(CameraTile).first).dy;

    await tester.pumpWidget(harness(const _WithAlerts([low])));
    await tester.pumpAndSettle();
    final with_ = tester.getTopLeft(find.byType(CameraTile).first).dy;

    expect(with_, greaterThan(without));
    expect(tester.takeException(), isNull);
  });
}
