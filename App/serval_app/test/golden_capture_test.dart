// Tagged so CI can run this suite apart from the rest: these are the only tests here that compare
// rendered pixels, and so the only ones whose result depends on which machine drew them.
@Tags(['golden'])
library;

import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/push/push_client.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/activity_filter_panel.dart';
import 'package:serval_app/widgets/activity_sheet.dart';
import 'package:serval_app/widgets/camera_tile.dart';
import 'package:serval_app/widgets/config_backup_section.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// Renders the screens at the design's 1440x900 with the real vendored
/// fonts, so the output can be compared against `Serval.dc.html` side by side.
void main() {
  setUpAll(() async {
    TestWidgetsFlutterBinding.ensureInitialized();
    goldenFileComparator = _TolerantComparator(
      Uri.parse('${Directory.current.path}/test/golden_capture_test.dart'),
    );
    await _loadFont('Inter', [
      'assets/fonts/Inter-400.ttf',
      'assets/fonts/Inter-500.ttf',
      'assets/fonts/Inter-600.ttf',
    ]);
    await _loadFont('JetBrainsMono', [
      'assets/fonts/JetBrainsMono-400.ttf',
      'assets/fonts/JetBrainsMono-500.ttf',
    ]);

    // Package fonts are not registered by `flutter test`, so the Phosphor
    // glyphs would otherwise render as tofu. The family name has to carry the
    // `packages/<name>/` prefix the icons' `fontPackage` resolves to.
    final phosphor = await _phosphorFontDir();
    await _loadFont('packages/phosphor_icons/PhosphorRegular', [
      '$phosphor/Phosphor.ttf',
    ]);
    await _loadFont('packages/phosphor_icons/PhosphorFill', [
      '$phosphor/Phosphor-Fill.ttf',
    ]);
  });

  late TestFlutterView view;

  setUp(() {
    view = TestWidgetsFlutterBinding.ensureInitialized()
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

  /// The size the compact designs are drawn at, for the tests below that are
  /// about a phone rather than about the 1440x900 the rest of this file pins.
  void phone() => view.physicalSize = const Size(412, 892);

  /// Stands in for a browser that has push, has been allowed it, and is already registered against
  /// the sample deployment.
  ///
  /// Only the notifications captures need it. Everything else is drawn from the repository, but
  /// that page reads the *browser* as well, and on the VM `PushClient` answers for a platform with
  /// no service worker at all — which is the right answer there and the wrong picture to compare
  /// against a design of the working state.
  void asBrowser() {
    PushClient.debugBrowser = (
      supported: true,
      permission: PushPermission.granted,
      subscription: const PushSubscriptionInfo(
        endpoint: sampleThisBrowserEndpoint,
        p256dh: 'sample-p256dh',
        auth: 'sample-auth',
      ),
    );
    addTearDown(() => PushClient.debugBrowser = null);
  }

  testWidgets('1b — the live wall', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();
    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/wall.png'),
    );
  });

  // The first screen a new install shows, and the one state the sample content cannot reach:
  // every other golden here renders six cameras. What it locks is that an empty registry says so
  // and offers the way out, rather than painting a blank panel over an empty timeline.
  testWidgets('1f — the wall with no cameras', (tester) async {
    await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
    await tester.pumpAndSettle();
    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/wall_empty.png'),
    );
  });

  // The design's own capture shows the wall mid-rearrange, so this is the frame
  // to compare against `Serval.dc.html` — `wall.png` above is the settled state
  // the app now opens in, which the capture has no equivalent of.
  testWidgets('1b — the live wall, rearranging', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Rearrange the wall'));
    await tester.pumpAndSettle();
    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/wall_rearranging.png'),
    );
  });

  // The state round 6 of the design draws: the filter panel open over the column with several
  // facets on, the chips underneath the search field, and the readout saying how much of the
  // column is left. `wall.png` above is the resting state the same design pairs it with.
  testWidgets('1e — the wall, filtering', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    await tester.tap(find.text('Filter'));
    await tester.pumpAndSettle();

    // Two kinds at once — which a segmented control could not express — plus a camera and a label,
    // so the chip row and the badge both have something to say. Scoped to the panel: a camera's
    // name is also on its tile, on its rows and, once ticked, on its chip.
    Finder inPanel(String label) => find.descendant(
      of: find.byType(ActivityFilterPanel),
      matching: find.text(label),
    );

    // Settled between taps, and that is not ceremony: each tap derives the next filter from the
    // one the panel was built with, so four taps against an unrebuilt tree would leave only the
    // last of them on.
    for (final facet in ['Speech', 'Objects seen', 'Front door', 'person']) {
      await tester.tap(inPanel(facet));
      await tester.pumpAndSettle();
    }

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/wall_filtering.png'),
    );
  });

  // The wall showing the past: a bar per camera, the transport, and the tiles in their gap state —
  // the sample repository has no Server to play from, so nothing opens. Worth pinning because it is
  // the frame where the timeline has to line up with the tiles above it.
  //
  // Reached by clicking the day rather than by a mode button, which is the whole point: the wall
  // and the single camera are now entered the same way.
  testWidgets('1d — the wall replaying', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    final scrubber = tester.getRect(find.byType(TimelineScrubber));
    await tester.tapAt(Offset(scrubber.center.dx, scrubber.top + 50));

    // One pump, then pause — and the single pump is the point. A playing wall derives its
    // playhead from the wall clock, so the line moves for as long as the test lets it: at an hour
    // across the track a pixel is about four seconds, and a `pumpAndSettle` here was enough to
    // cross one. Pausing freezes it at whatever has elapsed, so the less that is, the better.
    await tester.pump();
    await tester.tap(find.byTooltip('Pause'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/wall_replay.png'),
    );
  });

  testWidgets('1c — the single camera', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.text('Glass heard').first);
    await tester.pumpAndSettle();
    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/camera.png'),
    );
  });

  testWidgets('13a — saved clips', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    // Through the rail rather than built directly, so this pins the second destination existing
    // and leading where it says as well as what the screen draws.
    await tester.tap(find.byTooltip('Saved clips'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/clips.png'),
    );
  });

  testWidgets('14a — the alert queue', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    // Through the rail, so this pins the third destination existing and leading where it says as
    // well as what the screen draws.
    await tester.tap(find.byTooltip('Alerts'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/alerts.png'),
    );
  });

  // Round 12 has no golden here, deliberately. The trimmer opens around the playhead, which live
  // is the wall clock — so every tick label, both time fields and the range in the button are
  // different on every run, and a pixel comparison would fail a minute after it was baked. What
  // that screen is for is checked structurally instead, in `clip_mode_test.dart`.

  testWidgets('2a — cameras and settings', (tester) async {
    await tester.pumpWidget(const ServalApp());

    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    // Reached through the sidebar rather than built directly, so the golden pins the row leading
    // where it says as well as what the page draws.
    await tester.tap(find.text('Cameras'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/cameras.png'),
    );
  });

  // Design 9b. Reached through the camera's *Masks & zones* section rather than by address, so
  // this pins the way in as well as what the editor draws — and that it really does replace the
  // whole window, rail and sidebar included.
  testWidgets('9b — the mask editor', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Cameras'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Masks & zones').first);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Edit masks'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/masks.png'),
    );
  });

  // What the Server is told to do. Reached through the sidebar rather than built directly, so this
  // pins the row leading where it says as well as what the page draws.
  testWidgets('2b — server settings', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Server settings'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/settings.png'),
    );
  });

  // Design 15b. The one settings page that belongs to the person rather than the deployment, and
  // the only one whose content depends on what the *browser* says — so it needs [asBrowser], or
  // the VM's "no push at all" would capture the page's failure state instead of its working one.
  testWidgets('15b — notifications', (tester) async {
    asBrowser();
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Notifications'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/notifications.png'),
    );
  });

  // The other page that belongs to the person rather than the deployment. Every route behind it
  // predates it by a long way — `PUT /api/auth/password` in particular — so what this captures is
  // a page catching up with a Server that could already do all three of these.
  testWidgets('2e — account', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Account'));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/account.png'),
    );
  });

  // And what it is actually doing. No second tap: the rail's gear lands here, so this golden also
  // pins settings opening on the one page that changes nothing.
  testWidgets('2c — server status', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    // No configuration-backup section here, and that is not an omission: this runs on the sample
    // repository, whose `canSaveMedia` is false, so both callbacks are null and `ServerScreenBody`
    // drops the section. This golden is therefore the page as the design drew it, and 2d below is
    // where the section gets its own picture.
    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/server.png'),
    );
  });

  /// The backup section, which the page-level golden above cannot reach.
  ///
  /// Pumped as a body rather than through `ServalApp`, because the only way to make the real page
  /// draw it is to put a Server behind the repository — which is the one thing the golden harness
  /// is built never to do.
  testWidgets('2d — configuration backup', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        // Inside a Scaffold and at the page's own content width. Without the Scaffold there is no
        // DefaultTextStyle and every label picks up WidgetsApp's fallback — yellow, double
        // underlined — which is the same trap the sign-in golden below carries a note about.
        home: Scaffold(
          backgroundColor: Serval.panel,
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 780,
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: ConfigBackupSection(
                  onBackup: () {},
                  onRestore: () {},
                  status:
                      'Saved serval-config-20260808-140311.json to ~/Downloads',
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ConfigBackupSection),
      matchesGoldenFile('goldens/server-backup.png'),
    );
  });

  testWidgets('3a — sign in', (tester) async {
    // Built through the real `ServalApp` rather than by pumping `LoginScreen` under a hand-rolled
    // `MaterialApp`: the login screen sits outside any `Scaffold`, so it depends on the app's own
    // `DefaultTextStyle` for its text. Reproducing that config here would be reproducing the very
    // thing the golden is meant to hold — without it every label picks up `WidgetsApp`'s fallback
    // error style, yellow double underline and all.
    //
    // A cold `AuthController` reports unauthenticated, which is what the router's redirect turns
    // into `/login`; nothing here reads storage or reaches the network, and the repository's
    // sockets are only constructed, never started, since `_RepositoryStarter` starts them on
    // sign-in.
    final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
    final auth = AuthController(config: config);
    addTearDown(auth.dispose);
    final repository = LiveServalRepository(auth: auth, config: config);
    addTearDown(repository.dispose);

    await tester.pumpWidget(ServalApp(repository: repository, auth: auth));
    await tester.pumpAndSettle();

    await expectLater(
      find.byType(ServalApp),
      matchesGoldenFile('goldens/login.png'),
    );
  });

  // The compact screens, at the 412x892 the design draws them at.
  //
  // These had no goldens at all until now: rounds 7b, 8 and 11 shipped pinned by widget tests
  // measuring one figure each, which say a sheet is 236 tall and nothing about what is in it. The
  // phone is where the App diverges most from the desktop it was designed for — a different bar,
  // a different navigation model, a sheet instead of a column — so it is the layout most worth
  // being able to see.
  group('on a phone', () {
    /// The sheet's own drag surface reaches over its title, so tapping the title
    /// toggles between the two detents — which is what the grabber advertises,
    /// and steadier in a test than a 18px strip found by coordinates.
    Future<void> raiseSheet(WidgetTester tester) async {
      await tester.tap(find.text('What’s happening'));
      await tester.pumpAndSettle();
    }

    /// The four facets `wall_filtering.png` uses, so the phone's chips and the
    /// desktop's are the same filter seen at two sizes.
    Future<void> narrow(WidgetTester tester) async {
      await tester.tap(find.text('Filter'));
      await tester.pumpAndSettle();

      for (final facet in ['Speech', 'Objects seen', 'Front door', 'person']) {
        final row = find.descendant(
          of: find.byType(ActivityFilterPanel),
          matching: find.text(facet),
        );

        // Scrolled to first, unlike the desktop's panel where all four are on
        // screen at once. *What was seen or heard* is the last section and sits
        // below the fold at this height, and a tap on a laid-out widget nobody
        // can see lands on whatever is drawn over it — here the footer bar,
        // which quietly does nothing.
        await tester.ensureVisible(row);
        await tester.pumpAndSettle();

        await tester.tap(row);
        await tester.pumpAndSettle();
      }

      // Back to the top, where the design frames it. Reaching the last facet
      // left the panel scrolled past the alert toggle and the section that
      // opens it, which is most of what 11c is about.
      await tester.ensureVisible(find.text("Only what's marked an alert"));
      await tester.pumpAndSettle();
    }

    testWidgets('11a — the wall, sheet at rest', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/wall_phone.png'),
      );
    });

    // The compact half of 1f. Worth its own capture because the empty state is the one part of
    // the wall the two layouts do not share a widget for — the phone has no scrubber to hide and
    // reaches the same panel through `_CompactWall` instead of the tile grid.
    testWidgets('11d — the wall with no cameras', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/wall_empty_phone.png'),
      );
    });

    testWidgets('11c — the wall, the filter as the sheet', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await raiseSheet(tester);
      await narrow(tester);

      // Captured before *Done*: the panel risen over a dimmed wall, with the
      // count in the bar beside the button.
      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/wall_phone_filter.png'),
      );
    });

    testWidgets('11b — the wall, sheet raised and filtered', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await raiseSheet(tester);
      await narrow(tester);

      // *Done* returns the sheet to the detent the filter was opened from, so
      // this is 11c's frame one tap later — the chips now in the fixed head and
      // one camera whole above them.
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/wall_phone_raised.png'),
      );
    });

    testWidgets('8a — one camera on a phone', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      // The second tile in the saved arrangement's reading order is Front door,
      // which is the camera round 8 draws — it is the one with talk-back, so it
      // is the only one whose *Hold to talk* pill is real.
      await tester.tap(find.byType(CameraTile).at(1));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/camera_phone.png'),
      );
    });

    testWidgets('8a — one camera on a phone, tray pushed down', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.byType(CameraTile).at(1));
      await tester.pumpAndSettle();

      // Flung down one detent to its peek. What is left of the tray is the controls the camera is
      // worked with, and the picture takes everything the feed was holding — 16:9 letterboxed into
      // a box twice its height, which is as large as a landscape scene gets on a phone held
      // upright, and what a pinch is given room in.
      await tester.flingFrom(
        _trayTop(tester) + const Offset(0, 9),
        const Offset(0, 60),
        1000,
      );
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/camera_phone_peek.png'),
      );
    });

    testWidgets('8a — one camera on a phone, tray stowed', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.byType(CameraTile).at(1));
      await tester.pumpAndSettle();

      // The bottom of the travel, one drag further down: the controls go too, and all that is
      // left over the picture is the handle and the line that says what is behind it. This is the
      // height a camera sending a portrait scene is watched at, where the picture is the only
      // thing on the screen worth the room.
      await tester.dragFrom(
        _trayTop(tester) + const Offset(0, 9),
        const Offset(0, 800),
      );
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/camera_phone_stowed.png'),
      );
    });

    testWidgets('8a — one camera on a phone, filtering', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.byType(CameraTile).at(1));
      await tester.pumpAndSettle();

      // The panel takes the screen below the picture rather than the feed's own band, where a
      // header and footer leave no room for a facet. The picture stays lit above it — the camera
      // you are filtering is the one on screen.
      await tester.tap(find.text('Filter'));
      await tester.pumpAndSettle();

      for (final facet in ['Speech', 'Objects seen']) {
        await tester.tap(
          find.descendant(
            of: find.byType(ActivityFilterPanel),
            matching: find.text(facet),
          ),
        );
        await tester.pumpAndSettle();
      }

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/camera_phone_filter.png'),
      );
    });

    testWidgets('14b — the alert queue on a phone', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Alerts'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/alerts_phone.png'),
      );
    });

    testWidgets('14c — one alert on a phone', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Alerts'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Person at Front door').first);
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/alert_phone.png'),
      );
    });

    // 14d: the same screen for a camera that was not recording. It is worth its own golden
    // precisely because it should look ordinary — the preview plays like any other, and the only
    // difference is the missing *Watch* and the dashed chip saying why.
    testWidgets('14d — an alert from a camera that was not recording', (
      tester,
    ) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Alerts'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Person at Side path'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/alert_phone_not_recorded.png'),
      );
    });

    testWidgets('13b — the clips list on a phone', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Saved clips'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/clips_phone.png'),
      );
    });

    testWidgets('13c — one clip on a phone', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Saved clips'));
      await tester.pumpAndSettle();

      // The first row, which is the design's own clip — the one with a summary and a transcript.
      await tester.tap(find.text('Parcel behind the planter'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/clip_phone.png'),
      );
    });

    // Design 15c — the same cards at 412px, one across instead of three.
    testWidgets('15c — notifications on a phone', (tester) async {
      phone();
      asBrowser();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Settings'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Notifications'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/notifications_phone.png'),
      );
    });

    testWidgets('7b — settings as a drill-down', (tester) async {
      phone();
      await tester.pumpWidget(const ServalApp());
      await tester.pumpAndSettle();

      // The gear in the wall's own bar, which below 950 carries the navigation the rail does
      // above it — the clips and the gear, so this has to name which. By semantics label rather
      // than by tooltip: a `CompactBarAction` spends its `tooltip` on a Semantics label and draws
      // no `Tooltip`, there being no pointer down here to hover with.
      await tester.tap(find.bySemanticsLabel('Settings'));
      await tester.pumpAndSettle();

      await expectLater(
        find.byType(ServalApp),
        matchesGoldenFile('goldens/settings_phone.png'),
      );
    });
  });
}

/// A golden comparator that ignores a handful of stray pixels.
///
/// These captures are whole screens, and some of what they draw moves on its own: the wall's
/// playhead is derived from the clock, so it lands where it lands depending on how long the run
/// took, and at an hour across the track a pixel is about four seconds. Failing a screenshot of
/// six cameras over a line one pixel to the left is a test reporting on the machine it ran on.
///
/// The threshold is deliberately tiny, and a fraction rather than a count so it means the same
/// thing on a 412x892 capture as on a 1440x900 one. A shifted hairline is a few hundred pixels of
/// the larger frame — a hundredth of a percent — where anything that has actually moved, resized or
/// changed colour is orders of magnitude more. This is slack for jitter, not for layout.
/// The top edge of the tray's own rounded surface, which is where its handle is and so where a
/// drag on it starts from.
Offset _trayTop(WidgetTester tester) => tester
    .getRect(
      find
          .descendant(
            of: find.byType(ActivitySheet),
            matching: find.byType(ClipRRect),
          )
          .first,
    )
    .topCenter;

/// The sample content with its registry emptied, which is what a server nobody has added a camera
/// to yet returns.
///
/// All three overrides are needed, and the third is the one that is easy to miss: an arrangement
/// outlives the cameras it names, and the activity feed is stored per episode rather than per
/// camera, so leaving either behind renders a wall that says it has no cameras beside a column of
/// things those cameras saw.
class _EmptyRegistry extends SampleServalRepository {
  const _EmptyRegistry();

  @override
  List<Camera> cameras() => const [];

  @override
  List<TileLayout> wallLayout() => const [];

  @override
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  }) => const [];
}

class _TolerantComparator extends LocalFileComparator {
  _TolerantComparator(super.testFile);

  /// 0.05%: ~650 pixels of this frame, against the ~170 a shifted playhead costs.
  static const _threshold = 0.0005;

  @override
  Future<bool> compare(Uint8List imageBytes, Uri golden) async {
    final result = await GoldenFileComparator.compareLists(
      imageBytes,
      await getGoldenBytes(golden),
    );

    if (result.passed || result.diffPercent <= _threshold) {
      result.dispose();
      return true;
    }

    final failure = await generateFailureOutput(result, golden, basedir);
    result.dispose();
    throw FlutterError(failure);
  }
}

/// Resolves the phosphor_icons package to wherever pub put it, via the
/// package config this test is already running against.
Future<String> _phosphorFontDir() async {
  final config = File('.dart_tool/package_config.json');
  final json = jsonDecode(await config.readAsString()) as Map<String, dynamic>;
  final package = (json['packages'] as List)
      .cast<Map<String, dynamic>>()
      .firstWhere((p) => p['name'] == 'phosphor_icons');
  // `rootUri` carries no trailing slash, which would make `resolve` replace
  // the package directory rather than descend into it.
  var rootUri = package['rootUri'] as String;
  if (!rootUri.endsWith('/')) rootUri = '$rootUri/';
  final root = Uri.parse(rootUri);
  final resolved = root.hasScheme ? root : config.parent.uri.resolveUri(root);
  return File.fromUri(resolved.resolve('lib/fonts/')).path;
}

Future<void> _loadFont(String family, List<String> paths) async {
  final loader = FontLoader(family);
  for (final path in paths) {
    final bytes = await File(path).readAsBytes();
    loader.addFont(Future.value(ByteData.sublistView(bytes)));
  }
  await loader.load();
}
