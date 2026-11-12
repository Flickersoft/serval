using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Protocol;
using Serval.Server.Media;

namespace Serval.Server.Mcp;

/// <summary>Wires the MCP endpoint and its authorization server into the host.</summary>
public static class McpRegistration
{
    /// <summary>
    /// What a client is told about this server when it connects — a few sentences of orientation a
    /// model reads before choosing tools.
    /// </summary>
    private const string Instructions =
        "Serval is a home security camera system. These tools read what its cameras recorded and noticed; "
        + "none of them can change anything. Start with list_cameras for camera ids and the server's current "
        + "time. get_activity gives a timeline of what happened (scene descriptions, detected objects, speech, "
        + "conversations, sounds); search_activity finds a word or phrase in it. list_alerts is what the "
        + "household asked to be alerted about. get_snapshot shows a camera now or at a past instant, and "
        + "get_alert_image / get_clip_image show the frame behind an alert or saved clip. Scene descriptions "
        + "and transcripts are machine-generated and can be wrong; when it matters, look at the picture. All "
        + "times are ISO-8601; include an offset when passing one. Most results carry a url that opens that "
        + "camera, moment, alert or clip in the Serval app, as video, for the person you are helping (it asks "
        + "them to sign in if they have not). Offer it whenever they would want to see for themselves. Copy a "
        + "url exactly as a tool returned it and never compose one: only returned urls are known to work.";

    /// <summary>
    /// CORS for the routes an MCP client calls, which may be a browser on any origin (the MCP
    /// Inspector, a web client). Any origin and no credentials is safe here because nothing on
    /// these routes is ambient: every credential is a bearer header the caller chose to send.
    /// <c>WWW-Authenticate</c> is exposed because the 401 that starts discovery is unreadable to a
    /// browser client without it. Its own policy, so tightening <c>Serval:Cors:AllowedOrigins</c>
    /// for the App cannot break assistants.
    /// </summary>
    public const string CorsPolicy = "mcp";

    /// <summary>Rate limits on the anonymous OAuth routes, each per caller address.</summary>
    public const string RegisterRateLimit = "mcp-register";
    public const string AuthorizeRateLimit = "mcp-authorize";
    public const string TokenRateLimit = "mcp-token";

    public static IServiceCollection AddServalMcp(this IServiceCollection services)
    {
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .WithMethods("GET", "POST", "OPTIONS")
            .WithExposedHeaders("WWW-Authenticate")));

        // Registration writes a row and authorize may fetch a URL from the internet, so both are
        // bounded per address. Token is called from an assistant vendor's servers, possibly shared
        // by every household using it, so its budget is the loosest.
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(RegisterRateLimit, context => PerAddress(context, 10));
            options.AddPolicy(AuthorizeRateLimit, context => PerAddress(context, 30));
            options.AddPolicy(TokenRateLimit, context => PerAddress(context, 120));
        });

        services.AddSingleton<McpGate>();
        services.AddSingleton<McpOAuthStore>();
        services.AddSingleton<McpClientResolver>();
        services.AddSingleton<RecordedFrameReader>();

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = DescribeServer(issuer: null);
                options.ServerInstructions = Instructions;
            })
            // Stateless: each request carries its own bearer token and needs nothing from the last
            // one, which is the only mode the 2026-07-28 protocol revision has, and means a restart
            // drops no sessions.
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                // Per request, because the icon and website addresses are built from the public
                // address, which can be changed in Settings without a restart.
                options.ConfigureSessionOptions = (context, server, _) =>
                {
                    server.ServerInfo = DescribeServer(context.RequestServices.GetRequiredService<McpGate>().Issuer);
                    return Task.CompletedTask;
                };
            })
            .WithTools([typeof(McpTools)]);

        return services;
    }

    /// <summary>
    /// The name, logo and website a client shows beside this connector. The icons are the App's own
    /// launcher images, which the web bundle serves without a sign-in; they are absolute URLs on the
    /// public address because the client fetches them from its own servers. Without a public address
    /// there is nowhere reachable to point at, so the icons are left out.
    /// </summary>
    internal static Implementation DescribeServer(string? issuer) => new()
    {
        Name = "serval",
        Title = "Serval",
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        WebsiteUrl = issuer,
        Icons = issuer is null
            ? null
            :
            [
                new Icon { Source = $"{issuer}/icons/Icon-192.png", MimeType = "image/png", Sizes = ["192x192"] },
                new Icon { Source = $"{issuer}/icons/Icon-512.png", MimeType = "image/png", Sizes = ["512x512"] },
            ],
    };

    /// <summary>A fixed one-minute window per remote address, the shape the login limiter uses.</summary>
    private static RateLimitPartition<string> PerAddress(HttpContext context, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });

    /// <summary>The 404-when-off gate. Must run before authentication, so a closed endpoint never challenges.</summary>
    public static IApplicationBuilder UseServalMcpGate(this IApplicationBuilder app) =>
        app.UseMiddleware<McpGateMiddleware>();

    public static void MapServalMcp(this IEndpointRouteBuilder app)
    {
        app.MapMcpOAuthEndpoints();
        app.MapMcp(McpGate.McpPath).RequireAuthorization(McpBearerHandler.PolicyName).RequireCors(CorsPolicy);
    }
}
