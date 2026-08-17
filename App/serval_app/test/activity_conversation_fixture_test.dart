// The feed, over documents a real pipeline actually emitted.
//
// Every other test in this suite hands the feed documents written by hand to match what the test
// expects. Those pin the rules well and cannot catch one thing: a document shape the app parses
// happily and the Server never sends, or the reverse. Hand-written fixtures agree with the parser
// by construction and with the pipeline only by luck.
//
// `fixtures/multi_speaker_conversation.json` closes that gap. It is the verbatim output of
// `ConversationOverFixtureTests` run over ninety seconds of a four-person meeting — twelve live
// utterances, the diarization, and the settled transcript, all sharing one conversation id. To
// regenerate it after a pipeline change:
//
//     SERVAL_MODELS=~/serval-local/models \
//     SERVAL_TRANSCRIPT_GOLDEN_OUT=$PWD/test/fixtures/multi_speaker_conversation.json \
//       dotnet test Shared/Serval.Ai.Tests --filter Capturing
//
// Nothing here asserts a speaker *count* of its own. The fixture declares one and the assertions
// read it, so re-baking after a diarization change is a one-command job rather than an edit. What
// is pinned is the rendering: that whatever the pipeline said arrives on screen intact.
import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/widgets/activity_column.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));

  final raw =
      jsonDecode(
            File(
              'test/fixtures/multi_speaker_conversation.json',
            ).readAsStringSync(),
          )
          as List<dynamic>;

  final documents = [
    for (final entry in raw.cast<Map<String, dynamic>>())
      parseTelemetryDocument(entry['type'] as String, entry)!,
  ];

  final transcript = documents
      .whereType<ConversationTranscriptDocument>()
      .single;
  final utterances = documents.whereType<UtteranceDocument>().toList();
  final cameraId = transcript.cameraId;

  LiveServalRepository seeded() {
    final auth = AuthController(config: config);
    final repository = LiveServalRepository(auth: auth, config: config)
      ..seedForTest(order: [cameraId], documents: documents);
    addTearDown(() {
      repository.dispose();
      auth.dispose();
    });
    return repository;
  }

  Future<void> pump(WidgetTester tester, List<ActivityItem> items) =>
      tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 376,
              height: 1400,
              child: ActivityFeed(items: items, showCamera: false),
            ),
          ),
        ),
      );

  group('the fixture itself', () {
    test('is a whole conversation, not a handful of rows', () {
      // The claims below are only worth making over a capture that still has both halves in it. A
      // fixture regenerated from a run that produced no transcript would leave every test here
      // trivially green.
      expect(utterances, isNotEmpty);
      expect(transcript.turns, isNotEmpty);
      expect(transcript.speakerCount, greaterThan(1));
      expect(
        utterances.map((u) => u.conversationId).toSet(),
        {transcript.conversationId},
        reason:
            'the utterances and the transcript must describe one conversation',
      );
    });
  });

  group('a settled conversation from the real pipeline', () {
    test('replaces its own raw utterances rather than doubling them', () {
      // The drop rule in `_ConversationIndex`. This is the one claim that genuinely needs paired
      // documents: a hand-written fixture pairs them by assumption, and the failure it guards —
      // every line on screen twice, once live and once settled — only appears when a real
      // conversation's own utterances are present alongside the transcript that superseded them.
      final items = seeded().activityFor();
      final ids = items.map((i) => i.id).toSet();

      expect(
        ids,
        contains('conversation_transcript:${transcript.conversationId}'),
      );

      for (final utterance in utterances) {
        expect(
          ids,
          isNot(contains('utterance:${utterance.id}')),
          reason:
              'utterance ${utterance.id} survived the transcript that settled it',
        );
      }
    });

    test('the diarization record never reaches the feed', () {
      // Superseded by the transcript, which carries the same segmentation plus the words.
      final items = seeded().activityFor();
      expect(items.any((i) => i.kind == TelemetryKind.diarization), isFalse);
    });

    test('says how many voices it heard, not which', () {
      final row = seeded().activityFor().single;

      expect(row.speaker, '${transcript.speakerCount} speakers');
      expect(row.turns, hasLength(transcript.turns.length));
    });

    test('numbers every turn from one', () {
      final row = seeded().activityFor().single;

      final numbers = row.turns.map((t) => t.speakerNumber).toSet();
      final expected = transcript.turns.map((t) => t.speaker + 1).toSet();

      expect(numbers, expected);
      expect(numbers, isNot(contains(0)));
    });
  });

  group('on screen', () {
    testWidgets('every turn the pipeline produced is drawn', (tester) async {
      final items = seeded().activityFor();
      await pump(tester, items);

      // The words, verbatim. `_turnsOf` wraps each turn in quotes, so the fixture's text is looked
      // for inside the rendered string rather than as the whole of it.
      for (final turn in transcript.turns) {
        expect(
          find.textContaining(turn.text),
          findsAtLeastNWidgets(1),
          reason: 'the turn "${turn.text}" is not on screen',
        );
      }
    });

    testWidgets('each voice gets its own bubble', (tester) async {
      final items = seeded().activityFor();
      await pump(tester, items);

      for (final number in transcript.turns.map((t) => t.speaker + 1).toSet()) {
        expect(
          find.text('$number'),
          findsAtLeastNWidgets(1),
          reason: 'no bubble was drawn for voice $number',
        );
      }
    });

    testWidgets('the heading counts the voices', (tester) async {
      final items = seeded().activityFor();
      await pump(tester, items);

      expect(find.text('${transcript.speakerCount} speakers'), findsOneWidget);
    });
  });
}
