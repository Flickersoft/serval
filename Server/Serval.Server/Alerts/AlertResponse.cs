using System.Text.Json.Serialization;
using Serval.Contracts;

namespace Serval.Server.Alerts;

/// <summary>
/// One alert as the App reads it — the queue row and the card are the same record, so there is one
/// shape rather than a summary and a detail.
///
/// <para>The camera's <em>name</em> is resolved when this is built rather than stored on the alert.
/// An alert is a live thing with a lifetime of days, unlike a saved clip, which freezes the name it
/// was taken under because it outlives the camera. Renaming a camera should rename it in a queue
/// somebody is looking at right now.</para>
/// </summary>
/// <param name="Recorded">
/// Whether the camera's recording covers this moment, which is the only thing that decides whether
/// the card offers to open it. False is ordinary, not a fault: detections fire whether or not a
/// camera is set to record. Null while the clip is still pending — see <see cref="Alert.Recorded"/>
/// for why the difference between "no" and "not yet" has to survive onto the wire.
/// </param>
public sealed record AlertResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("camera_name")] string CameraName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("peak_at")] DateTimeOffset PeakAt,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("box")] DetectionBox? Box,
    [property: JsonPropertyName("read")] bool Read,
    [property: JsonPropertyName("dismissed")] bool Dismissed,
    [property: JsonPropertyName("clip_state")] string ClipState,
    [property: JsonPropertyName("clip_seconds")] double? ClipSeconds,
    [property: JsonPropertyName("recorded")] bool? Recorded)
{
    /// <summary>The wire type on the live feed, and what the App switches on to route the message.</summary>
    public const string EventType = "alert";

    public static AlertResponse From(Alert alert, string cameraName) => new(
        Id: alert.Id,
        CameraId: alert.CameraId,
        CameraName: cameraName,
        Kind: alert.Kind.ToString().ToLowerInvariant(),
        At: alert.At,
        PeakAt: alert.PeakAt,
        Label: alert.Label,
        Title: alert.Title,
        Box: alert.Box,
        Read: alert.Read,
        Dismissed: alert.DismissedAt is not null,
        ClipState: alert.ClipState.ToString().ToLowerInvariant(),
        ClipSeconds: alert.ClipSeconds,
        Recorded: alert.Recorded);
}

/// <summary>
/// The queue and how much of it is new.
///
/// The count rides along rather than living on its own route because it is drawn on the same screen
/// as the list and has to agree with it — two requests can disagree, and a header saying "4 new"
/// over three rows is the kind of wrong nobody can explain.
/// </summary>
public sealed record AlertPage(
    [property: JsonPropertyName("items")] IReadOnlyList<AlertResponse> Items,
    [property: JsonPropertyName("unread")] long Unread);
