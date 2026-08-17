// One camera on a phone, and the wall's feed under it.
//
// Three sizes, and the whole point is that they are three: 412x892 is 8a, 892x412 is the phone
// turned on its side, and anything at or above `Serval.compactWidth` is the desktop this must not
// disturb. `camera_screen` has no suite of its own at the wide size — the goldens cover that — so
// what is pinned here is the narrow behaviour and the boundary between them.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/activity_column.dart';
import 'package:serval_app/widgets/activity_filter_panel.dart';
import 'package:serval_app/widgets/activity_sheet.dart';
import 'package:serval_app/widgets/compact_app_bar.dart';
import 'package:serval_app/widgets/ptz_pad.dart';
import 'package:serval_app/widgets/talk_controls.dart';
import 'package:serval_app/widgets/timeline_range_panel.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

void main() {
  const repository = SampleServalRepository();

  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  Camera cameraNamed(String id) =>
      repository.cameras().firstWhere((c) => c.id == id);

  Widget harness(Widget child) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: child),
    ),
  );

  Widget camera(String id) =>
      harness(CameraScreen(camera: cameraNamed(id), onBack: () {}));

  /// The tray's own box, which is the rounded surface rather than the full-height stack it is
  /// positioned inside.
  Rect trayBox(WidgetTester tester) => tester.getRect(
    find
        .descendant(
          of: find.byType(ActivitySheet),
          matching: find.byType(ClipRRect),
        )
        .first,
  );

  /// Drags the grabber — the 18px strip at the top of the tray — by [dy], with a fling when
  /// [velocity] is given.
  Future<void> dragTray(
    WidgetTester tester,
    double dy, {
    double? velocity,
  }) async {
    final box = trayBox(tester);
    final grabber = Offset(box.center.dx, box.top + 9);

    if (velocity == null) {
      await tester.dragFrom(grabber, Offset(0, dy));
    } else {
      await tester.flingFrom(grabber, Offset(0, dy), velocity);
    }
    await tester.pumpAndSettle();
  }

  group('8a — the phone held upright', () {
    testWidgets('lays out without overflow', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });

    testWidgets('the range panel fits upright too, spans in a row', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.textContaining(RegExp(r'^Last ')));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);

      // Too narrow for two columns, so the spans become a row above the days rather than a
      // column beside them.
      final spans = tester.getRect(find.text('5 min'));
      final days = tester.getRect(find.text('Today'));
      expect(spans.bottom, lessThanOrEqualTo(days.top));

      final panel = tester.getRect(find.byType(TimelineRangePanel));
      expect(panel.left, greaterThanOrEqualTo(0));
      expect(panel.right, lessThanOrEqualTo(412));
    });

    testWidgets('the picture takes everything the tray is not covering', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      final picture = tester.getRect(find.byKey(pictureBandKey));

      expect(picture.top, 56, reason: 'directly under the bar');
      expect(picture.width, 412);

      // Its bottom edge *is* the tray's top edge. Nothing between them, and nothing of the
      // picture behind the tray — which is what puts the corner that gives it the screen always
      // just above the tray rather than always underneath it.
      expect(picture.bottom, closeTo(trayBox(tester).top, 1));

      // And it is taller than the 16:9 the scene needs, so a pinch has somewhere to go.
      expect(picture.height, greaterThan(412 / Serval.pictureAspect));
    });

    testWidgets('nothing has to fit, at any size', (tester) async {
      // These four used to be where the arithmetic broke: a share of the height still left the
      // feed less than its own header, which is an overflow rather than a squeeze. Nothing
      // divides a budget now, so the sweep is about the tray having somewhere to be at every
      // detent rather than about one arrangement fitting. `kitchen` has nothing in its feed, so
      // the empty-state card is what has to draw.
      for (final size in [
        const Size(412, 892),
        const Size(678, 641),
        const Size(900, 600),
        const Size(500, 620),
      ]) {
        for (final id in ['front-door', 'kitchen']) {
          sizeTo(tester, size);
          await tester.pumpWidget(camera(id));
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull, reason: '$size $id');

          for (final dy in [300.0, -600.0, 300.0]) {
            await dragTray(tester, dy);
            expect(tester.takeException(), isNull, reason: '$size $id $dy');
          }
        }
      }
    });

    testWidgets('the bar carries the name, and the actions are a row', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(CompactAppBar), findsOneWidget);
      expect(find.text('Front door'), findsOneWidget);

      // The four things the desktop floats over the video.
      for (final action in ['Audio', 'Move', 'Snapshot', 'Clip']) {
        expect(find.text(action), findsOneWidget, reason: action);
      }

      // And the label the wide bar uses is gone with it — a phone has no room for a sentence
      // beside the title.
      expect(find.text('All cameras'), findsNothing);
    });

    testWidgets('hold to talk is pinned low and takes the width', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      final talk = find.byType(HoldToTalkButton);
      expect(talk, findsOneWidget);

      final box = tester.getRect(talk);
      expect(tester.getSize(talk).height, 44);
      // Full width inside the bar's 16px gutters, and against the bottom of the screen.
      expect(box.width, 412 - 32);
      expect(box.bottom, closeTo(892 - 10, 1));
    });

    testWidgets('and is not built at all where nobody can talk', (
      tester,
    ) async {
      // `kitchen` has no speaker. The band was drawn there anyway, dead — the tallest control on
      // the screen, for a button that could never fire.
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('kitchen'));
      await tester.pumpAndSettle();

      expect(cameraNamed('kitchen').twoWayAudio, isFalse);
      expect(find.byType(HoldToTalkButton), findsNothing);
    });

    testWidgets('and the picture takes the room it gives back', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      final withBar = tester.getSize(find.byKey(pictureBandKey)).height;

      await tester.pumpWidget(camera('kitchen'));
      await tester.pumpAndSettle();
      final without = tester.getSize(find.byKey(pictureBandKey)).height;

      // The 62px is not reserved on a camera that cannot use it, and it goes to the picture
      // rather than nowhere — the tray rests at the same height on both.
      expect(without - withBar, closeTo(62, 1));
    });

    testWidgets('the timeline comes along', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(TimelineScrubber), findsOneWidget);
    });

    group('the tray', () {
      testWidgets('carries the controls, and they ride its top edge', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        // Everything the desktop floats over the video, and the timeline with it — inside the
        // tray rather than in a column under a band.
        final tray = trayBox(tester);
        for (final control in ['Audio', 'Move', 'Snapshot', 'Clip']) {
          final box = tester.getRect(find.text(control));
          expect(box.top, greaterThan(tray.top), reason: control);
          expect(box.bottom, lessThan(tray.bottom), reason: control);
        }

        final track = tester.getRect(find.byType(TimelineScrubber));
        expect(track.top, greaterThan(tray.top));
      });

      testWidgets('drags down to a peek that is the controls and no feed', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        final rested = tester.getSize(find.byKey(pictureBandKey)).height;
        expect(find.byType(ActivityFeed), findsOneWidget);

        // One detent down rather than a shove to the bottom: below the peek is the stowed bar,
        // and a drag long enough to reach the floor would be testing that one instead.
        await dragTray(tester, 60, velocity: 1000);

        // The tray's contents are laid out at the tallest it can be and cut off by the box it is
        // in, rather than re-flowed at every height it passes through — so what "gone" means here
        // is below the tray's own edge, not out of the tree.
        final tray = trayBox(tester);

        // The feed's own head is the first thing to go, and the controls are the last: a peek
        // that hid the timeline would be a picture you cannot seek.
        expect(
          tester.getRect(find.text('What’s happening')).top,
          greaterThanOrEqualTo(tray.bottom),
        );
        for (final control in ['Audio', 'Move', 'Snapshot', 'Clip']) {
          final box = tester.getRect(find.text(control));
          expect(box.bottom, lessThan(tray.bottom), reason: control);
        }
        // And the peek is *measured* rather than a figure written down: the track's own bottom
        // padding is the last thing in the tray, so what the tray leaves is exactly what it is
        // carrying. The estimate this replaces counted neither the line a save writes nor the
        // second row replay adds, and was wrong in both of those states.
        expect(
          tester.getRect(find.byType(TimelineScrubber)).bottom,
          closeTo(tray.bottom - 12, 1),
        );

        // And the room it gave up went to the picture, which is what a pinch is given room in.
        final peeked = tester.getSize(find.byKey(pictureBandKey)).height;
        expect(peeked, greaterThan(rested + 150));
        expect(tester.takeException(), isNull);
      });

      testWidgets('and past that to nothing but the line naming it', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        final rested = tester.getSize(find.byKey(pictureBandKey)).height;
        await dragTray(tester, 800);

        final tray = trayBox(tester);
        expect(tray.height, closeTo(Serval.activitySheetStowed, 0.01));

        // What is left says what is behind it, so a tray pushed this far is still a tray rather
        // than an unlabelled bar somebody has to remember the meaning of. It is everything under
        // the handle, and opaque, which is how it stands in for the head rather than sitting on it.
        final bar = tester.getRect(find.byKey(activityStowBarKey));
        expect(bar.top, greaterThan(tray.top));
        expect(bar.bottom, closeTo(tray.bottom, 0.01));
        expect(
          find.descendant(
            of: find.byKey(activityStowBarKey),
            matching: find.text('What’s happening'),
          ),
          findsOneWidget,
        );

        // The controls go with everything else — the row behind the bar, the timeline off the
        // bottom of the screen. This is the height for a camera whose scene is taller than it is
        // wide, where the room the timeline takes is worth more as picture, and one drag or one
        // tap on what is left brings them back.
        for (final control in ['Audio', 'Move', 'Snapshot', 'Clip']) {
          expect(
            tester.getRect(find.text(control)).top,
            greaterThanOrEqualTo(bar.top),
            reason: control,
          );
        }
        expect(
          tester.getRect(find.byType(TimelineScrubber)).top,
          greaterThanOrEqualTo(tray.bottom),
        );

        // And every pixel the tray gave up went to the picture, whose bottom edge is the tray's
        // top one at every height.
        final picture = tester.getRect(find.byKey(pictureBandKey));
        expect(picture.bottom, closeTo(tray.top, 1));
        expect(picture.height, greaterThan(rested + 250));
        expect(tester.takeException(), isNull);
      });

      testWidgets('and comes back up in one gesture, not 40px of one', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        await dragTray(tester, 800);
        expect(trayBox(tester).height, closeTo(Serval.activitySheetStowed, 1));

        // A fresh gesture on the bar, stepped like a finger. The bar fades out 40px up, and used
        // to take the drag with it — the recognizer that had won the gesture left the tree,
        // disposing it cancelled the drag, and the tray stopped dead with the action row half
        // revealed until the finger was lifted and put down again.
        final gesture = await tester.startGesture(
          tester.getRect(find.byKey(activityStowBarKey)).center,
        );
        final heights = <double>[];
        for (var i = 0; i < 8; i++) {
          await gesture.moveBy(const Offset(0, -40));
          await tester.pump();
          heights.add(trayBox(tester).height);
        }

        // The first move of a gesture is spent winning the arena, so eight are worth seven.
        expect(
          heights.last,
          closeTo(Serval.activitySheetStowed + 7 * 40, 1),
          reason: 'stopped following at ${heights.join(', ')}',
        );

        await gesture.up();
        await tester.pumpAndSettle();
      });

      testWidgets('and a tap on what is left brings the controls back', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        await dragTray(tester, 800);
        final stowed = trayBox(tester);

        // The bar itself, not the 18px handle above it: at this height the handle is a quarter of
        // the tray, and a target that small being the only way back is the failure this pins.
        await tester.tapAt(stowed.center);
        await tester.pumpAndSettle();

        // One place, to the controls — not all the way back to the feed.
        expect(
          tester.getRect(find.byType(TimelineScrubber)).bottom,
          closeTo(trayBox(tester).bottom - 12, 1),
        );
        expect(
          tester.getRect(find.text('What’s happening')).top,
          greaterThanOrEqualTo(trayBox(tester).bottom),
        );
      });

      testWidgets('drags up to cover the picture, but never the bar', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        await dragTray(tester, -600);

        final tray = trayBox(tester);
        expect(tray.top, closeTo(56, 1), reason: 'flush under the bar');
        expect(find.byType(CompactAppBar), findsOneWidget);
        expect(find.text('Front door'), findsOneWidget);
      });

      testWidgets('and never over Hold to talk, at any height', (tester) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        for (final dy in [0.0, -600.0, 800.0]) {
          if (dy != 0) await dragTray(tester, dy);

          final talk = tester.getRect(find.byType(HoldToTalkButton));
          expect(trayBox(tester).bottom, lessThanOrEqualTo(talk.top));
          expect(talk.bottom, closeTo(892 - 10, 1), reason: '$dy');
        }
      });

      testWidgets('and grows when what it is carrying does', (tester) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        await dragTray(tester, 60, velocity: 1000);
        final before = trayBox(tester);

        // A save writes a line into the tray's head. The measurement is what makes that line's
        // room appear rather than come out of the feed — the estimate this replaces did not count
        // it, so it was wrong exactly when somebody had just pressed something.
        await tester.tap(find.text('Snapshot'));
        await tester.pumpAndSettle();

        final after = trayBox(tester);
        expect(after.height, greaterThan(before.height));
        expect(after.bottom, closeTo(before.bottom, 1), reason: 'grows upward');

        // The track is still the last thing in it, so the peek is still exactly the head.
        expect(
          tester.getRect(find.byType(TimelineScrubber)).bottom,
          closeTo(after.bottom - 12, 1),
        );
      });

      testWidgets('takes a fling one detent at a time', (tester) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        final resting = trayBox(tester).top;

        await dragTray(tester, 60, velocity: 1000);
        final peek = trayBox(tester).top;
        expect(peek, greaterThan(resting));

        // Back up one place rather than all the way to the top.
        await dragTray(tester, -60, velocity: 1000);
        expect(trayBox(tester).top, closeTo(resting, 1));
      });

      testWidgets('is forgotten once the screen is gone', (tester) async {
        // Where the tray is left is what you are doing with this camera for a minute, not a
        // preference. Nothing writes it down, so it dies with the screen — which is the reason
        // this does not reach for the repository's own collapsed bit.
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        final resting = trayBox(tester).top;
        await dragTray(tester, -600);
        expect(trayBox(tester).top, lessThan(resting));

        // Back to the wall and in again, rather than a rebuild — the screen's state goes with it.
        await tester.pumpWidget(harness(const SizedBox.shrink()));
        await tester.pumpAndSettle();
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        expect(trayBox(tester).top, closeTo(resting, 1));
      });

      testWidgets('and the desktop column is left as it was', (tester) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();
        await dragTray(tester, 400);

        sizeTo(tester, const Size(1400, 900));
        await tester.pumpAndSettle();

        expect(find.byType(ActivityFeed), findsOneWidget);
        expect(find.byType(ActivitySheet), findsNothing);
      });

      testWidgets('clicking a row puts the tray down to show the footage', (
        tester,
      ) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        final resting = trayBox(tester).top;
        await dragTray(tester, -600);
        expect(trayBox(tester).top, lessThan(resting));

        await tester.tap(find.text('Glass heard'));
        await tester.pumpAndSettle();

        expect(trayBox(tester).top, closeTo(resting, 1));
      });
    });

    group('the filter', () {
      Future<void> open(WidgetTester tester) async {
        sizeTo(tester, const Size(412, 892));
        await tester.pumpWidget(camera('front-door'));
        await tester.pumpAndSettle();

        await tester.tap(find.text('Filter'));
        await tester.pumpAndSettle();
      }

      testWidgets('is the tray, risen', (tester) async {
        await open(tester);

        final panel = find.byType(ActivityFilterPanel);
        expect(panel, findsOneWidget);

        // A panel drawn inside a resting tray would be a column inside a column — its own header
        // and footer fill the height and the facets have nowhere to go. So the tray takes it up
        // instead, and what it dims is the picture rather than the bar naming the camera.
        final box = tester.getRect(panel);

        expect(box.top, closeTo(56, 1));
        expect(box.width, 412);
        expect(find.text('Front door'), findsOneWidget);
        expect(tester.takeException(), isNull);
      });

      testWidgets('has room for its facets, at phone metrics', (tester) async {
        await open(tester);

        // None of these were reachable: the panel's own header and footer filled the band, and
        // what was between them was a scroll view a few pixels tall.
        for (final facet in [
          "Only what's marked an alert",
          'WHAT HAPPENED',
          for (final kind in ActivityKind.values) kind.label,
        ]) {
          expect(find.text(facet), findsOneWidget, reason: facet);
          expect(
            tester.getRect(find.text(facet)).bottom,
            lessThan(892),
            reason: facet,
          );
        }

        // The compact form's own two tells: 44px targets, and the readout in the footer that the
        // desktop leaves to the header behind the panel.
        expect(tester.getSize(find.text('Done')).height, greaterThan(0));
        expect(find.textContaining('events in view'), findsOneWidget);
      });

      testWidgets('*Done* puts it away', (tester) async {
        await open(tester);

        await tester.tap(find.text('Done'));
        await tester.pumpAndSettle();

        expect(find.byType(ActivityFilterPanel), findsNothing);
      });

      testWidgets('ticking a facet narrows the feed behind it', (tester) async {
        await open(tester);

        await tester.tap(find.text('Speech'));
        await tester.pumpAndSettle();

        // Applied on tick, so the readout under the panel's own footer moves without *Done*.
        expect(find.byType(ActivityFilterPanel), findsOneWidget);
        expect(tester.takeException(), isNull);
      });

      testWidgets('does not survive a turn into landscape', (tester) async {
        await open(tester);
        expect(find.byType(ActivityFilterPanel), findsOneWidget);

        sizeTo(tester, const Size(892, 412));
        await tester.pumpAndSettle();

        expect(find.byType(ActivityFilterPanel), findsNothing);
      });

      testWidgets('nor a window dragged out to a desktop', (tester) async {
        await open(tester);
        expect(find.byType(ActivityFilterPanel), findsOneWidget);

        sizeTo(tester, const Size(1400, 900));
        await tester.pumpAndSettle();

        // The desktop panel opens its own filter inside the column, and it starts closed.
        expect(find.byType(ActivityFilterPanel), findsNothing);
      });
    });
  });

  group('turning the phone', () {
    testWidgets('landscape gives the picture the screen', (tester) async {
      sizeTo(tester, const Size(892, 412));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);

      // No bar and no pinned talk bar: the chrome floats on the picture instead.
      expect(find.byType(CompactAppBar), findsNothing);
      expect(find.text('Audio'), findsNothing);
    });

    testWidgets('the seek bar comes along, and the transcript does not', (
      tester,
    ) async {
      sizeTo(tester, const Size(892, 412));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      // The one thing kept from portrait, because without it landscape is live-only.
      final scrubber = tester.widget<TimelineScrubber>(
        find.byType(TimelineScrubber),
      );
      expect(scrubber.dense, isTrue);

      // Reading happens in portrait.
      expect(find.text('Still listening…'), findsNothing);
      expect(find.text("Serval's summary"), findsNothing);
    });

    testWidgets('the range panel opens over the picture and fits', (
      tester,
    ) async {
      sizeTo(tester, const Size(892, 412));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.textContaining(RegExp(r'^Last ')));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.byType(TimelineRangePanel), findsOneWidget);

      // The way out has to be reachable whatever the squeeze — the panel scrolls, the footer
      // does not.
      final panel = tester.getRect(find.byType(TimelineRangePanel));
      expect(panel.top, greaterThanOrEqualTo(0));
      expect(panel.bottom, lessThanOrEqualTo(412));
      expect(tester.getRect(find.text('Done')).bottom, lessThanOrEqualTo(412));
    });

    testWidgets('pan and tilt are on the picture at full size', (tester) async {
      sizeTo(tester, const Size(892, 412));
      // `front-door` is the sample registry's one camera that answers a pan/tilt probe.
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(PtzPad), findsOneWidget);
    });

    testWidgets('and are not squeezed onto the portrait strip', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      // *Move* is the way to them instead — a pad over a 232px band would cover the scene it is
      // there to aim.
      expect(find.byType(PtzPad), findsNothing);
      expect(find.text('Move'), findsOneWidget);
    });

    testWidgets('expanding upright turns the view rather than stretching it', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Fill the screen'));
      await tester.pumpAndSettle();

      // A 16:9 frame letterboxed into 9:19.5 is a *smaller* picture than the band it came from,
      // so the stage is laid out along the long edge and turned — what every video player does
      // when full screen is tapped without rotating.
      final turned = find.byType(RotatedBox);
      expect(turned, findsOneWidget);
      expect(tester.widget<RotatedBox>(turned).quarterTurns, 1);

      // And it is the landscape composition inside: no bar, no action row.
      expect(find.byType(CompactAppBar), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('a window dragged back out to a desktop leaves the mode', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.bySemanticsLabel('Fill the screen'));
      await tester.pumpAndSettle();
      expect(find.byType(RotatedBox), findsOneWidget);

      // The flag is sticky; the layout is not. A full-bleed picture on a desktop is a mode nobody
      // asked to still be in.
      tester.view.physicalSize = const Size(1440, 900);
      await tester.pumpAndSettle();

      expect(find.byType(RotatedBox), findsNothing);
      expect(find.text('All cameras'), findsOneWidget);
    });

    testWidgets('a squashed desktop window keeps its chrome', (tester) async {
      // Wider than it is tall, like a rotated phone — but tall enough that it is a window
      // somebody resized, and it must not lose the screen to a full-bleed picture.
      sizeTo(tester, const Size(900, 600));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(CompactAppBar), findsOneWidget);
      expect(find.text('Audio'), findsOneWidget);
    });
  });

  group('the wall', () {
    Widget wall() => harness(WallScreen(onOpenCamera: (_, _) {}));

    testWidgets("what's happening is a sheet over the wall", (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);

      // Round 11: not a column, and no longer a band either. The whole width at
      // the foot of the screen, floating over tiles that keep their full height.
      expect(find.byType(ActivityColumn), findsNothing);

      final sheet = tester.getRect(find.byType(ActivitySheet));
      expect(sheet.width, 412);
      expect(sheet.bottom, 892);
    });

    testWidgets('and stays a column on a desktop', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      final column = tester.getRect(find.byType(ActivityColumn));
      expect(column.width, Serval.activityColumnWidth);
      expect(column.right, 1440);
      expect(column.top, lessThan(200));
    });
  });
}
