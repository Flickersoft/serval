import 'package:flutter/widgets.dart';

/// A scroll view for a list that grows at the *top*, which holds your place when it does.
///
/// Serval's two running lists — the transcript and the activity feed — read newest-first, so an
/// arriving row is inserted above what is already there and pushes it down by its own height. A
/// scroll offset is measured from the top of the content, which is exactly what just moved: read an
/// older row on a busy camera and it walks off the bottom of the panel on its own.
///
/// When [revision] changes, this grows the list upward out of view and leaves the viewport where it
/// is. The correction is the change in `maxScrollExtent` across the rebuild — the height of what
/// was added, without measuring a row that has not been laid out yet.
///
/// **Only when you have scrolled.** At the top there is nothing to hold: the newest row is what you
/// are looking at, and it should simply appear.
///
/// Two deliberate limits:
///
///  * **It does not lay out lazily.** [child] must be a `Column`, not a sliver list. A lazy list
///    only *estimates* `maxScrollExtent` and refines it a frame later, so the correction inherits
///    the error and the panel creeps a few pixels per row. Both feeds are bounded by the live
///    repository's feed cap, so laying all of it out is affordable and makes the number exact.
///  * **It assumes the change happened above the viewport**, which is where a newest-first list
///    changes. An edit below it would move you by its height. The exact fix is a `CustomScrollView`
///    split at a `center` sliver — a great deal of machinery for a case the data does not produce.
class TopAnchoredScrollView extends StatefulWidget {
  const TopAnchoredScrollView({
    super.key,
    required this.revision,
    required this.child,
    this.padding,
  });

  /// Changes whenever rows may have been added above. Use a value-equal token — the length of the
  /// list and the identity of its first row is enough, and is cheaper than comparing the rest.
  final Object? revision;

  final EdgeInsets? padding;
  final Widget child;

  @override
  State<TopAnchoredScrollView> createState() => _TopAnchoredScrollViewState();
}

class _TopAnchoredScrollViewState extends State<TopAnchoredScrollView> {
  final _scroll = ScrollController();

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  @override
  void didUpdateWidget(covariant TopAnchoredScrollView oldWidget) {
    super.didUpdateWidget(oldWidget);

    if (oldWidget.revision == widget.revision) return;
    if (!_scroll.hasClients) return;

    final position = _scroll.position;
    if (position.pixels <= 0) return;

    final offset = position.pixels;
    final extentBefore = position.maxScrollExtent;

    // The new rows have not been laid out yet, so what they add is only knowable after this frame.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_scroll.hasClients) return;
      final delta = _scroll.position.maxScrollExtent - extentBefore;
      if (delta == 0) return;
      _scroll.jumpTo(
        (offset + delta).clamp(0.0, _scroll.position.maxScrollExtent),
      );
    });
  }

  @override
  Widget build(BuildContext context) => SingleChildScrollView(
    controller: _scroll,
    padding: widget.padding,
    child: widget.child,
  );
}
