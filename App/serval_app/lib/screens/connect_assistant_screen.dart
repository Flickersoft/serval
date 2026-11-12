import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../data/providers.dart';
import '../data/serval_api.dart';
import '../models/assistants.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/source_link.dart';

/// Where a person decides whether an AI assistant may read this Serval — `/connect?request=<id>`.
///
/// **How somebody gets here.** They added Serval to an assistant (Claude, Gemini, anything that
/// speaks MCP). The assistant sent their browser to the Server's authorize endpoint, which checked
/// everything it could, parked the request, and redirected here. If they were signed out, the
/// router took them through `/login` first and brought them back. Allow or Deny goes to the
/// Server, which answers with the address to send the browser to next — back to the assistant,
/// carrying a code or a refusal — and this screen hands the browser over.
///
/// **What it must make plain is who is asking.** Any app can register itself under any name, so the
/// name is never shown alone: a verified client's domain is the one fact about it that is not its
/// own claim, an unverified one says so in the warning colour, and both show where the answer is
/// going. The access granted is all-or-nothing and read-only, and it says that too — including
/// that whatever the assistant looks at leaves the house.
///
/// Outside both shells, like `/connecting`: it is a single decision, and a rail beside it would
/// offer ways to wander off with a request half-answered.
class ConnectAssistantScreen extends ConsumerStatefulWidget {
  const ConnectAssistantScreen({
    super.key,
    required this.requestId,
    this.leave = _replaceThisPage,
  });

  /// Null or empty when the address carried none, which is drawn as an expired request.
  final String? requestId;

  /// Sends the browser to the assistant's callback. A parameter so a test can see where it would
  /// have gone without a platform plugin to answer.
  final Future<void> Function(Uri destination) leave;

  /// `_self`: the assistant's callback replaces this page rather than opening beside it, which is
  /// what an OAuth redirect is — and for a desktop client listening on a loopback port, a new tab
  /// would leave this one stranded.
  static Future<void> _replaceThisPage(Uri destination) =>
      launchUrl(destination, webOnlyWindowName: '_self');

  @override
  ConsumerState<ConnectAssistantScreen> createState() =>
      _ConnectAssistantScreenState();
}

enum _Phase { loading, asking, answering, handedOff, gone, failed }

class _ConnectAssistantScreenState
    extends ConsumerState<ConnectAssistantScreen> {
  _Phase _phase = _Phase.loading;
  AssistantRequest? _request;
  String? _failure;

  /// Which answer was given, for the line shown while the browser leaves.
  bool _approved = false;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    final id = widget.requestId;
    if (id == null || id.isEmpty) {
      setState(() => _phase = _Phase.gone);
      return;
    }

    try {
      final request = await ref.read(repositoryProvider).assistantRequest(id);
      if (!mounted) return;
      setState(() {
        _request = request;
        _phase = _Phase.asking;
      });
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _phase = error.statusCode == 404 ? _Phase.gone : _Phase.failed;
        _failure = '$error';
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _phase = _Phase.failed;
        _failure = '$error';
      });
    }
  }

  Future<void> _answer({required bool approve}) async {
    setState(() {
      _phase = _Phase.answering;
      _approved = approve;
    });

    try {
      final url = await ref
          .read(repositoryProvider)
          .answerAssistantRequest(widget.requestId!, approve: approve);
      if (!mounted) return;
      setState(() => _phase = _Phase.handedOff);
      await widget.leave(Uri.parse(url));
    } on ServalApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _phase = error.statusCode == 404 ? _Phase.gone : _Phase.failed;
        _failure = '$error';
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _phase = _Phase.failed;
        _failure = '$error';
      });
    }
  }

  @override
  Widget build(BuildContext context) => Container(
    color: Nocturne.bg,
    child: Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(
          horizontal: Nocturne.space6,
          vertical: Nocturne.space8,
        ),
        child: ConstrainedBox(
          // The login screen's column, so arriving here from sign-in reads as the same place.
          constraints: const BoxConstraints(maxWidth: 440),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const _Masthead(),
              const SizedBox(height: 18),
              ..._body(),
              const SizedBox(height: 34),
              const SourceLine(),
            ],
          ),
        ),
      ),
    ),
  );

  List<Widget> _body() => switch (_phase) {
    _Phase.loading => [const _Title('Connect an assistant')],
    _Phase.gone => [
      const _Title('This request has ended'),
      const SizedBox(height: 14),
      const _Prose(
        'It expired, or it was already answered. Start connecting again from the assistant — '
        'requests last ten minutes.',
      ),
      const SizedBox(height: 26),
      NocturneButton(
        label: 'Go to Serval',
        variant: NocturneButtonVariant.ghost,
        height: 46,
        onPressed: () => context.go('/wall'),
      ),
    ],
    _Phase.failed => [
      const _Title('Something went wrong'),
      const SizedBox(height: 14),
      _Prose(
        _failure ?? 'The Server did not answer.',
        colour: Serval.alertText,
      ),
      const SizedBox(height: 26),
      NocturneButton(
        label: 'Try again',
        variant: NocturneButtonVariant.ghost,
        height: 46,
        onPressed: () {
          setState(() => _phase = _Phase.loading);
          unawaited(_load());
        },
      ),
    ],
    _Phase.handedOff => [
      _Title(_approved ? 'Connected' : 'Declined'),
      const SizedBox(height: 14),
      _Prose(
        'Returning you to ${_request?.displayName ?? 'the assistant'}. You can close this tab if '
        'it does not move on its own.',
      ),
    ],
    _Phase.asking || _Phase.answering => _asking(_request!),
  };

  List<Widget> _asking(AssistantRequest request) {
    final username = ref.watch(currentUsernameProvider);
    final busy = _phase == _Phase.answering;

    return [
      const _Title('Connect an assistant'),
      const SizedBox(height: 16),
      Text.rich(
        TextSpan(
          children: [
            TextSpan(
              text: request.displayName,
              style: const TextStyle(
                fontWeight: FontWeight.w600,
                color: Nocturne.text,
              ),
            ),
            const TextSpan(text: ' wants to read this Serval for you.'),
          ],
        ),
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 14.5,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 80),
        ),
      ),
      const SizedBox(height: 18),
      _Fact(
        label: request.verified ? 'Verified as' : 'Identity',
        value: request.verifiedHost ?? 'Not verified',
        icon: request.verified
            ? PhosphorIconsRegular.sealCheck
            : PhosphorIconsRegular.warning,
        colour: request.verified ? Serval.healthyText : Serval.alertText,
      ),
      _Fact(
        label: 'Returns to',
        value: request.redirectTarget,
        icon: PhosphorIconsRegular.arrowUUpLeft,
      ),
      if (!request.verified) ...[
        const SizedBox(height: 6),
        _Prose(
          'This app registered itself, so its name is its own claim and cannot be checked. Only '
          'allow it if you started connecting it yourself, just now.',
          colour: Serval.alertText,
        ),
      ],
      const SizedBox(height: 18),
      const _Prose(
        'If you allow it, it can see every camera — live and recorded — and read what Serval '
        'noticed: scene descriptions, detections, transcripts, sounds, alerts and saved clips. It '
        'cannot change anything, move a camera or dismiss an alert.',
      ),
      const SizedBox(height: 10),
      _Prose(
        'Whatever it looks at is sent to the company that runs it. You can disconnect it at any '
        'time from Settings → Account.',
        colour: Nocturne.mix(Nocturne.text, 55),
      ),
      const SizedBox(height: 26),
      NocturneButton(
        label: busy && _approved ? 'Allowing…' : 'Allow',
        variant: NocturneButtonVariant.primary,
        height: 46,
        fontSize: 14.5,
        onPressed: busy ? null : () => unawaited(_answer(approve: true)),
      ),
      const SizedBox(height: 12),
      NocturneButton(
        label: busy && !_approved ? 'Declining…' : 'Deny',
        variant: NocturneButtonVariant.ghost,
        height: 46,
        fontSize: 14.5,
        onPressed: busy ? null : () => unawaited(_answer(approve: false)),
      ),
      if (username != null) ...[
        const SizedBox(height: 16),
        Text(
          'Signed in as $username',
          textAlign: TextAlign.center,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12,
            color: Nocturne.mix(Nocturne.text, 45),
          ),
        ),
      ],
    ];
  }
}

class _Masthead extends StatelessWidget {
  const _Masthead();

  @override
  Widget build(BuildContext context) => Row(
    children: [
      SvgPicture.asset('assets/icons/serval_mark.svg', width: 42, height: 45),
      const SizedBox(width: 13),
      const Text(
        'Serval',
        style: TextStyle(
          fontFamily: Nocturne.fontHeading,
          fontSize: 21,
          fontWeight: Nocturne.headingWeight,
          color: Nocturne.text,
          letterSpacing: -0.01 * 21,
        ),
      ),
    ],
  );
}

class _Title extends StatelessWidget {
  const _Title(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Text(
    text,
    style: const TextStyle(
      fontFamily: Nocturne.fontHeading,
      fontSize: 27,
      fontWeight: Nocturne.headingWeight,
      color: Nocturne.text,
      letterSpacing: -0.015 * 27,
    ),
  );
}

class _Prose extends StatelessWidget {
  const _Prose(this.text, {this.colour});

  final String text;
  final Color? colour;

  @override
  Widget build(BuildContext context) => Text(
    text,
    style: TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 13.5,
      height: 1.5,
      color: colour ?? Nocturne.mix(Nocturne.text, 68),
    ),
  );
}

class _Fact extends StatelessWidget {
  const _Fact({
    required this.label,
    required this.value,
    required this.icon,
    this.colour,
  });

  final String label;
  final String value;
  final IconData icon;
  final Color? colour;

  @override
  Widget build(BuildContext context) {
    final tint = colour ?? Nocturne.mix(Nocturne.text, 70);

    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        children: [
          SizedBox(
            width: 96,
            child: Text(
              label,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),
          ),
          Icon(icon, size: 15, color: tint),
          const SizedBox(width: 7),
          Expanded(
            child: Text(
              value,
              style: TextStyle(
                fontFamily: Nocturne.fontMono,
                fontSize: 12.5,
                color: tint,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
