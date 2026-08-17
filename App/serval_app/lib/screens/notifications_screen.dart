import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/camera_record.dart';
import '../data/providers.dart';
import '../data/serval_api.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/push.dart';
import '../models/server_camera_defaults.dart';
import '../models/user_preferences.dart';
import '../push/push_client.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/alert_row.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/link_text.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_toggle.dart';
import '../widgets/section_kicker.dart';
import '../widgets/settings_cards.dart';
import '../widgets/storage_bar.dart';

part 'notifications/cameras.dart';
part 'notifications/surfaces.dart';

/// Being told about an alert while nobody is looking at the screen.
///
/// **This page is the signed-in person's own**, unlike the rest of settings. Any role reaches it,
/// nothing on it is administrative, and two people looking at the same house see different answers
/// here. That is the whole reason it is a page rather than a group on the server settings screen.
///
/// Design 15b, and 15c for the same thing at 412px. It asks two questions and nothing else:
///
/// **May this browser notify me** — a browser-level permission, one per machine, and the part that
/// fails on a deployment without HTTPS. One card, whose headline is whichever of five things is
/// true, and a chip per device the account has registered.
///
/// **What am I told about** — a card per camera, wrapping three across on a desktop and one across
/// on a phone, each holding only the classes that camera can actually raise. A preference stored on
/// the Server, and the third and last link in the chain that decides whether somebody is
/// interrupted.
///
/// **The per-camera lists can only narrow.** Whether an alert exists at all was settled long before
/// this by the deployment's alert classes and then the camera's own override; a notification needs
/// an alert to carry it. So the chips offered for a camera are that camera's *effective* alert
/// classes, and there is deliberately no way to type one in — an option that could never arrive
/// would be a promise the page cannot keep. Widening is a change to the camera, on the camera's own
/// settings page, and needs an Admin.
class NotificationsScreen extends ConsumerStatefulWidget {
  const NotificationsScreen({super.key});

  @override
  ConsumerState<NotificationsScreen> createState() =>
      _NotificationsScreenState();
}

class _NotificationsScreenState extends ConsumerState<NotificationsScreen> {
  late final ServalRepository _repository = ref.read(repositoryProvider);

  final _search = TextEditingController();

  PushConfig? _config;
  List<PushDevice> _devices = const [];
  ServerCameraDefaults _defaults = ServerCameraDefaults.unknown;

  /// Which registered device is *this* browser, or null when this one is not registered.
  ///
  /// Read from the browser rather than inferred from [_devices]: the same account may be registered
  /// on three machines, and only one of them is this one. Kept as the id rather than a flag so the
  /// chip row can mark which chip you are sitting at.
  String? _hereId;

  /// Whether the narrow list is showing its search field. The bar keeps a magnifier rather than the
  /// field, so a phone that is not searching gives the whole width to the cards.
  bool _searching = false;

  bool _busy = false;
  String? _error;
  String? _note;

  bool get _subscribedHere => _hereId != null;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final config = await _repository.pushConfig();
      final devices = await _repository.pushDevices();
      final settings = await _repository.settings();
      final here = await PushClient.current();

      if (!mounted) return;
      setState(() {
        _config = config;
        _devices = devices;
        _defaults = ServerCameraDefaults.from(settings);
        _hereId = here == null ? null : pushDeviceIdFor(here.endpoint);
        _error = null;
      });
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() => _error = error.message);
    }
  }

  /// Turns this browser on: asks the browser, then tells the Server what it said.
  Future<void> _enableHere() async {
    if (_config case final config?) {
      await _guard(() async {
        final subscription = await PushClient.subscribe(config.vapidPublicKey);

        if (subscription == null) {
          // Refused. Not an error — a choice — but the browser gives a page no way to ask again,
          // so saying where the switch actually lives is the only useful thing left.
          setState(
            () => _note =
                'Your browser refused notifications for this site. Nothing here can ask '
                'again — it has to be changed in the browser’s own site settings.',
          );
          return;
        }

        await _repository.registerPushDevice(
          endpoint: subscription.endpoint,
          p256dh: subscription.p256dh,
          auth: subscription.auth,
        );

        _devices = await _repository.pushDevices();
        _hereId = pushDeviceIdFor(subscription.endpoint);
        _note = null;
      });
    }
  }

  Future<void> _disableHere() async {
    await _guard(() async {
      if (await PushClient.current() case final subscription?) {
        await _repository.unregisterPushDevice(
          pushDeviceIdFor(subscription.endpoint),
        );
      }

      await PushClient.unsubscribe();

      _devices = await _repository.pushDevices();
      _hereId = null;
      _note = null;
    });
  }

  Future<void> _sendTest() async {
    await _guard(() async {
      final result = await _repository.sendTestNotification();

      // Worded as "accepted" rather than "sent": a push service taking the message is all that is
      // knowable from here. A phone that is switched off counts, and is shown it later.
      _note = result.accepted == 0
          ? 'No push service accepted the test. Nothing will arrive until that is fixed.'
          : 'Sent to ${result.accepted} of ${result.devices} '
                '${result.devices == 1 ? 'device' : 'devices'}. It can take a few seconds.';
    });
  }

  Future<void> _forget(PushDevice device) async {
    await _guard(() async {
      // The path for retiring a machine you are not sitting at, which is why it cannot also
      // unsubscribe the browser — only that browser can do that. It will notice on its next launch
      // and either re-register or stay gone. [_disableHere] is the path for this browser.
      await _repository.unregisterPushDevice(device.id);
      _devices = await _repository.pushDevices();
    });
  }

  Future<void> _setEnabled(bool enabled) =>
      _guard(() => _repository.saveNotificationPreferences(enabled: enabled));

  /// Stores a changed rule, dropping it entirely when it no longer says anything.
  ///
  /// A rule equal to the default is removed rather than written, so the stored set holds choices
  /// rather than a row for every camera that exists — which is what keeps "a camera I have never
  /// thought about notifies me" true as cameras are added.
  Future<void> _setRule(CameraNotificationRule rule) => _guard(() async {
    final rules = [
      for (final existing in _repository.notificationPreferences.notifications)
        if (existing.cameraId != rule.cameraId) existing,
      if (!rule.isDefault) rule,
    ];

    await _repository.saveNotificationPreferences(rules: rules);
  });

  /// Runs one action with the page marked busy, turning a failed call into a message rather than
  /// an exception nobody sees.
  Future<void> _guard(Future<void> Function() action) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await action();
    } on ServalApiException catch (error) {
      _error = error.message;
    } on Object catch (error) {
      // The browser's own failures land here — a service worker that would not register, a push
      // service that refused the subscription. They are not ServalApiExceptions and they are
      // exactly what somebody debugging this needs to see.
      _error = error.toString();
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// The device row for this browser, when there is one. Its `lastSuccessAt` is the only thing that
  /// can say whether the machine you are sitting at has ever actually been reached.
  PushDevice? get _hereDevice {
    for (final device in _devices) {
      if (device.id == _hereId) return device;
    }
    return null;
  }

  List<CameraRecord> get _matchingCameras {
    final query = _search.text.trim().toLowerCase();
    final cameras = _repository.cameraRecords();

    if (query.isEmpty) return cameras;
    return [
      for (final camera in cameras)
        if (camera.name.toLowerCase().contains(query)) camera,
    ];
  }

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: const BoxDecoration(color: Serval.panel),
    child: isCompact(context) ? _compact() : _wide(),
  );

  // --------------------------------------------------------------------------------------- wide

  Widget _wide() {
    final preferences = _repository.notificationPreferences;
    final known = _repository.preferencesKnown;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(28, 24, 28, 0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ..._messages(),
              _PageHeader(
                enabled: preferences.notificationsEnabled,
                onChanged: _busy || !known ? null : _setEnabled,
              ),
              const SizedBox(height: 18),
              _BrowserCard(
                state: _browserState,
                devices: _devices,
                hereId: _hereId,
                busy: _busy,
                canToggle: !_busy && _config != null,
                canTest: _subscribedHere,
                onEnable: _enableHere,
                onDisable: _disableHere,
                onTest: _sendTest,
                onForget: _forget,
              ),
              const SizedBox(height: 18),
              _SectionHeader(
                count: _repository.cameraRecords().length,
                search: _search,
                onSearchChanged: () => setState(() {}),
              ),
              const SizedBox(height: 11),
            ],
          ),
        ),
        Expanded(
          child: _CameraGrid(
            cameras: _matchingCameras,
            query: _search.text.trim(),
            preferences: preferences,
            defaults: _defaults,
            enabled: known && preferences.notificationsEnabled && !_busy,
            padding: const EdgeInsets.fromLTRB(28, 0, 28, 26),
            onChanged: _setRule,
          ),
        ),
      ],
    );
  }

  // ------------------------------------------------------------------------------------ compact

  Widget _compact() {
    final preferences = _repository.notificationPreferences;
    final known = _repository.preferencesKnown;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        CompactAppBar(
          title: 'Notifications',
          onBack: () => context.go('/settings'),
          actions: [
            CompactBarAction(
              icon: PhosphorIconsRegular.magnifyingGlass,
              tooltip: _searching ? 'Close search' : 'Find a camera',
              selected: _searching,
              onPressed: () => setState(() {
                _searching = !_searching;
                if (!_searching) _search.clear();
              }),
            ),
          ],
        ),
        if (_searching)
          CompactSubBar(
            children: [
              SettingsSearchBox(
                controller: _search,
                onChanged: () => setState(() {}),
                placeholder: 'Find a camera',
              ),
            ],
          ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ..._messages(),
              _PhoneHeaderCard(
                state: _browserState,
                here: _hereDevice,
                devices: _devices,
                hereId: _hereId,
                notifyAll: preferences.notificationsEnabled,
                onNotifyAll: _busy || !known ? null : _setEnabled,
                busy: _busy,
                canToggle: !_busy && _config != null,
                canTest: _subscribedHere,
                onEnable: _enableHere,
                onDisable: _disableHere,
                onTest: _sendTest,
                onForget: _forget,
              ),
              const SizedBox(height: 16),
              Row(
                crossAxisAlignment: CrossAxisAlignment.baseline,
                textBaseline: TextBaseline.alphabetic,
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  const SectionKicker('Cameras'),
                  Text(
                    _cameraCount(_repository.cameraRecords().length),
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 11.5,
                      color: Nocturne.mix(Nocturne.text, 40),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 9),
            ],
          ),
        ),
        Expanded(
          child: _CameraGrid(
            cameras: _matchingCameras,
            query: _search.text.trim(),
            preferences: preferences,
            defaults: _defaults,
            enabled: known && preferences.notificationsEnabled && !_busy,
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 20),
            onChanged: _setRule,
          ),
        ),
      ],
    );
  }

  // ------------------------------------------------------------------------------------- shared

  _BrowserState get _browserState {
    if (!PushClient.isSupported) return _BrowserState.unsupported;
    if (PushClient.permission == PushPermission.denied) {
      return _BrowserState.denied;
    }
    if (_config?.enabled == false) return _BrowserState.serverOff;
    if (!_subscribedHere) return _BrowserState.unregistered;
    return _BrowserState.ready;
  }

  List<Widget> _messages() => [
    if (_error case final error?) ...[
      VitalsAlertStrip(
        message: error,
        critical: true,
        boxed: true,
        padding: const EdgeInsets.all(12),
      ),
      const SizedBox(height: 14),
    ],
    if (_note case final note?) ...[
      _Note(message: note),
      const SizedBox(height: 14),
    ],
  ];
}

String _cameraCount(int count) => '$count ${count == 1 ? 'camera' : 'cameras'}';

/// Which of the five things that can be true about this browser is.
///
/// Five facts rather than one sentence with holes in it: the reason nothing arrives is different in
/// every one of them, and only one of the five is fixed on this page.
enum _BrowserState {
  /// No service worker, which on a real deployment almost always means plain HTTP.
  unsupported,

  /// The person said no, and a page cannot ask twice.
  denied,

  /// An admin has push switched off deployment-wide.
  serverOff,

  /// It could be registered and is not.
  unregistered,

  /// Registered, and the server will send.
  ready;

  String get headline => switch (this) {
    unsupported => 'This browser cannot show notifications',
    denied => 'You have refused notifications for this site',
    serverOff => 'This server has notifications switched off',
    unregistered => 'This browser is not registered',
    ready => 'This browser is allowed to notify you',
  };

  String get detail => switch (this) {
    // Much the most likely reason on a real deployment, and the one that is invisible from the
    // browser's own error messages: service workers are withheld outside a secure context, so a
    // Serval reached over plain HTTP simply has no push at all.
    unsupported =>
      'The usual reason is that the page is not being served over HTTPS — browsers withhold the '
          'machinery entirely on a plain HTTP address, apart from localhost. A reverse proxy or a '
          'VPN with TLS is what turns this back on. See Docs/deployment.md.',
    denied =>
      'Nothing on this page can ask again — browsers deliberately give a page no way to '
          're-prompt — so it has to be changed in the browser’s own site settings.',
    serverOff =>
      'Nothing would be sent even after you turn this on. An admin can change that under Server '
          'settings → Notifications.',
    unregistered =>
      'Turning this on asks the browser for permission and registers this machine. What you are '
          'told about is set below, and is shared by every device you register.',
    ready =>
      'Permission belongs to the browser, not the account — each machine you sign in on asks '
          'again.',
  };

  /// The same fact in the room a phone has for it.
  String shortLine(PushDevice? here) => switch (this) {
    unsupported => 'Not available over plain HTTP',
    denied => 'Refused in this browser',
    serverOff => 'Turned off on this server',
    unregistered => 'Not registered',
    ready =>
      here?.lastSuccessAt == null
          ? 'Never notified'
          : 'Last notified ${_whenLabel(here!.lastSuccessAt!)}',
  };

  PhosphorIconData get icon => switch (this) {
    unsupported || denied || serverOff => PhosphorIconsRegular.bellSlash,
    unregistered || ready => PhosphorIconsRegular.bellRinging,
  };

  /// Warm for the three that will not deliver, accent for the two that will. The same hue the
  /// never-notified chip spends, and for the same claim: look at this.
  bool get wrong => this == unsupported || this == denied || this == serverOff;
}

/// `8:12 am` today, `3 Aug` before that — a clock is only a useful answer for the day you are on.
String _whenLabel(DateTime at) {
  final now = DateTime.now();
  final today =
      at.year == now.year && at.month == now.month && at.day == now.day;
  return today ? clockLabel(at) : dayLabel(at);
}
