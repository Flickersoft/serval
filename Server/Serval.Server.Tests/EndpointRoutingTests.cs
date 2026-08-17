using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Media;
using Serval.Server.Onvif;
using Serval.Server.Preferences;
using Serval.Server.Ptz;
using Serval.Server.Push;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Tests;

/// <summary>
/// Builds the route table and asks for the endpoints, which is the only thing that checks a minimal
/// API handler's parameters actually bind.
///
/// <para><b>This exists because none of the rest of the suite covers it.</b> Parameter binding is
/// resolved when the endpoint graph is first materialised — lazily, at startup, inside
/// <c>AuthorizationMiddleware</c>'s constructor. A handler that cannot bind therefore compiles
/// cleanly, passes every unit test, produces a green build, and then throws
/// <see cref="InvalidOperationException"/> on boot. The container restarts, hits it again, and the
/// deployment is in a crash loop whose first symptom is that the App will not load.</para>
///
/// <para>The specific trap that got through: <b>minimal APIs refuse to infer a body parameter on
/// DELETE</b> (and on GET and HEAD). A complex type in the parameter list of a <c>MapDelete</c>
/// handler is inferred as a body, the method does not allow one, and the graph refuses to build.
/// It is a whole-application failure caused by one route.</para>
///
/// <para>Services are registered but never resolved. Binding asks
/// <c>IServiceProviderIsService</c> whether a parameter's type is a service — which is exactly the
/// question that separates "this is a dependency" from "this is the request body" — and that needs
/// the registrations to exist, not to be constructible. Nothing here touches MongoDB.</para>
/// </summary>
public class EndpointRoutingTests
{
    [Fact]
    public void PushEndpointsBind()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapPushEndpoints());

        // Named rather than counted, so this fails usefully: a route that stops binding disappears
        // from this list, and a route renamed by accident is caught by the same assertion.
        string[] routes = [.. endpoints
            .OfType<RouteEndpoint>()
            .Select(e => $"{Verb(e)} /{e.RoutePattern.RawText?.TrimStart('/')}")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                "DELETE /api/push/subscriptions/{id}",
                "GET /api/push/config",
                "GET /api/push/subscriptions",
                "POST /api/push/subscriptions",
                "POST /api/push/test",
            ],
            routes);
    }

    /// <summary>
    /// The other group the notification work touched. <c>PUT</c> allows an inferred body, so this
    /// has never been at risk the way the DELETE was — it is here because "the group I also edited"
    /// is exactly the one worth checking, and the check costs nothing.
    /// </summary>
    [Fact]
    public void PreferenceEndpointsBind()
    {
        Assert.NotEmpty(Materialize(app => app.MapPreferencesEndpoints()));
    }

    /// <summary>
    /// Which authorization policy each media route carries.
    ///
    /// <para><b>Why this is worth pinning.</b> The difference between the two answers below is
    /// invisible at the call site — <c>.RequireAuthorization()</c> and
    /// <c>.RequireAuthorization("MediaAccess")</c> are one word apart — and it decides whether a
    /// <c>?stream_token=</c> in the URL is read at all. Only the <c>"StreamToken"</c> scheme looks
    /// at the query string (<c>OnMessageReceived</c> in <c>Program.cs</c>), and only
    /// <c>MediaAccess</c> lists that scheme; the default policy uses the default scheme alone, and
    /// <c>OnTokenValidated</c> then rejects a stream token outright.
    ///
    /// <para>So every route fetched by something that cannot set an <c>Authorization</c> header —
    /// a player resolving a playlist, and the browser fetching a notification's picture — has to be
    /// on <c>MediaAccess</c> or it 401s. That is not visible in a unit test of the handler, which
    /// never runs, and it is not visible in a build. It is visible here.</para>
    /// </summary>
    [Fact]
    public void MediaRoutesTakeAStreamToken()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapMediaEndpoints());

        Dictionary<string, string> policies = endpoints
            .OfType<RouteEndpoint>()
            .ToDictionary(
                e => $"/{e.RoutePattern.RawText?.TrimStart('/')}",
                e => e.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? "(default)",
                StringComparer.Ordinal);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Fetched by the browser as a notification's picture, with the token in the URL
                // that AlertNotifier.Compose puts there — see the Push notifications section of
                // Docs/alerts.md. The App fetches the same route with a header, which is what hid
                // this for as long as the notification was the only caller that could not.
                ["/api/cameras/{id}/snapshot.jpg"] = "MediaAccess",

                // Fetched by hls.js and libmpv, which are handed a URL and nothing else.
                ["/api/cameras/{id}/{file}.m4s"] = "MediaAccess",
                ["/api/cameras/{id}/{file}.mp4"] = "MediaAccess",
                ["/api/cameras/{id}/vod.m3u8"] = "MediaAccess",

                // The default policy, and correctly: every one of these is fetched by the
                // authenticated client, which sets a header. The export is a download rather than
                // a playback — see ServalApi.clipUrl and downloadSavedClip — and the two JSON
                // routes are ordinary reads.
                ["/api/cameras/{id}/clip.mp4"] = "(default)",
                ["/api/cameras/{id}/recordings"] = "(default)",
                ["/api/cameras/{id}/coverage"] = "(default)",
            },
            policies);
    }

    /// <summary>
    /// The same question for the two alert routes a player or a browser fetches by URL.
    ///
    /// <c>poster.jpg</c> is the one that matters here: it is an <c>Image.network</c> in the App, so
    /// it carries a stream token and nothing else, and it is fetched for every row in the queue.
    /// </summary>
    [Fact]
    public void AlertMediaRoutesTakeAStreamToken()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapAlertEndpoints());

        string[] streamed = [.. endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>()?.Policy == "MediaAccess")
            .Select(e => $"/{e.RoutePattern.RawText?.TrimStart('/')}")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["/api/alerts/{id}/clip.mp4", "/api/alerts/{id}/poster.jpg"],
            streamed);
    }

    /// <summary>
    /// That PTZ takes any signed-in account, on every route in the group.
    ///
    /// <para><b>Why this is worth pinning.</b> The policy is set once, on the group, so a single
    /// word decides it for seven routes at a stroke — and the role contract in <c>Auth/User.cs</c>
    /// puts pointing a camera on the operating side of the line, alongside talk-back, which rides
    /// the WebRTC route and is out of reach of a policy. Restoring <c>"Admin"</c> here would not
    /// fail a handler test: it would 403 a Viewer's <c>GET /capabilities</c>, and the App renders
    /// that as an error card over the picture rather than as absent controls.</para>
    /// </summary>
    [Fact]
    public void PtzRoutesTakeAnySignedInAccount()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapPtzEndpoints());

        Dictionary<string, string> policies = endpoints
            .OfType<RouteEndpoint>()
            .ToDictionary(
                e => $"{Verb(e)} /{e.RoutePattern.RawText?.TrimStart('/')}",
                e => e.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? "(default)",
                StringComparer.Ordinal);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["POST /api/cameras/{id}/ptz/move"] = "(default)",
                ["POST /api/cameras/{id}/ptz/stop"] = "(default)",
                ["POST /api/cameras/{id}/ptz/zoom"] = "(default)",
                ["POST /api/cameras/{id}/ptz/preset"] = "(default)",
                ["POST /api/cameras/{id}/ptz/home"] = "(default)",
                ["GET /api/cameras/{id}/ptz/capabilities"] = "(default)",
                ["GET /api/cameras/{id}/ptz/status"] = "(default)",
            },
            policies);
    }

    private static string Verb(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "?";

    /// <summary>
    /// The specific shape that took the server down, pinned so it cannot come back: a complex type
    /// in a <c>MapDelete</c> handler is inferred as a body, and DELETE does not allow one.
    ///
    /// Asserting the failure rather than trusting the note above it — a comment saying "do not do
    /// this" is worth much less than a test proving what happens if somebody does.
    /// </summary>
    [Fact]
    public void ADeleteHandlerCannotInferABody()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(app =>
            app.MapDelete("/example", (ExampleBody body) => Results.NoContent())));

        Assert.Contains("Body was inferred", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The same shape on POST, which is allowed — so the test above is about the verb.</summary>
    [Fact]
    public void APostHandlerCanInferABody()
    {
        Assert.NotEmpty(Materialize(app =>
            app.MapPost("/example", (ExampleBody body) => Results.NoContent())));
    }

    /// <summary>
    /// Maps something onto a real <see cref="WebApplication"/> and forces the endpoint graph to be
    /// built, which is what throws if a handler cannot bind.
    /// </summary>
    private static IReadOnlyList<Endpoint> Materialize(Action<IEndpointRouteBuilder> map)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Registered, never constructed. Validation is off for that reason: these have real
        // dependencies — a Mongo connection among them — that this test has no business standing up
        // and does not need, since binding only asks whether a type *is* a service.
        builder.Services.Configure<ServiceProviderOptions>(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });

        builder.Services.AddSingleton<VapidKeyStore>();
        builder.Services.AddSingleton<VapidSigner>();
        builder.Services.AddSingleton<WebPushClient>();
        builder.Services.AddSingleton<PushSubscriptionRepository>();
        builder.Services.AddSingleton<AlertNotifier>();
        builder.Services.AddSingleton<UserPreferencesRepository>();
        builder.Services.AddSingleton<SnapshotBroadcaster>();
        builder.Services.AddSingleton<RecordingIndex>();
        builder.Services.AddSingleton<ClipExporter>();
        builder.Services.AddSingleton<AlertRepository>();
        builder.Services.AddSingleton<AlertStorage>();
        builder.Services.AddSingleton<CameraRepository>();
        builder.Services.AddSingleton<OnvifClient>();

        WebApplication app = builder.Build();
        map(app);

        // Read the builder's own sources rather than the composite one in DI, which is not
        // populated until the routing middleware runs at startup and would answer empty here.
        //
        // Asking each source for `Endpoints` is the line that does the work: it is lazy, and
        // forcing it is exactly what the hosting layer does on boot — a group's source resolves
        // every handler's parameters at this point, and throws here if one cannot bind.
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];
    }

    private sealed record ExampleBody(string Value);
}
