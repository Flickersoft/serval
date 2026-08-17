import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/auth/auth_models.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'source_link.dart';

/// The settings pages this app actually has. The mockup (3b) draws two more — Alerts and
/// About Serval — that don't exist yet, so they aren't listed here; adding a real page is what
/// earns a spot in this enum; a placeholder link to nowhere is not.
///
/// `status` is the mockup's *Recordings & storage* row, under a name that also covers the
/// processor and memory figures beside the disk ones. It earned its place the way this comment
/// asks: `GET /api/system/stats` exists, and every number on the page comes from it.
///
/// `server` is what the Server is *told* to do, as against what it is doing. The two were one page
/// while there was nothing to change — vitals are read-only — and split when `/api/settings`
/// arrived, because "how much disk is left" and "how long to keep footage" answer different
/// questions and a page mixing them makes neither easy to find.
///
/// `status` leads, and is where `/settings` with no page named lands: it is the one page here that
/// changes nothing, reads on any account, and answers the question somebody arriving at settings
/// most often has — is this machine alright, and is there room left.
/// `notifications` is this account's own, unlike every other page here: it says which cameras may
/// interrupt *you*, and it takes any role because being told about your own house is not an
/// administrative act. It is a page rather than a section of `server` for that reason — the two
/// answer to different owners, and mixing them would put a Viewer's own switches on a page whose
/// other controls they cannot use.
/// `account` is the second page here that belongs to the person rather than the machine, and it
/// takes any role for the same reason `notifications` does. It exists because the Server has always
/// supported all three of the things on it — `PUT /api/auth/password` among them — and the App
/// offered none of them, so the only way to change your own password was to ask an Admin to set one
/// for you, which tells them what it is.
enum SettingsSection { status, cameras, server, notifications, account, users }

/// Where a section lives, so the sidebar and the drill-down index send you to the same address.
String settingsPathFor(SettingsSection section) => switch (section) {
  SettingsSection.cameras => '/settings/cameras',
  SettingsSection.server => '/settings/server',
  SettingsSection.status => '/settings/status',
  SettingsSection.notifications => '/settings/notifications',
  SettingsSection.account => '/settings/account',
  SettingsSection.users => '/settings/users',
};

/// The 236px "Settings" column the mockup puts beside every settings screen: a heading, the
/// section switcher, and — pinned to the bottom — who is signed in and a way to sign out.
///
/// [CamerasScreen] and `UsersScreen` each place this beside [IconRail] the same way; it exists
/// once here rather than being redrawn in both because the two screens must always offer the
/// same way back to each other.
///
/// [compact] is the same four sections as design 7b's first phone screen: the column gives up its
/// width and its heading, the rows grow to a thumb's target and gain a caret, and the source line
/// and the account footer are the ones drawn here. One list, so a section can never exist on the
/// desktop and be unreachable on a phone.
class SettingsNav extends StatelessWidget {
  const SettingsNav({
    super.key,
    required this.selected,
    required this.onSelect,
    required this.isAdmin,
    this.currentUsername,
    this.currentRole = Role.viewer,
    this.onLogout,
    this.compact = false,
  });

  final SettingsSection selected;
  final ValueChanged<SettingsSection> onSelect;

  /// Draws the drill-down index rather than the sidebar.
  final bool compact;

  /// Hides *Users & access* — only an Admin can reach the accounts Server routes this page
  /// calls, so there is nothing behind that tab for anyone else.
  final bool isAdmin;

  /// Null hides the account footer entirely — the sample repository (tests, goldens) has no
  /// session to describe.
  final String? currentUsername;
  final Role currentRole;

  final VoidCallback? onLogout;

  @override
  Widget build(BuildContext context) {
    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (!compact)
          Container(
            padding: const EdgeInsets.fromLTRB(16, 18, 16, 14),
            decoration: BoxDecoration(
              border: Border(bottom: BorderSide(color: Serval.hairline)),
            ),
            child: const Text(
              'Settings',
              style: TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 16,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
          ),
        Padding(
          padding: compact
              ? const EdgeInsets.symmetric(vertical: 8)
              : const EdgeInsets.all(10),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: _rows,
          ),
        ),
        const Spacer(),
        if (compact) const _SourceLink(),
        if (currentUsername != null)
          _AccountFooter(
            username: currentUsername!,
            role: currentRole,
            onLogout: onLogout,
          ),
      ],
    );

    if (compact) return body;

    return Container(
      width: 236,
      decoration: BoxDecoration(
        color: Serval.rail,
        border: Border(right: BorderSide(color: Serval.hairline)),
      ),
      child: body,
    );
  }

  /// The sections, in the order the enum declares them.
  ///
  /// Nothing is lit in [compact]: the index is a menu you leave, not a place you are, and a tinted
  /// row there would claim otherwise.
  List<Widget> get _rows => [
    // Not gated on [isAdmin]: `/api/system/stats` is a read and takes any signed-in
    // account. A Viewer cannot clear a full volume, but they are exactly who will see
    // the wall's warning and want to know what it is about.
    _NavRow(
      icon: PhosphorIconsRegular.hardDrives,
      activeIcon: PhosphorIconsFill.hardDrives,
      label: 'Server status',
      selected: !compact && selected == SettingsSection.status,
      compact: compact,
      onTap: () => onSelect(SettingsSection.status),
    ),
    _NavRow(
      icon: PhosphorIconsRegular.videoCamera,
      activeIcon: PhosphorIconsFill.videoCamera,
      label: 'Cameras',
      selected: !compact && selected == SettingsSection.cameras,
      compact: compact,
      onTap: () => onSelect(SettingsSection.cameras),
    ),
    // Not gated either, for the reason above: reading `/api/settings` takes any
    // signed-in account, and the Server refuses the write rather than this hiding the
    // page. A Viewer who can see how long footage is kept is better placed to ask for
    // it to change.
    _NavRow(
      icon: PhosphorIconsRegular.slidersHorizontal,
      activeIcon: PhosphorIconsFill.slidersHorizontal,
      label: 'Server settings',
      selected: !compact && selected == SettingsSection.server,
      compact: compact,
      onTap: () => onSelect(SettingsSection.server),
    ),
    // Never gated: these are the signed-in person's own switches, and a Viewer is exactly who
    // needs them — they cannot change what the cameras alert on, so narrowing what reaches them
    // is the only control over their own notifications they have.
    _NavRow(
      icon: PhosphorIconsRegular.bellRinging,
      activeIcon: PhosphorIconsFill.bellRinging,
      label: 'Notifications',
      selected: !compact && selected == SettingsSection.notifications,
      compact: compact,
      onTap: () => onSelect(SettingsSection.notifications),
    ),
    // Never gated either: it is this person's own name, their own password and their own sessions.
    // A Viewer needs it most — they have no other page here they can write to.
    _NavRow(
      icon: PhosphorIconsRegular.userCircle,
      activeIcon: PhosphorIconsFill.userCircle,
      label: 'Account',
      selected: !compact && selected == SettingsSection.account,
      compact: compact,
      onTap: () => onSelect(SettingsSection.account),
    ),
    if (isAdmin)
      _NavRow(
        icon: PhosphorIconsRegular.users,
        activeIcon: PhosphorIconsFill.users,
        label: 'Users & access',
        selected: !compact && selected == SettingsSection.users,
        compact: compact,
        onTap: () => onSelect(SettingsSection.users),
      ),
  ];
}

class _NavRow extends StatefulWidget {
  const _NavRow({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.selected,
    required this.onTap,
    this.compact = false,
  });

  final PhosphorIconData icon;
  final PhosphorIconData activeIcon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  /// A full-width 56px row with a caret, rather than the sidebar's rounded chip.
  final bool compact;

  @override
  State<_NavRow> createState() => _NavRowState();
}

class _NavRowState extends State<_NavRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final compact = widget.compact;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        behavior: HitTestBehavior.opaque,
        child: Container(
          constraints: compact ? const BoxConstraints(minHeight: 56) : null,
          margin: compact ? null : const EdgeInsets.only(bottom: 2),
          padding: compact
              ? const EdgeInsets.symmetric(horizontal: 18, vertical: 10)
              : const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: widget.selected
                ? Nocturne.mix(Nocturne.accent, 16)
                : _hovered
                ? Nocturne.mix(Nocturne.text, 5)
                : null,
            borderRadius: compact ? null : BorderRadius.circular(8),
            border: compact
                ? null
                : widget.selected
                ? Border.all(color: Nocturne.mix(Nocturne.accent, 40))
                : Border.all(color: const Color(0x00000000)),
          ),
          child: Row(
            children: [
              PhosphorIcon(
                widget.selected ? widget.activeIcon : widget.icon,
                size: compact ? 19 : 16,
                color: widget.selected
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, 72),
              ),
              SizedBox(width: compact ? 14 : 10),
              Expanded(
                child: Text(
                  widget.label,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: compact ? 15 : 13.5,
                    fontWeight: widget.selected
                        ? Nocturne.headingWeight
                        : FontWeight.w400,
                    color: widget.selected
                        ? Nocturne.text
                        : Nocturne.mix(Nocturne.text, compact ? 85 : 72),
                  ),
                ),
              ),
              if (compact)
                PhosphorIcon(
                  PhosphorIconsRegular.caretRight,
                  size: 15,
                  color: Nocturne.mix(Nocturne.text, 35),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// [SourceLine] under the rule that separates it from the account footer.
///
/// [SettingsNav.compact] only: below [Serval.compactWidth] there is no rail, so this drill-down
/// index is the bottom of the only column on screen and the offer has to live here. Wider, the
/// rail carries it under every screen — including these — and a second copy a sidebar away said
/// the same thing twice.
///
/// It sits above the account footer rather than inside it so that it does not disappear with the
/// session: the footer is hidden when nobody is signed in, and the offer is owed to everyone the
/// interface reaches.
class _SourceLink extends StatelessWidget {
  const _SourceLink();

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
    decoration: BoxDecoration(
      border: Border(top: BorderSide(color: Serval.hairline)),
    ),
    child: const SourceLine(),
  );
}

class _AccountFooter extends StatelessWidget {
  const _AccountFooter({
    required this.username,
    required this.role,
    this.onLogout,
  });

  final String username;
  final Role role;
  final VoidCallback? onLogout;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
    decoration: BoxDecoration(
      border: Border(top: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        Container(
          width: 28,
          height: 28,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: Nocturne.mix(Nocturne.accent, 20),
            border: Border.all(color: Nocturne.mix(Nocturne.accent, 40)),
          ),
          child: Text(
            username.isEmpty ? '?' : username[0].toUpperCase(),
            style: const TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: 12,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.accent300,
            ),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                username,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              Text(
                role == Role.admin ? 'Admin · you' : 'View only · you',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 11.5,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
            ],
          ),
        ),
        if (onLogout != null)
          MouseRegion(
            cursor: SystemMouseCursors.click,
            child: GestureDetector(
              onTap: onLogout,
              child: PhosphorIcon(
                PhosphorIconsRegular.signOut,
                size: 15,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),
          ),
      ],
    ),
  );
}
