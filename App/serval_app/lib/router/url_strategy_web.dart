import 'package:flutter_web_plugins/url_strategy.dart';

/// Drops the `#` from every URL, so a camera's address is `/camera/front-door` and reads like one.
void useServalUrlStrategy() => usePathUrlStrategy();
