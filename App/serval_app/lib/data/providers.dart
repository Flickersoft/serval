/// What the screens reach for instead of taking it as a constructor argument.
///
/// This container is deliberately small. It is mostly a **dependency-injection** container: the two
/// things in it that are not derived — [repositoryProvider] and [authProvider] — are overridden
/// once, in `ServalApp.build`, from the values `main` constructs.
///
/// What it also owns, and only this: **the repository's change signals, split by what changed.**
/// [activityRevisionProvider] and the slices beside it exist because one notification for
/// everything the repository holds meant a detection heartbeat at 2Hz rebuilt every screen
/// listening to it. Nothing here caches a derivation — the repository still answers the questions
/// and memoises the answers — and nothing here replaces `setState`.
///
/// Three rules keep it that way:
///
///  * **Riverpod stops at `lib/screens/`.** Everything under `lib/widgets/` takes what it needs as
///    parameters, including `repository`. They are hand-built Nocturne controls, and a control that
///    reaches into a container is no longer a control — it cannot be dropped into a test, a golden
///    or a different screen without bringing the container with it.
///  * **Ephemeral UI state stays in `State`.** Text controllers, drag gestures, which tab is open,
///    whether a save is in flight, where the replay playhead is. Riverpod is not a `setState`
///    replacement, and the screens are not more testable for having their scroll offsets in a
///    global container. A per-screen value that rebuilds too much is fixed by subscribing to it
///    further down, not by lifting it in here.
///  * **A slice is watched from a `Consumer`, never from `ConsumerState.build`.** The point of
///    splitting the signal is that a small part of the tree rebuilds; `ref.watch` at the top of a
///    screen throws that away and rebuilds all of it, which is what the split was undoing.
///
/// What this buys: [ServalRepository] reaches the talk button without threading through four widget
/// layers, the session values below are declared once rather than on every screen that passes them
/// along, and `go_router`'s route builders — which have no parent widget to take arguments from —
/// can reach both.
///
/// The seam the tests and goldens use is the `ServalRepository` interface and
/// `SampleServalRepository`, not `overrideWithValue`.
library;

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'auth/auth_controller.dart';
import 'auth/auth_models.dart' show Role;
import 'auth/user_account.dart';
import 'serval_repository.dart';

/// The repository the screens read. Overridden in `ServalApp.build`; unoverridden it throws, since
/// there is no sensible default — the sample one is a deliberate choice a caller makes, not a
/// fallback to land on by accident.
final repositoryProvider = Provider<ServalRepository>(
  (ref) => throw StateError(
    'repositoryProvider was read without an override. ServalApp supplies one; '
    'a test pumping a screen directly must do the same.',
  ),
);

/// Ticks whenever the activity pool changes, and at no other time.
///
/// **Watch this, not the repository.** The count itself means nothing — it is a change signal with
/// a value attached so that Riverpod has something to compare. What it buys is that a widget
/// reading [ServalRepository.activityFor], `feedHorizon`, `detectionsAt` or the timeline's marks
/// can say so, and be rebuilt for those and for nothing else. A detection episode heartbeats twice
/// a second per camera; without the split, each of those rebuilds every screen with a listener on
/// the repository, all the way down from the route.
///
/// Watched from a `Consumer` sited at the narrowest widget that reads the pool — **not** from a
/// `ConsumerState.build`, which would rebuild that whole screen and put back exactly what this is
/// here to remove.
///
/// The derivation stays in the repository rather than moving into providers keyed on the query.
/// `activityFor` takes `(cameraId, asOf, range, includeAllDetections)` with `asOf` quantised to the
/// second, so a family over that mints a provider per second of playhead; the repository already
/// memoises those answers against the same revision this is fired with. Signal here, derivation
/// there, until the churn of doing otherwise has been measured on the target hardware.
final activityRevisionProvider = NotifierProvider<ActivityRevision, int>(
  ActivityRevision.new,
);

/// Counts activity changes. See [activityRevisionProvider].
class ActivityRevision extends Notifier<int> {
  @override
  int build() {
    final changes = ref.watch(repositoryProvider).activityChanges;
    void bump() => state++;

    changes.addListener(bump);
    ref.onDispose(() => changes.removeListener(bump));

    return 0;
  }
}

/// Ticks when a timeline window lands. See [ServalRepository.timelineChanges].
///
/// The one slice that *is* meant to be watched from a `ConsumerState.build`: coverage arriving
/// changes what the whole camera screen can do — whether a row can be seeked to, whether a clip can
/// be saved — and it happens once per window rather than twice a second.
final timelineRevisionProvider = NotifierProvider<TimelineRevision, int>(
  TimelineRevision.new,
);

/// Counts timeline loads. See [timelineRevisionProvider].
class TimelineRevision extends Notifier<int> {
  @override
  int build() {
    final changes = ref.watch(repositoryProvider).timelineChanges;
    void bump() => state++;

    changes.addListener(bump);
    ref.onDispose(() => changes.removeListener(bump));

    return 0;
  }
}

/// The session, or null where there is none.
///
/// **Null means the sample repository** — the widget tests and the goldens construct
/// `const ServalApp()` with neither a session nor a Server, and there is no login to gate on. That
/// single fact is what the four derived providers below encode, in one place rather than as a
/// default repeated on every screen that displays a session value.
final authProvider = Provider<AuthController?>((ref) => null);

/// Whether the settings sidebar offers *Users & access*.
///
/// Defaults to true with no session so the sample path keeps rendering both tabs, which is what the
/// goldens capture.
final isAdminProvider = Provider<bool>(
  (ref) => ref.watch(authProvider)?.isAdmin ?? true,
);

/// Who is signed in, for the settings footer. Null on the sample path.
final currentUsernameProvider = Provider<String?>(
  (ref) => ref.watch(authProvider)?.username,
);

final currentRoleProvider = Provider<Role>(
  (ref) => ref.watch(authProvider)?.role ?? Role.viewer,
);

/// Null hides the rail's sign-out button: a sample repository has no session to sign out of.
final logoutProvider = Provider<VoidCallback?>(
  (ref) => ref.watch(authProvider)?.logout,
);

/// Every account on this Server.
///
/// The one read here that is asynchronous, and the one place `AsyncValue` earns its keep — the
/// hand-rolled alternative is a nullable list standing in for *loading*, a separate error field
/// beside it, `mounted` guards around the re-fetch, and a manual re-select afterwards.
///
/// A mutation invalidates this rather than patching a cached copy — which is what
/// [ServalRepository]'s user methods already assumed by being plain passthroughs. There is no
/// socket for accounts, so there is nothing to keep a local copy fresh against.
///
/// `autoDispose` because this is not the wall: leaving *Users & access* should drop the roster
/// rather than hold every account in memory for a screen nobody is looking at.
final usersProvider = FutureProvider.autoDispose<List<UserAccount>>(
  (ref) => ref.watch(repositoryProvider).listUsers(),
);
