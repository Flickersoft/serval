import 'push_client.dart';

/// The conditional import's default branch — neither `dart.library.js_interop` nor a browser.
/// Reached by `flutter test`, which runs on the VM, and by any future desktop or mobile build.
///
/// Push on a native app is not this: it is FCM on Android and APNs on iOS, arriving as further
/// `Transport` values on the server's subscription rows rather than through anything here. So this
/// answers "not supported" rather than pretending — the screen draws its unsupported state, which
/// is honest on every platform that compiles this file today.
bool get isSupported => false;

PushPermission get permission => PushPermission.denied;

Future<PushSubscriptionInfo?> current() async => null;

Future<PushSubscriptionInfo?> subscribe(String vapidPublicKey) async => null;

Future<void> unsubscribe() async {}

void onNavigate(void Function(String route) handler) {}

String? get subscribedKey => null;
