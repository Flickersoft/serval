namespace Serval.Server.Mcp;

/// <summary>
/// Addresses in the App that a tool result hands back, so an assistant can offer the person a link
/// to see something for themselves — the recording behind a description, rather than a still or a
/// paraphrase. They are the App's own routes on the public address, so opening one needs the
/// person's own sign-in, the same as any other bookmark; nothing in them is a credential.
///
/// <para>Built here and nowhere else, and the server instructions tell a model to pass them on
/// verbatim rather than compose its own: a model that has seen the pattern will otherwise invent
/// links for things no tool returned.</para>
///
/// <para>Every method returns null when there is no public address, which only happens while the
/// endpoint is closed and no tool can run.</para>
/// </summary>
internal static class McpLinks
{
    /// <summary>
    /// How far before the moment a recording link opens. The App's feed and alert cards seek to the
    /// same five seconds before (<c>feedLeadIn</c> in the App's <c>models/timeline.dart</c>), because
    /// a detection is stamped once its cause is already underway; the App applies it when a row is
    /// clicked, not when it reads <c>?at=</c>, so a link from outside has to carry it already.
    /// </summary>
    internal static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(5);

    /// <summary>The camera's own page, live.</summary>
    internal static string? Camera(string? issuer, string cameraId) =>
        issuer is null ? null : $"{issuer}/camera/{Uri.EscapeDataString(cameraId)}";

    /// <summary>
    /// The camera's recording, opened <see cref="LeadIn"/> before <paramref name="at"/>. In UTC with
    /// a <c>Z</c>, because a <c>+</c> in an offset reads as a space once it is in a query string.
    /// </summary>
    internal static string? CameraAt(string? issuer, string cameraId, DateTimeOffset at) =>
        Camera(issuer, cameraId) is { } camera
            ? $"{camera}?at={(at - LeadIn).UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z"
            : null;

    /// <summary>The alert's card, which carries its picture and a Watch button.</summary>
    internal static string? Alert(string? issuer, string alertId) =>
        issuer is null ? null : $"{issuer}/alerts/{Uri.EscapeDataString(alertId)}";

    /// <summary>The saved clip's page, which plays it.</summary>
    internal static string? Clip(string? issuer, string clipId) =>
        issuer is null ? null : $"{issuer}/clips/{Uri.EscapeDataString(clipId)}";
}
