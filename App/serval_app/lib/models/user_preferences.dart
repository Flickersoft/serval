import 'camera.dart';

/// What the signed-in person has arranged for themselves, held by the Server against their account.
///
/// Not to be confused with [ServerSettings], which is the other kind of setting entirely: that one
/// is the deployment's configuration, Admin-only to change, and the same for everybody looking at
/// it. This is one person's own, takes any role to write, and follows them to another browser —
/// which is the whole reason it stopped living in `shared_preferences`.
///
/// **This is the place to add the next one.** A new preference is a field here, a line in
/// `ServalApi.preferences`, and a nullable property on the Server's request record. Because the
/// Server *merges* rather than replaces, a build that predates a preference leaves it alone rather
/// than erasing it — which is what makes adding one safe while an older tab is still open.
///
/// Deliberately **not** where the playback volume or the activity panel's collapsed state went.
/// Those read as per-user and are not: how loud you want the app and whether a 376px column fits
/// are properties of the machine you are sitting at, and syncing them would have a phone dictate a
/// desktop's volume. They stay in `shared_preferences` for that reason, not for want of a home.
class UserPreferences {
  const UserPreferences({
    this.wallLayout = const [],
    this.notificationsEnabled = true,
    this.notifications = const [],
    this.updatedAt,
  });

  /// Where this person has put their camera tiles.
  ///
  /// Empty means *never arranged one*, and the wall answers that by packing the design's default
  /// rather than by drawing nothing — `WallGrid.reconcile` treats the two identically, so there is
  /// no separate first-run path.
  final List<TileLayout> wallLayout;

  /// Whether this person wants alerts pushed to them at all, across every device they have
  /// registered. Defaults true, so an account that has never opened the screen is notified.
  final bool notificationsEnabled;

  /// Which cameras may interrupt this person, and about what.
  ///
  /// A camera with no entry notifies about everything it raises an alert for — absent is *inherit*,
  /// not *off*. See [CameraNotificationRule] for why these can only ever narrow.
  final List<CameraNotificationRule> notifications;

  /// When the Server last stored a change, or null for an account that never has.
  final DateTime? updatedAt;

  /// This account's rule for one camera, or null if it has never set one.
  CameraNotificationRule? ruleFor(String cameraId) {
    for (final rule in notifications) {
      if (rule.cameraId == cameraId) {
        return rule;
      }
    }
    return null;
  }

  UserPreferences copyWith({
    List<TileLayout>? wallLayout,
    bool? notificationsEnabled,
    List<CameraNotificationRule>? notifications,
  }) => UserPreferences(
    wallLayout: wallLayout ?? this.wallLayout,
    notificationsEnabled: notificationsEnabled ?? this.notificationsEnabled,
    notifications: notifications ?? this.notifications,
    updatedAt: updatedAt,
  );

  factory UserPreferences.fromJson(
    Map<String, dynamic> json,
  ) => UserPreferences(
    wallLayout: [
      for (final tile in (json['wallLayout'] as List<dynamic>? ?? const []))
        TileLayout(
          cameraId: (tile as Map<String, dynamic>)['cameraId'] as String,
          column: (tile['column'] as num?)?.toInt() ?? 0,
          row: (tile['row'] as num?)?.toInt() ?? 0,
          columnSpan: (tile['columnSpan'] as num?)?.toInt() ?? 1,
          rowSpan: (tile['rowSpan'] as num?)?.toInt() ?? 1,
        ),
    ],
    notificationsEnabled: json['notificationsEnabled'] as bool? ?? true,
    notifications: [
      for (final rule in (json['notifications'] as List<dynamic>? ?? const []))
        CameraNotificationRule.fromJson(rule as Map<String, dynamic>),
    ],
    updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '')?.toLocal(),
  );
}

/// What one camera is allowed to interrupt this person about.
///
/// **These narrow; they cannot widen.** Whether an alert exists at all was decided long before this
/// — by the deployment's alert classes and then the camera's own override — and a notification
/// needs an alert to carry it. So the menu the screen offers is the camera's *effective* alert
/// classes, and naming something outside that set is stored but simply never matches. It starts
/// working the day an admin adds the class to the camera, which is why nothing rejects it.
///
/// **Null is not empty.** Null means *everything this camera alerts on*, which is what somebody who
/// has never touched the screen has. An empty list means they chose nothing and want nothing of
/// that kind — the two are one tap apart and mean opposite things.
class CameraNotificationRule {
  const CameraNotificationRule({
    required this.cameraId,
    this.enabled = true,
    this.objectClasses,
    this.soundLabels,
    this.cooldownSeconds,
  });

  final String cameraId;

  /// Whether this camera may notify at all. The coarse switch, above the two lists.
  final bool enabled;

  /// Which detected objects notify, or null to inherit everything the camera alerts on.
  final List<String>? objectClasses;

  /// Which sounds notify, or null to inherit everything the camera alerts on.
  final List<String>? soundLabels;

  /// How long this camera waits before interrupting again about the same thing, or null to inherit
  /// the Server's `Serval:Push:CooldownSeconds`. Zero notifies every time.
  ///
  /// The one field here that narrows by rate rather than by kind, and the only one whose effect
  /// never shows in the alert queue — a held notification still leaves its row, its clip and its
  /// unread dot.
  final int? cooldownSeconds;

  /// Whether this rule is doing anything a plain "notify me about everything" would not.
  ///
  /// Used to keep the stored set small: a rule that changes nothing is dropped on save rather than
  /// written, so the document holds choices rather than a row per camera that exists. Every
  /// nullable field has to be named here — one left out is a choice that saves and vanishes.
  bool get isDefault =>
      enabled &&
      objectClasses == null &&
      soundLabels == null &&
      cooldownSeconds == null;

  CameraNotificationRule copyWith({
    bool? enabled,
    List<String>? objectClasses,
    List<String>? soundLabels,
    int? cooldownSeconds,
    bool clearObjectClasses = false,
    bool clearSoundLabels = false,
    bool clearCooldownSeconds = false,
  }) => CameraNotificationRule(
    cameraId: cameraId,
    enabled: enabled ?? this.enabled,
    objectClasses: clearObjectClasses
        ? null
        : (objectClasses ?? this.objectClasses),
    soundLabels: clearSoundLabels ? null : (soundLabels ?? this.soundLabels),
    cooldownSeconds: clearCooldownSeconds
        ? null
        : (cooldownSeconds ?? this.cooldownSeconds),
  );

  factory CameraNotificationRule.fromJson(Map<String, dynamic> json) =>
      CameraNotificationRule(
        cameraId: json['cameraId'] as String,
        enabled: json['enabled'] as bool? ?? true,
        objectClasses: (json['objectClasses'] as List<dynamic>?)
            ?.map((c) => c as String)
            .toList(growable: false),
        soundLabels: (json['soundLabels'] as List<dynamic>?)
            ?.map((c) => c as String)
            .toList(growable: false),
        cooldownSeconds: (json['cooldownSeconds'] as num?)?.round(),
      );

  Map<String, dynamic> toJson() => {
    'cameraId': cameraId,
    'enabled': enabled,
    // Sent explicitly as null rather than omitted: the Server reads null as inherit, and omitting
    // would mean the same thing here only by accident.
    'objectClasses': objectClasses,
    'soundLabels': soundLabels,
    'cooldownSeconds': cooldownSeconds,
  };
}
