import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/time_labels.dart';
import '../models/timeline.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_editable_text.dart';

/// Picking what stretch of the recording the scrubber shows.
///
/// A panel rather than a modal, and it applies as you touch it: every span, every day and every
/// readable pair of times goes straight to the track behind, so you can see what a choice gets you
/// before committing to it. *Done* closes what you are already looking at; there is no button that
/// makes it true.
///
/// It replaced a row of three presets and a *Custom* escape hatch. A preset row with an exception
/// beside it says the presets are the real answer and everything else is unusual, which is backwards
/// once there are days of footage behind you — the days here are as ordinary a thing to pick as
/// *1 h* is, and the calendar behind *Earlier…* is one click rather than a mode.
///
/// The spans are not a second thing to choose. *1 h* and *today, 13:30 to 14:30* name the same
/// window, so a span fills in the day and the two times and lights those: one highlight, on the
/// right, wherever the choice came from. Two halves that lit independently could show two answers at
/// once, or none.
///
/// The one thing it will not do is show more than [TimelineRange.maxSpan] at once. Ask for more
/// through the two fields and it holds there and says so.
class TimelineRangePanel extends StatefulWidget {
  const TimelineRangePanel({
    super.key,
    required this.now,
    required this.range,
    required this.onChanged,
    required this.onDone,
    this.onBackToLive,
  });

  /// Passed in rather than read from the clock, so the strip of days agrees with the window the
  /// caller is about to build, and so this is testable.
  final DateTime now;

  /// What the scrubber is showing as the panel opens. Read once, to seed the day and the two
  /// fields; the panel's own state is what it draws from thereafter.
  final TimelineRange range;

  /// Fires on every touch, not on the way out.
  final ValueChanged<TimelineRange> onChanged;

  final VoidCallback onDone;

  /// Null while the scrubber is already live, which is when the footer's way back is not an
  /// action but a description of where you are.
  final VoidCallback? onBackToLive;

  /// How many days the strip offers before *Earlier…* takes over. A week is the shape of nearly
  /// every question asked of a camera, and it is the run a weekday name reads unambiguously over.
  static const daysOffered = 7;

  /// Under this the spans stop being a column beside the days and become a row above them. A
  /// phone has no room for two columns, and the days are the wider of the two.
  static const stackBelow = 460.0;

  @override
  State<TimelineRangePanel> createState() => _TimelineRangePanelState();
}

class _TimelineRangePanelState extends State<TimelineRangePanel> {
  /// The day being shown. The day the window *starts* on, which is the day a window crossing
  /// midnight is named by everywhere else — see `rangeButtonLabel`.
  late DateTime _day;

  /// Whether the two times were put there by a span rather than typed.
  ///
  /// The difference only matters when a day is picked next: an hour that means "the last one" is an
  /// arbitrary pair of clock edges on any other day.
  late bool _fromSpan;

  /// Whether the days half has swapped itself for a month.
  bool _earlier = false;

  /// The month on show behind *Earlier…*, as its first day.
  late DateTime _month;

  late final TextEditingController _from;
  late final TextEditingController _to;
  final _fromFocus = FocusNode();
  final _toFocus = FocusNode();

  /// Typing is not touching. Every keystroke would otherwise be a new window key, and with it a
  /// fresh `/coverage` read and four telemetry reads for a time that is halfway typed.
  Timer? _typed;
  static const _typingSettles = Duration(milliseconds: 350);

  @override
  void initState() {
    super.initState();

    _from = TextEditingController();
    _to = TextEditingController();
    _reflect(widget.range);

    _fromFocus.addListener(_onFocusChanged);
    _toFocus.addListener(_onFocusChanged);
  }

  @override
  void dispose() {
    _typed?.cancel();
    _from.dispose();
    _to.dispose();
    _fromFocus.dispose();
    _toFocus.dispose();
    super.dispose();
  }

  void _onFocusChanged() => setState(() {});

  static DateTime _midnight(DateTime at) => DateTime(at.year, at.month, at.day);

  static DateTime _firstOfMonth(DateTime at) => DateTime(at.year, at.month);

  static String _clock(DateTime at) =>
      '${at.hour.toString().padLeft(2, '0')}:${at.minute.toString().padLeft(2, '0')}';

  /// `21:00`, `9:5`, `2100` — all of them, because a time field that only accepts one spelling is
  /// a field that gets retyped. `24:00` too, which is the only way to say the far end of a day.
  /// Null when it cannot be read as a time at all.
  static Duration? parseClock(String raw) {
    final text = raw.trim();
    if (text.isEmpty) return null;

    final match = RegExp(r'^(\d{1,2})\s*[:.h]?\s*(\d{2})?$').firstMatch(text);
    if (match == null) return null;

    final hours = int.parse(match.group(1)!);
    final minutes = int.parse(match.group(2) ?? '0');
    if (minutes > 59) return null;
    if (hours > 24 || (hours == 24 && minutes > 0)) return null;

    return Duration(hours: hours, minutes: minutes);
  }

  /// The window the panel currently describes, or null while the fields do not describe one.
  ///
  /// A *To* at or before *From* runs into the next day rather than being an error — "23:00 to
  /// 01:00" is an ordinary way to say the small hours, and rejecting it would be pedantry.
  (DateTime, DateTime)? get _window {
    final from = parseClock(_from.text);
    final to = parseClock(_to.text);
    if (from == null || to == null) return null;

    final start = _day.add(from);
    var end = _day.add(to);
    if (!end.isAfter(start)) end = end.add(const Duration(days: 1));

    return (start, end);
  }

  /// What is wrong with the fields, or what the panel had to do to them. Null while the day and
  /// the two times are showing exactly as asked.
  String? get _note {
    if (_window == null) return 'Times read as 21:00 or 9:30.';

    final (start, end) = _window!;
    if (end.difference(start) > TimelineRange.maxSpan) {
      return 'Holding at ${TimelineRange.maxSpan.inHours} h — the most that can be shown at once.';
    }
    if (start.isAfter(widget.now)) return 'That period has not happened yet.';

    return null;
  }

  /// Today back through the week the strip offers, newest first — the order the feed reads in.
  List<DateTime> get _days => [
    for (var i = 0; i < TimelineRangePanel.daysOffered; i++)
      _midnight(widget.now).subtract(Duration(days: i)),
  ];

  String _dayName(DateTime day) {
    final back = _midnight(widget.now).difference(day).inDays;
    if (back == 0) return 'Today';
    return weekdayLabel(day);
  }

  // ---------------------------------------------------------------- applying

  /// Shows [range] on the days and the two fields, without applying anything.
  ///
  /// Both ends land on the same day whenever the range fits inside one, and on consecutive days
  /// when it does not — which [_window] reads back the same way round, since a *To* at or before
  /// the *From* is already how this panel spells a window running past midnight.
  void _reflect(TimelineRange range) {
    final start = range.startAt(widget.now);

    _day = _midnight(start);
    _month = _firstOfMonth(start);
    _from.text = _clock(start);
    _to.text = _clock(range.endAt(widget.now));
    _fromSpan = range.live;
  }

  /// A span applies as itself — live, ending at now — and shows itself on the right as the window
  /// it currently means. Reflecting it is not the same as converting it: the preset is what keeps
  /// the track pacing the clock, and a window ending at 14:30 would freeze there.
  void _pickSpan(TimelineRange preset) {
    _typed?.cancel();
    setState(() => _reflect(preset));
    widget.onChanged(preset);
  }

  void _pickDay(DateTime day) {
    _typed?.cancel();
    setState(() {
      // A span carries no times worth transplanting onto a day — "the last hour" moved onto last
      // Tuesday would mean whatever o'clock it happens to be now. The whole day is what picking a
      // day out of the blue means, and it is the ceiling exactly.
      if (_fromSpan) {
        _from.text = '00:00';
        _to.text = '24:00';
        _fromSpan = false;
      }

      _day = day;
      _month = _firstOfMonth(day);
    });
    _emit();
  }

  /// Applies what the fields say, if they say anything and it has already happened.
  ///
  /// A span over the ceiling still applies: [TimelineRange.window] holds it at
  /// [TimelineRange.maxSpan] keeping the *To* edge, and [_note] says so — which is a more useful
  /// answer than refusing, since the end you named last is the end you were reaching for.
  void _emit() {
    final window = _window;
    if (window == null) return;

    final (start, end) = window;
    if (start.isAfter(widget.now)) return;

    widget.onChanged(TimelineRange.window(from: start, to: end));
  }

  void _onTyped() {
    setState(() => _fromSpan = false);
    _typed?.cancel();
    _typed = Timer(_typingSettles, _emit);
  }

  /// Sets the fields and applies them at once — the two chips, which are not typing.
  void _setTimes(String from, String to) {
    _typed?.cancel();
    _from.text = from;
    _to.text = to;
    setState(() => _fromSpan = false);
    _emit();
  }

  void _extendAnHour() {
    final window = _window;
    if (window == null) return;

    final (start, end) = window;
    final wanted = end.add(const Duration(hours: 1));
    // The ceiling is the ceiling from either direction: pushing the far edge out past a day drags
    // the near one with it rather than quietly showing less than the field claims.
    final capped = wanted.difference(start) > TimelineRange.maxSpan
        ? wanted.subtract(TimelineRange.maxSpan)
        : start;

    _setTimes(_clock(capped), _clock(wanted));
  }

  void _backToLive() {
    _typed?.cancel();
    widget.onBackToLive?.call();
    widget.onDone();
  }

  // ----------------------------------------------------------------- drawing

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final stacked = constraints.maxWidth < TimelineRangePanel.stackBelow;

      return DecoratedBox(
        decoration: BoxDecoration(
          color: Serval.overlay,
          borderRadius: BorderRadius.circular(Nocturne.radiusMd + 1),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 13)),
          boxShadow: Nocturne.shadowLg,
        ),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(Nocturne.radiusMd + 1),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Scrollable, and the footer outside it: a phone held sideways leaves barely more
              // height than the panel wants, and the way out must not be the part that goes
              // over the edge.
              Flexible(
                child: SingleChildScrollView(
                  child: stacked
                      ? Column(
                          mainAxisSize: MainAxisSize.min,
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [_spansRow, _daysHalf(fill: false)],
                        )
                      : IntrinsicHeight(
                          child: Row(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              SizedBox(width: 150, child: _spansColumn),
                              Container(
                                width: 1,
                                color: Nocturne.mix(Nocturne.text, 8),
                              ),
                              // The two columns are rarely the same height, and the spare
                              // belongs between the days and the times rather than under the
                              // times — which would leave the fields adrift in the middle.
                              Expanded(child: _daysHalf(fill: true)),
                            ],
                          ),
                        ),
                ),
              ),
              _footer,
            ],
          ),
        ),
      );
    },
  );

  /// The spans as a column, which is what they are whenever there is width for two of them.
  Widget get _spansColumn => Padding(
    padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 8),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 2, 8, 6),
          child: Text('UP TO NOW', style: kickerStyle(fontSize: 9.5)),
        ),
        for (final preset in TimelineRange.presets)
          _SpanRow(label: preset.label, onTap: () => _pickSpan(preset)),
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 6, 8, 2),
          child: Text(
            'the most at once',
            style: monoStyle(
              fontSize: 10,
              color: Nocturne.mix(Nocturne.text, 33),
            ),
          ),
        ),
      ],
    ),
  );

  /// The same spans as a row of pills, for a panel too narrow to give them a column.
  Widget get _spansRow => Container(
    padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
    decoration: BoxDecoration(
      border: Border(bottom: BorderSide(color: Nocturne.mix(Nocturne.text, 8))),
    ),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('UP TO NOW', style: kickerStyle(fontSize: 9.5)),
        const SizedBox(height: 8),
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            for (final preset in TimelineRange.presets)
              _Pill(label: preset.label, onTap: () => _pickSpan(preset)),
          ],
        ),
      ],
    ),
  );

  /// The days half: a week, or the month behind *Earlier…*, and under either the two times.
  Widget _daysHalf({required bool fill}) => Padding(
    padding: const EdgeInsets.fromLTRB(14, 12, 14, 14),
    child: Column(
      mainAxisSize: fill ? MainAxisSize.max : MainAxisSize.min,
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (_earlier) _month2 else _week,
        Padding(
          padding: const EdgeInsets.only(top: 12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _times,
              if (_note != null) ...[const SizedBox(height: 9), _noteLine],
            ],
          ),
        ),
      ],
    ),
  );

  Widget get _week => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Text('A DAY', style: kickerStyle(fontSize: 9.5)),
      const SizedBox(height: 8),
      Row(
        children: [
          for (final day in _days)
            Expanded(
              child: Padding(
                padding: const EdgeInsets.only(right: 4),
                child: _DayCard(
                  name: _dayName(day),
                  number: day.day,
                  selected: day == _day,
                  onTap: () => _pickDay(day),
                ),
              ),
            ),
        ],
      ),
      const SizedBox(height: 8),
      Align(
        alignment: Alignment.centerRight,
        child: _Link(
          label: 'Earlier…',
          icon: PhosphorIconsRegular.calendarBlank,
          onTap: () => setState(() => _earlier = true),
        ),
      ),
    ],
  );

  /// Only the days block swaps for the month. The spans, the fields and the footer stay where
  /// they were, so the panel does not jump out from under the pointer that opened it.
  Widget get _month2 {
    final today = _midnight(widget.now);
    final first = _month;
    final days = DateTime(first.year, first.month + 1, 0).day;

    // Monday-first, so the two weekend columns end up beside each other.
    final blanks = first.weekday - 1;
    final forward = _firstOfMonth(widget.now);

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('A DAY · EARLIER', style: kickerStyle(fontSize: 9.5)),
            ),
            _MonthArrow(
              icon: PhosphorIconsRegular.caretLeft,
              onTap: () => setState(
                () => _month = DateTime(first.year, first.month - 1),
              ),
            ),
            const SizedBox(width: 8),
            Text(
              '${_monthNames[first.month - 1]} ${first.year}',
              style: const TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: Nocturne.text,
              ),
            ),
            const SizedBox(width: 8),
            _MonthArrow(
              icon: PhosphorIconsRegular.caretRight,
              // Forward stops at this month: there is nothing recorded on a day that has not
              // happened, and a grid of them is a grid of dead ends.
              onTap: first.isBefore(forward)
                  ? () => setState(
                      () => _month = DateTime(first.year, first.month + 1),
                    )
                  : null,
            ),
          ],
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            for (final letter in const ['M', 'T', 'W', 'T', 'F', 'S', 'S'])
              Expanded(
                child: Text(
                  letter,
                  textAlign: TextAlign.center,
                  style: monoStyle(
                    fontSize: 9,
                    letterSpacing: 0.06 * 9,
                    color: Nocturne.mix(Nocturne.text, 30),
                  ),
                ),
              ),
          ],
        ),
        const SizedBox(height: 4),
        for (var row = 0; row * 7 < blanks + days; row++)
          Padding(
            padding: const EdgeInsets.only(bottom: 3),
            child: Row(
              children: [
                for (var column = 0; column < 7; column++)
                  Expanded(
                    child: Padding(
                      padding: const EdgeInsets.only(right: 3),
                      child: _monthCell(
                        row * 7 + column - blanks + 1,
                        days,
                        today,
                      ),
                    ),
                  ),
              ],
            ),
          ),
        const SizedBox(height: 5),
        Row(
          children: [
            _Pill(
              label: 'Today',
              small: true,
              selected: _day == today,
              onTap: () => _pickDay(today),
            ),
            const SizedBox(width: 8),
            _Pill(
              label: 'Yesterday',
              small: true,
              selected: _day == today.subtract(const Duration(days: 1)),
              onTap: () => _pickDay(today.subtract(const Duration(days: 1))),
            ),
            const Spacer(),
            _Link(
              label: 'This week',
              icon: PhosphorIconsRegular.arrowUUpLeft,
              onTap: () => setState(() => _earlier = false),
            ),
          ],
        ),
      ],
    );
  }

  Widget _monthCell(int number, int days, DateTime today) {
    if (number < 1 || number > days) return const SizedBox.shrink();

    final date = DateTime(_month.year, _month.month, number);
    final ahead = date.isAfter(today);

    return _MonthCell(
      number: number,
      selected: date == _day,
      // Dimmed rather than absent: a month with holes punched in it is harder to read than one
      // whose unreachable days are simply quiet.
      ahead: ahead,
      onTap: ahead ? null : () => _pickDay(date),
    );
  }

  Widget get _times => Row(
    crossAxisAlignment: CrossAxisAlignment.end,
    children: [
      Expanded(
        child: _TimeField(
          label: 'From',
          controller: _from,
          focus: _fromFocus,
          onChanged: _onTyped,
        ),
      ),
      const SizedBox(width: 10),
      Expanded(
        child: _TimeField(
          label: 'To',
          controller: _to,
          focus: _toFocus,
          onChanged: _onTyped,
        ),
      ),
      const SizedBox(width: 10),
      _Pill(label: '+1 h', onTap: _extendAnHour),
      const SizedBox(width: 5),
      _Pill(
        label: 'whole day',
        // Exactly the ceiling, so the two halves of the panel meet at the same limit rather than
        // contradicting each other.
        onTap: () => _setTimes('00:00', '24:00'),
      ),
    ],
  );

  Widget get _noteLine => Text(
    _note!,
    style: TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 11.5,
      height: 1.4,
      color: _window == null ? Serval.alert : Nocturne.mix(Nocturne.text, 45),
    ),
  );

  Widget get _footer => Container(
    padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 2),
      border: Border(top: BorderSide(color: Nocturne.mix(Nocturne.text, 8))),
    ),
    child: Row(
      children: [
        if (widget.onBackToLive != null)
          _Link(
            label: 'Back to live',
            dot: Serval.recording,
            onTap: _backToLive,
          ),
        const Spacer(),
        _Pill(label: 'Done', primary: true, onTap: widget.onDone),
      ],
    ),
  );
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

/// One row of the *Up to now* column.
///
/// Never lit: a span is a way of filling in the day and the times beside it, and what it filled in
/// is what shows the choice.
class _SpanRow extends StatefulWidget {
  const _SpanRow({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  State<_SpanRow> createState() => _SpanRowState();
}

class _SpanRowState extends State<_SpanRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTap: widget.onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
        decoration: BoxDecoration(
          color: _hovered ? Nocturne.mix(Nocturne.text, 6) : null,
          borderRadius: BorderRadius.circular(Nocturne.radiusSm + 2),
        ),
        child: Text(
          widget.label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12.5,
            fontWeight: FontWeight.w400,
            color: Nocturne.mix(Nocturne.text, 80),
          ),
        ),
      ),
    ),
  );
}

/// One day in the week strip: its name over its date.
class _DayCard extends StatefulWidget {
  const _DayCard({
    required this.name,
    required this.number,
    required this.selected,
    required this.onTap,
  });

  final String name;
  final int number;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_DayCard> createState() => _DayCardState();
}

class _DayCardState extends State<_DayCard> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTap: widget.onTap,
      child: Container(
        padding: const EdgeInsets.fromLTRB(2, 6, 2, 5),
        decoration: BoxDecoration(
          color: widget.selected
              ? Nocturne.mix(Nocturne.accent, 16)
              : _hovered
              ? Nocturne.mix(Nocturne.text, 6)
              : null,
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: widget.selected
                ? Nocturne.mix(Nocturne.accent, 60)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              widget.name,
              maxLines: 1,
              overflow: TextOverflow.clip,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 11,
                fontWeight: widget.selected
                    ? Nocturne.headingWeight
                    : FontWeight.w400,
                color: widget.selected
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, 75),
              ),
            ),
            const SizedBox(height: 3),
            Text(
              '${widget.number}',
              style: monoStyle(
                fontSize: 9,
                color: widget.selected
                    ? Nocturne.mix(Nocturne.accent300, 70)
                    : Nocturne.mix(Nocturne.text, 35),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

/// One date in the month behind *Earlier…*.
class _MonthCell extends StatefulWidget {
  const _MonthCell({
    required this.number,
    required this.selected,
    required this.ahead,
    required this.onTap,
  });

  final int number;
  final bool selected;

  /// A day that has not happened. Drawn, but quiet and unreachable.
  final bool ahead;

  final VoidCallback? onTap;

  @override
  State<_MonthCell> createState() => _MonthCellState();
}

class _MonthCellState extends State<_MonthCell> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: widget.onTap == null
        ? SystemMouseCursors.basic
        : SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTap: widget.onTap,
      child: Container(
        alignment: Alignment.center,
        padding: const EdgeInsets.symmetric(vertical: 5),
        decoration: BoxDecoration(
          color: widget.selected
              ? Nocturne.mix(Nocturne.accent, 16)
              : _hovered && widget.onTap != null
              ? Nocturne.mix(Nocturne.text, 6)
              : null,
          borderRadius: BorderRadius.circular(Nocturne.radiusSm + 2),
          border: Border.all(
            color: widget.selected
                ? Nocturne.mix(Nocturne.accent, 60)
                : const Color(0x00000000),
          ),
        ),
        child: Text(
          '${widget.number}',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 11.5,
            fontWeight: widget.selected
                ? Nocturne.headingWeight
                : FontWeight.w400,
            color: widget.selected
                ? Nocturne.accent300
                : Nocturne.mix(Nocturne.text, widget.ahead ? 22 : 78),
          ),
        ),
      ),
    ),
  );
}

class _MonthArrow extends StatelessWidget {
  const _MonthArrow({required this.icon, required this.onTap});

  final PhosphorIconData icon;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: onTap == null ? SystemMouseCursors.basic : SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      child: PhosphorIcon(
        icon,
        size: 13,
        color: Nocturne.mix(Nocturne.text, onTap == null ? 12 : 45),
      ),
    ),
  );
}

/// One of the two typed edges of a window.
class _TimeField extends StatelessWidget {
  const _TimeField({
    required this.label,
    required this.controller,
    required this.focus,
    required this.onChanged,
  });

  final String label;
  final TextEditingController controller;
  final FocusNode focus;
  final VoidCallback onChanged;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 55),
        ),
      ),
      const SizedBox(height: 5),
      Container(
        height: 30,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        alignment: Alignment.centerLeft,
        decoration: BoxDecoration(
          color: focus.hasFocus
              ? Nocturne.mix(Nocturne.accent, 6)
              : Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(Nocturne.radiusSm + 2),
          border: Border.all(
            color: focus.hasFocus
                ? Nocturne.mix(Nocturne.accent, 55)
                : Nocturne.mix(Nocturne.text, 15),
          ),
        ),
        child: NocturneEditableText(
          controller: controller,
          focusNode: focus,
          style: monoStyle(fontSize: 12.5, color: Nocturne.text),
          onChanged: (_) => onChanged(),
        ),
      ),
    ],
  );
}

/// The panel's small round buttons — the two field shortcuts, the month's *Today*, and *Done*.
class _Pill extends StatefulWidget {
  const _Pill({
    required this.label,
    required this.onTap,
    this.selected = false,
    this.primary = false,
    this.small = false,
  });

  final String label;
  final VoidCallback? onTap;
  final bool selected;

  /// The one action shaped like the design's primary button — an accent outline over an accent
  /// tint, never a fill.
  final bool primary;

  final bool small;

  @override
  State<_Pill> createState() => _PillState();
}

class _PillState extends State<_Pill> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onTap != null;
    final lit = widget.primary || widget.selected;
    final height = widget.small ? 26.0 : 30.0;

    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        // No `alignment` on the Container: one that has it expands to fill the loose constraints
        // a Wrap hands it, so every pill would stretch to the panel's width and stack one per
        // row. The same trap `_DayChip` documented before it.
        child: Container(
          height: height,
          padding: EdgeInsets.symmetric(horizontal: widget.primary ? 16 : 9),
          decoration: BoxDecoration(
            color: lit
                ? Nocturne.mix(Nocturne.accent, 16)
                : _hovered && enabled
                ? Nocturne.mix(Nocturne.text, 6)
                : null,
            borderRadius: BorderRadius.circular(
              widget.primary ? 7 : height / 2,
            ),
            border: Border.all(
              color: lit
                  ? Nocturne.mix(Nocturne.accent, 65)
                  : Nocturne.mix(Nocturne.text, enabled ? 14 : 8),
            ),
          ),
          child: Center(
            widthFactor: 1,
            child: Text(
              widget.label,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: widget.primary ? 12.5 : 11.5,
                fontWeight: lit ? Nocturne.headingWeight : FontWeight.w400,
                color: lit
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, enabled ? 70 : 30),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A word you can click, with a glyph or a dot ahead of it — *Earlier…*, *Back to live*.
class _Link extends StatefulWidget {
  const _Link({required this.label, required this.onTap, this.icon, this.dot});

  final String label;
  final VoidCallback onTap;
  final PhosphorIconData? icon;
  final Color? dot;

  @override
  State<_Link> createState() => _LinkState();
}

class _LinkState extends State<_Link> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    onEnter: (_) => setState(() => _hovered = true),
    onExit: (_) => setState(() => _hovered = false),
    child: GestureDetector(
      onTap: widget.onTap,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (widget.dot != null) ...[
            Container(
              width: 6,
              height: 6,
              decoration: BoxDecoration(
                color: widget.dot,
                shape: BoxShape.circle,
              ),
            ),
            const SizedBox(width: 7),
          ] else if (widget.icon != null) ...[
            PhosphorIcon(
              widget.icon!,
              size: 13,
              color: _hovered ? Nocturne.accent300 : Nocturne.accent400,
            ),
            const SizedBox(width: 5),
          ],
          Text(
            widget.label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              color: _hovered ? Nocturne.accent300 : Nocturne.accent400,
            ),
          ),
        ],
      ),
    ),
  );
}

/// Opens [TimelineRangePanel] over whatever opened it, anchored to that control.
///
/// A route rather than an overlay entry, so the screen underneath stays mounted and repaints as
/// the range changes — which is the point of the thing. The barrier is transparent and dismissive:
/// clicking away is the same as *Done*, because everything is already applied.
Future<void> showTimelineRangePanel({
  required BuildContext context,
  required RenderBox anchor,
  required DateTime now,
  required TimelineRange range,
  required ValueChanged<TimelineRange> onChanged,
  VoidCallback? onBackToLive,
}) {
  final overlay =
      Navigator.of(context).overlay!.context.findRenderObject()! as RenderBox;
  final topLeft = anchor.localToGlobal(Offset.zero, ancestor: overlay);

  return Navigator.of(context).push(
    _RangePanelRoute(
      anchor: topLeft & anchor.size,
      builder: (context) => TimelineRangePanel(
        now: now,
        range: range,
        onChanged: onChanged,
        onBackToLive: onBackToLive,
        onDone: () => Navigator.of(context).pop(),
      ),
    ),
  );
}

class _RangePanelRoute extends PopupRoute<void> {
  _RangePanelRoute({required this.anchor, required this.builder});

  final Rect anchor;
  final WidgetBuilder builder;

  @override
  Color? get barrierColor => null;

  @override
  bool get barrierDismissible => true;

  @override
  String get barrierLabel => 'Close the range panel';

  @override
  Duration get transitionDuration => const Duration(milliseconds: 90);

  @override
  Widget buildPage(
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
  ) => CustomSingleChildLayout(
    delegate: _PanelLayout(anchor),
    child: Builder(builder: builder),
  );

  @override
  Widget buildTransitions(
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
    Widget child,
  ) => FadeTransition(opacity: animation, child: child);
}

/// Puts the panel above its control and against its right edge, and inside the screen either way.
class _PanelLayout extends SingleChildLayoutDelegate {
  const _PanelLayout(this.anchor);

  final Rect anchor;

  static const _margin = 8.0;
  static const _gap = 6.0;
  static const _width = 520.0;

  @override
  BoxConstraints getConstraintsForChild(BoxConstraints constraints) {
    final width = math.min(_width, constraints.maxWidth - _margin * 2);
    final above = anchor.top - _gap - _margin;
    final below = constraints.maxHeight - anchor.bottom - _gap - _margin;

    return BoxConstraints(
      minWidth: width,
      maxWidth: width,
      maxHeight: math.max(math.max(above, below), 0),
    );
  }

  @override
  Offset getPositionForChild(Size size, Size childSize) {
    final x = (anchor.right - childSize.width).clamp(
      _margin,
      math.max(_margin, size.width - childSize.width - _margin),
    );

    // Above by preference: the scrubber lives along the bottom of every screen it is on, so
    // below is where there is never any room.
    final above = anchor.top - _gap - childSize.height;
    final y = above >= _margin
        ? above
        : (anchor.bottom + _gap).clamp(
            _margin,
            math.max(_margin, size.height - childSize.height - _margin),
          );

    return Offset(x.toDouble(), y.toDouble());
  }

  @override
  bool shouldRelayout(_PanelLayout oldDelegate) => oldDelegate.anchor != anchor;
}
