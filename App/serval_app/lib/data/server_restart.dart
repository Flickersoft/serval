import 'serval_repository.dart';

/// Restarts the Server and waits for it to answer again. Null when it is back, otherwise what to
/// tell the person.
///
/// The wait is a plain retry of a settings read: a restarting container refuses connections for a
/// few seconds, and loading a scene model can take far longer, so it keeps asking for two minutes
/// before giving up. Shared by every page that offers *Restart now*, so they cannot disagree about
/// how long a restart is allowed to take.
Future<String?> restartServerAndWait(ServalRepository repository) async {
  await repository.restartServer();

  // The Server stops a second after accepting; asking sooner would reach the old process.
  await Future<void>.delayed(const Duration(seconds: 3));

  final deadline = DateTime.now().add(const Duration(minutes: 2));
  while (true) {
    try {
      await repository.settings();
      return null;
    } on Object {
      if (DateTime.now().isAfter(deadline)) {
        return 'The Server has not come back after two minutes. If it was not started with a '
            'restart policy, start it again by hand.';
      }
      await Future<void>.delayed(const Duration(seconds: 2));
    }
  }
}
