/// The Server's settings catalogue, as `GET /api/settings` serves it.
///
/// **The Server, not the App, decides what is settable and what each thing means.** Every field is
/// described there once — in `Server/Serval.Server/Configuration/SettingsCatalog.cs` — and this
/// screen draws whatever arrives. A knob added to the Server appears here with its own explanation
/// and no App change; a knob the Server does not list cannot be written, however it is spelled.
/// That is what keeps the environment-only settings (where the database is, what signs a token)
/// out of reach of a UI.
library;

/// Where the value in force came from.
enum SettingSource {
  /// Nothing sets it; this is what Serval ships with.
  builtIn('default'),

  /// An appsettings file or an environment variable sets it — someone's deployment did this.
  deployment('deployment'),

  /// Changed here, in the App. The only ones that can be reset.
  user('user');

  const SettingSource(this.wireName);

  final String wireName;

  static SettingSource fromWire(String? value) {
    for (final source in values) {
      if (source.wireName == value) return source;
    }
    return SettingSource.builtIn;
  }
}

/// What kind of value a setting holds, which decides the control drawn for it.
enum SettingKind {
  boolean('bool'),
  integer('int'),
  number('number'),
  text('text'),
  textList('textlist'),
  choice('choice');

  const SettingKind(this.wireName);

  final String wireName;

  static SettingKind fromWire(String? value) {
    for (final kind in values) {
      if (kind.wireName == value) return kind;
    }
    // An unknown kind from a newer Server is drawn as text rather than dropped: showing the value
    // and letting it be edited as written is closer to right than pretending it does not exist.
    return SettingKind.text;
  }
}

/// One setting: what it is, what it holds, what it would go back to, and how to explain it.
class ServerSetting {
  const ServerSetting({
    required this.key,
    required this.group,
    required this.label,
    required this.help,
    required this.kind,
    required this.source,
    required this.restartRequired,
    this.value,
    this.defaultValue,
    this.min,
    this.max,
    this.unit,
    this.choices,
    this.fallback,
    this.unavailableChoices = const {},
    this.advanced = false,
    this.appliesWhen,
  });

  factory ServerSetting.fromJson(Map<String, dynamic> json) => ServerSetting(
    key: (json['key'] as String?) ?? '',
    group: (json['group'] as String?) ?? '',
    label: (json['label'] as String?) ?? '',
    help: (json['help'] as String?) ?? '',
    kind: SettingKind.fromWire(json['kind'] as String?),
    source: SettingSource.fromWire(json['source'] as String?),
    restartRequired: (json['restartRequired'] as bool?) ?? false,
    value: json['value'],
    defaultValue: json['default'],
    min: (json['min'] as num?)?.toDouble(),
    max: (json['max'] as num?)?.toDouble(),
    unit: json['unit'] as String?,
    choices: _strings(json['choices']),
    fallback: _strings(json['fallback']),
    unavailableChoices: _reasons(json['unavailableChoices']),
    advanced: (json['advanced'] as bool?) ?? false,
    appliesWhen: SettingDependency.fromJson(json['appliesWhen']),
  );

  /// The configuration path, e.g. `Serval:Media:RetentionDays`. What a write is keyed by.
  final String key;

  /// Which section of the page this belongs under.
  final String group;

  /// The field's name, as shown.
  final String label;

  /// What this does and what changing it costs, in a sentence or two.
  ///
  /// **Shown, not hidden behind a hover.** These settings are the kind where a wrong value is not
  /// an error but a system that quietly stops noticing things, and the explanation is the only
  /// thing standing between a person and that. A tooltip nobody opens would leave the page a grid
  /// of unlabelled numbers.
  final String help;

  final SettingKind kind;
  final SettingSource source;

  /// True when the Server reads this while starting and cannot swap it underneath what is already
  /// built. Stored and reported, but not in use until a restart.
  final bool restartRequired;

  /// The value in force: a `bool`, a number, a `String`, or a `List<String>`.
  final Object? value;

  /// What this reverts to when reset — the deployment's value with the user's change taken out.
  final Object? defaultValue;

  final double? min;
  final double? max;

  /// A unit to show after the field: "days", "seconds", "% free".
  final String? unit;

  /// The allowed values, for a [SettingKind.choice], or suggestions for a list.
  final List<String>? choices;

  /// What a list quietly resolves to while it is left empty.
  ///
  /// Several of these mean "use the built-in set" rather than "match nothing", so an empty box
  /// would be drawn for a setting that is doing something. Shown as placeholder text, never as a
  /// value — see `SettingDescriptor.Fallback` on the Server.
  final List<String>? fallback;

  /// Which of [choices] this Server cannot actually deliver, and why.
  ///
  /// Drawn dimmed and unselectable rather than dropped: a list that silently shrinks teaches
  /// nothing, and a value already in force that has become unavailable has to stay visible — that
  /// is the case where a deployment's choice is being ignored, and the only place it can be seen.
  final Map<String, String> unavailableChoices;

  /// Whether this sits below the *Advanced* rule at the foot of its group.
  ///
  /// A statement about who a setting is for, not about how dangerous it is: a model file path, a
  /// thread count, a tracker's process noise. Plenty of everyday settings will empty a disk; these
  /// are the ones whose label cannot be made to mean anything to somebody who has never read the
  /// code.
  ///
  /// **Sorted, never hidden.** Somebody following a support thread needs the field on screen, so
  /// the divider carries the signal and a click is not asked for.
  final bool advanced;

  /// Another setting whose value decides whether this one is read at all.
  final SettingDependency? appliesWhen;

  /// Whether someone has changed this here, which is what makes *Use the default* meaningful.
  bool get isOverridden => source == SettingSource.user;

  /// The value as a list, for [SettingKind.textList].
  List<String> get asList => switch (value) {
    final List<dynamic> items => [for (final item in items) '$item'],
    _ => const [],
  };

  /// What the Server is using while [asList] is empty — the built-in set, which the field draws in
  /// the row itself rather than naming underneath it.
  List<String> get listFallback => fallback ?? const [];

  /// The value as text, for a field or a dropdown.
  String get asText => value == null ? '' : '$value';

  bool get asBool => value == true;

  double? get asNumber => switch (value) {
    final num number => number.toDouble(),
    _ => null,
  };

  /// The bounds as the card reads them out beside the control — "1–10", "0–1". Null unless both
  /// ends are known: "at least 1" is a sentence and belongs in [rangeHint], not at the end of a
  /// row where a pair of numbers goes.
  String? get rangeShort => switch ((min, max)) {
    (final double low, final double high) => '${_trim(low)}–${_trim(high)}',
    _ => null,
  };

  /// The range as a sentence, for the field's hint. Null when it is unbounded both ways.
  String? get rangeHint {
    final suffix = unit == null ? '' : ' $unit';
    return switch ((min, max)) {
      (final double low, final double high) =>
        '${_trim(low)} to ${_trim(high)}$suffix',
      (final double low, null) => 'at least ${_trim(low)}$suffix',
      (null, final double high) => 'at most ${_trim(high)}$suffix',
      _ => null,
    };
  }
}

/// Everything `GET /api/settings` returns.
class ServerSettings {
  const ServerSettings({
    required this.groups,
    required this.settings,
    required this.restartRequired,
    this.updatedAt,
    this.updatedBy,
  });

  factory ServerSettings.fromJson(Map<String, dynamic> json) => ServerSettings(
    groups: [
      for (final group in (json['groups'] as List<dynamic>? ?? const []))
        group as String,
    ],
    settings: [
      for (final setting in (json['settings'] as List<dynamic>? ?? const []))
        ServerSetting.fromJson(setting as Map<String, dynamic>),
    ],
    restartRequired: (json['restartRequired'] as bool?) ?? false,
    updatedAt: DateTime.tryParse(
      (json['updatedAt'] as String?) ?? '',
    )?.toLocal(),
    updatedBy: json['updatedBy'] as String?,
  );

  const ServerSettings.empty()
    : groups = const [],
      settings = const [],
      restartRequired = false,
      updatedAt = null,
      updatedBy = null;

  /// The sections, in the order the page should draw them.
  final List<String> groups;

  final List<ServerSetting> settings;

  /// True when a restart-gated setting has been changed since the Server started — so there is a
  /// change sitting here that the running Server is not using. Not "some settings need a restart",
  /// which is always true of some of them.
  final bool restartRequired;

  final DateTime? updatedAt;
  final String? updatedBy;

  ServerSetting? operator [](String key) {
    for (final setting in settings) {
      if (setting.key == key) return setting;
    }
    return null;
  }

  /// The settings in one section, in catalogue order.
  List<ServerSetting> inGroup(String group) => [
    for (final setting in settings)
      if (setting.group == group) setting,
  ];

  /// One setting by its key, or null. A dependency names a key rather than carrying a setting, and
  /// the controlling one need not be in the same group as what depends on it.
  ServerSetting? byKey(String key) {
    for (final setting in settings) {
      if (setting.key == key) return setting;
    }
    return null;
  }

  /// The restart-gated settings that have been changed but are not yet in use, so the banner can
  /// name them rather than only announcing that something is pending.
  List<ServerSetting> get pendingRestart => restartRequired
      ? [
          for (final setting in settings)
            if (setting.restartRequired && setting.isOverridden) setting,
        ]
      : const [];

  /// How many settings someone has changed here, for the "reset everything" affordance.
  int get overriddenCount => settings.where((s) => s.isOverridden).length;
}

List<String>? _strings(Object? value) =>
    value == null ? null : [for (final item in value as List<dynamic>) '$item'];

Map<String, String> _reasons(Object? value) => switch (value) {
  final Map<String, dynamic> entries => {
    for (final entry in entries.entries) entry.key: '${entry.value}',
  },
  _ => const {},
};

/// One setting's dependency on another's value — see `SettingDependency` on the Server.
///
/// **Resolved against the value being edited, not the one the Server is running.** The settings
/// that control others all need a restart, so a device chosen a moment ago is not yet in force;
/// judging by what is in force would dim exactly the fields that the new choice needs set before
/// that restart, which is the one moment they have to be reachable.
class SettingDependency {
  const SettingDependency({
    required this.key,
    required this.values,
    required this.reason,
  });

  static SettingDependency? fromJson(Object? json) => switch (json) {
    final Map<String, dynamic> map => SettingDependency(
      key: (map['key'] as String?) ?? '',
      values: _strings(map['values']) ?? const [],
      reason: (map['reason'] as String?) ?? '',
    ),
    _ => null,
  };

  /// The controlling setting's key.
  final String key;

  /// The controlling values that make the dependent setting live.
  final List<String> values;

  /// What to say when it does not apply.
  final String reason;

  /// Whether the dependent setting is read, given what the controlling one currently holds.
  ///
  /// An unknown controlling value counts as applicable. A rule that cannot be evaluated should
  /// leave a field editable rather than grey out something that may well be in use.
  bool satisfiedBy(Object? current) =>
      current == null || values.isEmpty || values.contains('$current');
}

/// Drops a trailing `.0` so a bound reads as "1 to 365", not "1.0 to 365.0".
String _trim(double value) =>
    value == value.roundToDouble() ? '${value.toInt()}' : '$value';
