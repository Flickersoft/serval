namespace Serval.Server.Vitals;

/// <summary>
/// What the server can say about itself.
///
/// Deliberately not a fourth WebSocket. A processor percentage is the difference between two
/// samples over the time between them, so a server-side sampler has to exist whatever the
/// transport is — and once it does, this route is a memory read, while a socket would add a
/// session to manage, a ticket to mint, a broadcaster to bound and a fourth reconnect path in the
/// App. Compare <see cref="Live.AudioLevelsEndpoint"/>, which is a socket for a real reason: ten
/// samples a second, measured only while somebody is subscribed. This is one sample every few
/// seconds that is being measured anyway.
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/system").WithTags("System");

        // Authenticated rather than Admin, matching the read/write split on the camera registry.
        // Nothing here is more sensitive than the registry a Viewer already reads, and the wall's
        // disk warning has to reach Viewers — they cannot fix a full volume, but they are who will
        // notice and say so.
        group.MapGet("/stats", (SystemStatsCollector collector) => Results.Ok(collector.Current))
            .WithSummary("Processor, memory, GPU and disk, as of the last background sample.")
            .WithDescription(
                "Served from memory — nothing is measured inside the request. Every figure is nullable and "
                + "each group carries an unavailableReason when absent: a host whose GPU driver publishes no "
                + "usage number says so rather than reporting zero, which would be indistinguishable from an "
                + "idle GPU.")
            .Produces<SystemStats>()
            .RequireAuthorization();

        // A second route rather than a bigger /stats payload. /stats is polled by the dashboard
        // wall for its disk banner, not only by the settings page, so folding an hour of samples
        // into it would put tens of kilobytes on every wall poll to serve a page that is usually
        // not open. Same instinct as the note above about not making this a socket: pay for the
        // thing where it is asked for.
        group.MapGet("/stats/history", (SystemStatsCollector collector) => Results.Ok(collector.History()))
            .WithSummary("Retained processor, memory and GPU samples, for the App's sparklines.")
            .WithDescription(
                "Parallel arrays, oldest first, all the same length — index i of each series belongs to "
                + "sampledAt index i. A null is a sample where that figure was unavailable and must be drawn "
                + "as a break in the line, never as a zero. Timestamps are sent rather than inferred from the "
                + "cadence, so a missed tick does not silently shift every earlier point. Empty arrays mean "
                + "the server started recently; unavailableReason means retention is off.")
            .Produces<VitalsHistory>()
            .RequireAuthorization();

        // Authenticated, matching everything else in this group. A version number is the first
        // thing an attacker wants — it turns "is this vulnerable" into a lookup — and the people
        // with a real need for it are already signed in: the App has its own copy stamped in at
        // build time, so this route exists for a person with a terminal, not for the UI.
        // Stops the process and relies on the container's restart policy to start it again — which
        // is every compose file this repo ships (`restart: unless-stopped`). Exists because the
        // settings a restart applies are set in the App, so applying them belongs there too rather
        // than on a terminal. Without a restart policy it is a stop, and the description says so.
        group.MapPost("/restart", (IHostApplicationLifetime lifetime, ILoggerFactory loggers, HttpContext context) =>
            {
                loggers.CreateLogger("Serval.Server.Vitals.Restart").LogWarning(
                    "Restart requested from the App by '{User}'; stopping so the container restarts.",
                    context.User.Identity?.Name ?? "unknown");

                // After the response has gone, so the App hears that the restart was accepted
                // rather than a dropped connection it cannot tell from a crash.
                _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(
                    _ => lifetime.StopApplication(), TaskScheduler.Default);
                return Results.Accepted();
            })
            .WithSummary("Restart the server to apply settings that only take effect at startup.")
            .WithDescription(
                "Stops the process; a container restart policy starts it again. Run without one, "
                + "this stops the server and it stays stopped.")
            .RequireAuthorization("Admin");

        group.MapGet("/version", () => Results.Ok(ServerVersion.Current))
            .WithSummary("The release and commit this server was built from.")
            .WithDescription(
                "Read from the assembly, so it cannot disagree with the binary answering. revision is null "
                + "on a build made outside the image workflow, and version then ends in -dev.")
            .Produces<ServerVersion>()
            .RequireAuthorization();
    }
}
