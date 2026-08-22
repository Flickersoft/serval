/// Everywhere that is not a browser: no Cast SDK, so no receivers and no button.
///
/// Empty streams rather than `Stream.value(false)` — the UI derives the button's presence from
/// what these produce, and a stream that never produces is exactly "there is nothing here".
Future<void> initialise(String appId) async {}

Stream<bool> get available => const Stream<bool>.empty();

Stream<bool> get casting => const Stream<bool>.empty();

Future<String?> cast(
  String appId,
  Uri url, {
  required String title,
  required bool live,
  Duration startAt = Duration.zero,
}) async => 'Casting is only available in the browser.';

Future<void> seek(Duration position) async {}

Future<void> stop() async {}
