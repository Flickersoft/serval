import 'dart:async';
import 'dart:convert';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/byte_labels.dart';
import '../data/providers.dart';
import '../data/serval_api.dart' show ServalApiException;
import '../data/serval_repository.dart';
import '../models/config_backup.dart';
import '../models/google_home.dart';
import '../models/system_stats.dart';
import '../models/vitals_history.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/config_backup_section.dart';
import '../widgets/google_home_section.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/nocturne_field.dart';
import '../widgets/storage_bar.dart';
import '../widgets/vitals_meter.dart';
import '../widgets/waiting_note.dart';

/// What this Server is doing and how much room it has left — the *Server status* page.
///
/// Sibling to [SettingsScreen](settings_screen.dart), which is what it is *told* to do. Read-only
/// throughout: nothing on this page is a setting, which is exactly why the two are separate pages.
///
/// The page is laid out around the one figure worth reading from across the room. Free space is
/// the hero, top left at full size, with the volume broken down beneath it and the per-camera list
/// directly under that — because "what is using it" is the follow-up question to "how much is
/// left". The three load meters take a column of their own down the right, stacked so their
/// sparklines can be read against each other. An alert is a card at the top carrying the one
/// action that resolves it, rather than a colour applied to a meter.
///
/// The one thing to preserve when editing this: **a missing figure is drawn as missing.** Every
/// number here is nullable and every group carries the Server's own sentence explaining an absence
/// — an NVIDIA host publishes no GPU utilisation at all, an Intel one publishes it only where the
/// container was granted the capability to read the counters, and a kernel without cgroup v2
/// publishes no per-container processor share. Those must read as *not reported*, never as a meter
/// resting at 0%, which is what they become the moment anything here defaults a null to zero.
/// Nothing on this page is drawn from a figure the payload does not carry.
class ServerScreen extends ConsumerStatefulWidget {
  const ServerScreen({super.key});

  @override
  ConsumerState<ServerScreen> createState() => _ServerScreenState();
}

class _ServerScreenState extends ConsumerState<ServerScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  /// Ephemeral UI state, so it lives in `State` rather than a provider — see
  /// [providers.dart](../data/providers.dart) for where that line is drawn.
  String? _busy;
  String? _status;
  String? _error;

  /// Read once when the page opens rather than on the vitals sweep: none of this moves on its own
  /// — the configuration is environment-only and a link is made from the Google Home app, not from
  /// here — so polling it every five seconds would be two requests a minute answering the same way
  /// forever.
  GoogleHomeStatus? _google;
  List<GoogleHomeLink> _googleLinks = const [];
  String? _googleError;

  /// The two cases where the section is meant to be absent rather than present and complaining:
  /// an account that may not read the status (a Viewer, 403), and a deployment that has not
  /// switched the integration on at all.
  bool _googleHidden = false;

  @override
  void initState() {
    super.initState();
    unawaited(_loadGoogleHome());
  }

  Future<void> _loadGoogleHome() async {
    try {
      final status = await _repository.googleHomeStatus();
      // Only worth asking who has linked once the integration is actually serving; a closed one
      // has nothing to list and the route would answer for an empty collection.
      final links = status.effective
          ? await _repository.googleHomeLinks()
          : const <GoogleHomeLink>[];
      if (!mounted) return;
      setState(() {
        // A deployment that has not switched this on gets no card — see
        // GoogleHomeStatus.switchedOff.
        _googleHidden = status.switchedOff;
        _google = status;
        _googleLinks = links;
        _googleError = null;
      });
    } on ServalApiException catch (error) {
      if (!mounted) return;
      // A Viewer gets 403, and the section simply does not appear for them — they have nothing to
      // do about it and it is not their page. Every other status is reported.
      setState(() {
        _googleHidden = error.statusCode == 403;
        _googleError = error.statusCode == 403 ? null : error.message;
      });
    } catch (error) {
      // Deliberately broad, and it is the fix for a real failure rather than defensive habit: the
      // first build of this screen caught only ServalApiException, a decoding error escaped into an
      // unawaited future, and the section vanished from the page with nothing logged anywhere. A
      // fault that removes a feature from the UI is the worst shape a fault can take, because it
      // is indistinguishable from the feature not existing.
      if (!mounted) return;
      setState(
        () => _googleError = 'Could not read the Google Home status: $error',
      );
    }
  }

  Future<void> _unlinkGoogleHome(GoogleHomeLink link) async {
    setState(() => _googleError = null);
    try {
      await _repository.unlinkGoogleHome(link.agentUserId);
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() => _googleError = error.message);
      return;
    }
    await _loadGoogleHome();
  }

  @override
  Widget build(BuildContext context) {
    // Both actions are Admin-only on the Server. Hiding them from a Viewer rather than letting the
    // 403 surface, because unlike the settings form — which a Viewer has a reason to read — there
    // is nothing here for them to look at: the section is two buttons and a warning.
    final canBackUp = ref.watch(isAdminProvider) && _repository.canSaveMedia;

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      // The two reads below are both vitals, and the five-second sweep that refreshes them is the
      // only thing that should redraw this.
      child: ListenableBuilder(
        listenable: _repository.vitalsChanges,
        builder: (context, _) => ServerScreenBody(
          stats: _repository.systemStats(),
          history: _repository.vitalsHistory(),
          onRetention: () => context.go('/settings/server'),
          onBackup: canBackUp ? _backup : null,
          onRestore: canBackUp ? _restore : null,
          configBusy: _busy,
          configStatus: _status,
          configError: _error,
          googleHome: _googleHidden ? null : _google,
          googleHomeHidden: _googleHidden,
          googleHomeLinks: _googleLinks,
          onUnlinkGoogleHome: canBackUp ? _unlinkGoogleHome : null,
          googleHomeError: _googleError,
        ),
      ),
    );
  }

  Future<void> _backup() async {
    final agreed = await showNocturneDialog<bool>(
      context: context,
      builder: (_) => const ConfirmBackupDialog(),
    );
    if (agreed != true || !mounted) return;

    setState(() {
      _busy = 'Preparing the file…';
      _status = null;
      _error = null;
    });

    try {
      final saved = await _repository.saveConfigBackup();
      if (!mounted) return;
      setState(() {
        _busy = null;
        // The web build gets no location: the browser chose the directory and will not say which,
        // so naming one would be a guess printed as a fact.
        _status = saved.location == null
            ? 'Saved ${saved.fileName}.'
            : 'Saved ${saved.fileName} to ${saved.location}.';
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _busy = null;
        _error = _sentence(error);
      });
    }
  }

  Future<void> _restore() async {
    final PlatformFile? file;
    try {
      file = await FilePicker.pickFile(
        type: FileType.custom,
        allowedExtensions: const ['json'],
      );
    } catch (error) {
      if (!mounted) return;
      setState(() => _error = _sentence(error));
      return;
    }

    final bytes = await file?.readAsBytes();
    if (bytes == null || !mounted) return;

    // Read here only to fill in the dialog and to catch "that is not a Serval backup" without a
    // round trip. Not validation — the Server checks the file properly, and anything this cannot
    // read is something it should get the chance to reject in its own words.
    ConfigBackupSummary summary;
    try {
      summary = ConfigBackupSummary.fromJson(jsonDecode(utf8.decode(bytes)));
    } on FormatException {
      summary = const ConfigBackupSummary(isServalBackup: false);
    }

    if (!summary.isServalBackup) {
      setState(() {
        _status = null;
        _error =
            '${file!.name} is not a Serval configuration backup. '
            'Pick a file downloaded from this page.';
      });
      return;
    }

    final agreed = await showNocturneDialog<bool>(
      context: context,
      builder: (_) =>
          ConfirmRestoreDialog(fileName: file!.name, summary: summary),
    );
    if (agreed != true || !mounted) return;

    setState(() {
      _busy = 'Restoring…';
      _status = null;
      _error = null;
    });

    try {
      final result = await _repository.restoreConfigBackup(bytes);
      if (!mounted) return;

      // The accounts list has no socket feed and is fetched per visit, so the one cached thing a
      // restore invalidates has to be dropped by hand. The registry is reloaded inside the
      // repository, which owns that cache.
      ref.invalidate(usersProvider);

      setState(() {
        _busy = null;
        _status = result.hasSkips
            ? 'Restored ${result.changed} entries, ${result.skipped.length} skipped.'
            : 'Restored ${result.changed} entries.';
      });

      await showNocturneDialog<void>(
        context: context,
        builder: (_) => RestoreResultDialog(result: result),
      );
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _busy = null;
        _error = _sentence(error);
      });
    }
  }

  /// The Server's own words where it gave any — its 400s are written for the person who caused
  /// them, and a restore's are the most useful sentences in the whole feature.
  static String _sentence(Object error) =>
      error is ServalApiException ? error.message : error.toString();
}

/// The page without its container, so the widget tests can render each state from a constructed
/// [SystemStats] rather than through a repository — the same prop-driven split every other
/// `lib/widgets/` file keeps to.
class ServerScreenBody extends StatelessWidget {
  const ServerScreenBody({
    super.key,
    required this.stats,
    this.history,
    this.onRetention,
    this.onBackup,
    this.onRestore,
    this.configBusy,
    this.configStatus,
    this.configError,
    this.googleHome,
    this.googleHomeHidden = false,
    this.googleHomeLinks = const [],
    this.onUnlinkGoogleHome,
    this.googleHomeError,
  });

  final SystemStats? stats;

  /// The retained samples behind the meters' sparklines. Optional, and null draws the page
  /// exactly as it drew before the history route existed — which is what the sample
  /// repository and the goldens get.
  final VitalsHistory? history;

  /// Where a space alert sends you: retention is the setting that frees the volume, and it lives
  /// on the other page. Null drops the button rather than drawing one that goes nowhere.
  final VoidCallback? onRetention;

  /// Take the configuration out to a file, and put one back. **Null on both drops the whole
  /// section**, which is what a Viewer gets and what the sample repository gets — so the design
  /// harness and the page-level golden draw the page without it.
  final VoidCallback? onBackup;
  final VoidCallback? onRestore;

  /// What the backup section is doing and what it last did. Held by the screen rather than by the
  /// section, so the section stays drawable from constructed values.
  final String? configBusy;
  final String? configStatus;
  final String? configError;

  /// Whether the Google Home integration is live, and what is stopping it. **Null drops the whole
  /// section** — which is what the page draws before the read lands, and what a Viewer gets, since
  /// `GET /api/google/status` is Admin-only and the 403 is swallowed rather than reported.
  ///
  /// Unlike the backup section above, this is *not* dropped for the sample repository: the sample
  /// answers the way nearly every real deployment does — switched off — and that is the state the
  /// section exists to explain.
  final GoogleHomeStatus? googleHome;

  /// True only for an account that may not read the status at all. It is what separates "this is
  /// not your page" — draw nothing — from "the read failed", which must always draw something.
  final bool googleHomeHidden;

  final List<GoogleHomeLink> googleHomeLinks;

  /// Null draws the section without its one action, which is what a Viewer sees.
  final void Function(GoogleHomeLink link)? onUnlinkGoogleHome;

  final String? googleHomeError;

  /// Below this the two columns will not both hold their content, so the page becomes one column.
  /// The meters' column is fixed at its design width and the volume needs a comparable share.
  static const _twoColumnWidth = 780.0;

  @override
  Widget build(BuildContext context) {
    final stats = this.stats;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Header(stats: stats),
        Expanded(
          child: stats == null
              ? const WaitingNote(message: 'Reading the Server…')
              : SingleChildScrollView(
                  padding: isCompact(context)
                      ? const EdgeInsets.fromLTRB(18, 16, 18, 24)
                      : const EdgeInsets.fromLTRB(24, 18, 24, 24),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      for (final alert in stats.alerts) ...[
                        VitalsAlertStrip(
                          message: alert.message,
                          critical: alert.isCritical,
                          boxed: true,
                          padding: const EdgeInsets.symmetric(
                            horizontal: 14,
                            vertical: 13,
                          ),
                          action: _isSpace(alert) && onRetention != null
                              ? NocturneButton(
                                  label: 'Retention settings',
                                  variant: NocturneButtonVariant.ghost,
                                  onPressed: onRetention!,
                                )
                              : null,
                        ),
                        const SizedBox(height: 16),
                      ],
                      LayoutBuilder(
                        builder: (context, constraints) =>
                            constraints.maxWidth < _twoColumnWidth
                            ? _oneColumn(stats)
                            : _twoColumns(stats),
                      ),
                      // Full width under both columns rather than inside either one. It is the
                      // only thing on this page that acts, so in the volume column it would
                      // compete with the hero figure and in the meters column it would put a
                      // button inside a stack of readings. Last, after everything the page
                      // reports, it reads as a footer of actions — which suits one carrying a
                      // warning.
                      // Above the backup section, because it reports rather than acts and this
                      // page reads reports first. It has one button, which is why it is not
                      // further up: the actions belong together at the foot.
                      if (!googleHomeHidden &&
                          (googleHome != null || googleHomeError != null)) ...[
                        const SizedBox(height: 22),
                        const SettingsDivider(),
                        const SizedBox(height: 22),
                        GoogleHomeSection(
                          status: googleHome,
                          links: googleHomeLinks,
                          onUnlink: onUnlinkGoogleHome,
                          error: googleHomeError,
                        ),
                      ],
                      if (onBackup != null && onRestore != null) ...[
                        const SizedBox(height: 22),
                        const SettingsDivider(),
                        const SizedBox(height: 22),
                        ConfigBackupSection(
                          onBackup: onBackup!,
                          onRestore: onRestore!,
                          busy: configBusy,
                          status: configStatus,
                          error: configError,
                        ),
                      ],
                    ],
                  ),
                ),
        ),
      ],
    );
  }

  /// Volume, its breakdown, and the meters beside them.
  Widget _twoColumns(SystemStats stats) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Expanded(child: _volumeColumn(stats)),
      const SizedBox(width: 26),
      SizedBox(
        width: 372,
        child: _Load(stats: stats, history: history),
      ),
    ],
  );

  /// A window too narrow for the pair. The order is the same one the columns read in, so nothing
  /// arrives in a different sequence than it does at full width.
  Widget _oneColumn(SystemStats stats) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      _Volume(disk: stats.disk, lowOnSpace: _lowOnSpace(stats)),
      const SizedBox(height: 22),
      const SettingsDivider(),
      const SizedBox(height: 22),
      _Load(stats: stats, history: history),
      const SizedBox(height: 22),
      const SettingsDivider(),
      const SizedBox(height: 22),
      _ByCamera(disk: stats.disk),
    ],
  );

  Widget _volumeColumn(SystemStats stats) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      _Volume(disk: stats.disk, lowOnSpace: _lowOnSpace(stats)),
      const SizedBox(height: 22),
      const SettingsDivider(),
      const SizedBox(height: 22),
      _ByCamera(disk: stats.disk),
    ],
  );

  static bool _isSpace(VitalsAlert alert) =>
      alert.kind == VitalsAlertKind.diskLow ||
      alert.kind == VitalsAlertKind.diskCritical;

  /// Whether the share-free figure wears the alert hue. Taken from the Server having raised the
  /// alert rather than from a threshold of the App's own — the Server owns where the line is, and
  /// a second copy of it here would eventually disagree with the banner sitting above it.
  static bool _lowOnSpace(SystemStats stats) => stats.alerts.any(_isSpace);
}

class _Header extends StatelessWidget {
  const _Header({required this.stats});

  final SystemStats? stats;

  @override
  Widget build(BuildContext context) {
    final uptime = stats?.processUptimeSeconds;
    final sampledAt = stats?.sampledAt;

    const blurb =
        'What this machine is doing, and how much room is left for footage · '
        'nothing here is a setting';

    final meta = [
      if (uptime != null)
        Text(
          formatUptime(uptime),
          style: monoStyle(
            fontSize: 11.5,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      if (sampledAt != null) ...[
        const SizedBox(height: 4),
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 6,
              height: 6,
              decoration: const BoxDecoration(
                color: Serval.healthy,
                shape: BoxShape.circle,
              ),
            ),
            const SizedBox(width: 6),
            Text(
              _measured(sampledAt),
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 11.5,
                color: Nocturne.mix(Nocturne.text, 40),
              ),
            ),
          ],
        ),
      ],
    ];

    if (isCompact(context)) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          CompactAppBar(
            title: 'Server status',
            onBack: () => context.go('/settings'),
          ),
          CompactSubBar(
            children: [
              Text(
                blurb,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  height: 1.45,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
              if (meta.isNotEmpty) ...[const SizedBox(height: 8), ...meta],
            ],
          ),
        ],
      );
    }

    return Container(
      padding: const EdgeInsets.fromLTRB(24, 18, 24, 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: Serval.hairline)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text(
                  'Server status',
                  style: TextStyle(
                    fontFamily: Nocturne.fontHeading,
                    fontSize: 17,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  blurb,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 50),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 16),
          Column(crossAxisAlignment: CrossAxisAlignment.end, children: meta),
        ],
      ),
    );
  }

  /// How long ago the sample this page is drawn from was taken. The dot beside it says the figures
  /// are current; without the age it would be saying so about numbers of unknown vintage.
  static String _measured(DateTime sampledAt) {
    final age = DateTime.now().difference(sampledAt);
    return age < const Duration(minutes: 1)
        ? 'measured just now'
        : 'measured ${formatSpan(age)} ago';
  }
}

/// The one hero figure on the page: free space. Everything else here is a supporting number.
class _Volume extends StatelessWidget {
  const _Volume({required this.disk, this.lowOnSpace = false});

  final DiskStats disk;

  /// Spends the alert hue on the share free. Only where the Server has said so.
  final bool lowOnSpace;

  @override
  Widget build(BuildContext context) {
    final free = disk.freeBytes;
    final total = disk.totalBytes;

    return SettingsSection(
      title: 'The volume',
      blurb: Text(
        disk.mountPoint == null
            ? 'Where recordings are written.'
            : 'Where recordings are written — ${disk.mountPoint}. Nothing deletes footage early '
                  'to make room, so what is left here is what is left.',
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (free == null || total == null)
            Text(
              disk.unavailableReason ?? 'The volume could not be measured.',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.45,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            )
          else ...[
            _hero(context, free, total),
            const SizedBox(height: 14),
            StorageBar(
              totalBytes: total,
              freeBytes: free,
              mediaBytes: disk.mediaBytes,
              height: 12,
            ),
          ],
        ],
      ),
    );
  }

  /// The one figure on this page meant to be read from across the room, and what qualifies it.
  ///
  /// Narrow, the three parts stack rather than compete: at 48px the figure alone is most of a
  /// phone's width, and a hero clipped to *6…* says less than no hero would.
  Widget _hero(BuildContext context, int free, int total) {
    final figure = Text(
      formatBytes(free),
      overflow: TextOverflow.ellipsis,
      style: const TextStyle(
        fontFamily: Nocturne.fontHeading,
        fontSize: 48,
        fontWeight: Nocturne.headingWeight,
        height: 1.1,
        color: Nocturne.text,
      ),
    );

    final outOf = Text(
      'free of ${formatBytes(total)}',
      overflow: TextOverflow.ellipsis,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 14,
        color: Nocturne.mix(Nocturne.text, 50),
      ),
    );

    final share = total <= 0
        ? null
        : Text(
            '${(free / total * 100).round()}% free',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              color: lowOnSpace
                  ? Serval.alert
                  : Nocturne.mix(Nocturne.text, 50),
            ),
          );

    if (isCompact(context)) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          figure,
          const SizedBox(height: 4),
          Row(
            children: [
              Expanded(child: outOf),
              if (share != null) ...[const SizedBox(width: 10), share],
            ],
          ),
        ],
      );
    }

    return Row(
      crossAxisAlignment: CrossAxisAlignment.baseline,
      textBaseline: TextBaseline.alphabetic,
      children: [
        Flexible(child: figure),
        const SizedBox(width: 10),
        Flexible(child: outOf),
        if (share != null) ...[
          const Spacer(),
          const SizedBox(width: 10),
          share,
        ],
      ],
    );
  }
}

/// Processor, memory and GPU — three ratios against three different ceilings, in a column so the
/// shape of one can be read against the shape of the next.
class _Load extends StatelessWidget {
  const _Load({required this.stats, this.history});

  final SystemStats stats;

  /// The retained samples, or null before the first read lands and on a repository with no Server.
  /// Each meter draws a sparkline only where its own series has something in it.
  final VitalsHistory? history;

  @override
  Widget build(BuildContext context) => SettingsSection(
    title: 'Load',
    blurb: const Text(
      'One ffmpeg per camera and a model in the same process — a busy machine here is a working '
      'one. These are for noticing a change, not for hitting zero.',
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _cpu(stats.cpu, history?.seriesOf(history!.cpu)),
        const SizedBox(height: 12),
        _memory(stats.memory, history?.seriesOf(history!.memory)),
        const SizedBox(height: 12),
        _gpu(stats.gpu, history?.seriesOf(history!.gpu)),
        // Between the resources it is one of and the accuracy figure it explains — and drawn at all
        // only where the detector reports devices. Every other meter in this card describes hardware
        // any host has; this one describes hardware most do not, and a *not reported* bar on every
        // processor-only server would be noise standing in for an answer nobody asked for.
        if (stats.accelerator.hasDevices) ...[
          const SizedBox(height: 12),
          _accelerator(
            stats.accelerator,
            history?.seriesOf(history!.accelerator),
          ),
        ],
        const SizedBox(height: 12),
        _detection(stats.detection),
      ],
    ),
  );

  /// The object detector's accelerators — a pair of Coral Edge TPUs, today.
  ///
  /// The meter is the **pool**, where the Graphics meter above it is its busiest *engine*. GPU
  /// engines are not interchangeable, so one number has to pick one and the busiest is the useful
  /// pick. Accelerators are: a frame goes to whichever is idle and the detection budget is their
  /// sum, so "how close is the pool to saturated" is the question worth a bar. The asymmetry between
  /// devices — real, and a factor of two on a pair split across USB generations — goes in the
  /// caption, where it is a fact about the hardware rather than a meter jumping between two of them.
  Widget _accelerator(AcceleratorStats accelerator, VitalsSeries? history) {
    final caption = StringBuffer();

    // Lost devices lead, like lane health does under detection coverage: the pooled meter simply
    // reads lower when one goes, which is indistinguishable from a quiet afternoon.
    final lost = accelerator.devices
        .where((device) => !device.healthy)
        .map((device) => device.name)
        .toList();

    if (lost.isNotEmpty) {
      caption.write(
        lost.length == 1
            ? '${lost.single} has stopped answering. '
            : '${lost.join(', ')} have stopped answering. ',
      );
    }

    // Each device on its own terms, one sentence each. This is the whole reason the card is not a
    // single number: a Coral on a USB 2 path delivers about a third of its twin, that difference is
    // invisible in a pooled figure, and nothing else in the product says so.
    final described = accelerator.devices
        .where((device) => device.inferencesPerSecond != null)
        .map((device) {
          // A whole number once it is large enough for a decimal to be noise, matching how the
          // detection meter below rounds its budget.
          final rate = device.inferencesPerSecond!.toStringAsFixed(
            device.inferencesPerSecond! >= 10 ? 0 : 1,
          );
          final latency = device.meanLatencyMs == null
              ? ''
              : ', ${device.meanLatencyMs!.toStringAsFixed(1)} ms each';
          final link = device.link == null ? '' : ' over ${device.link}';
          return '${device.name} at $rate a second$latency$link.';
        })
        .join(' ');

    if (described.isNotEmpty) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(described);
    }

    if ((accelerator.declinedPerSecond ?? 0) > 0.05) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Declining ${accelerator.declinedPerSecond!.toStringAsFixed(1)} frames a second '
        'because every device was busy.',
      );
    } else if ((accelerator.busyPercent ?? 100) < 5) {
      // Idle is the normal reading on a quiet scene, and worth saying so — the same courtesy the
      // Graphics meter extends to a box that is recording without re-encoding.
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Idle is normal — detection only looks where something moved.',
      );
    }

    return VitalsMeter(
      label: accelerator.label ?? 'Accelerator',
      percent: accelerator.busyPercent,
      caption: caption.isEmpty ? null : caption.toString(),
      unavailableReason: accelerator.busyPercent == null
          ? accelerator.unavailableReason
          : null,
      history: history,
    );
  }

  /// How much of what detection wanted to look at it actually did.
  ///
  /// The odd one out in this card, and deliberately here anyway: the meters above are resources,
  /// where a high number is a machine working, and this one is accuracy, where a low number means
  /// events were missed. It belongs beside them because it is the consequence of them — when the
  /// host runs out of room, this is what it costs, and there is nowhere else in the product that
  /// says so.
  Widget _detection(DetectionStats detection) {
    final caption = StringBuffer();

    // Lane health leads, because it is the one thing here that coverage cannot tell you: a host that
    // has lost an accelerator can still read 100% examined.
    if (detection.lanesDegraded) {
      caption.write(
        'Running on ${detection.healthyLanes} of ${detection.lanes} accelerators. ',
      );
    }

    if (detection.budgetPerSecond != null) {
      caption.write(
        'This host manages about ${detection.budgetPerSecond!.round()} looks a second',
      );
      if (detection.cameras != null && detection.cameras! > 0) {
        caption.write(', shared between ${detection.cameras} cameras');
      }
      // Named only when there is more than one possibility worth distinguishing. On a CPU-only
      // deployment this reads as it always did.
      final backend = detection.backend;
      if (backend != null && backend.isNotEmpty) {
        caption.write(' on $backend');
      }
      caption.write('.');
    }

    if ((detection.shedPerSecond ?? 0) > 0.05) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Skipping ${detection.shedPerSecond!.toStringAsFixed(1)} places a second that '
        'something moved in.',
      );
    }

    if ((detection.droppedFramesPerSecond ?? 0) > 0.05) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Dropping ${detection.droppedFramesPerSecond!.toStringAsFixed(1)} frames a second '
        'before they were examined.',
      );
    }

    if (caption.isEmpty && detection.coverage != null) {
      caption.write('Everything movement suggested looking at was looked at.');
    }

    return VitalsMeter(
      label: 'Detection coverage',
      // A fraction on the wire, a percentage on the bar, like every other meter here.
      percent: detection.coverage == null ? null : detection.coverage! * 100,
      caption: caption.isEmpty ? null : caption.toString(),
      // A still scene proposes nothing to look at, so there is no coverage to report and that is
      // the healthy case — saying so beats an empty bar that reads as a failure.
      unavailableReason: detection.coverage == null
          ? (detection.unavailableReason ??
                'Nothing has needed examining since the last sample.')
          : null,
    );
  }

  Widget _cpu(CpuStats cpu, VitalsSeries? history) {
    final caption = StringBuffer();

    if (cpu.hostPercent != null) {
      // Two figures, because they answer different questions and the difference between them is
      // itself informative: Serval at 20% on a box at 90% means something else is the problem.
      caption.write(
        'The whole machine is at ${formatPercent(cpu.hostPercent)}',
      );
      if (cpu.cores != null) {
        caption.write(' across ${cpu.cores!.round()} cores');
      }
      caption.write('.');
    }

    if (cpu.loadAverage case final load? when load.length >= 3) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Load ${load[0].toStringAsFixed(1)}, ${load[1].toStringAsFixed(1)}, '
        '${load[2].toStringAsFixed(1)}.',
      );
    }

    return VitalsMeter(
      label: 'Processor',
      percent: cpu.containerPercent,
      caption: caption.isEmpty ? null : caption.toString(),
      // Only a reason that means the figure is genuinely unavailable. "Waiting for a second
      // sample" is the Server saying it will have one shortly, and greying the meter out for the
      // first five seconds after a restart would be a worse answer than an empty bar.
      unavailableReason: cpu.containerPercent == null
          ? cpu.unavailableReason
          : null,
      history: history,
    );
  }

  Widget _memory(MemoryStats memory, VitalsSeries? history) => VitalsMeter(
    label: 'Memory',
    percent: memory.percent,
    figure: memory.usedBytes == null
        ? null
        : memory.limitBytes == null
        ? formatBytes(memory.usedBytes)
        : '${formatBytes(memory.usedBytes)} of ${formatBytes(memory.limitBytes)}',
    caption: memory.limitBytes == null
        ? 'No limit is set for this container, so there is no ceiling to be near.'
        : 'Past the limit the container is stopped without warning.',
    unavailableReason: memory.unavailableReason,
    history: history,
  );

  Widget _gpu(GpuStats gpu, VitalsSeries? history) {
    final caption = StringBuffer();

    // The meter is the busiest engine, so the split is what says which one — and on a recording
    // server that is the whole story. A box at 40% because ffmpeg is encoding and a box at 40%
    // because the vision model is running are different boxes.
    if (gpu.engines.isNotEmpty) {
      final busiest = [...gpu.engines]
        ..sort((a, b) => b.busyPercent.compareTo(a.busyPercent));
      final named = busiest
          .where((engine) => engine.busyPercent >= 1)
          .take(3)
          .map((engine) => '${engine.name} ${engine.busyPercent.round()}%')
          .join(', ');

      caption.write(
        named.isEmpty
            ? 'Every engine idle.'
            : '${named[0].toUpperCase()}${named.substring(1)}.',
      );
    }

    if (gpu.hostWide) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write('The whole GPU, not just Serval’s share.');
    }
    if (gpu.vramUsedBytes != null && gpu.vramTotalBytes != null) {
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        '${formatBytes(gpu.vramUsedBytes)} of ${formatBytes(gpu.vramTotalBytes)} video memory.',
      );
    }
    if ((gpu.busyPercent ?? 100) < 5) {
      // Worth saying out loud: on the default configuration this reads near zero and is correct.
      // Video is copied rather than re-encoded unless a stream opts into it, and the vision model
      // runs on the processor unless it is told otherwise.
      if (caption.isNotEmpty) caption.write(' ');
      caption.write(
        'Idle is normal — video is recorded untouched unless a stream asks to be re-encoded.',
      );
    }

    return VitalsMeter(
      label: gpu.driver == null ? 'Graphics' : 'Graphics · ${gpu.driver}',
      percent: gpu.busyPercent,
      caption: caption.isEmpty ? null : caption.toString(),
      unavailableReason: gpu.unavailableReason,
      history: history,
    );
  }
}

/// What each camera is holding, biggest first — directly under the volume it explains.
class _ByCamera extends StatelessWidget {
  const _ByCamera({required this.disk});

  final DiskStats disk;

  @override
  Widget build(BuildContext context) {
    final cameras = disk.cameras;
    final largest = cameras.isEmpty
        ? 0
        : cameras.map((c) => c.bytes).reduce((a, b) => a > b ? a : b);

    return SettingsSection(
      title: 'What is using it',
      blurb: Text(
        cameras.isEmpty
            ? 'Measured by walking each camera’s directory, which this Server is not doing — '
                  'set Serval:Vitals:DiskScanMinutes above zero to turn it on.'
            : 'Measured from the files themselves rather than from the recording index, so this '
                  'includes anything left behind that the index no longer knows about.',
      ),
      trailing: disk.scanSeconds == null
          ? null
          : Text(
              'scanned in ${disk.scanSeconds!.toStringAsFixed(1)}s',
              style: monoStyle(
                fontSize: 11,
                color: Nocturne.mix(Nocturne.text, 32),
              ),
            ),
      child: cameras.isEmpty
          ? const SizedBox.shrink()
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                for (final camera in cameras)
                  StorageRow(
                    label: camera.label,
                    bytes: camera.bytes,
                    largestBytes: largest,
                    muted: camera.cameraId == null,
                    detail: _detail(camera),
                  ),
              ],
            ),
    );
  }

  /// `back 7 days · keeping 7 days · about 59 GB/day` — the byte count given a span, which is
  /// the only form in which it answers a question somebody actually has.
  ///
  /// A directory that is not a camera has no span and no retention, so it carries a note instead:
  /// what it is, and — for saved clips — that nothing prunes it.
  static String? _detail(CameraDiskUsage camera) {
    if (camera.note case final note?) return note;

    final parts = <String>[];

    if (camera.oldestSegmentAt case final oldest?) {
      parts.add('back ${formatSpan(DateTime.now().difference(oldest))}');
    }
    if (camera.retentionDays case final days?) {
      parts.add('keeping ${days == 1 ? '1 day' : '$days days'}');
    }
    if (camera.bytesPerDay != null) {
      parts.add('about ${formatBytesPerDay(camera.bytesPerDay)}');
    }

    return parts.isEmpty ? null : parts.join(' · ');
  }
}
