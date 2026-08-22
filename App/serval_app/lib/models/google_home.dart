/// Whether the Google Home integration is live, and what is stopping it.
///
/// **Read-only, and there is nothing to write.** Every `Serval:GoogleHome:*` key is
/// environment-only — two are secrets, and two more decide where an anonymous endpoint sends
/// credentials — so the App neither renders a form for them nor holds any of them. What it can
/// usefully do is say which single condition is unmet, which is the thing an operator cannot get
/// from a 503. See `Docs/google-home.md`.
class GoogleHomeStatus {
  const GoogleHomeStatus({
    required this.effective,
    required this.blocker,
    required this.reason,
    required this.publicBaseUrl,
    required this.homeGraphKeyConfigured,
    required this.castReceiverConfigured,
  });

  /// Reads defensively, and the reason is a scar. `blocker` first shipped as an unattributed C#
  /// enum, which System.Text.Json writes as a *number* — so this parsed `1` as a String, threw,
  /// and the screen drew nothing at all. The Server now sends a name and a test pins it, but a
  /// cast that can take a whole feature off the screen is not worth keeping for tidiness: every
  /// field below tolerates the wrong shape rather than throwing.
  factory GoogleHomeStatus.fromJson(Map<String, dynamic> json) =>
      GoogleHomeStatus(
        effective: json['effective'] == true,
        blocker: json['blocker']?.toString() ?? 'Disabled',
        reason: json['reason']?.toString(),
        publicBaseUrl: json['publicBaseUrl']?.toString(),
        homeGraphKeyConfigured: json['homeGraphKeyConfigured'] == true,
        castReceiverConfigured: json['castReceiverConfigured'] == true,
      );

  /// Whether a request to any Google Home route would be served rather than answered 503.
  final bool effective;

  /// The Server's name for the first unmet condition, or `None`. Not shown; [reason] is.
  final String blocker;

  /// The Server's own sentence naming the fix. Null when [effective].
  ///
  /// Rendered verbatim rather than mapped to text here, the same contract the settings page has
  /// with the catalogue: the Server owns the wording, so a condition added there needs no App
  /// release to explain itself.
  final String? reason;

  /// Echoed back so the value can be checked against what was pasted into the Google console.
  final String? publicBaseUrl;

  /// Whether a HomeGraph key path is set. Not a requirement — without one the integration works
  /// and Google simply does not hear about a renamed camera until someone re-links.
  final bool homeGraphKeyConfigured;

  /// Whether a Cast application is registered to play streams with Serval's own receiver.
  ///
  /// Not a requirement. Without one there is simply no Cast button, which is worth reporting
  /// because nothing else says so: Google will not put a camera on a television by voice whatever
  /// is configured here, so the button is the only route to one and its absence looks like a bug.
  final bool castReceiverConfigured;

  /// The deployment has not turned this on — which for almost every deployment is the permanent
  /// state, since it needs a public HTTPS address and a Nest Hub.
  ///
  /// **The App draws nothing at all in this case.** The card's whole value is naming the one
  /// remaining unmet condition while somebody is part-way through setting it up; when the switch
  /// itself is off there is no diagnosis to offer, and a permanently inert card on the status page
  /// of every deployment that will never use this is clutter for a feature nobody asked for.
  ///
  /// Matched on [blocker] rather than on [reason], because the blocker is a stable machine-readable
  /// name and the reason is prose the Server owns and may reword.
  bool get switchedOff => blocker.toLowerCase() == 'disabled';
}

/// A linked Google account. There is at most one.
class GoogleHomeLink {
  const GoogleHomeLink({
    required this.agentUserId,
    required this.linkedAt,
    required this.lastFulfillmentAt,
    required this.lastSyncAt,
  });

  factory GoogleHomeLink.fromJson(Map<String, dynamic> json) => GoogleHomeLink(
    agentUserId: json['agentUserId']?.toString() ?? '',
    linkedAt: DateTime.tryParse(json['linkedAt']?.toString() ?? '')?.toLocal(),
    lastFulfillmentAt: DateTime.tryParse(
      json['lastFulfillmentAt']?.toString() ?? '',
    )?.toLocal(),
    lastSyncAt: DateTime.tryParse(
      json['lastSyncAt']?.toString() ?? '',
    )?.toLocal(),
  );

  /// A generated id, not a username — it is what Google is told and sends back.
  final String agentUserId;

  final DateTime? linkedAt;

  /// The last time Google actually called. This is the field worth reading: it distinguishes a
  /// link that works from one that was made and then quietly stopped being used.
  final DateTime? lastFulfillmentAt;

  /// The last successful `requestSync`. Null when there is no HomeGraph key.
  final DateTime? lastSyncAt;
}
