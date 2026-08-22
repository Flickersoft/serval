import 'package:flutter/widgets.dart';

import '../data/time_labels.dart';
import '../models/google_home.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_field.dart';

/// Whether the cameras are reachable from Google Home, and if not, the one reason why.
///
/// **Nothing here is a setting, which is why it belongs on this page.** Every
/// `Serval:GoogleHome:*` key is environment-only — two are secrets, and two more decide where an
/// anonymous endpoint sends credentials Google issued — so there is no form to render and no value
/// to submit. What is worth showing is the thing an operator cannot get any other way: six
/// conditions have to hold for the integration to answer anything but a 503, and knowing *which*
/// one is unmet is the difference between a minute and an afternoon.
///
/// **The reason sentence comes from the Server and is rendered verbatim.** The same contract the
/// settings page has with the catalogue: the Server owns the wording, so a condition added there
/// explains itself without an App release. Mapping [GoogleHomeStatus.blocker] to local text here
/// would put the two out of step on the first change.
///
/// Prop-driven like everything under `widgets/`, so every state can be pumped into a widget test.
class GoogleHomeSection extends StatelessWidget {
  const GoogleHomeSection({
    super.key,
    required this.status,
    required this.links,
    this.onUnlink,
    this.busy = false,
    this.error,
  });

  /// Null when the status could not be read at all. The section still draws — with [error] — so a
  /// failure reads as a failure rather than as the feature not existing.
  final GoogleHomeStatus? status;

  /// At most one, and usually none.
  final List<GoogleHomeLink> links;

  /// Null hides the action — a Viewer reaching this page reads it and does not act on it.
  final void Function(GoogleHomeLink link)? onUnlink;

  final bool busy;

  final String? error;

  @override
  Widget build(BuildContext context) => SettingsSection(
    title: 'Google Home',
    blurb: Text(
      'Cameras on a Nest Hub or a Chromecast, by voice. Configured entirely in the '
      'deployment’s environment — there is nothing to change here.',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12.5,
        height: 1.45,
        color: Nocturne.mix(Nocturne.text, 50),
      ),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _StateLine(
          effective: status?.effective ?? false,
          linked: links.isNotEmpty,
          unknown: status == null,
        ),

        // The Server's own sentence. Not an error strip: for the overwhelmingly common case —
        // the integration is simply switched off — nothing is wrong, and painting that in the
        // alert colour would make a default look like a fault.
        if (status?.reason case final reason?) ...[
          const SizedBox(height: 12),
          Text(
            reason,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 55),
            ),
          ),
        ],

        if (status?.effective ?? false) ...[
          const SizedBox(height: 14),
          _Fact(
            label: 'Public address',
            // Only ever set when effective, since the gate requires it — but rendered as absent
            // rather than as an empty string if it somehow is not.
            value: status?.publicBaseUrl ?? '—',
          ),
          _Fact(
            label: 'Camera changes',
            value: (status?.homeGraphKeyConfigured ?? false)
                ? 'Pushed to Google automatically'
                : 'Not pushed — re-link, or say “sync my devices”, after adding a camera',
          ),
          // Both answers are a working deployment, so this says what you get rather than what is
          // missing. Unset is not a degraded television, it is no Cast button at all — Google will
          // not send a camera to a television by voice however this is configured.
          _Fact(
            label: 'On a television',
            value: (status?.castReceiverConfigured ?? false)
                ? 'Cast from a camera screen — live, or a recording from where you are'
                : 'No Cast receiver registered, so the app offers no Cast button',
          ),
        ],

        if (links.isEmpty && (status?.effective ?? false)) ...[
          const SizedBox(height: 12),
          Text(
            'No Google account has linked yet. Add it from the Google Home app: '
            'Add → Works with Google.',
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ],

        for (final link in links) ...[
          const SizedBox(height: 14),
          _LinkRow(
            link: link,
            onUnlink: busy || onUnlink == null ? null : () => onUnlink!(link),
          ),
        ],

        if (error case final message?) ...[
          const SizedBox(height: 12),
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
      ],
    ),
  );
}

/// One line saying what state the integration is in, in the words an operator would use.
class _StateLine extends StatelessWidget {
  const _StateLine({
    required this.effective,
    required this.linked,
    this.unknown = false,
  });

  final bool effective;
  final bool linked;

  /// The status could not be read. Distinct from "not active": one is a deployment that has not
  /// turned this on, the other is a question we failed to get an answer to.
  final bool unknown;

  @override
  Widget build(BuildContext context) {
    // Four states. "On but nobody has linked" is a real and common step in the middle of the setup
    // runbook, and collapsing it into "on" would leave somebody waiting for cameras that are never
    // going to appear; "could not be read" must not read as "off", which would send them looking
    // at configuration that is fine.
    final (String label, Color colour) = switch ((unknown, effective, linked)) {
      (true, _, _) => ('Status unavailable', Serval.alertText),
      (false, false, _) => ('Not active', Nocturne.mix(Nocturne.text, 40)),
      (false, true, false) => (
        'Ready — no account linked',
        Nocturne.mix(Nocturne.text, 60),
      ),
      (false, true, true) => ('Active', Serval.healthyText),
    };

    return Row(
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(color: colour, shape: BoxShape.circle),
        ),
        const SizedBox(width: 9),
        Text(
          label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            fontWeight: FontWeight.w600,
            color: colour,
          ),
        ),
      ],
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: 132,
          child: Text(
            label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
          ),
        ),
        Expanded(
          child: Text(
            value,
            style: monoStyle(
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 60),
            ),
          ),
        ),
      ],
    ),
  );
}

class _LinkRow extends StatelessWidget {
  const _LinkRow({required this.link, this.onUnlink});

  final GoogleHomeLink link;
  final VoidCallback? onUnlink;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Linked ${link.linkedAt == null ? '—' : _whenLabel(link.linkedAt!)}',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 55),
              ),
            ),
            const SizedBox(height: 4),
            // The field worth reading. A link that was made and then never used again looks
            // identical to a working one from everything else on this card, and this is what
            // separates them.
            Text(
              link.lastFulfillmentAt == null
                  ? 'Google has not called since linking'
                  : 'Google last called ${_whenLabel(link.lastFulfillmentAt!)}',
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
        NocturneButton(label: 'Unlink', onPressed: onUnlink),
      ],
    ],
  );
}

/// `8:12 am` today, `3 Aug` before that — the same rule the notifications screen uses, because a
/// clock time is only a useful answer for the day you are on.
String _whenLabel(DateTime at) {
  final now = DateTime.now();
  final today =
      at.year == now.year && at.month == now.month && at.day == now.day;
  return today ? clockLabel(at) : dayLabel(at);
}
