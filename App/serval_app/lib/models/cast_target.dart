import 'timeline.dart';

/// Where to cast one camera: which receiver application to launch, and what to hand it.
///
/// Both halves come from the server rather than being built here. The application id is registered
/// by the operator against their own deployment, so it is not something the App could know; and the
/// URL carries a camera-scoped ticket the server mints, which is what lets the receiver — running
/// on a television, with no Serval session — reach the camera at all.
class CastTarget {
  const CastTarget({required this.receiverAppId, required this.contentUrl});

  factory CastTarget.fromJson(Map<String, dynamic> json) => CastTarget(
    receiverAppId: json['receiverAppId']?.toString() ?? '',
    contentUrl: Uri.parse(json['contentUrl']?.toString() ?? ''),
  );

  /// The Cast application the sender launches. Serval's own receiver, not Google's default one:
  /// the default can only play [contentUrl] as HLS, several seconds behind, where this one
  /// negotiates WebRTC first and is live.
  final String receiverAppId;

  /// A live HLS playlist for the camera. What the receiver actually plays if its peer connection
  /// does not come up — so it is the fallback rather than the plan, and it is a real URL either
  /// way.
  final Uri contentUrl;

  bool get usable => receiverAppId.isNotEmpty && contentUrl.hasScheme;
}

/// The stretch of recording a television is currently playing.
///
/// Held while a recording is cast so that scrubbing in the App can be mirrored there. What it
/// decides is which of two very different things a scrub means — see [covers].
class CastWindow {
  const CastWindow({required this.from, required this.to});

  /// Where the cast started, and how far the playlist reaches. [to] is the moment the cast was
  /// begun, not now: the playlist was built then and does not grow.
  final DateTime from;
  final DateTime to;

  /// Whether [at] is inside the media the television already has.
  ///
  /// **Inside is a seek; outside is a whole new cast.** The playlist covers this window and nothing
  /// else, so scrubbing back to before it began — or forward past the moment it was started — asks
  /// for footage that was never sent, and no seek can reach it. Inclusive at both ends, because the
  /// first and last instants are in the playlist.
  bool covers(DateTime at) => !at.isBefore(from) && !at.isAfter(to);

  /// How far into the cast [at] sits, which is what a seek is measured in.
  Duration offsetOf(DateTime at) => at.difference(from);

  /// The most footage one cast covers.
  ///
  /// The window wants to be the whole visible timeline, and at the shorter spans it is. The two
  /// longest are not free to send: a day is around 21,600 recorded segments, which is a playlist of
  /// several thousand lines for a television to parse and hold. Six hours covers every span up to
  /// its own, and a scrub past it on a wider one costs a re-cast rather than being refused.
  static const maxSpan = Duration(hours: 6);

  /// The window to cast when the playhead is at [at] and the scrubber is showing [timeline].
  ///
  /// **Wide on purpose.** Casting only from the playhead made every move of it a fresh cast, and a
  /// fresh cast is a second or two of black screen. Sending the whole visible timeline instead
  /// means a click anywhere on the bar is already inside what the television has, so it is a seek.
  ///
  /// Centred on [at] when the timeline is wider than [maxSpan], because scrubbing goes both ways.
  factory CastWindow.around(DateTime at, TimelineWindow timeline) {
    final span = timeline.to.difference(timeline.from);

    var from = timeline.from;
    var to = timeline.to;

    if (span.isNegative || span > maxSpan) {
      final half = maxSpan ~/ 2;
      from = at.subtract(half);
      to = at.add(half);

      // Slid back inside the timeline rather than truncated, so a playhead near either edge still
      // gets the full span instead of half of one.
      if (from.isBefore(timeline.from)) {
        from = timeline.from;
        to = from.add(maxSpan);
      }
      if (to.isAfter(timeline.to)) {
        to = timeline.to;
        from = to.subtract(maxSpan);
        if (from.isBefore(timeline.from)) from = timeline.from;
      }
    }

    return CastWindow(from: _firstFootageFrom(from, timeline), to: to);
  }

  /// Where the recording the television is sent actually begins.
  ///
  /// **This is what a seek is measured from, so it has to be the footage and not the window.** The
  /// cast playlist's clock starts at its first segment, and a window that opens on a stretch with
  /// nothing recorded in it has its first segment wherever recording resumed — an hour later, on a
  /// camera that was off overnight. Measuring seeks from the window's own left edge would then miss
  /// by that whole hour. Gaps *inside* the window need no such treatment: the playlist spans them
  /// at wall-clock length, so everything after one still lines up.
  static DateTime _firstFootageFrom(DateTime from, TimelineWindow timeline) {
    for (final span in timeline.coverage) {
      if (span.to.isAfter(from)) {
        return span.from.isAfter(from) ? span.from : from;
      }
    }

    return from;
  }
}
