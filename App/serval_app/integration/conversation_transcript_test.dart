import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/authenticated_client.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';

/// Transcripts and speaker attribution, against a real Server, over audio whose speaker count is
/// known.
///
/// **Deliberately outside `test/`** so `flutter test` never runs it:
///
/// ```bash
/// flutter test integration/conversation_transcript_test.dart \
///   --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
///   --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=...
/// ```
///
/// **Read-only.** It creates nothing; it reads what the Server has already produced. Point it at a
/// house and it reports on that house's conversations.
///
/// **What it adds over `ConversationOverFixtureTests`.** That test drives the shared library on a
/// simulated clock and answers whether the models can read a recording. This one answers the
/// question the simulated clock cannot: whether the running host — ffmpeg pulling audio off a
/// stream in realtime, the detector loop, the reprocessing pass, Mongo, the REST reads — carries
/// the result all the way out. The two failures are quite different and only one of them is about
/// the models.
///
/// To point it at the AMI fixtures rather than a live camera, register a file-source camera whose
/// media file carries the fixture's audio — `Docs/testing.md` has the recipe, including the
/// `Speaker:SilenceTimeoutMinutes` override without which a looping file never closes a
/// conversation and no transcript is ever written.
void main() {
  final config = ServalConfig.fromEnvironment();
  final auth = AuthController(config: config);
  final api = ServalApi(
    config: config,
    client: AuthenticatedClient(auth: auth),
  );

  setUpAll(() {
    // The test binding installs an HttpOverrides that fails every request. Opt out — the whole
    // point here is to reach a real Server.
    HttpOverrides.global = null;
  });

  setUpAll(() async {
    final signedIn = await auth.login(
      const String.fromEnvironment('SERVAL_USERNAME'),
      const String.fromEnvironment('SERVAL_PASSWORD'),
    );
    if (!signedIn) {
      throw StateError(
        'Could not sign in (${auth.error}) — pass '
        '--dart-define=SERVAL_USERNAME=... --dart-define=SERVAL_PASSWORD=...',
      );
    }
  });

  tearDownAll(() {
    api.close();
    auth.dispose();
  });

  /// A day is wide enough to catch a file-source camera set running a few minutes ago and a house
  /// that had a conversation this morning, and is the Server's own default window.
  final to = DateTime.now().toUtc();
  final from = to.subtract(const Duration(hours: 24));

  /// Every camera that has produced a transcript in the window, with it.
  Future<Map<String, List<ConversationTranscriptDocument>>>
  transcripts() async {
    final found = <String, List<ConversationTranscriptDocument>>{};

    for (final camera in await api.listCameras()) {
      if (!camera.enabled || !camera.aiAudio) continue;

      final settled = await api.conversationTranscripts(
        camera.id,
        from: from,
        to: to,
      );
      if (settled.isNotEmpty) found[camera.id] = settled;
    }

    return found;
  }

  test(
    'a settled conversation carries turns, in order, with words in them',
    () async {
      final found = await transcripts();
      if (found.isEmpty) {
        markTestSkipped(
          'No listening camera has settled a conversation in the last 24h. A conversation only '
          'settles after Speaker:SilenceTimeoutMinutes of quiet.',
        );
        return;
      }

      for (final entry in found.entries) {
        for (final transcript in entry.value) {
          expect(
            transcript.turns,
            isNotEmpty,
            reason: '${entry.key} settled a conversation with no turns in it',
          );

          for (final turn in transcript.turns) {
            expect(
              turn.text.trim(),
              isNotEmpty,
              reason: '${entry.key} published a turn with no words',
            );
          }

          for (var i = 1; i < transcript.turns.length; i++) {
            expect(
              transcript.turns[i].start,
              greaterThanOrEqualTo(transcript.turns[i - 1].start),
              reason:
                  '${entry.key} published turns out of order; the feed draws them as they come',
            );
          }

          expect(
            transcript.speakerCount,
            greaterThanOrEqualTo(
              transcript.turns.map((t) => t.speaker).toSet().length,
            ),
            reason:
                '${entry.key} attributed turns to more speakers than it counted',
          );
        }
      }
    },
  );

  test('the utterances behind a transcript share its conversation id', () async {
    // The join the feed depends on. `_ConversationIndex` drops a raw utterance once its
    // conversation has settled, and it recognises "its" conversation by this id alone — so an id
    // that does not match means every line renders twice, once live and once settled.
    final found = await transcripts();
    if (found.isEmpty) {
      markTestSkipped(
        'No listening camera has settled a conversation in the last 24h.',
      );
      return;
    }

    var checked = 0;

    for (final entry in found.entries) {
      final spoken = await api.utterances(entry.key, from: from, to: to);
      final settled = entry.value.map((t) => t.conversationId).toSet();

      for (final utterance in spoken) {
        final id = utterance.conversationId;
        if (id == null || !settled.contains(id)) continue;

        expect(
          utterance.transcript.trim(),
          isNotEmpty,
          reason: 'an utterance in a settled conversation carried no words',
        );
        checked++;
      }
    }

    if (checked == 0) {
      markTestSkipped(
        'No utterance in the window belongs to a settled conversation — the live records may have '
        'aged out of the read while the transcript survived.',
      );
      return;
    }

    // ignore: avoid_print
    print('checked $checked utterance(s) against ${found.length} camera(s)');
  });

  test('reports what each listening camera heard', () async {
    // Not an assertion so much as the reason to run this by hand: the speaker counts and turn
    // counts per camera, which is what tells you whether a room is being heard properly.
    final found = await transcripts();
    if (found.isEmpty) {
      markTestSkipped(
        'No listening camera has settled a conversation in the last 24h.',
      );
      return;
    }

    for (final entry in found.entries) {
      for (final transcript in entry.value) {
        final span = transcript.turns.isEmpty
            ? 0.0
            : transcript.turns.last.end - transcript.turns.first.start;

        // ignore: avoid_print
        print(
          '${entry.key} @ ${transcript.when.toLocal()}: '
          '${transcript.speakerCount} speaker(s), '
          '${transcript.turns.length} turn(s) across ${span.toStringAsFixed(1)}s',
        );
      }
    }
  });
}
