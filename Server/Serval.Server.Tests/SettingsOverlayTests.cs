using Microsoft.Extensions.Configuration;
using Serval.Ai;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// That the stored settings overlay wins, and that resetting one gives back the deployment's value.
///
/// The precedence is the whole design: appsettings seeds a fresh install, the environment carries
/// the deployment's choices, and the overlay carries what someone has since changed in the App. Get
/// the order wrong in either direction and the failure is silent — a change that appears to save
/// and does nothing, or a compose file that stops meaning anything.
/// </summary>
public class SettingsOverlayTests
{
    /// <summary>
    /// The same three-layer chain <c>Program.cs</c> builds, with the overlay last.
    /// <paramref name="environment"/> stands in for the compose file's <c>Serval__Foo__Bar</c>.
    /// </summary>
    private static IConfigurationRoot Build(
        Dictionary<string, string?> file,
        Dictionary<string, string?> environment,
        Dictionary<string, string?> overlay) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(file)
            .AddInMemoryCollection(environment)
            .AddInMemoryCollection(overlay)
            .Build();

    private static ServerOptions Bind(IConfigurationRoot configuration) =>
        configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();

    [Fact]
    public void The_overlay_beats_the_environment_which_beats_the_file()
    {
        ServerOptions options = Bind(Build(
            file: new() { ["Serval:Media:RetentionDays"] = "7" },
            environment: new() { ["Serval:Media:RetentionDays"] = "30" },
            overlay: new() { ["Serval:Media:RetentionDays"] = "3" }));

        Assert.Equal(3, options.Media.RetentionDays);
    }

    [Fact]
    public void Removing_an_overlay_key_falls_back_to_the_deployment()
    {
        // What Reset does: the key is deleted from the stored document, not blanked. An empty
        // string here would bind as an empty value and pin the setting rather than releasing it.
        ServerOptions options = Bind(Build(
            file: new() { ["Serval:Media:RetentionDays"] = "7" },
            environment: new() { ["Serval:Media:RetentionDays"] = "30" },
            overlay: []));

        Assert.Equal(30, options.Media.RetentionDays);
    }

    [Fact]
    public void A_setting_the_deployment_never_mentions_still_reaches_the_options()
    {
        ServerOptions options = Bind(Build(
            file: [],
            environment: [],
            overlay: new() { ["Serval:Ai:Detection:ScoreThreshold"] = "0.4" }));

        Assert.Equal(0.4f, options.Ai.Detection.ScoreThreshold);
    }

    /// <summary>
    /// The regression this whole layout exists to prevent, arriving from the other direction.
    ///
    /// The binder appends to a list that already has entries, and <c>ConfigurationRoot</c> unions
    /// child keys across providers — so a three-entry overlay over a ten-entry appsettings list
    /// yields ten, and narrowing the list does nothing. That is why every list the settings page
    /// can write is null-by-default with a <c>Default…</c>/<c>Effective…</c> pair and is absent
    /// from appsettings.json.
    /// </summary>
    [Fact]
    public void Narrowing_a_list_from_the_overlay_actually_narrows_it()
    {
        SoundOptions sound = Bind(Build(
            file: [],
            environment: [],
            overlay: new()
            {
                ["Serval:Ai:Sound:AlertLabels:0"] = "Glass",
                ["Serval:Ai:Sound:AlertLabels:1"] = "Siren",
            })).Ai.Sound;

        Assert.Equal(["Glass", "Siren"], sound.EffectiveAlertLabels);
    }

    [Fact]
    public void An_unset_alert_label_list_falls_back_to_the_built_in_set()
    {
        SoundOptions sound = Bind(Build(file: [], environment: [], overlay: [])).Ai.Sound;

        Assert.Equal(SoundOptions.DefaultAlertLabels, sound.EffectiveAlertLabels);
    }

}
