using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serval.Ai;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// What the settings API accepts, and what it turns a request into.
///
/// The catalogue is the allowlist: a key not in it cannot be written, which is the only thing
/// keeping the environment-only settings — where the database is, what signs a token, which render
/// node encodes — out of reach of anyone who can reach the API. It is also the source of every
/// bound, so a value the Server would reject at startup should be rejected here instead, while
/// there is still a person to tell.
/// </summary>
public class SettingsCatalogTests
{

    /// <summary>
    /// A reader over a stated deployment and a stated overlay. <paramref name="file"/> stands in for
    /// whatever the deployment sets, by file or by environment variable; empty is a fresh install,
    /// which is the state the source classification is easiest to get wrong in.
    ///
    /// <para>Stated rather than read from the shipped <c>appsettings.json</c>, because what is under
    /// test is what a deployment key <em>means</em> — a test that says "and 7 is what the file
    /// happens to contain" breaks when the file changes and proves nothing when it does not.</para>
    /// </summary>
    private static SettingsReader Reader(
        Dictionary<string, string?>? file = null,
        Dictionary<string, string>? overlay = null,
        IEnumerable<ISettingChoiceProbe>? probes = null)
    {
        // A source over a fixed overlay: what is under test is what an overlay *means*, not how it
        // is stored, and reaching for Mongo to answer that would make this depend on a database
        // being up.
        var source = new SettingsConfigurationSource(
            overlay ?? new Dictionary<string, string>());

        IConfigurationRoot deployment = new ConfigurationBuilder()
            .AddInMemoryCollection(file ?? [])
            .Build();

        IConfigurationRoot effective = new ConfigurationBuilder()
            .AddInMemoryCollection(file ?? [])
            .Add(source)
            .Build();

        return new SettingsReader(
            effective, new DeploymentConfiguration(deployment), source.Provider, probes);
    }

    private static Dictionary<string, JsonElement> Request(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static Dictionary<string, string?> Translate(
        string json, params string[] alreadyStored) =>
        SettingsEndpoints.Translate(
            Request(json), alreadyStored.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- the allowlist

    [Fact]
    public void An_unknown_key_is_refused()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Nonsense:Widget": 1 }"""));

        Assert.Contains("not a setting that can be changed here", error.Message);
    }

    /// <summary>
    /// The point of the allowlist, stated as a test rather than as a comment. Each of these is a
    /// real configuration path that binds to a real property — none is a typo — and each would be
    /// a way to take the Server away from whoever is holding it.
    /// </summary>
    [Theory]
    [InlineData("Serval:Mongo:ConnectionString")]
    [InlineData("Serval:Media:Root")]
    [InlineData("Serval:Auth:SigningKey")]
    [InlineData("Serval:Auth:BootstrapAdminPassword")]
    [InlineData("Serval:Ingest:FfmpegPath")]
    [InlineData("Serval:Ingest:HwAccelDevice")]
    [InlineData("Serval:ApiKey")]
    [InlineData("Serval:Cors:AllowedOrigins")]
    [InlineData("Serval:OpenApi:Enabled")]
    public void The_environment_only_settings_cannot_be_written(string key)
    {
        Assert.False(SettingsCatalog.IsWritable(key));
        Assert.Throws<SettingsValidationException>(
            () => Translate($$"""{ "{{key}}": "anything" }"""));
    }

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void A_value_below_its_floor_is_refused_by_name()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Media:RetentionDays": 0 }"""));

        Assert.Contains("Keep recordings for", error.Message);
        Assert.Contains("below 1", error.Message);
    }

    [Fact]
    public void A_value_above_its_ceiling_is_refused()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Ai:Detection:ScoreThreshold": 1.5 }"""));

        Assert.Contains("above 1", error.Message);
    }

    [Fact]
    public void A_value_inside_its_range_is_accepted()
    {
        Dictionary<string, string?> writes =
            Translate("""{ "Serval:Media:RetentionDays": 14 }""");

        Assert.Equal("14", writes["Serval:Media:RetentionDays"]);
    }

    [Fact]
    public void A_choice_outside_its_options_is_refused()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Ai:Asr:Language": "klingon" }"""));

        Assert.Contains("must be one of", error.Message);
    }

    [Fact]
    public void Text_where_a_number_belongs_is_refused()
    {
        Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Media:RetentionDays": "a fortnight" }"""));
    }

    /// <summary>
    /// A number is stored round-trip and culture-invariantly, so a value written on a host with a
    /// comma decimal separator parses back to the same number everywhere.
    /// </summary>
    [Fact]
    public void A_fraction_survives_being_written_as_a_string()
    {
        Dictionary<string, string?> writes =
            Translate("""{ "Serval:Ai:Detection:ScoreThreshold": 0.35 }""");

        Assert.Equal("0.35", writes["Serval:Ai:Detection:ScoreThreshold"]);
    }

    [Fact]
    public void A_switch_is_stored_as_a_bindable_bool()
    {
        Dictionary<string, string?> writes =
            Translate("""{ "Serval:Vitals:Enabled": false }""");

        Assert.Equal("false", writes["Serval:Vitals:Enabled"]);
    }

    // ---------------------------------------------------------------- resetting

    [Fact]
    public void Null_removes_the_override()
    {
        Dictionary<string, string?> writes =
            Translate("""{ "Serval:Media:RetentionDays": null }""");

        Assert.Null(writes["Serval:Media:RetentionDays"]);
    }

    /// <summary>
    /// An empty string is a value, not an absence. Accepting one would pin a text setting to being
    /// empty while looking exactly like a reset, so it is refused and the message says what to do.
    /// </summary>
    [Fact]
    public void An_empty_string_is_refused_rather_than_treated_as_a_reset()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Ai:Vision:Prompt": "  " }"""));

        Assert.Contains("Reset it instead", error.Message);
    }

    // ---------------------------------------------------------------- lists

    [Fact]
    public void A_list_is_stored_as_indexed_keys()
    {
        Dictionary<string, string?> writes = Translate(
            """{ "Serval:Ai:Sound:AlertLabels": ["Glass", "Gunshot, gunfire"] }""");

        Assert.Equal("Glass", writes["Serval:Ai:Sound:AlertLabels:0"]);
        Assert.Equal("Gunshot, gunfire", writes["Serval:Ai:Sound:AlertLabels:1"]);
    }

    /// <summary>
    /// The other half of the append trap. A list is stored as indexed keys, so replacing a
    /// five-entry list with a two-entry one has to <em>delete</em> indices 2 to 4 — writing only
    /// the first two would leave the tail behind and the list could never get shorter.
    /// </summary>
    [Fact]
    public void Shortening_a_list_deletes_the_indices_it_no_longer_uses()
    {
        Dictionary<string, string?> writes = Translate(
            """{ "Serval:Ai:Sound:AlertLabels": ["Glass", "Siren"] }""",
            "Serval:Ai:Sound:AlertLabels:0",
            "Serval:Ai:Sound:AlertLabels:1",
            "Serval:Ai:Sound:AlertLabels:2",
            "Serval:Ai:Sound:AlertLabels:3",
            "Serval:Ai:Sound:AlertLabels:4");

        Assert.Equal("Glass", writes["Serval:Ai:Sound:AlertLabels:0"]);
        Assert.Equal("Siren", writes["Serval:Ai:Sound:AlertLabels:1"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:2"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:3"]);
        Assert.Null(writes["Serval:Ai:Sound:AlertLabels:4"]);
    }

    [Fact]
    public void Resetting_a_list_removes_every_index_it_occupied()
    {
        Dictionary<string, string?> writes = Translate(
            """{ "Serval:Ai:Detection:Classes": null }""",
            "Serval:Ai:Detection:Classes:0",
            "Serval:Ai:Detection:Classes:1");

        Assert.Null(writes["Serval:Ai:Detection:Classes:0"]);
        Assert.Null(writes["Serval:Ai:Detection:Classes:1"]);
    }

    [Fact]
    public void A_list_entry_outside_its_allowed_values_is_refused()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate(
                """{ "Serval:Ingest:VideoPassthroughCodecs": ["h264", "mjpeg"] }"""));

        Assert.Contains("mjpeg", error.Message);
    }

    [Fact]
    public void A_blank_list_entry_is_refused()
    {
        Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Ai:Detection:Classes": ["person", " "] }"""));
    }

    [Fact]
    public void A_scalar_where_a_list_belongs_is_refused()
    {
        SettingsValidationException error = Assert.Throws<SettingsValidationException>(
            () => Translate("""{ "Serval:Ai:Detection:Classes": "person" }"""));

        Assert.Contains("must be a list", error.Message);
    }

    // ---------------------------------------------------------------- keys from outside

    /// <summary>
    /// <see cref="SettingsCatalog.ValidateStored"/> is for a caller holding a key it did not
    /// construct — a restored backup, or a hand-edited settings document. The form-driven path
    /// above always has a descriptor in hand; this one has to find its own, and an indexed list
    /// child has no descriptor of its own to find.
    /// </summary>
    [Fact]
    public void A_stored_scalar_is_checked_against_its_descriptor()
    {
        Assert.Null(SettingsCatalog.ValidateStored("Serval:Media:RetentionDays", "14"));
        Assert.Contains("cannot be above",
            SettingsCatalog.ValidateStored("Serval:Media:RetentionDays", "99999")!);
    }

    /// <summary>
    /// The trap this method exists for. A list entry checked against its parent's descriptor lands
    /// in <see cref="SettingsCatalog.Validate"/>'s TextList arm, which only looks for blanks — so
    /// an entry outside the allowed values would restore cleanly while the same value sent through
    /// the settings form is refused. Two write paths, two answers.
    /// </summary>
    [Fact]
    public void A_stored_list_entry_is_checked_against_the_values_its_list_allows()
    {
        Assert.Null(SettingsCatalog.ValidateStored("Serval:Ingest:VideoPassthroughCodecs:0", "hevc"));

        string? error = SettingsCatalog.ValidateStored("Serval:Ingest:VideoPassthroughCodecs:0", "mpeg2");
        Assert.NotNull(error);
        Assert.Contains("mpeg2", error);
    }

    [Fact]
    public void A_blank_stored_list_entry_is_refused()
    {
        Assert.NotNull(SettingsCatalog.ValidateStored("Serval:Ai:Detection:Classes:0", " "));
    }

    [Fact]
    public void An_environment_only_key_is_not_a_key_this_server_will_take()
    {
        string? error = SettingsCatalog.ValidateStored("Serval:Auth:SigningKey", "hunter2");

        Assert.NotNull(error);
        Assert.Contains("environment-only", error);
    }

    /// <summary>
    /// A list is stored one entry per key, so the bare parent is a shape the overlay never writes.
    /// Reaching it means a malformed file, and the message says so rather than reporting a value
    /// out of range.
    /// </summary>
    [Fact]
    public void A_bare_list_key_holds_no_value()
    {
        string? error = SettingsCatalog.ValidateStored("Serval:Ai:Detection:Classes", "person");

        Assert.NotNull(error);
        Assert.Contains("is a list", error);
    }

    [Theory]
    [InlineData("Serval:Ai:Detection:Classes:notanumber")]
    [InlineData("Serval:Ai:Detection:Classes:-1")]
    [InlineData("Serval:Media:RetentionDays:0")]
    [InlineData("Nonsense:Key")]
    public void A_key_that_is_not_a_setting_here_is_refused(string key)
    {
        Assert.NotNull(SettingsCatalog.ValidateStored(key, "x"));
    }

    // ---------------------------------------------------------------- the catalogue itself

    /// <summary>
    /// Every entry has to explain itself. The App shows [SettingDescriptor.Help] under the field —
    /// it is the only account of what a setting does that most people will ever read — so an entry
    /// added without one would ship a live knob with no explanation attached.
    /// </summary>
    [Fact]
    public void Every_setting_carries_a_label_and_an_explanation()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(descriptor.Label),
                $"{descriptor.Key} has no label.");

            // Long enough to be a sentence rather than a restatement of the label.
            Assert.True(
                descriptor.Help.Length > 40,
                $"{descriptor.Key} needs an explanation, not just a name.");
        }
    }

    [Fact]
    public void Every_setting_belongs_to_a_group_the_app_draws()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            Assert.Contains(descriptor.Group, SettingsCatalog.Groups);
        }
    }

    /// <summary>
    /// Two settings in one group draw two cards in one pane, and a person reading that pane has
    /// only the label to tell them apart — the key is not on screen. The catalogue carried exactly
    /// this for a long time: <c>Serval:Ai:AudioGate:RmsThreshold</c> and
    /// <c>Serval:Ai:Sound:Gate:RmsThreshold</c> were both <em>Counts as silence below</em>, and the
    /// camera editor, which drew both in one section, had to invent labels of its own to tell them
    /// apart — labels that then matched nothing the search index knew about.
    ///
    /// <para>Across groups a repeat is fine and often right: the two gates are the same knob on two
    /// pipelines, and the group name is what separates them.</para>
    /// </summary>
    [Fact]
    public void No_two_settings_in_a_group_share_a_label()
    {
        List<string> collisions = [.. SettingsCatalog.All
            .GroupBy(d => (d.Group, d.Label))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Group}: \"{g.Key.Label}\" is used by "
                + string.Join(", ", g.Select(d => d.Key)))];

        Assert.Empty(collisions);
    }

    /// <summary>
    /// A group whose every setting is advanced is a group the everyday reader opens to find nothing
    /// they can act on — which usually means those settings belong inside a neighbouring group
    /// rather than in one of their own.
    ///
    /// <para><see cref="SettingsCatalog.GroupTracking"/> is the deliberate exception: all six are the
    /// tracker's filter internals, and that is precisely why they are not sitting beside the class
    /// lists in <see cref="SettingsCatalog.GroupObjects"/>. Adding a second name to this list should
    /// take an argument, not a keystroke.</para>
    /// </summary>
    [Fact]
    public void Every_group_has_something_an_everyday_reader_can_act_on()
    {
        string[] allowedToBeEntirelyAdvanced = [SettingsCatalog.GroupTracking];

        List<string> empty = [.. SettingsCatalog.Groups
            .Where(g => !allowedToBeEntirelyAdvanced.Contains(g))
            .Where(g => SettingsCatalog.All.Where(d => d.Group == g).All(d => d.Advanced))];

        Assert.Empty(empty);
    }

    [Fact]
    public void The_tracking_group_is_advanced_all_the_way_down()
    {
        Assert.All(
            SettingsCatalog.All.Where(d => d.Group == SettingsCatalog.GroupTracking),
            d => Assert.True(d.Advanced, $"{d.Key} is in Object tracking but not marked advanced."));
    }

    [Fact]
    public void No_setting_is_listed_twice()
    {
        List<string> duplicates = [.. SettingsCatalog.All
            .GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// An empty list means "nothing has set this", not "someone chose an empty list". Reported as
    /// <c>deployment</c> it reads as a decision, and offers a reset for a setting that is quietly
    /// running on its built-in set — which is what every untouched list did until this was caught
    /// end to end.
    /// </summary>
    [Fact]
    public void An_untouched_list_is_reported_as_built_in_rather_than_as_a_choice()
    {
        SettingsResponse response = Reader().Read(new ServerSettingsDocument());

        SettingView labels = response.Settings.Single(s => s.Key == "Serval:Ai:Sound:AlertLabels");

        Assert.Equal(SettingSource.Default, labels.Source);
    }

    [Fact]
    public void A_setting_the_file_names_is_reported_as_the_deployments()
    {
        SettingsReader reader = Reader(
            file: new Dictionary<string, string?> { ["Serval:Media:RetentionDays"] = "14" });

        SettingView retention = reader.Read(new ServerSettingsDocument())
            .Settings.Single(s => s.Key == "Serval:Media:RetentionDays");

        Assert.Equal(SettingSource.Deployment, retention.Source);
        Assert.Equal(14L, retention.Value);
    }

    /// <summary>
    /// What the fallback exists for. A default written as a property initialiser is not
    /// configuration and never becomes configuration — binding constructs the object and then
    /// overwrites only the properties a source has a key for — so asking configuration alone
    /// produces a null. Reported as one, the App draws an empty box for a setting the Server is
    /// using perfectly well, and someone typing into that box is not correcting a blank; they are
    /// overriding a working value they have never been shown.
    /// </summary>
    [Fact]
    public void A_setting_nothing_configures_reports_its_built_in_value()
    {
        SettingsResponse response = Reader().Read(new ServerSettingsDocument());

        SettingView fps = response.Settings.Single(s => s.Key == "Serval:Ai:Detection:MaxFps");

        Assert.Equal(SettingSource.Default, fps.Source);
        Assert.Equal(0d, fps.Value);

        // And Reset offers that same value rather than offering to blank the field. Zero is a real
        // value here — "no per-camera ceiling" — not an absent one.
        Assert.Equal(0d, fps.Default);
    }

    /// <summary>
    /// The one shape the fallback must not touch. An empty writable list means "using the built-in
    /// set" and is drawn from <see cref="SettingDescriptor.Fallback"/> as placeholder text; filling
    /// the value in would make it indistinguishable from someone having chosen exactly those
    /// entries, and saving the form would pin it.
    /// </summary>
    [Fact]
    public void An_untouched_list_stays_empty_rather_than_being_filled_in()
    {
        SettingsResponse response = Reader().Read(new ServerSettingsDocument());

        SettingView classes = response.Settings.Single(s => s.Key == "Serval:Ai:Detection:Classes");

        Assert.Equal(SettingSource.Default, classes.Source);
        Assert.Empty((List<string>)classes.Value!);
        Assert.Equal(DetectionOptions.DefaultClasses, classes.Fallback);
    }

    /// <summary>
    /// Every catalogued scalar has to resolve to a real property on the option classes. A mistyped
    /// key is otherwise invisible: it validates, it stores, it reports — and it configures nothing,
    /// because the binder never looks at a path no property answers to.
    /// </summary>
    [Fact]
    public void Every_catalogued_setting_has_a_built_in_default()
    {
        List<string> missing = [.. SettingsCatalog.All
            .Where(d => d.Kind != SettingKind.TextList && BuiltInDefaults.For(d.Key) is null)
            .Select(d => d.Key)];

        Assert.Empty(missing);
    }

    /// <summary>
    /// And each of those defaults has to sit inside the bounds the catalogue advertises, or the App
    /// opens a field showing a value it will not let you save — and offers a Reset that produces a
    /// rejected state.
    /// </summary>
    [Fact]
    public void Every_built_in_default_sits_inside_its_own_bounds()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            if (BuiltInDefaults.For(descriptor.Key) is not { } value)
            {
                continue;
            }

            Assert.Null(SettingsCatalog.Validate(descriptor, value));
        }
    }

    /// <summary>
    /// And a list someone <em>has</em> set reports itself as theirs, with a reset offered. The
    /// pair matters: the case above is what makes this one mean anything.
    /// </summary>
    [Fact]
    public void A_list_written_here_is_reported_as_the_users()
    {
        SettingsReader reader = Reader(overlay: new Dictionary<string, string>
        {
            ["Serval:Ai:Sound:AlertLabels:0"] = "Siren",
        });

        SettingView labels = reader.Read(new ServerSettingsDocument())
            .Settings.Single(s => s.Key == "Serval:Ai:Sound:AlertLabels");

        Assert.Equal(SettingSource.User, labels.Source);
        Assert.Equal(new List<string> { "Siren" }, labels.Value);
    }

    /// <summary>
    /// A list that resolves to a built-in set while empty has to say so. These are the ones with a
    /// <c>Default…</c>/<c>Effective…</c> pair on the option class, and without the fallback declared
    /// here the App draws them as an empty box — the same silence about a setting quietly doing
    /// something that a scalar with no built-in value to fall back on would have.
    /// </summary>
    [Theory]
    [InlineData("Serval:Ingest:VideoPassthroughCodecs")]
    [InlineData("Serval:Ingest:AudioPassthroughCodecs")]
    [InlineData("Serval:Ai:Detection:Classes")]
    [InlineData("Serval:Ai:Detection:DescribeClasses")]
    [InlineData("Serval:Ai:Detection:AlertClasses")]
    [InlineData("Serval:Ai:Sound:AlertLabels")]
    public void A_list_with_a_built_in_set_names_what_it_falls_back_to(string key)
    {
        SettingDescriptor descriptor = SettingsCatalog.Find(key)!;

        Assert.NotNull(descriptor.Fallback);
        Assert.NotEmpty(descriptor.Fallback!);
    }

    [Fact]
    public void Every_choice_setting_names_the_values_it_accepts()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            if (descriptor.Kind == SettingKind.Choice)
            {
                Assert.NotNull(descriptor.Choices);
                Assert.NotEmpty(descriptor.Choices!);
            }
        }
    }

    /// <summary>
    /// A bounded number whose default sits outside its own bounds would be a field the App opens
    /// showing a value it will not let you save — and a Reset that produces a rejected state.
    /// </summary>
    [Fact]
    public void Every_bounded_setting_has_a_range_that_makes_sense()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            if (descriptor is { Min: { } min, Max: { } max })
            {
                Assert.True(min < max, $"{descriptor.Key} has a range that excludes everything.");
            }
        }
    }

    // ------------------------------------------------------------ what applies to what

    /// <summary>
    /// A dependency naming a value the controlling setting does not offer is a rule that can never
    /// be satisfied, so the App would grey the field out permanently — a setting nobody can reach,
    /// reporting a reason that is not the real one. Nothing else catches it: both halves are valid
    /// strings, and the App has no way to tell a rule that never matches from one that happens not
    /// to right now.
    /// </summary>
    [Fact]
    public void Every_dependency_names_a_real_setting_and_real_values_of_it()
    {
        foreach (SettingDescriptor descriptor in SettingsCatalog.All)
        {
            if (descriptor.AppliesWhen is not { } rule)
            {
                continue;
            }

            SettingDescriptor? controlling = SettingsCatalog.Find(rule.Key);

            Assert.True(
                controlling is not null,
                $"{descriptor.Key} depends on '{rule.Key}', which is not in the catalogue.");
            Assert.NotEmpty(rule.Values);
            Assert.False(
                string.IsNullOrWhiteSpace(rule.Reason),
                $"{descriptor.Key} dims with no explanation.");

            foreach (string value in rule.Values)
            {
                Assert.Contains(value, controlling!.Choices ?? []);
            }
        }
    }

    [Fact]
    public void The_detection_tuning_settings_dim_on_a_coral_and_not_on_a_cpu()
    {
        // The three the Edge TPU path reads none of: it runs one inference at a time, on a model whose
        // input shape is compiled in, through a lane pinned to a single host thread.
        string[] onnxOnly =
        [
            "Serval:Ai:Detection:MaxConcurrency",
            "Serval:Ai:Detection:InputPixels",
            "Serval:Ai:Detection:NumThreads",
        ];

        foreach (string key in onnxOnly)
        {
            SettingDependency rule = SettingsCatalog.Find(key)!.AppliesWhen!;

            Assert.Equal("Serval:Ai:Detection:Device", rule.Key);
            Assert.Contains("onnx-cpu", rule.Values);
            Assert.DoesNotContain("tflite-edgetpu", rule.Values);
        }

        // The model file is not among them: one path serves both runtimes now, so it always applies.
        Assert.Null(SettingsCatalog.Find("Serval:Ai:Detection:ModelPath")!.AppliesWhen);
    }

    // ------------------------------------------------------- what this host cannot run

    [Fact]
    public void A_setting_nothing_probes_reports_no_unavailable_choices()
    {
        // Null rather than empty, so "nobody asked" stays distinguishable from "everything works".
        SettingView view = Reader().Read(new ServerSettingsDocument())
            .Settings.First(s => s.Key == "Serval:Ai:Asr:Language");

        Assert.Null(view.UnavailableChoices);
    }

    [Fact]
    public void An_unavailable_device_is_still_offered_as_a_choice_and_carries_its_reason()
    {
        // Both halves matter. Dropping it from Choices would leave the App unable to render a value
        // that is nonetheless in force — which is the case worth surfacing, since it means this
        // deployment's device is being ignored.
        var reader = Reader(
            file: new Dictionary<string, string?>
            {
                ["Serval:Ai:Detection:Device"] = "tflite-edgetpu",
            },
            probes: [new StubProbe(
                "Serval:Ai:Detection:Device",
                new Dictionary<string, string> { ["tflite-edgetpu"] = "No Edge TPU found." })]);

        SettingView view = reader.Read(new ServerSettingsDocument())
            .Settings.First(s => s.Key == "Serval:Ai:Detection:Device");

        Assert.Equal("tflite-edgetpu", view.Value);
        Assert.Contains("tflite-edgetpu", view.Choices!);
        Assert.Equal("No Edge TPU found.", view.UnavailableChoices!["tflite-edgetpu"]);
    }

    [Fact]
    public void An_unavailable_device_is_still_a_valid_write()
    {
        // Availability is advisory, and deliberately does not reach Validate: a config backup taken on
        // the Coral host has to restore onto one without a Coral, or the backup is only good for the
        // machine it came from.
        Assert.Null(SettingsCatalog.ValidateStored(
            "Serval:Ai:Detection:Device", "tflite-edgetpu"));
    }

    private sealed class StubProbe(string key, IReadOnlyDictionary<string, string> unavailable)
        : ISettingChoiceProbe
    {
        public string Key => key;

        public IReadOnlyDictionary<string, string> Unavailable() => unavailable;
    }
}
