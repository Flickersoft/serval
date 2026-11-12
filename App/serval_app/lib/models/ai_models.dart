/// Settings → AI: the model packs the Server can download, and any download in progress — the
/// client half of `Server/Serval.Server/Ai/ModelPacks.cs`.
class AiModelsStatus {
  const AiModelsStatus({
    required this.packs,
    this.cannotInstall,
    this.job,
  });

  factory AiModelsStatus.fromJson(Map<String, dynamic> json) => AiModelsStatus(
    packs: [
      for (final pack in json['packs'] as List<dynamic>? ?? const [])
        AiModelPack.fromJson(pack as Map<String, dynamic>),
    ],
    cannotInstall: json['cannotInstall'] as String?,
    job: json['job'] == null
        ? null
        : AiModelJob.fromJson(json['job'] as Map<String, dynamic>),
  );

  final List<AiModelPack> packs;

  /// Why this Server cannot download anything at all — a read-only models mount, most often.
  final String? cannotInstall;

  /// The current or most recent download, or null when there has been none since the Server started.
  final AiModelJob? job;

  bool get downloading => job != null && job!.finishedAt == null;
}

class AiModelPack {
  const AiModelPack({
    required this.id,
    required this.name,
    required this.description,
    required this.downloadBytes,
    this.memory,
    this.requires = const [],
    this.installed = false,
    this.enabled = false,
    this.unavailable,
  });

  factory AiModelPack.fromJson(Map<String, dynamic> json) => AiModelPack(
    id: json['id'] as String,
    name: json['name'] as String,
    description: json['description'] as String? ?? '',
    downloadBytes: (json['downloadBytes'] as num?)?.toInt() ?? 0,
    memory: json['memory'] as String?,
    requires: [
      for (final id in json['requires'] as List<dynamic>? ?? const []) id as String,
    ],
    installed: json['installed'] as bool? ?? false,
    enabled: json['enabled'] as bool? ?? false,
    unavailable: json['unavailable'] as String?,
  );

  final String id;
  final String name;
  final String description;

  /// 0 for a pack that ships inside the server image.
  final int downloadBytes;

  /// A measured working set where there is one; null where nothing was measured.
  final String? memory;

  /// Packs this one cannot work without — installed alongside it.
  final List<String> requires;

  final bool installed;

  /// Switched on in the stored settings. It loads at the next restart if it is not running yet.
  final bool enabled;

  /// Why this pack cannot be installed from here, or null.
  final String? unavailable;
}

class AiModelJob {
  const AiModelJob({
    required this.packs,
    required this.startedAt,
    this.finishedAt,
    this.succeeded = false,
    this.error,
    this.step,
    this.bytesWritten,
    this.log = const [],
  });

  factory AiModelJob.fromJson(Map<String, dynamic> json) => AiModelJob(
    packs: [
      for (final id in json['packs'] as List<dynamic>? ?? const []) id as String,
    ],
    startedAt: DateTime.parse(json['startedAt'] as String),
    finishedAt: json['finishedAt'] == null
        ? null
        : DateTime.parse(json['finishedAt'] as String),
    succeeded: json['succeeded'] as bool? ?? false,
    error: json['error'] as String?,
    step: json['step'] as String?,
    bytesWritten: (json['bytesWritten'] as num?)?.toInt(),
    log: [
      for (final line in json['log'] as List<dynamic>? ?? const []) line as String,
    ],
  );

  final List<String> packs;
  final DateTime startedAt;
  final DateTime? finishedAt;
  final bool succeeded;
  final String? error;

  /// The last thing the downloader said it was doing.
  final String? step;

  /// Bytes landed so far, while running.
  final int? bytesWritten;

  final List<String> log;
}
