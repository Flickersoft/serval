/// AI assistants — any MCP client, such as Claude or Gemini — that a person has let read this
/// Serval. See `Docs/mcp.md`.
///
/// Every reader here tolerates the wrong shape rather than throwing, the rule
/// [GoogleHomeStatus](google_home.dart) learned the hard way: a cast that fails takes the whole
/// section off the screen.
library;

/// Whether assistants can connect, and the address to give them.
class AssistantStatus {
  const AssistantStatus({
    required this.effective,
    required this.blocker,
    required this.reason,
    required this.serverUrl,
  });

  factory AssistantStatus.fromJson(Map<String, dynamic> json) =>
      AssistantStatus(
        effective: json['effective'] == true,
        blocker: json['blocker']?.toString() ?? 'disabled',
        reason: json['reason']?.toString(),
        serverUrl: json['serverUrl']?.toString(),
      );

  final bool effective;

  /// The Server's name for what is keeping the endpoint closed, or `none`. Not shown; [reason] is.
  final String blocker;

  /// The Server's own sentence naming the fix, rendered verbatim. Null when [effective].
  final String? reason;

  /// What to paste into an assistant's "add a connector" box. Null while closed.
  final String? serverUrl;

  bool get switchedOff => blocker.toLowerCase() == 'disabled';
}

/// An assistant asking to connect, as the consent screen shows it.
///
/// **The name is the client's own claim.** Anything can register itself as "Claude", so the screen
/// never shows [clientName] alone: [verifiedHost] is the domain a verified client proved it
/// controls, and [redirectTarget] is where the answer is going to be sent.
class AssistantRequest {
  const AssistantRequest({
    required this.clientName,
    required this.verifiedHost,
    required this.redirectTarget,
    required this.verified,
    required this.expiresAt,
  });

  factory AssistantRequest.fromJson(Map<String, dynamic> json) =>
      AssistantRequest(
        clientName: json['clientName']?.toString(),
        verifiedHost: json['verifiedHost']?.toString(),
        redirectTarget: json['redirectTarget']?.toString() ?? '',
        verified: json['verified'] == true,
        expiresAt: DateTime.tryParse(json['expiresAt']?.toString() ?? ''),
      );

  final String? clientName;
  final String? verifiedHost;
  final String redirectTarget;
  final bool verified;
  final DateTime? expiresAt;

  /// What to call the client in a sentence: its name, else the domain it proved, else something
  /// honest about not knowing.
  String get displayName => clientName ?? verifiedHost ?? 'An unnamed app';
}

/// An assistant this person has linked.
class AssistantLink {
  const AssistantLink({
    required this.id,
    required this.clientName,
    required this.verifiedHost,
    required this.verified,
    required this.createdAt,
    required this.lastUsedAt,
  });

  factory AssistantLink.fromJson(Map<String, dynamic> json) => AssistantLink(
    id: json['id']?.toString() ?? '',
    clientName: json['clientName']?.toString(),
    verifiedHost: json['verifiedHost']?.toString(),
    verified: json['verified'] == true,
    createdAt: DateTime.tryParse(json['createdAt']?.toString() ?? ''),
    lastUsedAt: DateTime.tryParse(json['lastUsedAt']?.toString() ?? ''),
  );

  final String id;
  final String? clientName;
  final String? verifiedHost;
  final bool verified;
  final DateTime? createdAt;
  final DateTime? lastUsedAt;

  String get displayName => clientName ?? verifiedHost ?? 'An unnamed app';
}
