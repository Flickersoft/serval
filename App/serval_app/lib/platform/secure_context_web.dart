import 'package:web/web.dart' as web;

/// The browser's own answer, rather than anything derived from the URL. `localhost` and
/// `127.0.0.1` are secure contexts over plain HTTP, a file:// page is not, and the rules have
/// changed before — so asking is the only version of this that stays right.
bool get isSecureContext => web.window.isSecureContext;
