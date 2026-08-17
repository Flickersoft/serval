import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/auth/auth_controller.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_field.dart';
import '../widgets/source_link.dart';

/// The gate in front of everything else. Shown whenever [AuthController.isAuthenticated] is
/// false — a cold start with no stored session, a logout, or a refresh token that finally expired.
///
/// Design 3a: two fields and one button, and nothing that leaks the state of the house — no
/// camera count, no places, no machine name — because a screen you haven't signed into shouldn't
/// describe what's behind it.
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key, required this.auth});

  final AuthController auth;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _username = TextEditingController();
  final _password = TextEditingController();
  bool _remember = true;

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_username.text.isEmpty || _password.text.isEmpty) return;
    await widget.auth.login(
      _username.text.trim(),
      _password.text,
      remember: _remember,
    );
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      // The form is centred at every width. Only the wall behind it is conditional: on a phone
      // or a split window it has nowhere honest to go, and the form reads fine against the plain
      // ground on its own.
      final wide = constraints.maxWidth >= 900;

      return Container(
        // The design's own ground for this screen — the app frame's colour, not the canvas one
        // step below it that the signed-in screens sit on.
        color: Nocturne.bg,
        child: Stack(
          fit: StackFit.expand,
          children: [
            if (wide) const _DecorativeWall(),
            Center(child: _form),
          ],
        ),
      );
    },
  );

  Widget get _form => SingleChildScrollView(
    padding: const EdgeInsets.symmetric(
      horizontal: Nocturne.space6,
      vertical: Nocturne.space8,
    ),
    child: ConstrainedBox(
      // The design's form column.
      constraints: const BoxConstraints(maxWidth: 404),
      child: ListenableBuilder(
        listenable: widget.auth,
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
              'Sign in',
              style: TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 27,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
                letterSpacing: -0.015 * 27,
              ),
            ),
            const SizedBox(height: 34),
            if (widget.auth.error != null) ...[
              _ErrorBanner(message: widget.auth.error!),
              const SizedBox(height: Nocturne.space4),
            ],
            NocturneField(
              label: 'Username',
              controller: _username,
              leadingIcon: PhosphorIconsRegular.user,
              enabled: widget.auth.status != AuthStatus.authenticating,
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 16),
            NocturneField(
              label: 'Password',
              controller: _password,
              leadingIcon: PhosphorIconsRegular.lockSimple,
              obscure: true,
              enabled: widget.auth.status != AuthStatus.authenticating,
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 16),
            _RememberMeCheckbox(
              value: _remember,
              onChanged: widget.auth.status == AuthStatus.authenticating
                  ? null
                  : (value) => setState(() => _remember = value),
            ),
            const SizedBox(height: 16),
            NocturneButton(
              label: widget.auth.status == AuthStatus.authenticating
                  ? 'Signing in…'
                  : 'Sign in',
              trailingIcon: PhosphorIconsRegular.arrowRight,
              variant: NocturneButtonVariant.primary,
              height: 46,
              fontSize: 14.5,
              onPressed:
                  widget.auth.status == AuthStatus.authenticating ||
                      _username.text.isEmpty ||
                      _password.text.isEmpty
                  ? null
                  : () => unawaited(_submit()),
            ),
            const SizedBox(height: 34),
            const _Hairline(),
            const SizedBox(height: 34),
            Text(
              "Forgotten your password? An admin can set a new one for you — "
              "there's no email reset.",
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12.5,
                height: 1.5,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            ),
            // AGPL section 13 is owed to everyone the interface reaches, and this screen is the
            // only one some of them get to: a person who cannot sign in has still reached Serval
            // over a network. It says nothing about the house — a commit hash and a licence
            // describe the software, not what is behind it.
            const SizedBox(height: 18),
            const SourceLine(),
          ],
        ),
      ),
    ),
  );
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(
      horizontal: Nocturne.space4,
      vertical: Nocturne.space3,
    ),
    decoration: BoxDecoration(
      color: Nocturne.mix(Serval.recording, 14),
      borderRadius: BorderRadius.circular(Nocturne.radiusSm),
      border: Border.all(color: Nocturne.mix(Serval.recording, 45)),
    ),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 1),
          child: PhosphorIcon(
            PhosphorIconsRegular.warningCircle,
            size: 14,
            color: Serval.recordingText,
          ),
        ),
        const SizedBox(width: 7),
        Expanded(
          child: Text(
            message,
            style: const TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.45,
              color: Serval.recordingText,
            ),
          ),
        ),
      ],
    ),
  );
}

/// "Stay signed in on this computer" — unchecked, [AuthController.login] keeps the session in
/// memory for this run rather than in secure storage, so a relaunch lands back here.
///
/// Local to this screen rather than in `widgets/`: every other on/off control in the app is a
/// [NocturneToggle], which reads as a switch you flip whenever, not a choice made once at sign-in.
class _RememberMeCheckbox extends StatelessWidget {
  const _RememberMeCheckbox({required this.value, required this.onChanged});

  final bool value;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) {
    final enabled = onChanged != null;

    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
        child: GestureDetector(
          onTap: enabled ? () => onChanged!(!value) : null,
          behavior: HitTestBehavior.opaque,
          child: Row(
            children: [
              Container(
                width: 17,
                height: 17,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: value ? Nocturne.mix(Nocturne.accent, 22) : null,
                  borderRadius: BorderRadius.circular(5),
                  border: Border.all(
                    color: value
                        ? Nocturne.mix(Nocturne.accent, 70)
                        : Nocturne.mix(Nocturne.text, 28),
                  ),
                ),
                child: value
                    ? const PhosphorIcon(
                        PhosphorIconsFill.check,
                        size: 11,
                        color: Nocturne.accent300,
                      )
                    : null,
              ),
              const SizedBox(width: 9),
              Text(
                'Stay signed in on this computer',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13,
                  color: Nocturne.mix(Nocturne.text, 68),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The system's rule: it fades to transparent at both ends rather than stopping cleanly.
class _Hairline extends StatelessWidget {
  const _Hairline();

  @override
  Widget build(BuildContext context) => Container(
    height: 1,
    decoration: BoxDecoration(
      gradient: LinearGradient(
        colors: [
          Nocturne.bg.withValues(alpha: 0),
          Nocturne.mix(Nocturne.text, 10),
          Nocturne.mix(Nocturne.text, 10),
          Nocturne.bg.withValues(alpha: 0),
        ],
        stops: const [0, 0.12, 0.88, 1],
      ),
    ),
  );
}

/// The design's wall behind the form: two accent blooms, a grid of striped camera tiles held to
/// the right, and a scrim that fades them back into the ground on the way to the form.
///
/// Every value is 3a's own. It is deliberately near-invisible — the design's note is that the
/// wall is "the app itself, dimmed far enough that no camera or place is legible", so this is a
/// texture rather than a picture, and nothing here is or pretends to be a real camera.
class _DecorativeWall extends StatelessWidget {
  const _DecorativeWall();

  /// The nine tiles' stripe pairs, in the design's order.
  static const _stripes = <(Color, Color)>[
    (Color(0xFF1B1E2D), Color(0xFF171A26)),
    (Color(0xFF191C28), Color(0xFF15171F)),
    (Color(0xFF1C1F2F), Color(0xFF181B28)),
    (Color(0xFF181B22), Color(0xFF14161C)),
    (Color(0xFF1E2131), Color(0xFF191C2A)),
    (Color(0xFF1A1D29), Color(0xFF161822)),
    (Color(0xFF1D2030), Color(0xFF181B27)),
    (Color(0xFF171A24), Color(0xFF13151D)),
    (Color(0xFF1B1E2C), Color(0xFF171924)),
  ];

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: [
      // radial-gradient(760px 520px at 16% 20%, rgba(145,132,217,0.16), transparent 70%)
      DecoratedBox(
        decoration: BoxDecoration(
          gradient: RadialGradient(
            center: const Alignment(-0.68, -0.6),
            radius: 0.85,
            colors: [
              Nocturne.mix(Nocturne.accent, 16),
              const Color(0x00000000),
            ],
            stops: const [0, 0.7],
          ),
        ),
      ),
      // radial-gradient(900px 700px at 100% 100%, rgba(145,132,217,0.07), transparent 70%)
      DecoratedBox(
        decoration: BoxDecoration(
          gradient: RadialGradient(
            center: Alignment.bottomRight,
            radius: 1.0,
            colors: [Nocturne.mix(Nocturne.accent, 7), const Color(0x00000000)],
            stops: const [0, 0.7],
          ),
        ),
      ),
      // The 620px-of-1440 tile grid, held to the right edge.
      Align(
        alignment: Alignment.centerRight,
        child: FractionallySizedBox(
          widthFactor: 620 / 1440,
          heightFactor: 1,
          child: Opacity(
            opacity: 0.4,
            child: Column(
              children: [
                for (var row = 0; row < 3; row++)
                  Expanded(
                    child: Row(
                      children: [
                        for (var col = 0; col < 3; col++)
                          Expanded(
                            child: Padding(
                              padding: EdgeInsets.only(
                                right: col < 2 ? 2 : 0,
                                bottom: row < 2 ? 2 : 0,
                              ),
                              child: ClipRect(
                                child: CustomPaint(
                                  painter: _StripePainter(
                                    _stripes[row * 3 + col].$1,
                                    _stripes[row * 3 + col].$2,
                                  ),
                                ),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
      // The scrim: opaque where the form sits, never fully clear even at the edge. The design's
      // is a right-aligned band easing leftward, for a form held to the left. With the form
      // centred it runs centre-out instead — same intent, mirrored about the middle, and
      // full-bleed so neither edge shows a seam.
      DecoratedBox(
        decoration: BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.centerLeft,
            end: Alignment.centerRight,
            colors: [
              Nocturne.bg.withValues(alpha: 0.8),
              Nocturne.bg.withValues(alpha: 0.94),
              Nocturne.bg,
              Nocturne.bg.withValues(alpha: 0.94),
              Nocturne.bg.withValues(alpha: 0.8),
            ],
            stops: const [0, 0.28, 0.5, 0.72, 1],
          ),
        ),
      ),
    ],
  );
}

/// One tile's `repeating-linear-gradient(118deg, light 0 7px, dark 7px 14px)`.
///
/// Painted rather than expressed as a [LinearGradient]: Flutter's gradients repeat over the whole
/// box, so a 14px period would have to be smuggled in through `begin`/`end` offsets that depend
/// on the tile's measured size. Bands 14px apart along a 118° axis is what the design says, and
/// it is what this draws.
class _StripePainter extends CustomPainter {
  const _StripePainter(this.light, this.dark);

  final Color light;
  final Color dark;

  /// 118° clockwise from north, in screen coordinates (x right, y down).
  static const _axis = Offset(0.883, 0.469);

  /// The bands themselves, perpendicular to the axis — a steep `/`.
  static const _band = Offset(0.469, -0.883);

  static const _period = 14.0;
  static const _lightWidth = 7.0;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = dark);

    final paint = Paint()
      ..color = light
      ..strokeWidth = _lightWidth
      ..style = PaintingStyle.stroke;

    // Far enough either way along the axis to cross the tile whatever its aspect.
    final reach = size.width + size.height;
    final centre = Offset(size.width / 2, size.height / 2);

    for (var d = -reach; d < reach; d += _period) {
      final origin = centre + _axis * (d + _lightWidth / 2);
      canvas.drawLine(origin - _band * reach, origin + _band * reach, paint);
    }
  }

  @override
  bool shouldRepaint(_StripePainter oldDelegate) =>
      oldDelegate.light != light || oldDelegate.dark != dark;
}
