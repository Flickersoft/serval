import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/auth/auth_controller.dart';
import '../theme/nocturne.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/source_link.dart';

/// Shown while a stored session is being renewed against a Server that has not answered yet — see
/// [AuthStatus.restoring].
///
/// **The reason this screen exists is that it is not the login screen.** Nothing has rejected this
/// session; the Server has not been reached. Sending somebody to a password prompt for that asks
/// for something that was not the problem. The retry behind this runs on its own backoff and this
/// screen goes away by itself.
///
/// Says as little about the house as [LoginScreen] does, and for the same reason: whoever is
/// looking at it has not been let in yet.
class ConnectingScreen extends StatelessWidget {
  const ConnectingScreen({super.key, required this.auth});

  final AuthController auth;

  @override
  Widget build(BuildContext context) => Container(
    // The app frame's ground, matching the login screen this stands in for.
    color: Nocturne.bg,
    child: Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(
          horizontal: Nocturne.space6,
          vertical: Nocturne.space8,
        ),
        child: ConstrainedBox(
          // The login screen's form column, so the two read as the same screen changing its mind.
          constraints: const BoxConstraints(maxWidth: 404),
          child: ListenableBuilder(
            listenable: auth,
            builder: (context, _) => Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    SvgPicture.asset(
                      'assets/icons/serval_mark.svg',
                      width: 42,
                      height: 45,
                    ),
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
                ),
                const SizedBox(height: 18),
                const Text(
                  'Reconnecting',
                  style: TextStyle(
                    fontFamily: Nocturne.fontHeading,
                    fontSize: 27,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                    letterSpacing: -0.015 * 27,
                  ),
                ),
                const SizedBox(height: 18),
                Text(
                  "You're still signed in. Waiting for the Server to answer — "
                  'this screen goes away on its own.',
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 13.5,
                    height: 1.5,
                    color: Nocturne.mix(Nocturne.text, 68),
                  ),
                ),
                if (auth.restoreAttempt > 0) ...[
                  const SizedBox(height: 10),
                  Text(
                    auth.restoreAttempt == 1
                        ? 'Retrying…'
                        : 'Retrying — attempt ${auth.restoreAttempt}.',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12.5,
                      height: 1.5,
                      color: Nocturne.mix(Nocturne.text, 50),
                    ),
                  ),
                ],
                const SizedBox(height: 30),
                NocturneButton(
                  label: 'Try now',
                  trailingIcon: PhosphorIconsRegular.arrowClockwise,
                  variant: NocturneButtonVariant.primary,
                  height: 46,
                  fontSize: 14.5,
                  onPressed: auth.retryRestoreNow,
                ),
                const SizedBox(height: 12),
                NocturneButton(
                  label: 'Sign in instead',
                  variant: NocturneButtonVariant.ghost,
                  height: 46,
                  fontSize: 14.5,
                  // Forgets the stored session, which is what moves the router to the login screen.
                  // The only way off this screen that is a decision rather than a wait, and it is
                  // deliberately the one that costs a password — nothing else here does.
                  onPressed: () => unawaited(auth.logout()),
                ),
                const SizedBox(height: 34),
                // Owed on any screen the interface reaches, signed in or not — same as 3a.
                const SourceLine(),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}
