import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

/// The browser half of [CastSender], talking to `web/cast.js`.
///
/// **This polls JavaScript rather than taking callbacks from it, deliberately.** A callback from
/// JavaScript into Dart is the direction that kept breaking: dart2js binds arguments and checks
/// types *before* the body runs, so an SDK that calls back with one argument where two are
/// documented throws inside Google's own code, with no Dart frame in the stack to find it by. A
/// poll costs one boolean read a second and cannot fail that way.
const _pollInterval = Duration(seconds: 1);

/// How long to watch the error slot after a launch, and how often.
///
/// Not a single wait, because the two failures arrive at different times: a synchronous refusal —
/// no SDK, an application id the device cannot run — is recorded immediately, while a load that
/// the receiver drops is only given up on after the sender has retried it. Reading once at two
/// seconds caught the first and silently missed the second.
const _errorWindow = Duration(seconds: 7);
const _errorPoll = Duration(milliseconds: 500);

@JS()
extension type _ServalCast._(JSObject _) implements JSObject {
  external void initialise(String appId);
  external bool available();
  external bool casting();
  external String takeError();
  external void start(
    String appId,
    String url,
    String title,
    bool live,
    double startSeconds,
  );
  external void seek(double seconds);
  external void stop();
}

_ServalCast? get _cast =>
    globalContext.getProperty<_ServalCast?>('servalCast'.toJS);

Future<void> initialise(String appId) async => _cast?.initialise(appId);

/// Polled rather than pushed, and distinct so each has its own subscription in the screen.
Stream<bool> get available => _poll(() => _cast?.available() ?? false);

Stream<bool> get casting => _poll(() => _cast?.casting() ?? false);

Stream<bool> _poll(bool Function() read) =>
    Stream<bool>.periodic(_pollInterval, (_) => read()).distinct();

Future<String?> cast(
  String appId,
  Uri url, {
  required String title,
  required bool live,
  Duration startAt = Duration.zero,
}) async {
  final api = _cast;
  if (api == null) return 'Casting is not available in this browser.';

  api.start(
    appId,
    url.toString(),
    title,
    live,
    startAt.inMilliseconds / 1000.0,
  );

  // Nothing calls back from JavaScript — see above — so the error slot is watched rather than
  // awaited. Returns the moment something is recorded, and null if nothing is by the end of the
  // window, which is the ordinary case: the picture is on the television and there is nothing to
  // say about it.
  final deadline = DateTime.now().add(_errorWindow);
  while (DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(_errorPoll);

    final error = api.takeError();
    if (error.isNotEmpty) return error;
  }

  return null;
}

Future<void> seek(Duration position) async =>
    _cast?.seek(position.inMilliseconds / 1000.0);

Future<void> stop() async => _cast?.stop();
