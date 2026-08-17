// How a row comes to wear a face, and — mostly — how it comes not to.
//
// The omissions carry the weight here. The analyzer emits nine words and only six are drawable:
// `neutral` would sit on nearly every speech row, and `emo_unknown` / `other` are the model saying
// it could not tell. All three, an absent field, and any word a later schema invents collapse to
// nothing, because a face beside a sentence is a claim and those three make none.
//
// Note what this file does *not* test: aligning utterances onto turns. That join happens on the
// module, where the VAD's minimum-silence setting lives — an utterance's timestamp is when the VAD
// emitted it, after the speech plus the silence it waited through, so this side has neither the
// offset nor the direction. The App reads a field the Server already resolved; the choosing is
// pinned in `Serval.Ai.Tests/ConversationReprocessorTests.cs`.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/activity.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
  final now = DateTime(2026, 8, 5, 18);

  UtteranceDocument said(String id, {String? emotion}) => UtteranceDocument(
    cameraId: 'cam1',
    source: 'module',
    when: now.subtract(const Duration(minutes: 1)),
    id: id,
    conversationId: null,
    transcript: 'something was said',
    speaker: null,
    emotion: emotion,
    audioEvent: null,
    durationSeconds: 2,
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

  group('reading the wire', () {
    test('the six expressive words each become a face', () {
      const expected = {
        'happy': ActivityEmotion.happy,
        'sad': ActivityEmotion.sad,
        'angry': ActivityEmotion.angry,
        'fearful': ActivityEmotion.fearful,
        'disgusted': ActivityEmotion.disgusted,
        'surprised': ActivityEmotion.surprised,
      };

      for (final (wire, emotion) in expected.entries.map(
        (e) => (e.key, e.value),
      )) {
        expect(ActivityEmotion.fromWire(wire), emotion, reason: wire);
      }
    });

    test('the words that mean nothing draw nothing', () {
      // `neutral` is the one that matters. It is by far the commonest reading,
      // so drawing it would put a glyph on nearly every speech row and make the
      // ordinary case the noisy one — while claiming a reading the model did
      // not really make.
      for (final wire in ['neutral', 'emo_unknown', 'other', '', 'wistful']) {
        expect(ActivityEmotion.fromWire(wire), isNull, reason: wire);
      }

      expect(ActivityEmotion.fromWire(null), isNull);
    });

    test('an absent reading and a neutral one are the same to a reader', () {
      // Both draw nothing, which is the point: there is no visual difference
      // between "we could not say" and "nothing to say", and inventing one
      // would be a distinction the audio does not support.
      expect(
        ActivityEmotion.fromWire('neutral'),
        ActivityEmotion.fromWire(null),
      );
    });
  });

  group('on a row', () {
    test('a live utterance wears its own emotion', () {
      final repository = seeded([said('u1', emotion: 'angry')]);

      expect(repository.activityFor().single.emotion, ActivityEmotion.angry);
    });

    test('an utterance the analyzer could not read wears nothing', () {
      final repository = seeded([said('u1', emotion: null)]);

      expect(repository.activityFor().single.emotion, isNull);
    });

    test('a turn wears the emotion the Server attributed to it', () {
      final repository = seeded([
        ConversationTranscriptDocument(
          cameraId: 'cam1',
          source: 'module',
          when: now.subtract(const Duration(minutes: 5)),
          conversationId: 'c-1',
          text: 'Get off my property. I am going.',
          turns: const [
            ConversationTurn(
              start: 0,
              end: 2,
              speaker: 0,
              text: 'Get off my property.',
              emotion: 'angry',
            ),
            ConversationTurn(
              start: 2,
              end: 4,
              speaker: 1,
              text: 'I am going.',
              emotion: 'fearful',
            ),
          ],
          speakerCount: 2,
        ),
      ]);

      final turns = repository.activityFor().single.turns;

      // Two speakers, two different feelings — which is exactly what a join on
      // this side could not have produced, since one VAD utterance holding both
      // voices has a single reading over the pair of them.
      expect(turns.map((t) => t.emotion), [
        ActivityEmotion.angry,
        ActivityEmotion.fearful,
      ]);
    });

    test('a settled conversation has no face of its own', () {
      // There is no whole-conversation feeling. Averaging its turns into one
      // would be an invention, and the turns already say it better.
      final repository = seeded([
        ConversationTranscriptDocument(
          cameraId: 'cam1',
          source: 'module',
          when: now.subtract(const Duration(minutes: 5)),
          conversationId: 'c-1',
          text: 'Hello. Hi.',
          turns: const [
            ConversationTurn(
              start: 0,
              end: 1,
              speaker: 0,
              text: 'Hello.',
              emotion: 'happy',
            ),
            ConversationTurn(
              start: 1,
              end: 2,
              speaker: 1,
              text: 'Hi.',
              emotion: 'sad',
            ),
          ],
          speakerCount: 2,
        ),
      ]);

      expect(repository.activityFor().single.emotion, isNull);
    });

    test('a conversation reprocessed before the field existed reads null', () {
      // Old documents have no `emotion` on their turns and never will. They
      // degrade to no face rather than to a wrong one.
      final repository = seeded([
        ConversationTranscriptDocument(
          cameraId: 'cam1',
          source: 'module',
          when: now.subtract(const Duration(minutes: 5)),
          conversationId: 'c-1',
          text: 'Hello.',
          turns: const [
            ConversationTurn(
              start: 0,
              end: 1,
              speaker: 0,
              text: 'Hello.',
              emotion: null,
            ),
          ],
          speakerCount: 1,
        ),
      ]);

      final item = repository.activityFor().single;

      expect(item.turns.single.emotion, isNull);
      // And the words are still there — a missing face costs nothing else.
      expect(item.turns.single.text, '“Hello.”');
    });
  });
}
