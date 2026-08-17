using System.Globalization;
using System.Text.Json;
using Serval.Server.Auth;

namespace Serval.Server.Configuration;

/// <summary>
/// Reading and changing the settings a user is allowed to change.
///
/// <para>The camera registry has its own endpoints because a camera is a thing that exists; this is
/// the other half — the settings that are not about any one camera. Between them they are meant to
/// remove the reasons to edit a compose file and restart, which is why the write path ends in a
/// reload rather than an instruction to redeploy.</para>
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("", async (
            SettingsReader reader,
            ServerSettingsStore store,
            CancellationToken ct) => Results.Ok(reader.Read(await store.LoadAsync(ct))))
            .WithSummary("Every setting that can be changed here, with its value and where it came from.")
            .WithDescription(
                "Each setting carries the value in force, the value it would revert to, and a "
                + "'source' of 'default' (built in), 'deployment' (an appsettings file or "
                + "environment variable) or 'user' (changed here — only these can be reset).\n\n"
                + "Settings marked restartRequired are read while the server is starting and cannot "
                + "be swapped underneath what has already been built. Changing one is stored and "
                + "reported, but does not take effect until the server restarts; the top-level "
                + "restartRequired flag says whether any such change is currently pending.\n\n"
                + "Settings not listed here are deliberately environment-only: where the database "
                + "is, what signs a token, which render node encodes, and who may call the API. "
                + "Changing those from a UI could lock an operator out of that UI.")
            .Produces<SettingsResponse>()
            .RequireAuthorization();

        group.MapPut("", async (
            Dictionary<string, JsonElement> changes,
            SettingsConfigurationProvider provider,
            SettingsReader reader,
            HttpContext http,
            CancellationToken ct) =>
        {
            Dictionary<string, string?> writes = Translate(changes, provider.OverriddenKeys);

            // Writes and reloads together, so a setting cannot be stored without being applied.
            //
            // TokenService.GetUserId rather than Identity.Name: the JWT bearer handler runs with
            // MapInboundClaims off, so "sub" arrives as "sub" and Identity.Name — which looks for
            // the XML-namespace name claim — is always null. That would have made "who changed
            // this" a field that is always empty and looks like nobody ever changes anything.
            ServerSettingsDocument document = await provider.SaveAsync(
                writes, TokenService.GetUserId(http.User), ct);

            return Results.Ok(reader.Read(document));
        })
            .WithSummary("Change settings, taking effect immediately where they can.")
            .WithDescription(
                "Send only the settings you are changing, keyed by their configuration path:\n\n"
                + "  { \"Serval:Media:RetentionDays\": 14,\n"
                + "    \"Serval:Ai:Sound:AlertLabels\": [\"Glass\", \"Siren\"],\n"
                + "    \"Serval:Ai:Detection:ScoreThreshold\": null }\n\n"
                + "A null value RESETS that setting — it removes the stored override so the value "
                + "falls back to whatever the deployment says. That is the only way to un-set "
                + "something; sending an empty string would pin it to an empty string.\n\n"
                + "Every key is checked against the catalogue returned by GET, so an unknown path "
                + "or an out-of-range value is a 400 naming the setting rather than a silently "
                + "ignored write. The response is the same shape GET returns, already reflecting "
                + "the change.")
            .Produces<SettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization("Admin");
    }

    /// <summary>
    /// Turns the App's typed request into the configuration keys and string values the overlay
    /// stores, rejecting anything the catalog does not allow.
    /// </summary>
    /// <param name="stored">
    /// What the overlay already holds, needed to shorten a list. A list is stored as indexed keys,
    /// so replacing a five-entry list with a three-entry one has to <em>delete</em> indices 3 and 4
    /// — writing only the first three would leave the tail behind and the list would never get
    /// shorter. This is the same append-not-replace hazard the option classes carry warnings about,
    /// arriving from the other direction.
    /// </param>
    internal static Dictionary<string, string?> Translate(
        IReadOnlyDictionary<string, JsonElement> changes,
        IReadOnlySet<string> stored)
    {
        Dictionary<string, string?> writes = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, JsonElement value) in changes)
        {
            SettingDescriptor descriptor = SettingsCatalog.Find(key)
                ?? throw new SettingsValidationException(
                    $"'{key}' is not a setting that can be changed here.");

            if (value.ValueKind == JsonValueKind.Null)
            {
                Reset(descriptor, stored, writes);
                continue;
            }

            if (descriptor.Kind == SettingKind.TextList)
            {
                WriteList(descriptor, value, stored, writes);
                continue;
            }

            string text = Scalar(descriptor, value);
            if (SettingsCatalog.Validate(descriptor, text) is { } error)
            {
                throw new SettingsValidationException(error);
            }

            writes[descriptor.Key] = text;
        }

        return writes;
    }

    /// <summary>Removes an override. For a list that means every index it currently occupies.</summary>
    private static void Reset(
        SettingDescriptor descriptor,
        IReadOnlySet<string> stored,
        Dictionary<string, string?> writes)
    {
        if (descriptor.Kind != SettingKind.TextList)
        {
            writes[descriptor.Key] = null;
            return;
        }

        foreach (string key in Indices(descriptor, stored))
        {
            writes[key] = null;
        }
    }

    private static void WriteList(
        SettingDescriptor descriptor,
        JsonElement value,
        IReadOnlySet<string> stored,
        Dictionary<string, string?> writes)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new SettingsValidationException($"{descriptor.Label} must be a list.");
        }

        // Clear what is there first, then write the new entries over the top. Clearing only the
        // indices past the new length would be enough, but doing it wholesale means the result
        // cannot depend on what happened to be stored before.
        foreach (string key in Indices(descriptor, stored))
        {
            writes[key] = null;
        }

        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (SettingsCatalog.ValidateListEntry(descriptor, text) is { } error)
            {
                throw new SettingsValidationException(error);
            }

            writes[$"{descriptor.Key}:{index}"] = text;
            index++;
        }
    }

    private static IEnumerable<string> Indices(SettingDescriptor descriptor, IReadOnlySet<string> stored) =>
        stored.Where(k => k.StartsWith(descriptor.Key + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A single value as configuration stores it — a string, always, because that is what
    /// configuration is. Invariant culture on the way in so a number written here parses back to
    /// the same number wherever the server runs.
    /// </summary>
    private static string Scalar(SettingDescriptor descriptor, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.String => value.GetString() ?? "",
        _ => throw new SettingsValidationException(
            $"{descriptor.Label} cannot be set to {value.ValueKind.ToString().ToLowerInvariant()}."),
    };
}

/// <summary>A rejected settings write.</summary>
public sealed class SettingsValidationException(string message) : ValidationException(message);
