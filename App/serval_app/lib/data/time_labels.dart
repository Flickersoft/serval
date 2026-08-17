/// The pre-rendered time strings the models carry.
///
/// [ActivityItem.timeLabel] holds a string rather than a timestamp because the design writes them
/// differently per context — "now", "heard just now", "4:12 pm", "6:40 pm yesterday" — and that is
/// a rendering decision, not a data one.
///
/// Hand-rolled rather than pulling in `intl`: this is a handful of formats in one locale, and the
/// design's exact wording ("heard just now") is not something a locale database supplies anyway.
library;

import '../models/timeline.dart';

/// Month abbreviations, for the one format that falls back to a date.
const _months = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

const _weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

/// `4:12 pm` — the design's clock format, lowercase meridiem, no leading zero on the hour.
String clockLabel(DateTime time) {
  final hour = time.hour % 12 == 0 ? 12 : time.hour % 12;
  final minute = time.minute.toString().padLeft(2, '0');
  return '$hour:$minute ${time.hour < 12 ? 'am' : 'pm'}';
}

/// `4:18:07 pm` — the transcript's format, which carries seconds because turns within one
/// conversation are often less than a minute apart.
String preciseClockLabel(DateTime time) {
  final hour = time.hour % 12 == 0 ? 12 : time.hour % 12;
  final minute = time.minute.toString().padLeft(2, '0');
  final second = time.second.toString().padLeft(2, '0');
  return '$hour:$minute:$second ${time.hour < 12 ? 'am' : 'pm'}';
}

/// `Tue 30 Jul · 4:18:07 pm` — the single-camera view's clock pill.
String stampLabel(DateTime time) =>
    '${_weekdays[time.weekday - 1]} ${time.day} ${_months[time.month - 1]}'
    ' · ${preciseClockLabel(time)}';

/// `28 Jul, 9:00 pm to 11:00 pm` — a chosen period, for the scrubber's header and for the picker's
/// echo of what you asked for.
///
/// Names the second day too when the window runs past midnight, which is the one case where the
/// start's date no longer describes the whole of it.
String periodLabel(DateTime from, DateTime to) {
  final sameDay =
      from.year == to.year && from.month == to.month && from.day == to.day;

  return sameDay
      ? '${dayLabel(from)}, ${clockLabel(from)} to ${clockLabel(to)}'
      : '${dayLabel(from)}, ${clockLabel(from)}'
            ' to ${dayLabel(to)}, ${clockLabel(to)}';
}

/// `28 Jul` — a day without its weekday, for places that already say which day it is.
String dayLabel(DateTime time) => '${time.day} ${_months[time.month - 1]}';

/// `Sat` — the weekday alone, for the range panel's strip of recent days.
String weekdayLabel(DateTime time) => _weekdays[time.weekday - 1];

/// `45 min`, `2 h`, `1.5 h` — how long a window is, in as few characters as it takes.
///
/// Under an hour stays in minutes; over it goes to hours and keeps one decimal only when the
/// span is not a whole number of them, so `2 h` never reads as `2.0 h`.
String spanLabel(Duration span) {
  if (span.inMinutes < 60) return '${span.inMinutes} min';

  final hours = span.inMinutes / 60;
  return hours == hours.roundToDouble()
      ? '${hours.round()} h'
      : '${hours.toStringAsFixed(1)} h';
}

/// `0:55`, `2:10`, `1:02:10` — a clip's length, as a player writes it.
///
/// Not [spanLabel], which is minute-granular and would render every clip worth keeping as `0 min`.
/// The two exist for different things: that one measures a window somebody chose to look at, this
/// one measures a piece of footage, and seconds are the whole content of the second question.
String clipLengthLabel(Duration length) {
  final seconds = length.inSeconds.remainder(60).toString().padLeft(2, '0');
  final minutes = length.inMinutes.remainder(60);

  return length.inHours > 0
      ? '${length.inHours}:${minutes.toString().padLeft(2, '0')}:$seconds'
      : '$minutes:$seconds';
}

/// `55 s`, `2 min 10 s`, `1 h 5 min` — the same length inside a sentence.
///
/// Separate from [clipLengthLabel] because *Save these 0:55…* reads as a timestamp rather than as
/// a duration. A colon is right beside a progress bar and wrong in prose.
String clipSpokenLabel(Duration length) {
  if (length.inSeconds < 60) return '${length.inSeconds} s';

  if (length.inMinutes < 60) {
    final seconds = length.inSeconds.remainder(60);
    return seconds == 0
        ? '${length.inMinutes} min'
        : '${length.inMinutes} min $seconds s';
  }

  final minutes = length.inMinutes.remainder(60);
  return minutes == 0
      ? '${length.inHours} h'
      : '${length.inHours} h $minutes min';
}

/// Which bucket a saved clip belongs in — `Today`, `This week`, `Earlier in July`, `July 2025`.
///
/// Grouped by when the thing *happened* rather than when it was saved, because that is how a clip
/// gets described later: the one from Tuesday. Coarser than [relativeDayLabel] on purpose — a
/// library goes back years, and a heading per day would be mostly headings.
String clipGroupLabel(DateTime at, DateTime now) {
  final today = DateTime(now.year, now.month, now.day);
  final day = DateTime(at.year, at.month, at.day);
  final back = today.difference(day).inDays;

  if (back == 0) return 'Today';
  if (back == 1) return 'Yesterday';
  if (back < 7) return 'This week';

  // Named by month once it is beyond the week — and "Earlier in July" only while July is still the
  // month it is, since in September that would read as this year's July with no year on it.
  if (at.year == now.year && at.month == now.month) {
    return 'Earlier in ${_monthNames[at.month - 1]}';
  }
  if (at.year == now.year) return _monthNames[at.month - 1];

  return '${_monthNames[at.month - 1]} ${at.year}';
}

const _monthNames = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

/// What the scrubber's range button reads — `Last 1 h`, `Sat, 2 h`, `27 Jul, 2 h`.
///
/// The two halves answer different questions, which is why a live range and a chosen one are
/// worded differently rather than both being a duration. Live, the only thing worth saying is how
/// far back the track reaches. A chosen period is somewhere *else*, and which day that is matters
/// more than its length — so the day leads, and the span follows it as the smaller fact.
///
/// A weekday alone is enough inside the last week, which is where nearly every window lands and
/// where "Sat" is unambiguous. Past that it takes a date, because the second Saturday back is not
/// a thing anyone can name.
String rangeButtonLabel(TimelineRange range, DateTime now) {
  // Measured off the window's own edges rather than read off its label, because a window still
  // growing into its width is narrower than the span it will settle at — and every preset's label
  // is what [spanLabel] says about its duration anyway.
  if (range.live) return 'Last ${spanLabel(rangeSpan(range, now))}';

  final start = range.startAt(now);
  final today = DateTime(now.year, now.month, now.day);
  final day = DateTime(start.year, start.month, start.day);
  final back = today.difference(day).inDays;

  final named = switch (back) {
    0 => 'Today',
    1 => 'Yesterday',
    < 7 && > 0 => weekdayLabel(day),
    _ => dayLabel(day),
  };

  return '$named, ${spanLabel(range.duration)}';
}

/// The window a feed is scoped to, worded to finish a sentence — `in the last 6 h`, `in this
/// window`.
///
/// Built from the same [TimelineRange.label] the range button reads rather than from prose of its
/// own, because that button is the control that set the window: a column explaining itself in
/// different words from the control that narrowed it reads as a second, unrelated filter.
///
/// A chosen period gets the vaguer half of the sentence on purpose. Naming its day here would
/// repeat what the button beside it already says, and the empty column's job is only to explain
/// that it is scoped at all.
String rangeScopeLabel(TimelineRange range, {DateTime? now}) => range.live
    ? 'in the last ${spanLabel(rangeSpan(range, now ?? DateTime.now()))}'
    : 'in this window';

/// How wide [range] is at [now], which for a window still growing is less than its duration.
Duration rangeSpan(TimelineRange range, DateTime now) =>
    range.endAt(now).difference(range.startAt(now));

/// How long a moment stays relative before it gets a clock time.
///
/// Read off the design rather than picked: its capture is stamped 4:18 pm and shows "now",
/// "heard just now" and "1 min ago" at the top, then "4:12 pm" — six minutes old — below. So the
/// switch happens within the first few minutes, not at the hour, and "37 min ago" is a shape the
/// design never produces. The same window divides the column's two sections, which is why the
/// mock's "4:12 pm" row sits under *Earlier today* rather than *Right now*.
const _relativeWindow = Duration(minutes: 5);

/// How an activity row names its time.
///
/// The ladder is the design's: the last minute is "now" so the top of the column reads as live,
/// the next few minutes count up, and past that it is a clock time — today bare, yesterday
/// named, anything older with a date. [heard] swaps the first rung for the speech wording the
/// design uses on utterance rows.
String activityTimeLabel(DateTime time, {DateTime? now, bool heard = false}) {
  final reference = now ?? DateTime.now();
  final elapsed = reference.difference(time);

  if (elapsed.inSeconds < 60) {
    return heard ? 'heard just now' : 'now';
  }
  if (elapsed < _relativeWindow) {
    final minutes = elapsed.inMinutes;
    return '$minutes min ago';
  }

  final today = DateTime(reference.year, reference.month, reference.day);
  final day = DateTime(time.year, time.month, time.day);

  if (day == today) return clockLabel(time);
  if (day == today.subtract(const Duration(days: 1))) {
    return '${clockLabel(time)} yesterday';
  }

  return '${clockLabel(time)} · ${time.day} ${_months[time.month - 1]}';
}

/// `6 am`, `12 pm` — an hour with no minutes, for a tick that lands on the hour.
String hourLabel(DateTime time) {
  final hour = time.hour % 12 == 0 ? 12 : time.hour % 12;
  return '$hour ${time.hour < 12 ? 'am' : 'pm'}';
}

/// Where the scrubber puts its tick labels over a window, and what they say.
///
/// Round clock boundaries rather than even divisions of the window. That is the design's own
/// choice, not an aesthetic one added here: its 12-hour track reads "6 am, 9 am, 12 pm, 3 pm",
/// which is a three-hour grid, and its 24-hour track reads "6 pm, 12 am, 6 am, 12 pm", a six-hour
/// one. A grid is also what makes two ranges comparable — the same instant carries the same label
/// whether you are looking at 12 hours or 24.
///
/// The short end has rungs the design never had to name, because it never drew a track this narrow:
/// a quarter-hour grid over a five-minute window is one label or none, and a window arriving from
/// the feed spends its first half hour down there.
///
/// Excludes both edges: the left one would be clipped by the track's own inset, and the right one
/// is where the caller writes the end of the window — see [timelineEndLabel].
List<(DateTime, String)> timelineTicks(DateTime from, DateTime to) {
  final span = to.difference(from);
  if (span <= Duration.zero) return const [];

  final step = span <= const Duration(minutes: 5)
      ? const Duration(minutes: 1)
      : span <= const Duration(minutes: 20)
      ? const Duration(minutes: 5)
      : span <= const Duration(hours: 1)
      ? const Duration(minutes: 15)
      : span <= const Duration(hours: 12)
      ? const Duration(hours: 3)
      : const Duration(hours: 6);

  // Rounded up to the first multiple of the step at or after `from`. Multiples are counted from
  // local midnight rather than from the epoch so a 3-hour grid lands on 12 am, 3 am, 6 am — the
  // epoch is UTC, and in most zones that grid would sit at an offset from the hours a person
  // reads off a clock.
  final midnight = DateTime(from.year, from.month, from.day);
  final elapsed = from.difference(midnight);
  final steps = (elapsed.inMicroseconds / step.inMicroseconds).ceil();

  final ticks = <(DateTime, String)>[];
  for (
    var at = midnight.add(step * steps);
    at.isBefore(to);
    at = at.add(step)
  ) {
    if (!at.isAfter(from)) continue;
    ticks.add((
      at,
      step < const Duration(hours: 1) ? clockLabel(at) : hourLabel(at),
    ));
  }

  return ticks;
}

/// What the label at the track's right edge says.
///
/// `now` while the window keeps pace with the clock, and the time it ends at otherwise. Reaching a
/// past day is an ordinary pick, and a track ending at midnight on the fifth labelled "now" is a
/// false statement about the picture the playhead is about to land on.
///
/// The minutes are kept unless there are none. A window ending at 4:29 is an ordinary shape — every
/// arrival from the feed is one — and labelling its edge `4 pm` is half an hour of lie in the one
/// place on the track that says where it stops.
String timelineEndLabel(DateTime to, {required bool live}) => live
    ? 'now'
    : to.minute == 0
    ? hourLabel(to)
    : clockLabel(to);

/// Whether a moment belongs in the column's "Right now" section.
///
/// The same window that keeps a label relative, so the two agree by construction: everything
/// under *Right now* reads "now" or "N min ago", and everything below it carries a clock time.
/// That is exactly how the design's capture splits — and it is one rule rather than two that
/// could drift apart.
bool isRecent(DateTime time, {DateTime? now}) =>
    (now ?? DateTime.now()).difference(time) < _relativeWindow;
