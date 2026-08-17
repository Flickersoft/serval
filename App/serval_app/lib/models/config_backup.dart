/// What a configuration restore did, as `POST /api/config/restore` reports it.
///
/// **A restore is best-effort, so this is not a receipt — it is the result.** The Server applies
/// what it can and names what it could not: a camera whose transcode codec that host's ffmpeg
/// cannot encode, a setting that is environment-only there, an account that would have left the
/// Server without an Admin. Each carries a sentence the Server wrote for the person reading it,
/// and this file's job is to carry those through to the dialog unedited.
library;

/// The whole outcome.
class ConfigRestoreResult {
  const ConfigRestoreResult({
    required this.restoredAt,
    required this.fileCreatedAt,
    this.fileCreatedBy,
    this.sections = const [],
    this.skipped = const [],
    this.notes = const [],
  });

  factory ConfigRestoreResult.fromJson(Map<String, dynamic> json) =>
      ConfigRestoreResult(
        restoredAt:
            DateTime.tryParse(json['restoredAt'] as String? ?? '')?.toLocal() ??
            DateTime.now(),
        fileCreatedAt:
            DateTime.tryParse(
              json['fileCreatedAt'] as String? ?? '',
            )?.toLocal() ??
            DateTime.now(),
        fileCreatedBy: json['fileCreatedBy'] as String?,
        sections: [
          for (final section in json['sections'] as List<dynamic>? ?? const [])
            RestoreSection.fromJson(section as Map<String, dynamic>),
        ],
        skipped: [
          for (final skip in json['skipped'] as List<dynamic>? ?? const [])
            RestoreSkip.fromJson(skip as Map<String, dynamic>),
        ],
        notes: [
          for (final note in json['notes'] as List<dynamic>? ?? const [])
            note as String,
        ],
      );

  final DateTime restoredAt;

  /// When the file was taken. Echoed back so restoring a much older backup than intended is
  /// visible in the result, not only in the dialog that preceded it.
  final DateTime fileCreatedAt;
  final String? fileCreatedBy;

  final List<RestoreSection> sections;
  final List<RestoreSkip> skipped;

  /// Things that happened and are worth saying, but are not failures — accounts signed out,
  /// settings stored that need a restart before they mean anything.
  final List<String> notes;

  bool get hasSkips => skipped.isNotEmpty;

  int get changed => sections.fold(
    0,
    (total, section) => total + section.created + section.updated,
  );
}

/// What one section of the file came to.
class RestoreSection {
  const RestoreSection({
    required this.name,
    this.created = 0,
    this.updated = 0,
    this.skipped = 0,
    this.cleared = 0,
  });

  factory RestoreSection.fromJson(Map<String, dynamic> json) => RestoreSection(
    name: json['name'] as String? ?? '',
    created: json['created'] as int? ?? 0,
    updated: json['updated'] as int? ?? 0,
    skipped: json['skipped'] as int? ?? 0,
    cleared: json['cleared'] as int? ?? 0,
  );

  /// The Server's label, not an enum. A later Server can back up something this build has never
  /// heard of and it still draws — see [label], which titlecases whatever arrives rather than
  /// looking it up.
  final String name;

  final int created;
  final int updated;
  final int skipped;

  /// Entries removed rather than written. Only ever the stale tail of a list setting the file has
  /// shortened, which is the one place a merge-only restore deletes anything — so it is counted
  /// apart from [updated] rather than folded into it.
  final int cleared;

  bool get isEmpty =>
      created == 0 && updated == 0 && skipped == 0 && cleared == 0;

  /// `Cameras`, `Settings`, `Preferences` — the Server's own word, capitalised.
  String get label =>
      name.isEmpty ? name : name[0].toUpperCase() + name.substring(1);

  /// `4 updated · 1 added · 1 skipped`, or `nothing to change`.
  ///
  /// A section with nothing to do says so rather than reading as three zeros — the same instinct
  /// the rest of the Server status page is built on, where a figure that is missing is drawn as
  /// missing rather than as a meter resting at zero.
  String get summary {
    if (isEmpty) return 'nothing to change';

    return [
      if (updated > 0) '$updated updated',
      if (created > 0) '$created added',
      if (cleared > 0) '$cleared removed',
      if (skipped > 0) '$skipped skipped',
    ].join(' · ');
  }
}

/// One thing the file asked for that was not done, and the Server's reason.
class RestoreSkip {
  const RestoreSkip({
    required this.section,
    required this.item,
    required this.reason,
  });

  factory RestoreSkip.fromJson(Map<String, dynamic> json) => RestoreSkip(
    section: json['section'] as String? ?? '',
    item: json['item'] as String? ?? '',
    reason: json['reason'] as String? ?? '',
  );

  final String section;

  /// The camera id, setting key or username this is about.
  final String item;

  /// Shown verbatim. The Server writes these for the person who caused them — a refused transcode
  /// names the encoder and the fix — and anything this App said instead would be vaguer.
  final String reason;
}

/// What a backup file says about itself, read in the App before it is uploaded.
///
/// Parsed here only to fill in the confirmation dialog and to catch "that is not a Serval backup"
/// without a round trip. **It is not validation** — the Server checks the file properly, and every
/// field here is tolerated as missing, because a file this cannot read is one the Server should get
/// the chance to reject in its own words.
class ConfigBackupSummary {
  const ConfigBackupSummary({
    required this.isServalBackup,
    this.version,
    this.createdAt,
    this.createdBy,
    this.cameras = 0,
    this.settings = 0,
    this.users = 0,
    this.preferences = 0,
  });

  /// Mirrors `ConfigBackupFile.FileKind` on the Server.
  static const fileKind = 'serval.config-backup';

  factory ConfigBackupSummary.fromJson(Object? decoded) {
    if (decoded is! Map<String, dynamic>) {
      return const ConfigBackupSummary(isServalBackup: false);
    }

    return ConfigBackupSummary(
      isServalBackup: decoded['kind'] == fileKind,
      version: decoded['version'] as int?,
      createdAt: DateTime.tryParse(
        decoded['createdAt'] as String? ?? '',
      )?.toLocal(),
      createdBy: decoded['createdBy'] as String?,
      cameras: (decoded['cameras'] as List<dynamic>?)?.length ?? 0,
      settings: (decoded['settings'] as Map<String, dynamic>?)?.length ?? 0,
      users: (decoded['users'] as List<dynamic>?)?.length ?? 0,
      preferences: (decoded['preferences'] as List<dynamic>?)?.length ?? 0,
    );
  }

  final bool isServalBackup;
  final int? version;
  final DateTime? createdAt;
  final String? createdBy;

  final int cameras;
  final int settings;
  final int users;
  final int preferences;

  /// `6 cameras, 12 settings, 3 accounts and 3 sets of preferences`, dropping whatever is empty.
  String get contents {
    final parts = [
      if (cameras > 0) '$cameras ${cameras == 1 ? 'camera' : 'cameras'}',
      if (settings > 0) '$settings ${settings == 1 ? 'setting' : 'settings'}',
      if (users > 0) '$users ${users == 1 ? 'account' : 'accounts'}',
      if (preferences > 0)
        '$preferences ${preferences == 1 ? 'set' : 'sets'} of preferences',
    ];

    if (parts.isEmpty) return 'Nothing';
    if (parts.length == 1) return parts.single;

    return '${parts.sublist(0, parts.length - 1).join(', ')} and ${parts.last}';
  }
}
