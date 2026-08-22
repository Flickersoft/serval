using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Cast;
using Serval.Server.GoogleHome;
using Serval.Server.Ingest;
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

    /// <summary>
    /// <b>The signalling route carries its own CORS policy, and must.</b>
    ///
    /// <para>It is called by a player page Google serves from <c>gstatic.com</c>, in a browser, so
    /// the answer is subject to CORS. The app-wide policy cannot serve it: that policy never allows
    /// credentials — correctly, since allowing them alongside a wildcard origin is what makes a
    /// drive-by able to ride a bearer token — and a credentialed fetch refuses a wildcard origin
    /// outright.</para>
    ///
    /// <para>The failure this pins is invisible from the server: the request arrives, the handler
    /// answers 200 with a valid SDP, and the browser discards the response before the page can read
    /// it. No ICE is started, nothing errors, and every log on both sides reports success while the
    /// camera shows a spinner forever. It also means <b>tightening
    /// <c>Serval:Cors:AllowedOrigins</c> — which every publicly reachable deployment should do —
    /// must not be able to break Google Home</b>, which is exactly what would happen if this route
    /// inherited the app policy.</para>
    /// </summary>
    [Fact]
    public void TheGoogleSignallingRouteHasItsOwnCorsPolicy()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapGoogleHomeEndpoints());

        Dictionary<string, string?> cors = endpoints
            .OfType<RouteEndpoint>()
            .ToDictionary(
                e => $"{Verb(e)} /{e.RoutePattern.RawText?.TrimStart('/')}",
                e => e.Metadata.GetMetadata<IEnableCorsAttribute>()?.PolicyName,
                StringComparer.Ordinal);

        // Every route a browser or a Cast receiver reaches directly. Signaling is fetched by
        // Google's player from gstatic; the HLS trio is fetched by a Cast Web Receiver, and
        // Chromecast is strict about CORS on adaptive streaming.
        string[] browserFacing =
        [
            "POST /api/google/camerastream/signal",
            "GET /api/google/camerastream/hls/{cameraId}/index.m3u8",
            "GET /api/google/camerastream/hls/{cameraId}/{file}.m4s",
            "GET /api/google/camerastream/hls/{cameraId}/{file}.mp4",
        ];

        foreach (string route in browserFacing)
        {
            Assert.Equal("google-signaling", cors[route]);
        }

        // And only those. The rest are server-to-server or Admin, and a route quietly picking this
        // up would widen what a Google-served page may read.
        Assert.Empty(cors.Where(pair =>
            !browserFacing.Contains(pair.Key, StringComparer.Ordinal) && pair.Value is not null));
    }

    private static string Verb(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "?";

    /// <summary>
    /// What a route's authorization actually resolves to, with <c>AllowAnonymous</c> told apart
    /// from "no explicit policy".
    ///
    /// <para><b><see cref="MediaRoutesTakeAStreamToken"/>'s reading of
    /// <see cref="IAuthorizeData.Policy"/> cannot make that distinction</b>, and for the media
    /// group it did not need to: every route there is authorized, and the question was only which
    /// policy. It is the wrong instrument for a group that is mostly anonymous. Both an anonymous
    /// route and one that simply forgot to say answer "(default)" through that lens — and they
    /// behave completely differently, because the application-wide fallback policy means the
    /// forgetful one 401s while the anonymous one is open to the internet.</para>
    ///
    /// <para>So <see cref="IAllowAnonymous"/> is checked first. A Google Home route that loses its
    /// <c>.AllowAnonymous()</c> reads "(default)" here and fails; one that gains it by accident
    /// reads "(anonymous)" and fails the same way.</para>
    /// </summary>
    private static string Authorization(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            ? "(anonymous)"
            : endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? "(default)";

    /// <summary>
    /// The App's own cast route: a POST, on the camera it casts, behind an ordinary session.
    ///
    /// <para><b>The contrast with its neighbours is the point.</b> Every other route that mints or
    /// spends a camera stream ticket is anonymous, because its caller is Google and Google holds no
    /// Serval session. This one's caller is the App, which does — so an <c>.AllowAnonymous()</c>
    /// picked up here would hand a camera-scoped credential to anyone who found the URL, and the
    /// symptom would be nothing at all.</para>
    ///
    /// <para>POST rather than GET because it mints that credential: a prefetch, a link, or a
    /// crawler should not be able to.</para>
    /// </summary>
    [Fact]
    public void TheAppsCastRoutesRequireASession()
    {
        List<RouteEndpoint> endpoints = Materialize(app => app.MapCastEndpoints())
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Read on every camera screen, so that device discovery can start. Names no camera
                // and mints nothing, which is why it is the one that may be a GET.
                ["/api/cast/receiver"] = "GET (default)",

                // Mints a camera-scoped credential, so a prefetch, a link or a crawler must not be
                // able to trigger it.
                ["/api/cameras/{id}/cast"] = "POST (default)",

                // Fetched by the television itself, which cannot set an Authorization header — so
                // these two take the token in the URL, which is exactly what MediaAccess is for.
                // On the default policy they would 401 every time and the cast would show nothing.
                ["/api/cameras/{id}/cast.m3u8"] = "GET MediaAccess",
                ["/api/cameras/{id}/cast/{file}.ts"] = "GET MediaAccess",
            },
            endpoints.ToDictionary(
                e => e.RoutePattern.RawText!,
                e => string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
                    + " " + (e.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? "(default)"),
                StringComparer.Ordinal));

        // Neither is anonymous, and that is the whole point of asserting it. Every *other* route
        // that touches these tickets is, because its caller is Google and Google holds no Serval
        // session — so an .AllowAnonymous() drifting onto one of these would look consistent with
        // its neighbours while handing a camera credential to anyone who found the URL.
        Assert.All(endpoints, e =>
        {
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>());
        });
    }

    /// <summary>
    /// The Google Home group: which routes exist, and which of them are open to the internet.
    ///
    /// <para><b>This is the group where getting authorization wrong is worst.</b> Every other route
    /// in the server is reached from a trusted LAN; the anonymous ones here are what an operator
    /// publishes through a reverse proxy so Google's servers — and the Cast devices Google runs
    /// pages on — can reach them, and none of those callers has a Serval session or can be given
    /// one. Each is therefore anonymous by necessity and authenticates itself in the handler, the
    /// arrangement telemetry ingest already uses, while the three administrative routes take an
    /// ordinary Admin session.</para>
    ///
    /// <para>The failure this prevents is silent in both directions. An administrative route that
    /// picked up <c>.AllowAnonymous()</c> from the group would publish the linked-account list and
    /// the unlink action to anyone who found the URL. A fulfillment route that lost it would 401
    /// every call from Google, and the only symptom is cameras that stop responding to voice with
    /// nothing in the App to explain it.</para>
    /// </summary>
    [Fact]
    public void GoogleHomeRoutesAreAnonymousOnlyWhereGoogleCalls()
    {
        IReadOnlyList<Endpoint> endpoints = Materialize(app => app.MapGoogleHomeEndpoints());

        Dictionary<string, string> authorization = endpoints
            .OfType<RouteEndpoint>()
            .ToDictionary(
                e => $"{Verb(e)} /{e.RoutePattern.RawText?.TrimStart('/')}",
                Authorization,
                StringComparer.Ordinal);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // The two Google calls. Anonymous by necessity — Google's servers hold no Serval
                // session — and each authenticates itself: /authorize on a constant-time client_id
                // compare, /token on client_id plus the client secret in the form body.
                ["GET /api/google/oauth/authorize"] = "(anonymous)",
                ["POST /api/google/oauth/token"] = "(anonymous)",

                // The two Google calls after linking. Fulfillment carries the access token this
                // server issued; signaling carries a ticket good for one camera for two minutes.
                ["POST /api/google/fulfillment"] = "(anonymous)",
                ["POST /api/google/camerastream/signal"] = "(anonymous)",
                // Also carries its own CORS policy — see CorsPolicyTests below. Anonymous alone is
                // not enough for this one: it is called by a page Google serves, in a browser.

                // The HLS trio, fetched by a Cast Web Receiver running on a Google TV. Anonymous
                // for a stronger reason than the others: that receiver cannot send an
                // Authorization header at all, which is why the camera-scoped ticket travels as
                // ?t= and why cameraStreamNeedAuthToken is false. Each handler spends the ticket
                // and refuses one minted for a different camera.
                ["GET /api/google/camerastream/hls/{cameraId}/index.m3u8"] = "(anonymous)",
                ["GET /api/google/camerastream/hls/{cameraId}/{file}.m4s"] = "(anonymous)",
                ["GET /api/google/camerastream/hls/{cameraId}/{file}.mp4"] = "(anonymous)",

                // Serval's own Cast Web Receiver. Anonymous because a Cast device fetches it
                // before any load request exists, so there is no ticket to present yet — and it
                // needs none: it is a static page naming no camera and carrying no credential.
                // Everything specific arrives afterwards and is checked by the routes above.
                ["GET /api/google/camerastream/receiver"] = "(anonymous)",

                // Read by the App's status card to explain why the integration is not working, so
                // it is deliberately *not* behind the 503 gate the other routes sit behind — but it
                // reports configuration, which is an Admin's business and nobody else's.
                ["GET /api/google/status"] = "Admin",

                // Administrative, and the pair most damaging to leak: the list names the linked
                // account, and the delete silently removes every camera from somebody's house.
                ["GET /api/google/links"] = "Admin",
                ["DELETE /api/google/links/{agentUserId}"] = "Admin",
            },
            authorization);
    }

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
        builder.Services.AddSingleton<GoogleHomeGate>();
        builder.Services.AddSingleton<GoogleOAuthStore>();
        builder.Services.AddSingleton<CameraStreamTicketService>();
        builder.Services.AddSingleton<SmartHomeFulfillment>();
        builder.Services.AddSingleton<IGo2RtcClient, Go2RtcClient>();

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
