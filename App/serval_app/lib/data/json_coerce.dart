/// Number coercion for JSON payloads. On the web every JSON number is a
/// double, so a field this app treats as `int` still arrives as `num` —
/// reading either type through these keeps the models honest on both
/// platforms without a cast that only fails in a browser.
library;

double? asDouble(Object? value) => switch (value) {
  final num number => number.toDouble(),
  _ => null,
};

int? asInt(Object? value) => switch (value) {
  final num number => number.round(),
  _ => null,
};
