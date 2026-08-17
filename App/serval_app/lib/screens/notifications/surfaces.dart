part of '../notifications_screen.dart';

// ------------------------------------------------------------------------------------------ wide

/// The page's own title, and the one switch that overrules everything under it.
class _PageHeader extends StatelessWidget {
  const _PageHeader({required this.enabled, required this.onChanged});

  final bool enabled;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.end,
    children: [
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text(
              'Notifications',
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
              'Yours alone — what anyone else signed in here is told is their own setting.',
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
      Column(
        crossAxisAlignment: CrossAxisAlignment.end,
        mainAxisSize: MainAxisSize.min,
        children: [
          const Text(
            'Send me notifications',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 13,
              color: Nocturne.text,
            ),
          ),
          const SizedBox(height: 1),
          Text(
            'Off for an evening — nothing below is lost',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 42),
            ),
          ),
        ],
      ),
      const SizedBox(width: 11),
      NocturneToggle(value: enabled, onChanged: onChanged),
    ],
  );
}

/// Whether this browser may notify you, and every browser the account has registered.
class _BrowserCard extends StatelessWidget {
  const _BrowserCard({
    required this.state,
    required this.devices,
    required this.hereId,
    required this.busy,
    required this.canToggle,
    required this.canTest,
    required this.onEnable,
    required this.onDisable,
    required this.onTest,
    required this.onForget,
  });

  final _BrowserState state;
  final List<PushDevice> devices;
  final String? hereId;
  final bool busy;
  final bool canToggle;
  final bool canTest;
  final Future<void> Function() onEnable;
  final Future<void> Function() onDisable;
  final Future<void> Function() onTest;
  final Future<void> Function(PushDevice) onForget;

  @override
  Widget build(BuildContext context) {
    final hue = state.wrong ? Serval.alert : Nocturne.accent;

    return _CardFrame(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
          child: Row(
            children: [
              Container(
                width: 32,
                height: 32,
                decoration: BoxDecoration(
                  color: Nocturne.mix(hue, 14),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Center(
                  child: PhosphorIcon(
                    state.icon,
                    size: 16,
                    color: state.wrong ? Serval.alertText : Nocturne.accent400,
                  ),
                ),
              ),
              const SizedBox(width: 13),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      state.headline,
                      style: const TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13.5,
                        fontWeight: Nocturne.headingWeight,
                        color: Nocturne.text,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(state.detail, style: settingHelpStyle()),
                  ],
                ),
              ),
              if (canTest) ...[
                const SizedBox(width: 13),
                NocturneButton(
                  label: 'Send a test',
                  icon: PhosphorIconsRegular.paperPlaneTilt,
                  height: 32,
                  fontSize: 12.5,
                  onPressed: busy ? null : () => onTest(),
                ),
              ],
              const SizedBox(width: 11),
              NocturneToggle(
                value: state == _BrowserState.ready || hereId != null,
                onChanged:
                    canToggle &&
                        state != _BrowserState.unsupported &&
                        state != _BrowserState.denied
                    ? (value) => value ? onEnable() : onDisable()
                    : null,
              ),
            ],
          ),
        ),
        if (devices.isNotEmpty)
          _DeviceBand(
            devices: devices,
            hereId: hereId,
            busy: busy,
            onForget: onForget,
          ),
      ],
    );
  }
}

/// The section's own heading, its count, and the way to find one camera among nine.
class _SectionHeader extends StatelessWidget {
  const _SectionHeader({
    required this.count,
    required this.search,
    required this.onSearchChanged,
  });

  final int count;
  final TextEditingController search;
  final VoidCallback onSearchChanged;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      const Text(
        'Cameras',
        style: TextStyle(
          fontFamily: Nocturne.fontHeading,
          fontSize: 14.5,
          fontWeight: Nocturne.headingWeight,
          color: Nocturne.text,
        ),
      ),
      const SizedBox(width: 12),
      Flexible(
        child: Text(
          '${_cameraCount(count)} · each shows only what it can actually alert on.',
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12.5,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      ),
      const SizedBox(width: 12),
      SizedBox(
        width: 190,
        child: SettingsSearchBox(
          controller: search,
          onChanged: onSearchChanged,
          placeholder: 'Find a camera',
        ),
      ),
    ],
  );
}

// --------------------------------------------------------------------------------------- compact

/// 15c's one card: the master switch, this device, and the chips for every device registered.
///
/// The chips are shown here rather than folded behind the count, because forgetting a machine you
/// are not sitting at is the only way to retire it and a phone is often the only thing to hand.
class _PhoneHeaderCard extends StatelessWidget {
  const _PhoneHeaderCard({
    required this.state,
    required this.here,
    required this.devices,
    required this.hereId,
    required this.notifyAll,
    required this.onNotifyAll,
    required this.busy,
    required this.canToggle,
    required this.canTest,
    required this.onEnable,
    required this.onDisable,
    required this.onTest,
    required this.onForget,
  });

  final _BrowserState state;
  final PushDevice? here;
  final List<PushDevice> devices;
  final String? hereId;
  final bool notifyAll;
  final ValueChanged<bool>? onNotifyAll;
  final bool busy;
  final bool canToggle;
  final bool canTest;
  final Future<void> Function() onEnable;
  final Future<void> Function() onDisable;
  final Future<void> Function() onTest;
  final Future<void> Function(PushDevice) onForget;

  @override
  Widget build(BuildContext context) => _CardFrame(
    children: [
      _PhoneRow(
        title: 'Send me notifications',
        control: NocturneToggle(value: notifyAll, onChanged: onNotifyAll),
        detail: Text(
          'Off for an evening — nothing below is lost',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      ),
      _PhoneRow(
        title: 'This device',
        topBorder: true,
        control: NocturneToggle(
          value: hereId != null,
          onChanged:
              canToggle &&
                  state != _BrowserState.unsupported &&
                  state != _BrowserState.denied
              ? (value) => value ? onEnable() : onDisable()
              : null,
        ),
        detail: Row(
          children: [
            Flexible(
              child: Text(
                state.shortLine(here),
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  color: state.wrong
                      ? Serval.alertText
                      : Nocturne.mix(Nocturne.text, 45),
                ),
              ),
            ),
            if (canTest) ...[
              Text(
                ' · ',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  color: Nocturne.mix(Nocturne.text, 45),
                ),
              ),
              LinkText(
                'send a test',
                onTap: busy ? null : onTest,
                fontSize: 11.5,
              ),
            ],
          ],
        ),
      ),
      if (devices.isNotEmpty)
        _DeviceBand(
          devices: devices,
          hereId: hereId,
          busy: busy,
          onForget: onForget,
        ),
    ],
  );
}

/// One band of the phone's header card: a title, a line under it, a switch at the end.
class _PhoneRow extends StatelessWidget {
  const _PhoneRow({
    required this.title,
    required this.detail,
    required this.control,
    this.topBorder = false,
  });

  final String title;
  final Widget detail;
  final Widget control;
  final bool topBorder;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(13, 12, 13, 12),
    decoration: topBorder
        ? BoxDecoration(
            border: Border(top: BorderSide(color: Serval.hairline)),
          )
        : null,
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                title,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 14,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(height: 2),
              detail,
            ],
          ),
        ),
        const SizedBox(width: 12),
        control,
      ],
    ),
  );
}

// -------------------------------------------------------------------------------------- devices

/// The band of chips under the browser card: one per browser the account has registered.
class _DeviceBand extends StatelessWidget {
  const _DeviceBand({
    required this.devices,
    required this.hereId,
    required this.busy,
    required this.onForget,
  });

  final List<PushDevice> devices;
  final String? hereId;
  final bool busy;
  final Future<void> Function(PushDevice) onForget;

  @override
  Widget build(BuildContext context) {
    final silent = devices.any((device) => device.lastSuccessAt == null);

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(14, 10, 14, 10),
      decoration: BoxDecoration(
        color: Nocturne.mix(Nocturne.text, 2),
        border: Border(top: BorderSide(color: Serval.hairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Wrap(
            spacing: 10,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              const Padding(
                padding: EdgeInsets.only(right: 4),
                child: SectionKicker('Registered devices'),
              ),
              for (final device in devices)
                _DeviceChip(
                  device: device,
                  here: device.id == hereId,
                  onForget: busy ? null : () => onForget(device),
                ),
            ],
          ),
          if (silent) ...[
            const SizedBox(height: 8),
            Text(
              'A device that has never been notified is the visible symptom of a setup that '
              'isn’t working.',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 11.5,
                height: 1.35,
                color: Nocturne.mix(Nocturne.text, 40),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// One registered browser: what it is, when it was last reached, and the way to retire it.
class _DeviceChip extends StatelessWidget {
  const _DeviceChip({
    required this.device,
    required this.here,
    required this.onForget,
  });

  final PushDevice device;
  final bool here;
  final VoidCallback? onForget;

  /// The server's `Describe` emits `Browser on Platform` from a fixed vocabulary, so the platform
  /// half is all that has to be recognised to know which kind of machine this is.
  bool get _handheld {
    final label = device.label?.toLowerCase() ?? '';
    return label.endsWith('iphone') ||
        label.endsWith('ipad') ||
        label.endsWith('android');
  }

  /// A device the server has never successfully reached. The whole reason the chip carries a date.
  bool get _silent => device.lastSuccessAt == null;

  String get _suffix {
    final when = _silent
        ? 'never notified'
        : 'notified ${_whenLabel(device.lastSuccessAt!)}';
    return here ? 'this one · $when' : when;
  }

  @override
  Widget build(BuildContext context) {
    final border = _silent
        ? Serval.alert.withValues(alpha: 0.4)
        : here
        ? Nocturne.mix(Nocturne.accent, 45)
        : Nocturne.mix(Nocturne.text, 14);

    final fill = _silent
        ? Serval.alert.withValues(alpha: 0.07)
        : here
        ? Nocturne.mix(Nocturne.accent, 10)
        : null;

    return Container(
      height: 26,
      padding: const EdgeInsets.only(left: 9, right: 5),
      decoration: BoxDecoration(
        color: fill,
        borderRadius: BorderRadius.circular(20),
        border: Border.all(color: border),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          PhosphorIcon(
            _handheld
                ? PhosphorIconsRegular.deviceMobile
                : PhosphorIconsRegular.desktop,
            size: 14,
            color: _silent
                ? Serval.alert
                : here
                ? Nocturne.accent400
                : Nocturne.mix(Nocturne.text, 45),
          ),
          const SizedBox(width: 7),
          // Both halves shrink rather than overflow. The label is a user-agent the server has
          // shortened, not a name anything here chose, and the row it sits in is as narrow as a
          // phone — a chip that cannot give is a chip that spills onto the one beside it.
          Flexible(
            child: Text(
              device.label ?? 'Unknown browser',
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: here ? Nocturne.text : Nocturne.mix(Nocturne.text, 80),
              ),
            ),
          ),
          const SizedBox(width: 7),
          Flexible(
            child: Text(
              _suffix,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 11,
                color: _silent
                    ? Serval.alert.withValues(alpha: 0.85)
                    : Nocturne.mix(Nocturne.text, 42),
              ),
            ),
          ),
          const SizedBox(width: 7),
          Semantics(
            button: true,
            label: 'Forget ${device.label ?? 'this device'}',
            child: MouseRegion(
              cursor: onForget == null
                  ? SystemMouseCursors.basic
                  : SystemMouseCursors.click,
              child: GestureDetector(
                onTap: onForget,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  child: PhosphorIcon(
                    PhosphorIconsRegular.x,
                    size: 12,
                    color: Nocturne.mix(
                      Nocturne.text,
                      onForget == null ? 20 : 40,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
