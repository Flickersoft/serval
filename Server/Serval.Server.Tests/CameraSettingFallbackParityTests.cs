using System.Globalization;
using System.Text.RegularExpressions;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// The camera editor's fallback table has to agree with the catalogue it falls back from.
///
/// <para><b>Why there are two copies at all.</b> The camera settings form draws a card per
/// overridable field using the label, help and bounds the Server sent with
/// <c>GET /api/settings</c> — one catalogue, one label, one range. But a Viewer whose settings read
/// fails, or an App opened before the Server answers, has no catalogue to draw from, so
/// <c>CameraSetting</c> in <c>server_camera_defaults.dart</c> carries a copy baked in. The App's own
/// doc comment is unambiguous about what that copy is: "A range here that disagrees with the
/// Server's is a bug in this table, not a second opinion."</para>
///
/// <para><b>Nothing enforced that until this test.</b> The two had already drifted — the fallback
/// bounded <c>ScoreThreshold</c> to 0.05–0.95 where the catalogue allows 0–1, and
/// <c>RetentionDays</c> to 90 days where the catalogue allows 365 — so the same camera showed one
/// range online and a narrower one offline, and the narrower one silently refused values the Server
/// would have taken.</para>
///
/// <para><b>Help text is deliberately not compared.</b> The Server's is the long version and the
/// fallback's is a shorter one written for a form with less room; they say the same thing at
/// different lengths, and pinning them together would force the wrong one on one of the two
/// surfaces. Label, kind and bounds are what a person acts on, and those must match.</para>
///
/// <para>Reading the Dart source rather than a generated artefact is the point: the file a person
/// edits is the file this checks.</para>
/// </summary>
public class CameraSettingFallbackParityTests
{
    /// <summary>One entry of the App's <c>CameraSetting</c> enum, as written in the Dart source.</summary>
    private sealed record Fallback(
        string Key, string Label, string Kind, double? Min, double? Max, string? Unit);

    private static string SourcePath()
    {
        // Walk up to the repository root rather than hard-coding a depth, so this keeps working
        // wherever the test binary is dropped.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "App")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not find the repository root from the test binary.");

        return Path.Combine(
            directory!.FullName,
            "App", "serval_app", "lib", "models", "server_camera_defaults.dart");
    }

    /// <summary>
    /// Every enum entry between <c>enum CameraSetting {</c> and its closing <c>;</c>, as
    /// <c>name('Key', label: '…', kind: …, min: …, max: …, unit: '…')</c>.
    /// </summary>
    private static IReadOnlyList<Fallback> ReadFallbacks()
    {
        string source = File.ReadAllText(SourcePath());

        int start = source.IndexOf("enum CameraSetting {", StringComparison.Ordinal);
        Assert.True(start >= 0, "server_camera_defaults.dart no longer declares enum CameraSetting.");

        // The enum's constants end at the first `;`, which closes the last one. Everything after is
        // the constructor and the fields.
        int end = source.IndexOf("\n  const CameraSetting(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of the CameraSetting constant list.");

        string body = source[start..end];

        var entries = new List<Fallback>();

        // The last constant closes with `);` rather than `),`, so both terminators have to be
        // accepted — matching only the comma silently drops the final entry, which is exactly what
        // The_parse_finds_every_overridable_field is here to catch.
        foreach (Match match in Regex.Matches(
            body, @"^  \w+\(\s*\n\s*'(?<key>[^']+)',(?<rest>.*?)^  \)[,;]?\s*$",
            RegexOptions.Multiline | RegexOptions.Singleline))
        {
            string rest = match.Groups["rest"].Value;

            entries.Add(new Fallback(
                match.Groups["key"].Value,
                // Dart adjacent-string concatenation is only used for help, never for a label.
                Single(rest, @"label:\s*'((?:[^']|\\')*)'") ?? "",
                Single(rest, @"kind:\s*SettingKind\.(\w+)") ?? "number",
                Number(rest, "min"),
                Number(rest, "max"),
                Single(rest, @"unit:\s*'([^']*)'")));
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    private static string? Single(string text, string pattern) =>
        Regex.Match(text, pattern) is { Success: true } m ? m.Groups[1].Value : null;

    private static double? Number(string text, string name) =>
        Single(text, $@"\b{name}:\s*(-?[\d.]+)") is { } raw
            ? double.Parse(raw, CultureInfo.InvariantCulture)
            : null;

    /// <summary>The catalogue's kind, spelled the way the Dart enum spells it.</summary>
    private static string DartKind(SettingKind kind) => kind switch
    {
        SettingKind.Bool => "boolean",
        SettingKind.Int => "integer",
        SettingKind.Number => "number",
        SettingKind.Text => "text",
        SettingKind.TextList => "textList",
        SettingKind.Choice => "choice",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The guard on every other test here. All three below pass vacuously if the regex stops
    /// matching — a reformat of the Dart file would turn this suite green while checking nothing —
    /// so the parse has to account for every field the four tuning bags and the two plain camera
    /// fields can override.
    /// </summary>
    [Fact]
    public void The_parse_finds_every_overridable_field()
    {
        IReadOnlyList<Fallback> fallbacks = ReadFallbacks();

        string[] expected =
        [
            // CameraDetectionTuning, less Masks — polygons have no catalogue entry to fall back to.
            "Serval:Ai:Detection:Classes", "Serval:Ai:Detection:DescribeClasses",
            "Serval:Ai:Detection:AlertClasses", "Serval:Ai:Detection:ScoreThreshold",
            "Serval:Ai:Detection:MinObjectFraction", "Serval:Ai:Detection:AlertMinConfidence",
            "Serval:Ai:Detection:Tracking:ConfirmSeconds",
            "Serval:Ai:Detection:Tracking:CoastSeconds", "Serval:Ai:Detection:MaxFps",
            "Serval:Ai:Detection:MinMovementFraction", "Serval:Ai:Detection:AbsenceSeconds",
            "Serval:Ai:Detection:NoveltySeconds",
            // CameraMotionTuning.
            "Serval:Ai:Motion:MinChangedFraction", "Serval:Ai:Motion:MaxChangedFraction",
            "Serval:Ai:Motion:PixelDelta",
            // CameraAudioTuning.
            "Serval:Ai:AudioGate:RmsThreshold", "Serval:Ai:Vad:Threshold",
            "Serval:Ai:Sound:Gate:RmsThreshold",
            // CameraSoundTuning.
            "Serval:Ai:Sound:AlertLabels", "Serval:Ai:Sound:IgnoredLabels",
            "Serval:Ai:Sound:MinConfidence", "Serval:Ai:Sound:AlertMinConfidence",
            "Serval:Ai:Sound:CooldownSeconds", "Serval:Ai:Sound:AlertCooldownSeconds",
            // A plain field on the camera.
            "Serval:Media:RetentionDays",
        ];

        Assert.Equal(
            expected.OrderBy(k => k, StringComparer.Ordinal),
            fallbacks.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_camera_fallback_names_a_setting_the_catalogue_has()
    {
        List<string> unknown = [.. ReadFallbacks()
            .Where(f => SettingsCatalog.Find(f.Key) is null)
            .Select(f => f.Key)];

        Assert.Empty(unknown);
    }

    /// <summary>
    /// The one that matters most. A camera showing a different name for a setting than the Server
    /// page shows for the same key is the drift this whole table exists to avoid.
    /// </summary>
    [Fact]
    public void Every_camera_fallback_uses_the_catalogues_label()
    {
        List<string> wrong = [.. ReadFallbacks()
            .Where(f => SettingsCatalog.Find(f.Key) is { } d && d.Label != f.Label)
            .Select(f => $"{f.Key}: app says \"{f.Label}\", catalogue says "
                + $"\"{SettingsCatalog.Find(f.Key)!.Label}\"")];

        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_camera_fallback_uses_the_catalogues_bounds_and_kind()
    {
        var mismatches = new List<string>();

        foreach (Fallback fallback in ReadFallbacks())
        {
            if (SettingsCatalog.Find(fallback.Key) is not { } descriptor)
            {
                continue;
            }

            if (DartKind(descriptor.Kind) != fallback.Kind)
            {
                mismatches.Add(
                    $"{fallback.Key}: app kind {fallback.Kind}, catalogue {DartKind(descriptor.Kind)}");
            }

            if (descriptor.Min != fallback.Min)
            {
                mismatches.Add($"{fallback.Key}: app min {fallback.Min}, catalogue {descriptor.Min}");
            }

            if (descriptor.Max != fallback.Max)
            {
                mismatches.Add($"{fallback.Key}: app max {fallback.Max}, catalogue {descriptor.Max}");
            }

            if (descriptor.Unit != fallback.Unit)
            {
                mismatches.Add(
                    $"{fallback.Key}: app unit \"{fallback.Unit}\", catalogue \"{descriptor.Unit}\"");
            }
        }

        Assert.Empty(mismatches);
    }
}
