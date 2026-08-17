using System.Globalization;

namespace Serval.Server.Alerts;

/// <summary>
/// The sentence an alert is announced with.
///
/// <para>Composed on the server and stored, rather than assembled by whichever screen is drawing
/// the row. Every alert is a thing somebody is told about, and what the notification said and what
/// the queue says have to be the same words — two independent renderings of the same record drift
/// the moment one of them is changed.</para>
///
/// <para><b>On the preposition.</b> The natural reading of a camera name varies with the place it
/// points at — "at the front door" but "in the driveway" — and nothing here knows which a name
/// wants. Rather than guess and be wrong half the time, or ask the operator to grammar their camera
/// names, this uses the one construction that works with any of them and reads as a label rather
/// than as a failed sentence.</para>
/// </summary>
internal static class AlertTitle
{
    public static string ForObject(string label, string cameraName) =>
        $"{Class(label)} at {cameraName}";

    public static string ForSound(string label, string cameraName) =>
        $"{Class(Head(label))} heard at {cameraName}";

    /// <summary>
    /// The detector's class string as a word to start a sentence with: <c>person</c> becomes
    /// <c>Person</c>. Only the first letter is touched — a model that already capitalises, or one
    /// whose classes are acronyms, keeps what it sent.
    /// </summary>
    private static string Class(string label)
    {
        string trimmed = label.Trim();

        return trimmed.Length == 0
            ? "Detection"
            : char.ToUpper(trimmed[0], CultureInfo.InvariantCulture) + trimmed[1..];
    }

    /// <summary>
    /// The first name in an AudioSet label. Several of them are a list of synonyms — "Smoke
    /// detector, smoke alarm", "Gunshot, gunfire" — which is useful in a taxonomy and reads as a
    /// stutter in a notification.
    /// </summary>
    private static string Head(string label) => label.Split(',')[0];
}
