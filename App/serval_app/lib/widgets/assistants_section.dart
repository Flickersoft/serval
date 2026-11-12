import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import '../data/time_labels.dart';
import '../models/assistants.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_field.dart';

/// The AI assistants this person has let read Serval, and the address to connect another.
///
/// **On the Account screen, not the Server's, because a link is a person's.** Each one was approved
/// by somebody signed in, acts with their account, and is listed only to them — the same reason
/// notification devices are per person. An Admin turning the feature off ends them all, which is
/// the deployment's say; which assistants exist is each person's.
///
/// The connect address is shown here because this is where somebody comes looking for it, and it
/// is the one value every assistant's "add a connector" box asks for.
///
/// Prop-driven like everything under `widgets/`, so every state can be pumped into a widget test.
class AssistantsSection extends StatelessWidget {
  const AssistantsSection({
    super.key,
    required this.status,
    required this.links,
    this.onUnlink,
    this.busy = false,
    this.error,
  });

  final AssistantStatus? status;
  final List<AssistantLink> links;
  final void Function(AssistantLink link)? onUnlink;
  final bool busy;
  final String? error;

  @override
  Widget build(BuildContext context) {
    final effective = status?.effective ?? false;

    return SettingsSection(
      title: 'AI assistants',
      blurb: const Text(
        'Assistants such as Claude or Gemini that you have let read this Serval. They can look at '
        'cameras, recordings, transcripts and alerts, and cannot change anything. Whatever they '
        'look at is sent to the company that runs them.',
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (effective && status?.serverUrl != null) ...[
            _ConnectAddress(url: status!.serverUrl!),
            const SizedBox(height: 16),
          ],

          // Rendered verbatim and not in the alert colour: "switched off" is a default, not a
          // fault, and the sentence comes from the Server so a new condition explains itself.
          if (!effective && status?.reason != null) ...[
            Text(
              status!.reason!,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.45,
                color: Nocturne.mix(Nocturne.text, 55),
              ),
            ),
            const SizedBox(height: 12),
          ],

          if (links.isEmpty && effective)
            Text(
              'Nothing linked yet.',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),

          for (final link in links) ...[
            _LinkRow(
              link: link,
              onUnlink: busy || onUnlink == null ? null : () => onUnlink!(link),
            ),
            const SizedBox(height: 12),
          ],

          if (error case final message?)
            Text(
              message,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.4,
                color: Serval.alertText,
              ),
            ),
        ],
      ),
    );
  }
}

class _ConnectAddress extends StatefulWidget {
  const _ConnectAddress({required this.url});

  final String url;

  @override
  State<_ConnectAddress> createState() => _ConnectAddressState();
}

class _ConnectAddressState extends State<_ConnectAddress> {
  /// Says "Copied" for a moment after the button is pressed — a clipboard write is otherwise
  /// invisible, and somebody about to paste into another app wants to know it took.
  Timer? _copied;

  @override
  void dispose() {
    _copied?.cancel();
    super.dispose();
  }

  Future<void> _copy() async {
    await Clipboard.setData(ClipboardData(text: widget.url));
    if (!mounted) return;
    _copied?.cancel();
    setState(
      () => _copied = Timer(const Duration(seconds: 2), () {
        if (mounted) setState(() => _copied = null);
      }),
    );
  }

  String get url => widget.url;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        children: [
          Expanded(
            child: NocturneReadOnlyField(
              label: 'Connector address',
              value: url,
              mono: true,
            ),
          ),
          const SizedBox(width: 10),
          Padding(
            padding: const EdgeInsets.only(top: 18),
            child: NocturneButton(
              label: _copied == null ? 'Copy' : 'Copied',
              onPressed: _copy,
            ),
          ),
        ],
      ),
      const SizedBox(height: 8),
      Text(
        'Add this as a custom connector — an MCP server — in your assistant. It will send you back '
        'here to approve it.',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12,
          height: 1.4,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );
}

class _LinkRow extends StatelessWidget {
  const _LinkRow({required this.link, this.onUnlink});

  final AssistantLink link;
  final VoidCallback? onUnlink;

  @override
  Widget build(BuildContext context) {
    final linked = link.createdAt == null ? '—' : _whenLabel(link.createdAt!);
    final used = link.lastUsedAt == null
        ? 'not used yet'
        : 'last used ${_whenLabel(link.lastUsedAt!)}';

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                link.displayName,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(height: 3),
              // Verified or not, because a self-registered app's name is only its own claim.
              Text(
                link.verified
                    ? 'Verified as ${link.verifiedHost}'
                    : 'Unverified — registered itself',
                style: monoStyle(
                  fontSize: 11.5,
                  color: link.verified
                      ? Nocturne.mix(Nocturne.text, 50)
                      : Serval.alertText,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                'Linked $linked · $used',
                style: monoStyle(
                  fontSize: 11.5,
                  color: Nocturne.mix(Nocturne.text, 40),
                ),
              ),
            ],
          ),
        ),
        if (onUnlink != null) ...[
          const SizedBox(width: 12),
          NocturneButton(label: 'Disconnect', onPressed: onUnlink),
        ],
      ],
    );
  }
}

/// `8:12 am` today, `3 Aug` before that — the rule the Google Home links use.
String _whenLabel(DateTime at) {
  final local = at.toLocal();
  final now = DateTime.now();
  final today =
      local.year == now.year &&
      local.month == now.month &&
      local.day == now.day;
  return today ? clockLabel(local) : dayLabel(local);
}
