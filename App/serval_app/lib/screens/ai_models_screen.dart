import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/server_restart.dart';
import '../models/ai_models.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_field.dart';
import '../widgets/nocturne_toggle.dart';
import '../widgets/storage_bar.dart' show VitalsAlertStrip;

/// *Settings → AI models*: one button that downloads the chosen models onto this Server and turns
/// them on, then a restart to load them.
///
/// A fresh install is a recorder. Everything here is what it takes to make it more than one,
/// without a terminal: the Server runs its own `fetch-models.sh`, verifies the files, and stores
/// the switches; the restart is offered here rather than left as an instruction.
///
/// Admin-only, like the route behind it.
class AiModelsScreen extends ConsumerStatefulWidget {
  const AiModelsScreen({super.key});

  @override
  ConsumerState<AiModelsScreen> createState() => _AiModelsScreenState();
}

class _AiModelsScreenState extends ConsumerState<AiModelsScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  AiModelsStatus? _status;
  String? _error;

  /// The packs ticked but not yet installed. Seeded once from the first read.
  Set<String>? _chosen;

  bool _starting = false;
  bool _restarting = false;

  /// A download finished in this visit, so its packs are stored but not yet loaded.
  bool _restartDue = false;

  Timer? _poll;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  @override
  void dispose() {
    _poll?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final status = await _repository.aiModels();
      if (!mounted) return;
      final wasDownloading = _status?.downloading ?? false;
      setState(() {
        _status = status;
        _error = null;
        _chosen ??= _defaultChoice(status);
        if (wasDownloading && !status.downloading && status.job!.succeeded) {
          _restartDue = true;
          _chosen = {};
        }
      });
      _schedulePoll(status.downloading);
    } catch (error) {
      if (!mounted) return;
      setState(() => _error = 'Could not read the AI models: $error');
    }
  }

  void _schedulePoll(bool downloading) {
    _poll?.cancel();
    if (downloading) {
      _poll = Timer(const Duration(seconds: 2), () => unawaited(_load()));
    }
  }

  /// Everything that is not already on, except the scene model: it is the one large download and
  /// the one large working set, so it is asked for rather than assumed.
  static Set<String> _defaultChoice(AiModelsStatus status) => {
    for (final pack in status.packs)
      if (!(pack.installed && pack.enabled) &&
          pack.unavailable == null &&
          pack.id != 'scene')
        pack.id,
  };

  bool _isOn(AiModelPack pack) => pack.installed && pack.enabled;

  void _toggle(AiModelPack pack, bool value) {
    final packs = _status!.packs;
    setState(() {
      final chosen = {...?_chosen};
      if (value) {
        chosen.add(pack.id);
        // What it needs comes with it.
        for (final id in pack.requires) {
          final needed = packs.where((p) => p.id == id).firstOrNull;
          if (needed != null && !_isOn(needed)) chosen.add(id);
        }
      } else {
        chosen.remove(pack.id);
        // And anything that needed it goes too.
        for (final other in packs) {
          if (other.requires.contains(pack.id)) chosen.remove(other.id);
        }
      }
      _chosen = chosen;
    });
  }

  int get _downloadBytes {
    final status = _status;
    if (status == null) return 0;
    return status.packs
        .where((p) => _chosen!.contains(p.id) && !p.installed)
        .fold(0, (sum, p) => sum + p.downloadBytes);
  }

  Future<void> _install() async {
    final chosen = _chosen;
    if (chosen == null || chosen.isEmpty) return;
    setState(() {
      _starting = true;
      _error = null;
    });
    try {
      final status = await _repository.installAiModels(chosen.toList());
      if (!mounted) return;
      setState(() => _status = status);
      _schedulePoll(true);
    } catch (error) {
      if (mounted) setState(() => _error = '$error');
    } finally {
      if (mounted) setState(() => _starting = false);
    }
  }

  Future<void> _restart() async {
    setState(() {
      _restarting = true;
      _error = null;
    });
    try {
      final problem = await restartServerAndWait(_repository);
      if (!mounted) return;
      setState(() {
        _error = problem;
        if (problem == null) _restartDue = false;
      });
      if (problem == null) await _load();
    } catch (error) {
      if (mounted) setState(() => _error = '$error');
    } finally {
      if (mounted) setState(() => _restarting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final compact = isCompact(context);
    final status = _status;

    final body = ListView(
      padding: EdgeInsets.fromLTRB(
        compact ? 18 : 28,
        compact ? 18 : 22,
        compact ? 18 : 28,
        28,
      ),
      children: [
        if (!compact) ...[
          const _PageHeading(),
          const SizedBox(height: 20),
          const SettingsDivider(),
          const SizedBox(height: 20),
        ],
        if (_error case final message?) ...[
          VitalsAlertStrip(
            message: message,
            critical: true,
            boxed: true,
            padding: const EdgeInsets.all(14),
          ),
          const SizedBox(height: 16),
        ],
        if (status == null && _error == null)
          Text(
            'Reading this Server’s models…',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 13,
              color: Nocturne.mix(Nocturne.text, 50),
            ),
          ),
        if (status != null) ..._content(status),
      ],
    );

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: compact
          ? Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                CompactAppBar(
                  title: 'AI models',
                  onBack: () => context.go('/settings'),
                ),
                Expanded(child: body),
              ],
            )
          : body,
    );
  }

  List<Widget> _content(AiModelsStatus status) {
    final job = status.job;
    final downloading = status.downloading;
    final chosen = _chosen ?? const <String>{};
    final busy = downloading || _starting || _restarting;
    final download = _downloadBytes;

    return [
      if (status.cannotInstall case final reason?) ...[
        VitalsAlertStrip(
          message: reason,
          critical: false,
          boxed: true,
          padding: const EdgeInsets.all(14),
        ),
        const SizedBox(height: 16),
      ],
      if (downloading)
        _Strip(
          message:
              'Downloading — ${job!.step ?? 'starting'}'
              '${(job.bytesWritten ?? 0) > 0 ? ' · ${_size(job.bytesWritten!)} so far' : ''}',
        )
      else if (job != null && !job.succeeded && job.error != null)
        VitalsAlertStrip(
          message: job.error!,
          critical: true,
          boxed: true,
          padding: const EdgeInsets.all(14),
        )
      else if (_restartDue || _restarting)
        VitalsAlertStrip(
          message: _restarting
              ? 'Restarting the Server — this page picks up again when it answers.'
              : 'Installed and switched on. They load when the Server restarts.',
          critical: false,
          boxed: true,
          padding: const EdgeInsets.all(14),
          action: NocturneButton(
            label: _restarting ? 'Restarting…' : 'Restart now',
            icon: PhosphorIconsRegular.arrowClockwise,
            variant: NocturneButtonVariant.primary,
            onPressed: _restarting ? null : () => unawaited(_restart()),
          ),
        ),
      if (downloading || job?.error != null || _restartDue || _restarting)
        const SizedBox(height: 16),
      SettingsSection(
        title: 'Models',
        blurb: const Text(
          'Each one runs on this machine and nothing it hears or sees leaves it. Cameras still '
          'choose for themselves whether to use them, under each camera’s settings.',
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (final pack in status.packs) ...[
              ToggleRow(
                title: '${pack.name} · ${_cost(pack)}',
                description: _describe(pack),
                value: _isOn(pack) || chosen.contains(pack.id),
                onChanged:
                    _isOn(pack) ||
                        pack.unavailable != null ||
                        busy ||
                        status.cannotInstall != null
                    ? null
                    : (value) => _toggle(pack, value),
              ),
              const SizedBox(height: 10),
            ],
          ],
        ),
      ),
      const SizedBox(height: 16),
      Align(
        alignment: Alignment.centerLeft,
        child: NocturneButton(
          label: switch ((downloading || _starting, download)) {
            (true, _) => 'Downloading…',
            (false, 0) => 'Turn on',
            (false, final bytes) => 'Download ${_size(bytes)} and turn on',
          },
          icon: PhosphorIconsRegular.downloadSimple,
          variant: NocturneButtonVariant.primary,
          height: 38,
          onPressed: chosen.isEmpty || busy || status.cannotInstall != null
              ? null
              : () => unawaited(_install()),
        ),
      ),
    ];
  }

  static String _cost(AiModelPack pack) {
    if (pack.installed) return 'downloaded';
    if (pack.downloadBytes == 0) return 'included';
    return _size(pack.downloadBytes);
  }

  static String _describe(AiModelPack pack) {
    final parts = <String>[pack.description];
    if (pack.memory case final memory?) parts.add('Uses $memory.');
    if (pack.unavailable case final reason?) {
      parts.add(reason);
    } else if (pack.installed && pack.enabled) {
      parts.add('On.');
    } else if (pack.installed) {
      parts.add('Downloaded, not switched on.');
    }
    return parts.join(' ');
  }

  static String _size(int bytes) {
    const gb = 1000 * 1000 * 1000;
    const mb = 1000 * 1000;
    if (bytes >= gb) return '${(bytes / gb).toStringAsFixed(1)} GB';
    return '${(bytes / mb).round()} MB';
  }
}

class _Strip extends StatelessWidget {
  const _Strip({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => VitalsAlertStrip(
    message: message,
    critical: false,
    boxed: true,
    padding: const EdgeInsets.all(14),
  );
}

class _PageHeading extends StatelessWidget {
  const _PageHeading();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      const Text(
        'AI models',
        style: TextStyle(
          fontFamily: Nocturne.fontHeading,
          fontSize: 19,
          fontWeight: Nocturne.headingWeight,
          letterSpacing: -0.19,
          color: Nocturne.text,
        ),
      ),
      const SizedBox(height: 3),
      Text(
        'Downloaded once to this Server · everything they process stays on it',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );
}
