import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/screens/cameras_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/widgets/nocturne_slider.dart';
import 'package:serval_app/widgets/settings_cards.dart';
import 'package:serval_app/widgets/status_indicators.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// The sample content reduced to one camera that keeps nothing, so the registry screen has to
/// render the state rather than the four ordinary cameras around it.
class _WatchOnlyRepository extends SampleServalRepository {
  const _WatchOnlyRepository();

  static const _record = CameraRecord(
    id: 'watchonly',
    name: 'Watch Only',
    location: 'Front yard',
    recording: false,
    streams: [
      CameraStreamRecord(
        name: 'main',
        url: 'rtsp://192.168.1.9:554/main',
        roles: [StreamRole.detect, StreamRole.live],
      ),
    ],
  );

  /// Frames arriving, nothing written — which is the whole state under test.
  static const _view = Camera(
    id: 'watchonly',
    name: 'Watch Only',
    connection: CameraConnection.online,
    isRecording: false,
    records: false,
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF1E2131),
      stripeDark: Color(0xFF181B28),
      bloom: Color(0x129184D9),
    ),
  );

  @override
  List<CameraRecord> cameraRecords() => const [_record];

  @override
  CameraRecord? cameraRecordById(String id) =>
      id == _record.id ? _record : null;

  @override
  List<Camera> cameras() => const [_view];

  @override
  Camera? cameraById(String id) => id == _view.id ? _view : null;
}

/// A camera that keeps nothing, which the Server accepts.
///
/// The screens all have somewhere they would otherwise claim footage — a retention slider, a
/// projection in gigabytes, a bar inviting a drag back into the day. None of those is *wrong*
/// exactly; they are all just describing a camera that is writing, and this one is not. An empty
/// timeline under a live "Drag back to replay today" is the failure worth pinning: it reads as a
/// quiet day, and someone loses an afternoon hunting footage that was never written.
///
/// There are two ways to get here and both are exercised: no stream set to Recording, and the
/// *Keeping footage* switch off over a stream that is. Everything below `CameraRecord.records`
/// treats them identically, which is exactly what makes it worth pinning that the one place
/// distinguishing them — the section holding the switch — actually does.
void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    // Taller than a real screen, so the whole form lays out and nothing needs scrolling to.
    view.physicalSize = const Size(1440, 4000);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  CameraRecord recording() => CameraRecord.blank().copyWith(
    id: 'testcam',
    name: 'Test',
    streams: [
      CameraRecord.blank().streams.single.copyWith(
        url: 'rtsp://127.0.0.1:1/main',
      ),
    ],
  );

  /// The same camera with Recording unticked — one stream, watching and viewable, keeping nothing.
  CameraRecord notRecorded() {
    final base = recording();
    return base.copyWith(
      recording: false,
      streams: [
        base.streams.single.copyWith(
          roles: const [StreamRole.detect, StreamRole.live],
        ),
      ],
    );
  }

  /// The other way to keep nothing: the stream still set to Recording, the switch off. Read the
  /// same way everywhere except in the section holding the switch.
  CameraRecord switchedOff() => recording().copyWith(recording: false);

  Future<void> pumpForm(WidgetTester tester, CameraRecord record) async {
    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        home: Scaffold(
          body: CameraSettingsForm(
            record: record,
            creating: false,
            health: CameraHealth.healthy,
            knownLocations: const [],
            existingIds: const {},
            onSave: (_) async {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('the settings form', () {
    testWidgets('lays out a camera that keeps nothing', (tester) async {
      await pumpForm(tester, notRecorded());

      expect(tester.takeException(), isNull);
    });

    testWidgets('has nothing to complain about', (tester) async {
      // The whole point: a camera that records nothing is valid. The failure is a save bar reading
      // "No stream is set to “Recording”." with a dead button and nothing saying why.
      await pumpForm(tester, notRecorded());

      expect(find.text('No unsaved changes'), findsOneWidget);
      expect(find.textContaining('set to “Recording”'), findsNothing);
    });

    testWidgets('says why Keeping footage is inert', (tester) async {
      await pumpForm(tester, notRecorded());

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      expect(find.textContaining('Nothing is written to disk'), findsOneWidget);
    });

    testWidgets('leaves Keeping footage alone for a camera that records', (
      tester,
    ) async {
      await pumpForm(tester, recording());

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      expect(find.textContaining('Nothing is written to disk'), findsNothing);
    });

    /// The switch has nothing to act on, and the fix is in another section — so it says which one
    /// rather than sitting there dead. Offering it anyway would mean guessing which stream to hand
    /// the role to, which is the guess this whole design avoids.
    testWidgets('cannot switch recording on with no stream to write', (
      tester,
    ) async {
      await pumpForm(tester, notRecorded());

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      expect(
        find.textContaining('No stream is set to Recording'),
        findsOneWidget,
      );
    });

    testWidgets('names the stream it would write, with recording off', (
      tester,
    ) async {
      // The point of the flag over a role edit: the assignment survives, so the way back on is a
      // switch rather than a decision.
      await pumpForm(tester, switchedOff());

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      expect(
        find.textContaining('“main” stays set to Recording'),
        findsOneWidget,
      );
    });

    testWidgets('a camera switched off still has a live retention slider', (
      tester,
    ) async {
      // Retention expires what is on disk whether or not anything is being added to it, so this
      // is still the dial for the footage the camera is holding.
      await pumpForm(tester, switchedOff());

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      final slider = tester.widget<NocturneSlider>(
        find.byType(NocturneSlider).first,
      );
      expect(slider.onChanged, isNotNull);
    });
  });

  group('the registry list', () {
    Widget harness() => ProviderScope(
      overrides: [
        repositoryProvider.overrideWithValue(const _WatchOnlyRepository()),
      ],
      child: MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        home: const Scaffold(body: CamerasScreen()),
      ),
    );

    testWidgets('calls a camera that keeps nothing healthy, not starting up', (
      tester,
    ) async {
      // The regression: health read off "is it recording" is the same question as "are frames
      // arriving" only while every camera records. Asked of a camera that keeps nothing it reads
      // "Starting up" forever — a permanent amber dot on a camera working exactly as configured.
      await tester.pumpWidget(harness());
      await tester.pumpAndSettle();

      expect(find.text('Watching · not kept'), findsOneWidget);
      expect(find.text('Starting up'), findsNothing);

      // The camera editor beside the list has a section called *Recording* now, so a bare text
      // finder is no longer a question about the row. Scoped instead: the only *Recording* on
      // screen is the section index's, and none of it is this camera's subtitle claiming a job it
      // is not doing.
      expect(find.text('Recording'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(SettingsGroupList),
          matching: find.text('Recording'),
        ),
        findsOneWidget,
      );
    });
  });

  group('the timeline', () {
    final from = DateTime(2026, 7, 24, 4, 0);
    final to = DateTime(2026, 7, 24, 16, 0);

    /// An empty window, which is what `/coverage` answers for a camera that records nothing.
    final window = TimelineWindow(
      from: from,
      to: to,
      coverage: const [],
      marks: const [],
    );

    Future<void> pumpScrubber(
      WidgetTester tester, {
      required bool records,
      ValueChanged<DateTime>? onSeek,
    }) async {
      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 1000,
              child: TimelineScrubber(
                window: window,
                range: TimelineRange.halfDay,
                records: records,
                onSeek: onSeek,
              ),
            ),
          ),
        ),
      );
      await tester.pump();
    }

    testWidgets('says nothing is kept instead of inviting a drag', (
      tester,
    ) async {
      await pumpScrubber(tester, records: false);

      expect(find.textContaining('Nothing is kept'), findsOneWidget);
      expect(find.text('Drag back to replay today'), findsNothing);
    });

    testWidgets('still invites a drag when the camera records', (tester) async {
      await pumpScrubber(tester, records: true);

      expect(find.text('Drag back to replay today'), findsOneWidget);
    });

    testWidgets('will not seek a camera that has nothing to seek to', (
      tester,
    ) async {
      DateTime? seeked;
      await pumpScrubber(tester, records: false, onSeek: (at) => seeked = at);

      await tester.tapAt(tester.getRect(find.byType(TimelineScrubber)).center);
      await tester.pump();

      expect(seeked, isNull);
    });
  });
}
