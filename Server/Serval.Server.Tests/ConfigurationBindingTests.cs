using Microsoft.Extensions.Configuration;
using Serval.Ai;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// That configuration means what it says.
///
/// The .NET binder <em>adds to</em> a list that already has items rather than replacing it, so a
/// property initialiser plus the same key in appsettings.json silently yields both — and a
/// deployment narrowing the list gets the union of its list and the default, i.e. narrowing does
/// nothing at all. That is precisely the class of setting-with-no-effect this configuration is
/// supposed to be free of, and it is invisible unless something asserts on it.
/// </summary>
public class ConfigurationBindingTests
{
    private static IngestOptions Bind(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration =
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        return configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()?.Ingest
            ?? new IngestOptions();
    }

    [Fact]
    public void Nothing_configured_gives_the_built_in_defaults()
    {
        IngestOptions ingest = Bind([]);

        Assert.Equal(
            IngestOptions.DefaultVideoPassthroughCodecs, ingest.ResolvedVideoPassthroughCodecs);
        Assert.Equal(
            IngestOptions.DefaultAudioPassthroughCodecs, ingest.ResolvedAudioPassthroughCodecs);
    }

    [Fact]
    public void A_configured_list_replaces_the_default_rather_than_extending_it()
    {
        IngestOptions ingest = Bind(new Dictionary<string, string?>
        {
            ["Serval:Ingest:VideoPassthroughCodecs:0"] = "h264",
        });

        Assert.Equal(["h264"], ingest.ResolvedVideoPassthroughCodecs);
    }

    [Fact]
    public void Narrowing_the_audio_list_also_takes_effect()
    {
        IngestOptions ingest = Bind(new Dictionary<string, string?>
        {
            ["Serval:Ingest:AudioPassthroughCodecs:0"] = "aac",
        });

        Assert.Equal(["aac"], ingest.ResolvedAudioPassthroughCodecs);
    }

    /// <summary>
    /// The shipped file says nothing about <c>Serval</c> at all, and that is the invariant the rest
    /// of this configuration rests on.
    ///
    /// <para>Restating a default in the file buys nothing and costs three things. It reports every
    /// restated key to the settings page as <c>deployment</c> — <em>set by this deployment</em> —
    /// for a value nobody chose. It gives each default a second home free to drift from the first.
    /// And for a list it is worse than untidy: an environment variable can only <em>edit</em> the
    /// entries a lower provider already declared, so a deployer setting
    /// <c>Serval__Ingest__VideoPassthroughCodecs__0</c> to narrow the list would get the file's other
    /// three entries anyway. Env vars are how every shipped compose stack is configured, so the
    /// section has to stay absent for narrowing to be possible at all.</para>
    /// </summary>
    [Fact]
    public void The_shipped_appsettings_declares_no_settings_of_its_own()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        Assert.Empty(configuration.GetSection(ServerOptions.SectionName).GetChildren());
    }

    [Fact]
    public void Cors_defaults_to_no_origins_which_Program_reads_as_any()
    {
        ServerOptions options = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();

        Assert.Empty(options.Cors.AllowedOrigins);
    }

    [Fact]
    public void Cors_origins_bind_from_the_indexed_environment_variable_form()
    {
        // The shape a deployment actually uses: Serval__Cors__AllowedOrigins__0=... . It only
        // works because appsettings.json leaves the list undeclared — see the test above for why
        // a default in the file would make this additive instead of authoritative.
        ServerOptions options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serval:Cors:AllowedOrigins:0"] = "https://serval.example",
                ["Serval:Cors:AllowedOrigins:1"] = "http://localhost:8080",
            })
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>()!;

        Assert.Equal(
            ["https://serval.example", "http://localhost:8080"], options.Cors.AllowedOrigins);
    }

    [Fact]
    public void Vitals_defaults_come_from_the_option_class()
    {
        VitalsOptions vitals = new ConfigurationBuilder()
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>()?.Vitals ?? new VitalsOptions();

        Assert.True(vitals.Enabled);
        Assert.Equal(5.0, vitals.SampleSeconds);
        Assert.Equal(15.0, vitals.DiskScanMinutes);
        Assert.Equal(10.0, vitals.DiskWarnPercentFree);
        Assert.Equal(5.0, vitals.DiskCriticalPercentFree);
        Assert.Equal(90.0, vitals.MemoryWarnPercent);
    }

    /// <summary>
    /// The escape hatch for the one expensive thing in the vitals sweep. Zero has to survive the
    /// binder as zero rather than falling back to the property initialiser, or "turn the disk walk
    /// off" would silently do nothing.
    /// </summary>
    [Fact]
    public void Turning_the_disk_walk_off_binds_from_the_environment_variable_form()
    {
        VitalsOptions vitals = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serval:Vitals:DiskScanMinutes"] = "0",
            })
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>()!.Vitals;

        Assert.Equal(0.0, vitals.DiskScanMinutes);

        // And the rest of the section is untouched by overriding one key.
        Assert.True(vitals.Enabled);
        Assert.Equal(10.0, vitals.DiskWarnPercentFree);
    }

    [Fact]
    public void Detection_settings_bind_from_the_environment_variable_form()
    {
        // The shape a container actually uses. Classes and masks are the awkward ones: both are
        // arrays, and a mask is an array of numbers nested inside one, which is exactly why the
        // points are a flat list rather than a list of point objects.
        DetectionOptions detection = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serval:Ai:Detection:Enabled"] = "true",
                ["Serval:Ai:Detection:ModelPath"] = "/models/detect/model.onnx",
                ["Serval:Ai:Detection:ScoreThreshold"] = "0.35",
                ["Serval:Ai:Detection:Tracking:ConfirmSeconds"] = "3",
                ["Serval:Ai:Detection:AbsenceSeconds"] = "45",
                ["Serval:Ai:Detection:NoveltySeconds"] = "90",
                ["Serval:Ai:Detection:Classes:0"] = "person",
                ["Serval:Ai:Detection:Classes:1"] = "car",
                ["Serval:Ai:Detection:DescribeClasses:0"] = "person",
                ["Serval:Ai:Detection:Masks:0:Name"] = "road",
                ["Serval:Ai:Detection:Masks:0:Points:0"] = "0",
                ["Serval:Ai:Detection:Masks:0:Points:1"] = "0",
                ["Serval:Ai:Detection:Masks:0:Points:2"] = "1",
                ["Serval:Ai:Detection:Masks:0:Points:3"] = "0",
                ["Serval:Ai:Detection:Masks:0:Points:4"] = "1",
                ["Serval:Ai:Detection:Masks:0:Points:5"] = "0.3",
            })
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>()!.Ai.Detection;

        Assert.True(detection.Enabled);
        Assert.Equal(0.35f, detection.ScoreThreshold);
        Assert.Equal(3, detection.Tracking.ConfirmSeconds);
        Assert.Equal(45, detection.AbsenceSeconds);
        Assert.Equal(90, detection.NoveltySeconds);
        // Authoritative, not additive: the arrays default to empty precisely so a narrowed list
        // is a narrowed list rather than the defaults with two more entries stuck on the end.
        Assert.Equal(["person", "car"], detection.Classes);
        Assert.Equal(["person", "car"], detection.EffectiveClasses);
        Assert.Equal(["person"], detection.DescribeClasses);

        DetectionMask mask = Assert.Single(detection.Masks);
        Assert.Equal("road", mask.Name);
        Assert.Equal([0, 0, 1, 0, 1, 0.3], mask.Points);
        Assert.True(mask.IsUsable);
    }

    [Fact]
    public void Detection_is_off_and_falls_back_to_the_motion_gate_by_default()
    {
        // An existing deployment must not change behaviour because it upgraded.
        DetectionOptions detection = new ConfigurationBuilder()
            .Build()
            .GetSection(ServerOptions.SectionName).Get<ServerOptions>()?.Ai.Detection
            ?? new DetectionOptions();

        Assert.False(detection.Enabled);

        // Unset means the built-in list, which is what keeps the arrays empty and binding
        // authoritative without losing a sensible out-of-the-box set.
        Assert.Empty(detection.Classes);
        Assert.Equal(DetectionOptions.DefaultClasses, detection.EffectiveClasses);
        Assert.Equal(DetectionOptions.DefaultDescribeClasses, detection.EffectiveDescribeClasses);
    }
}
