import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../data/time_labels.dart';
import '../models/alert.dart';
import '../models/timeline.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/alert_detail_pane.dart';
import '../widgets/alert_row.dart';
import '../widgets/compact_app_bar.dart';

/// One alert, on a phone — and where a tapped notification lands.
///
/// One destination however you arrive: the row opens this, and so does the push. Which is why it
/// fetches the alert by id rather than taking one: arriving from a notification, there is no list
/// to have taken it from.
///
/// **A camera that was not recording gets the same screen.** The design drew that case as a dead
/// end — a still, and a card explaining that the still was all there was. It is not, any more: the
/// Server buffers each camera's detect stream and cuts a preview whether or not anything is being
/// recorded, so this plays like any other alert. The only difference left is that there is no
/// recording to go on to, so *Watch* is absent and the dashed chip says why.
class AlertScreen extends ConsumerStatefulWidget {
  const AlertScreen({
    super.key,
    required this.alertId,
    this.onBack,
    this.onWatch,
  });

  final String alertId;
  final VoidCallback? onBack;

  /// Opens the camera's recording at a moment — what *Watch* does.
  final void Function(String cameraId, DateTime at)? onWatch;

  @override
  ConsumerState<AlertScreen> createState() => _AlertScreenState();
}

class _AlertScreenState extends ConsumerState<AlertScreen> {
  Future<Alert>? _alert;
  Uri? _poster;

  /// The clip settling, for this alert alone. See [_load].
  StreamSubscription<Alert>? _updates;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    unawaited(_updates?.cancel());
    super.dispose();
  }

  void _load() {
    final repository = ref.read(repositoryProvider);

    // Assigned directly rather than through setState: this runs from initState, where there is no
    // state to change yet — and `setState(() => _alert = ...)` *returns* the assigned Future, which
    // setState rejects.
    final alert = repository.alert(widget.alertId);
    _alert = alert;

    unawaited(_loadPoster(repository, alert));

    // The alert above is fetched once, and arriving from a notification means arriving before its
    // clip exists: the push goes out on the raise and the cut lands some sixteen seconds later. So
    // the first read is of an alert with no poster and no player, and without this the screen would
    // hold that answer for as long as it stayed open — the camera's stripe with a box on it.
    //
    // The Server republishes the alert when the clip settles, which is what this catches. Handing
    // the [FutureBuilder] below an already-completed future is what redraws it: the pane sees a new
    // clip state and goes and gets its clip, and [_loadPoster] now passes the guard that turned it
    // back the first time.
    _updates = repository.alertUpdates
        .where((update) => update.id == widget.alertId)
        .listen((update) {
          final settled = Future.value(update);

          // A block body, for the reason above: the arrow form returns the assigned Future and
          // setState rejects it.
          setState(() {
            _alert = settled;
          });

          unawaited(_loadPoster(repository, settled));
        });

    // Arriving here is what marks it seen, whichever way you arrived — a row, or a notification.
    unawaited(
      repository.markAlertRead(widget.alertId).catchError((Object _) {}),
    );
  }

  Future<void> _loadPoster(
    ServalRepository repository,
    Future<Alert> pending,
  ) async {
    try {
      // Waits for the alert rather than racing it, because the URL carries its clip state — that is
      // what makes the exact frame a different URL from the stand-in it replaces.
      //
      // Asked for unconditionally, not gated on the clip being ready: the Server writes the
      // camera's live snapshot as a poster the moment the alert is raised, so every alert has a
      // picture — including one there was never any footage to cut.
      final alert = await pending;

      final posters = await repository.alertPosterUrls([alert]);
      if (mounted) setState(() => _poster = posters[widget.alertId]);
    } on Object {
      // No poster is a quieter failure than an error box; the camera's stripe stands in.
    }
  }

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: const BoxDecoration(color: Serval.panel),
    child: FutureBuilder<Alert>(
      future: _alert,
      builder: (context, snapshot) {
        final alert = snapshot.data;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            CompactAppBar(
              title: alert?.title ?? 'Alert',
              subtitle: alert == null
                  ? null
                  : '${alert.cameraName} · '
                        '${clipGroupLabel(alert.at, DateTime.now())}, '
                        '${clockLabel(alert.at)}',
              onBack: widget.onBack,
            ),
            Expanded(
              child: alert == null
                  ? _pending(snapshot.connectionState)
                  : SingleChildScrollView(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Stack(
                            children: [
                              AlertDetailPane(
                                alert: alert,
                                poster: _poster,
                                compact: true,
                                onWatch: alert.recorded == true
                                    ? () => _watch(alert)
                                    : null,
                                onDismiss: () => _dismiss(alert),
                              ),
                              // On the picture rather than beside it: at 412px the class is worth
                              // a corner of the frame and not a line of the body. Held to the
                              // width of the frame, so a long class name ellipsizes in the corner
                              // instead of running out across it.
                              Positioned(
                                left: 12,
                                right: 12,
                                top: 232 - 34,
                                child: Align(
                                  alignment: Alignment.centerRight,
                                  child: alert.recorded == false
                                      ? const NotRecordedChip()
                                      : AlertChip(alert: alert),
                                ),
                              ),
                            ],
                          ),
                        ],
                      ),
                    ),
            ),
          ],
        );
      },
    ),
  );

  void _watch(Alert alert) {
    final at = replayStartFor(alert.at) ?? alert.at;
    widget.onWatch?.call(alert.cameraId, at);
  }

  /// Clears it and leaves.
  ///
  /// The pop comes first and does not wait: this card *is* the alert, so staying on it after
  /// clearing it would be a screen showing something that is no longer in the queue. The queue
  /// behind refetches when it rebuilds.
  void _dismiss(Alert alert) {
    widget.onBack?.call();
    unawaited(
      ref
          .read(repositoryProvider)
          .dismissAlert(alert.id)
          .catchError((Object _) {}),
    );
  }

  Widget _pending(ConnectionState state) => Center(
    child: Padding(
      padding: const EdgeInsets.all(32),
      child: Text(
        state == ConnectionState.waiting
            ? ''
            : 'That alert is no longer here. It may have been cleared, or aged past the '
                  'time alerts are kept for.',
        textAlign: TextAlign.center,
        style: TextStyle(
          fontSize: 13.5,
          height: 1.5,
          color: Nocturne.mix(Nocturne.text, 45),
        ),
      ),
    ),
  );
}
