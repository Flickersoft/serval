/// How byte counts are written.
///
/// Its own file with its own test for the same reason
/// [time_labels.dart](time_labels.dart) is one: these are pre-rendered strings the design is
/// specific about, and "1.8 TB of 4 TB" is a sentence the mockup wrote before any endpoint
/// existed to fill it in.
///
/// **Decimal, not binary.** 1 TB is 10^12 bytes here, not 2^40. That is what the drive was sold
/// as, what `df -h --si` and the NAS's own UI say, and what the design's numbers assume — a
/// 4 TB pool rendering as "3.6 TiB" would read as a missing 400 GB to everyone who owns it.
library;

const _units = ['bytes', 'KB', 'MB', 'GB', 'TB', 'PB'];

/// What a null figure looks like. An em dash rather than "0" or "unknown", because the pages that
/// show this already say *why* something is missing in words beside it.
const kNoFigure = '—';

/// `1.8 TB`, `412 GB`, `940 bytes`.
///
/// Three significant figures at most, and no decimal point once the number is large enough not to
/// need one: `1.8 TB` but `412 GB`, not `412.0 GB`. Bytes are never fractional.
String formatBytes(num? bytes) {
  if (bytes == null) return kNoFigure;
  if (bytes < 0) return kNoFigure;

  var value = bytes.toDouble();
  var unit = 0;

  while (value >= 1000 && unit < _units.length - 1) {
    value /= 1000;
    unit++;
  }

  if (unit == 0) return '${value.round()} ${_units[0]}';

  // One decimal below 10 — the difference between 1.8 TB and 1.9 TB is 100 GB and worth showing;
  // the difference between 412.3 GB and 412.4 GB is noise on a number that moves every minute.
  // A trailing ".0" is then dropped, because the design writes "1.8 TB of 4 TB" and not "4.0 TB".
  var text = value < 10 ? value.toStringAsFixed(1) : value.round().toString();
  if (text.endsWith('.0')) text = text.substring(0, text.length - 2);

  return '$text ${_units[unit]}';
}

/// `59 GB/day`, or [kNoFigure] when the Server has not measured a span long enough to divide by.
String formatBytesPerDay(num? bytesPerDay) =>
    bytesPerDay == null ? kNoFigure : '${formatBytes(bytesPerDay)}/day';

/// `41%`, `6%` — whole percentages, because nothing on these pages is decided by a tenth of one.
String formatPercent(num? percent) =>
    percent == null ? kNoFigure : '${percent.round()}%';

/// `7 days`, `14 hours`, `36 minutes` — how far back the oldest footage goes.
///
/// Coarse on purpose: this stands beside a byte count to give it a span, and "6 days 22 hours" is
/// a precision nobody asked for on a figure whose whole job is to make "412 GB" mean something.
String formatSpan(Duration? span) {
  if (span == null || span < Duration.zero) return kNoFigure;

  if (span.inDays >= 1) {
    return span.inDays == 1 ? '1 day' : '${span.inDays} days';
  }
  if (span.inHours >= 1) {
    return span.inHours == 1 ? '1 hour' : '${span.inHours} hours';
  }
  final minutes = span.inMinutes;
  return minutes == 1 ? '1 minute' : '$minutes minutes';
}

/// `up 4 days`, `up 3 hours` — the Server's own uptime, in the design's lowercase style.
String formatUptime(double? seconds) => seconds == null
    ? kNoFigure
    : 'up ${formatSpan(Duration(seconds: seconds.round()))}';
