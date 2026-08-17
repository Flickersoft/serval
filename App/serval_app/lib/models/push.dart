import 'dart:convert';

import 'package:crypto/crypto.dart';

/// The Server's id for the subscription at [endpoint].
///
/// Derived rather than remembered, and it must stay byte-identical to `PushSubscription.IdFor` on
/// the Server: lowercase hex of the SHA-256 of the endpoint's UTF-8 bytes. That is what lets this
/// browser name its own row without the Server ever handing the endpoint back, and without the App
/// having to keep an id alive across cleared storage.
String pushDeviceIdFor(String endpoint) =>
    sha256.convert(utf8.encode(endpoint)).toString();

/// What the browser needs before it can subscribe, from `GET /api/push/config`.
class PushConfig {
  const PushConfig({required this.vapidPublicKey, required this.enabled});

  /// The deployment's VAPID public key, passed to the browser as `applicationServerKey`.
  ///
  /// Generated once by the Server and then fixed: every existing subscription is bound to it, so a
  /// different value here means every device in the house has to subscribe again. The App notices
  /// that by remembering which key it used — see `PushClient.subscribedKey`.
  final String vapidPublicKey;

  /// Whether the deployment has notifications switched on at all. False means a subscription would
  /// be stored and then never sent to, so the screen says so rather than looking healthy.
  final bool enabled;

  factory PushConfig.fromJson(Map<String, dynamic> json) => PushConfig(
    vapidPublicKey: json['vapid_public_key'] as String? ?? '',
    enabled: json['enabled'] as bool? ?? false,
  );
}

/// One browser this account has registered, from `GET /api/push/subscriptions`.
///
/// A device rather than a session: signing out and back in on the same browser is the same row.
/// The endpoint itself is never sent to the App — the id is derived from it and is enough to talk
/// about a row.
class PushDevice {
  const PushDevice({
    required this.id,
    this.label,
    required this.createdAt,
    this.lastSuccessAt,
  });

  final String id;

  /// Something a person can recognise, like "Chrome on Android". Null when the browser said
  /// nothing useful about itself.
  final String? label;

  final DateTime createdAt;

  /// When a notification was last accepted for this device, or null if none ever has been.
  ///
  /// The field worth showing: a device registered weeks ago that has never received anything is
  /// the visible symptom of almost every way this can be misconfigured.
  final DateTime? lastSuccessAt;

  factory PushDevice.fromJson(Map<String, dynamic> json) => PushDevice(
    id: json['id'] as String,
    label: json['label'] as String?,
    createdAt:
        DateTime.tryParse(json['created_at'] as String? ?? '')?.toLocal() ??
        DateTime.now(),
    lastSuccessAt: DateTime.tryParse(
      json['last_success_at'] as String? ?? '',
    )?.toLocal(),
  );
}

/// How a test send went, from `POST /api/push/test`.
class PushTestResult {
  const PushTestResult({required this.devices, required this.accepted});

  /// How many devices were tried.
  final int devices;

  /// How many push services took the message.
  ///
  /// Not the same as how many notifications appeared: a phone that is switched off counts as
  /// accepted and is shown the message when it comes back. The screen has to word it that way.
  final int accepted;

  factory PushTestResult.fromJson(Map<String, dynamic> json) => PushTestResult(
    devices: (json['devices'] as num?)?.toInt() ?? 0,
    accepted: (json['accepted'] as num?)?.toInt() ?? 0,
  );
}
