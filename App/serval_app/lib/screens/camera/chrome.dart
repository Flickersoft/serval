part of '../camera_screen.dart';

class _TopBar extends StatelessWidget {
  const _TopBar({
    required this.camera,
    required this.onBack,
    required this.onSnapshot,
    required this.onSaveClip,
    this.onOpenSettings,
    this.snapshotJob,
    this.clipJob,
    this.choosingClip = false,
    this.castState = CastState.unavailable,
    this.onCast,
    this.castProblem,
  });

  final Camera camera;
  final VoidCallback onBack;
  final VoidCallback? onOpenSettings;
  final VoidCallback onSnapshot;
  final VoidCallback onSaveClip;
  final _SaveJob? snapshotJob;
  final _SaveJob? clipJob;

  /// Whether a Cast device is reachable, and whether one is already playing this. Absent is the
  /// ordinary case — no Chromecast on the network, or a browser without the Cast SDK at all — and
  /// the button is not rendered then rather than rendered dead.
  final CastState castState;
  final VoidCallback? onCast;
  final String? castProblem;

  /// The screen is a trimmer. The bar says so, and everything that would take you off it stops
  /// working — a gear pressed mid-trim would lose a range that took a minute to set.
  final bool choosingClip;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        NocturneButton(
          label: 'All cameras',
          icon: PhosphorIconsRegular.arrowLeft,
          variant: NocturneButtonVariant.ghost,
          horizontalPadding: 0,
          onPressed: onBack,
        ),
        const SizedBox(width: 14),
        Container(width: 1, height: 18, color: Nocturne.mix(Nocturne.text, 12)),
        const SizedBox(width: 14),
        Text(
          camera.name,
          style: const TextStyle(
            fontFamily: Nocturne.fontHeading,
            fontSize: 17,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.text,
          ),
        ),
        if (camera.needsAttention && !choosingClip) ...[
          const SizedBox(width: 14),
          // Fixed wording. An alert-labelled sound says a camera needs
          // attention, but "Glass" is a class name, not a sentence — the
          // label itself is on the activity row that raised this.
          Pill.solid("Someone's here"),
        ],
        if (choosingClip) ...[
          const SizedBox(width: 14),
          Pill(
            label: 'Choosing a clip',
            icon: PhosphorIconsRegular.scissors,
            background: Nocturne.mix(Nocturne.accent, 16),
            border: Nocturne.mix(Nocturne.accent, 50),
            foreground: Nocturne.accent300,
            fontSize: 12,
            padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 5),
          ),
        ],
        const SizedBox(width: 14),
        // The one place either job's outcome is written down, and the gap that holds the buttons
        // against the right edge — one Expanded doing both, because they are the same space. A
        // Flexible beside a Spacer would be two flex children splitting it, and with nothing to
        // report the status takes none of its half and the buttons stop short of the edge by that
        // much. Expanded also ellipsises a long failure — the Server's own sentence — rather than
        // pushing the buttons off the bar.
        Expanded(
          child: _SaveStatus(
            snapshot: snapshotJob,
            clip: clipJob,
            castProblem: castProblem,
          ),
        ),
        // Only where there is something to cast to. A disabled button would raise the question
        // of what is wrong, and on most machines nothing is — there is simply no television.
        if (castState != CastState.unavailable && !choosingClip) ...[
          NocturneButton(
            label: castState == CastState.casting ? 'Stop casting' : 'Cast',
            // The same icon the phone layouts put in the corner of the picture, and the one a cast
            // control is recognised by. Filled while a session runs.
            icon: castState == CastState.casting
                ? PhosphorIconsFill.screencast
                : PhosphorIconsRegular.screencast,
            onPressed: onCast,
          ),
          const SizedBox(width: 8),
        ],
        NocturneButton(
          label: switch (snapshotJob) {
            _SaveWorking() => 'Saving…',
            _ => 'Snapshot',
          },
          icon: PhosphorIconsRegular.camera,
          // Inert while its own job runs, so the label change is visible and a second press
          // cannot start a second request. Enabled otherwise even where there is no Server:
          // disabling would drop it to 0.45 in the goldens, and the honest answer belongs in the
          // status line rather than in an absence.
          onPressed: choosingClip || snapshotJob is _SaveWorking
              ? null
              : onSnapshot,
        ),
        const SizedBox(width: 8),

        // The trimmer carries its own Cancel and Save, so this would be a third way to act on a
        // range and the only one that does not say what it will do.
        if (!choosingClip) ...[
          NocturneButton(
            label: switch (clipJob) {
              // A live byte count rather than a spinner: there is no total to make a percentage
              // from, and Nocturne has no spinner. The figure is the honest progress signal.
              _SaveWorking(:final bytes) when bytes > 0 =>
                'Saving… ${_megabytes(bytes)}',
              _SaveWorking() => 'Saving…',
              _ => 'Save clip',
            },
            icon: PhosphorIconsRegular.scissors,
            onPressed: clipJob is _SaveWorking ? null : onSaveClip,
          ),
          const SizedBox(width: 8),
        ],
        NocturneButton.icon(
          icon: PhosphorIconsRegular.gearSix,
          onPressed: choosingClip ? null : onOpenSettings,
        ),
      ],
    ),
  );
}

/// The video and everything painted over it.
