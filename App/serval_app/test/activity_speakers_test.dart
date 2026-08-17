// Which voice a row is attributed to, and when a row says so at all.
//
// Worth its own file because the wrong answer here is silent and specific: it puts somebody else's
// number beside your words, or promises a second voice and then draws one bubble. The rule it is
// pinning — a bubble only where there was more than one voice to tell apart — is the whole reason
// the field is nullable rather than always set.
//
// Nothing here reaches the network: the repository is constructed but never started, and documents
// go in through the same `seedForTest` seam the rest of the feed suite uses.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/activity.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
  final now = DateTime(2026, 8, 5, 18);

  UtteranceDocument said(
    String id, {
    required String? speaker,
    String? conversationId = 'c-1',
    String transcript = 'hello',
    String? emotion,
    int minutesAgo = 1,
  }) => UtteranceDocument(
    cameraId: 'cam1',
    source: 'module',
    when: now.subtract(Duration(minutes: minutesAgo)),
    id: id,
    conversationId: conversationId,
    transcript: transcript,
    speaker: speaker,
    emotion: emotion,
    audioEvent: null,
    durationSeconds: 2,
  );

  ConversationTranscriptDocument settled({
    String conversationId = 'c-1',
    required int speakerCount,
    required List<ConversationTurn> turns,
  }) => ConversationTranscriptDocument(
    cameraId: 'cam1',
    source: 'module',
    when: now.subtract(const Duration(minutes: 5)),
    conversationId: conversationId,
    text: turns.map((t) => t.text).join(' '),
    turns: turns,
    speakerCount: speakerCount,
  );

  ConversationTurn turn(int speaker, String text, {String? emotion}) =>
      ConversationTurn(
        start: speaker.toDouble(),
        end: speaker + 1.0,
        speaker: speaker,
        text: text,
        emotion: emotion,
      );

  LiveServalRepository seeded(List<TelemetryDocument> documents) {
    final auth = AuthController(config: config);
    final repository = LiveServalRepository(auth: auth, config: config)
      ..seedForTest(order: const ['cam1'], documents: documents);
    addTearDown(() {
      repository.dispose();
      auth.dispose();
    });
    return repository;
  }

  group('a live utterance', () {
    test('one voice draws no bubble', () {
      // A permanent ① beside a monologue is a distinction with nothing on the
      // other side of it.
      final repository = seeded([
        said('u1', speaker: 'speaker_0'),
        said('u2', speaker: 'speaker_0', minutesAgo: 2),
      ]);

      final items = repository.activityFor();
      expect(items, hasLength(2));
      expect(items.every((i) => i.speakerNumber == null), isTrue);
    });

    test('a second voice gives the first one a bubble too', () {
      // Retroactive on purpose: a row is attributed by what the conversation
      // turned out to be, not by what was known when it arrived. The moment a
      // second voice lands, the earlier rows have someone to be told apart from.
      final repository = seeded([
        said('u1', speaker: 'speaker_0', minutesAgo: 2),
        said('u2', speaker: 'speaker_1'),
      ]);

      final numbers = {
        for (final item in repository.activityFor())
          item.id: item.speakerNumber,
      };

      expect(numbers['utterance:u1'], 1);
      expect(numbers['utterance:u2'], 2);
    });

    test('the wire counts from zero and the screen counts from one', () {
      final repository = seeded([
        said('u1', speaker: 'speaker_0'),
        said('u2', speaker: 'speaker_1'),
      ]);

      final numbers = repository
          .activityFor()
          .map((i) => i.speakerNumber)
          .toSet();

      // Never a 0: `speaker_0` is an index, and ① is for a person to read.
      expect(numbers, {1, 2});
    });

    test('no conversation means no bubble, however many voices are about', () {
      // There is no scope in which to count, so there is nothing to number
      // against — even with a busy multi-voice conversation alongside it.
      final repository = seeded([
        said('u1', speaker: 'speaker_0'),
        said('u2', speaker: 'speaker_1'),
        said('lone', speaker: 'speaker_0', conversationId: null),
      ]);

      final lone = repository.activityFor().firstWhere(
        (i) => i.id == 'utterance:lone',
      );

      expect(lone.speakerNumber, isNull);
    });

    test('a label that is not speaker_N draws nothing rather than guessing', () {
      // `Speaker 1` is already 1-based, so reading a trailing digit would draw
      // ② for the first voice. A shape the contract does not define is refused.
      final repository = seeded([
        said('u1', speaker: 'Speaker 1'),
        said('u2', speaker: 'speaker_1'),
      ]);

      final numbers = {
        for (final item in repository.activityFor())
          item.id: item.speakerNumber,
      };

      expect(numbers['utterance:u1'], isNull);

      // And it did not count towards the conversation either: one readable
      // voice is still one voice, so the readable row draws nothing either.
      expect(numbers['utterance:u2'], isNull);
    });

    test('an utterance with no speaker at all draws nothing', () {
      final repository = seeded([
        said('u1', speaker: null),
        said('u2', speaker: 'speaker_1'),
      ]);

      final u1 = repository.activityFor().firstWhere(
        (i) => i.id == 'utterance:u1',
      );

      expect(u1.speakerNumber, isNull);
    });
  });

  group('a settled conversation', () {
    test('is one row whose turns carry the numbers', () {
      // The design's "a settled conversation is one row" is unchanged. What
      // changed is that the row now shows its turns, so the attribution does
      // not vanish the moment people stop talking.
      final repository = seeded([
        settled(
          speakerCount: 2,
          turns: [
            turn(0, 'Hello?'),
            turn(0, 'Anyone in?'),
            turn(1, 'Round the back.'),
          ],
        ),
      ]);

      final item = repository.activityFor().single;

      expect(item.turns.map((t) => t.speakerNumber), [1, 1, 2]);
      expect(item.turns.first.text, '“Hello?”');
      // The flowing text stays as the fallback for a transcript with no turns.
      expect(item.text, isNotEmpty);
    });

    test('one speaker numbers nothing', () {
      final repository = seeded([
        settled(speakerCount: 1, turns: [turn(0, 'Just me out here.')]),
      ]);

      final item = repository.activityFor().single;

      expect(item.turns.single.speakerNumber, isNull);
      // And the heading says where, not how many — a one-voice conversation
      // reads the same as a lone utterance, because that is what it is.
      expect(item.speaker, 'At the camera');
    });

    test('a blank turn is dropped rather than drawn empty', () {
      final repository = seeded([
        settled(speakerCount: 2, turns: [turn(0, 'Hello?'), turn(1, '   ')]),
      ]);

      expect(repository.activityFor().single.turns, hasLength(1));
    });

    test('its own utterances still do not get rows of their own', () {
      // The pre-existing rule, re-pinned because the voice count is now built
      // from those same utterances and they have to survive into the pool for
      // that to work — without ever becoming rows.
      final repository = seeded([
        said('u1', speaker: 'speaker_0'),
        said('u2', speaker: 'speaker_1'),
        settled(speakerCount: 2, turns: [turn(0, 'Hello?'), turn(1, 'Hi.')]),
      ]);

      final items = repository.activityFor();

      expect(items, hasLength(1));
      expect(items.single.kind, TelemetryKind.conversationTranscript);
    });
  });

  test('the heading never prints the wire label again', () {
    // The regression guard for the bug that started this: `speaker_0` was
    // being rendered to people verbatim on the camera panel.
    final repository = seeded([
      said('u1', speaker: 'speaker_0'),
      said('u2', speaker: 'speaker_1'),
      settled(
        conversationId: 'c-2',
        speakerCount: 2,
        turns: [turn(0, 'Hello?'), turn(1, 'Hi.')],
      ),
    ]);

    for (final item in repository.activityFor()) {
      expect(item.speaker, isNot(matches(RegExp(r'^speaker_'))));
    }
  });
}
