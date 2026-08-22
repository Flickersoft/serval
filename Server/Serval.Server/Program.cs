using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serval.Ai;
using Serval.Server;
using Serval.Server.Ai;
using Serval.Server.Alerts;
using Serval.Server.Auth;
using Serval.Server.Backup;
using Serval.Server.Cameras;
using Serval.Server.Cast;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.Dashboard;
using Serval.Server.Events;
using Serval.Server.GoogleHome;
using Serval.Server.Ingest;
using Serval.Server.Live;
using Serval.Server.Media;
using Serval.Server.Onvif;
using Serval.Server.Preferences;
using Serval.Server.Ptz;
using Serval.Server.Push;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;
using Serval.Server.Storage;
using Serval.Server.Telemetry;
using Serval.Server.Vitals;
using Serval.Server.WebRtc;

// Every MongoDB serialization decision, before anything is serialized. Must be first.
BsonRegistration.Register();

var builder = WebApplication.CreateBuilder(args);

// Long enough for every camera's ingest to be torn down, which the default five seconds is not.
// Stopping a session cancels its ffmpeg, kills the process tree and drains the loops harvesting its
// output; when the budget runs out the host stops waiting and the process exits, and any ffmpeg not
// yet reached is orphaned — still recording, still writing detect frames into tmpfs, with nothing
// left alive to read or delete them. StreamIngestManager stops its sessions in parallel so this is
// one camera's teardown rather than the sum of them, and this is the ceiling on that.
builder.Host.ConfigureHostOptions(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

// What this server would run on with nothing changed in the App — the same sources, in the same
// order, without the overlay added below. Captured before that source exists rather than filtered
// out afterwards, and kept for the lifetime of the process: it is what a Reset restores a setting
// to, and the settings page has to be able to show that value before the button is pressed.
// ConfigurationManager implements IConfigurationBuilder explicitly, so the source list and Add are
// only reachable through the interface.
var configuration = (IConfigurationBuilder)builder.Configuration;

var deploymentBuilder = new ConfigurationBuilder();
foreach (IConfigurationSource source in configuration.Sources)
{
    deploymentBuilder.Add(source);
}

builder.Services.AddSingleton(new DeploymentConfiguration(deploymentBuilder.Build()));

// The settings a user has changed in the App, layered last so they win over both appsettings and
// the environment. appsettings seeds a fresh install and the compose file carries the deployment's
// choices; this carries what someone has since decided, and an absent key here means "not
// overridden" rather than "empty". Added before ServerOptions is read below, because the eager
// snapshot that follows decides which services get registered at all — a stored ServerAi:Enabled
// would otherwise never apply, however many times the server was restarted.
var settingsStore = new ServerSettingsStore(
    builder.Configuration.GetSection("Serval:Mongo").Get<MongoOptions>() ?? new MongoOptions());
var settingsSource = new SettingsConfigurationSource(settingsStore);
configuration.Add(settingsSource);

builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(settingsSource.Provider);
builder.Services.AddSingleton<ISettingChoiceProbe, DetectionDeviceAvailability>();
builder.Services.AddSingleton<SettingsReader>();

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

var serverOptions = builder.Configuration
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();

// Unlike Serval:ApiKey, there is no safe empty value here: every route except telemetry ingest
// now requires a logged-in user, so a server that cannot sign or verify a token is a server that
// cannot do anything useful. Refusing to start is the deviation from this file's usual
// warn-and-run-degraded posture (see the AI model checks below) — there is no degraded mode to run in.
//
// The placeholder check runs first so the message is the true one. A published `CHANGE-ME` fails
// the length test too, and being told to write something longer sends an operator looking for a
// longer placeholder rather than for `openssl`.
PlaceholderCredentials.ThrowIfPlaceholder(
    serverOptions.Auth.SigningKey,
    "Serval:Auth:SigningKey",
    "Generate one with `openssl rand -base64 32` and set it before starting.");

if (string.IsNullOrWhiteSpace(serverOptions.Auth.SigningKey) || serverOptions.Auth.SigningKey.Length < 32)
{
    throw new InvalidOperationException(
        "Serval:Auth:SigningKey must be set to a random secret of at least 32 characters — "
        + "generate one with `openssl rand -base64 32`. The server cannot issue or verify a login "
        + "token without it.");
}

// Empty stays legal — AdminBootstrap warns and skips, which is the right answer for a server whose
// accounts already exist. A placeholder is different: it is an account someone can sign into.
PlaceholderCredentials.ThrowIfPlaceholder(
    serverOptions.Auth.BootstrapAdminPassword,
    "Serval:Auth:BootstrapAdminPassword",
    "Choose a real password — it is the first Admin account, and it is created on first boot.");

// Deferred until there is a logger: there is none before Build(), and these are too important to
// emit as bare stderr text that carries no level and no category.
var startupWarnings = new List<string>();

// A database this cannot reach must not stop the server booting — losing the overlay degrades it to
// its deployment configuration, which is what it ran on before any of this existed. Recording is
// the job; refusing to start because a tuning value could not be read would trade a working
// appliance for a settings page.
if (settingsSource.Provider.LoadError is { } settingsError)
{
    startupWarnings.Add(
        $"Saved settings could not be read from MongoDB ({settingsError}). The server is running on "
        + "its appsettings and environment configuration alone — anything changed in the App is not "
        + "applied. It will be picked up on the next restart once the database is reachable.");
}

// Stated rather than enforced. A LAN appliance talking to cameras that mostly have no certificate
// of their own is a reasonable thing to run over plain HTTP, and redirecting to a port with nothing
// listening on it would break a working install to make a point. But it should be a choice someone
// made, not one they discover later — and two of the symptoms below get blamed on the App.
if (serverOptions.Cors.AllowedOrigins.Count == 0)
{
    startupWarnings.Add(
        "Serval:Cors:AllowedOrigins is empty, so any web origin may call this server. That is the "
        + "LAN default; set it to the App's own origin before this server is reachable from "
        + "anywhere else. See Docs/deployment.md, 'TLS and exposure'.");
}

// Telemetry ingest is the one machine-to-machine route, and it is closed until a key exists. Said
// once at boot because the alternative is a CameraModule that retries forever against a 401 while
// the Server looks healthy — the failure is at the other end of the wire from where it is noticed.
if (string.IsNullOrEmpty(serverOptions.ApiKey))
{
    startupWarnings.Add(
        "Serval:ApiKey is not set, so POST /api/cameras/{id}/telemetry rejects every request. "
        + "A CameraModule cannot deliver until it is set here and matched in the module's "
        + "Output:ApiKey. Nothing else is affected — a Server with no modules needs no key.");
}

// Named once at boot, and specifically. Six conditions have to hold for the Google Home routes to
// answer anything but 503, and an operator who has just filled in five of them needs to be told
// which one is left — not that "Google Home is off", which is true of every failure and points at
// none of them. Silent while the feature is simply switched off, which is the ordinary state.
if (serverOptions.GoogleHome.Enabled)
{
    GoogleHomeStatus googleHome = GoogleHomeGate.Evaluate(serverOptions);
    if (!googleHome.Effective)
    {
        startupWarnings.Add(
            $"Google Home is switched on but not serving: {googleHome.Reason}");
    }
    else if (!googleHome.HomeGraphKeyConfigured)
    {
        // Not a failure. The integration works without it; what is lost is worth naming because
        // the symptom appears days later, as a camera that Google never hears about.
        startupWarnings.Add(
            "Google Home is live without a HomeGraph key (Serval:GoogleHome:HomeGraphKeyPath is "
            + "unset), so Google will not learn about camera changes on its own. Re-link in the "
            + "Google Home app, or say \"Hey Google, sync my devices\", after adding or renaming "
            + "a camera. Camera online/offline is also not reported, so SYNC declares "
            + "willReportState: false and Google's Test Suite will skip these devices. Streaming "
            + "is unaffected.");
    }
}

IngestOptions ingest = serverOptions.Ingest;

// What this host's ffmpeg can actually encode, read once. Fatal if it cannot be read at all: a
// server that cannot run ffmpeg cannot record, and the alternative is every camera failing
// separately at runtime with the reason buried in a retry loop.
FfmpegCapabilities capabilities = FfmpegCapabilities.Probe(ingest.FfmpegPath, TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(capabilities);

// The render node is checked here, not per-camera at ffmpeg-start time, and the two ways it is
// wrong in a container need different fixes — so they get different messages.
if (!string.IsNullOrWhiteSpace(ingest.HwAccelDevice))
{
    string device = ingest.HwAccelDevice;
    if (!File.Exists(device))
    {
        throw new InvalidOperationException(
            $"Serval:Ingest:HwAccelDevice is '{device}', which does not exist. In Docker, pass the "
            + "GPU in with 'devices: [ /dev/dri:/dev/dri ]'. Clear the setting to encode in software.");
    }

    try
    {
        using FileStream probe = File.OpenRead(device);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Serval:Ingest:HwAccelDevice is '{device}', which exists but this process cannot open "
            + $"({ex.Message}). The container user needs the host's 'render' (or 'video') group — "
            + "add 'group_add: [ <host render gid> ]'.", ex);
    }
}

builder.Services.AddSingleton<MongoContext>();
builder.Services.AddSingleton<CameraRepository>();
builder.Services.AddSingleton<RecordingIndex>();
builder.Services.AddSingleton<TelemetryRepository>();
builder.Services.AddSingleton<SnapshotBroadcaster>();
builder.Services.AddSingleton<DetectFrameBroadcaster>();
builder.Services.AddSingleton<DetectionLoad>();
builder.Services.AddSingleton<EventBroadcaster>();

builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<UserPreferencesRepository>();
builder.Services.AddSingleton<RefreshTokenRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<StreamTicketService>();

// Reach across the camera registry, the settings overlay, the accounts and the preferences, which
// is why they sit here rather than in any one of those blocks.
builder.Services.AddSingleton<ConfigBackupBuilder>();
builder.Services.AddSingleton<ConfigRestoreService>();
builder.Services.AddHostedService<StreamTicketSweepWorker>();

// Registered unconditionally, outside the ServerAi block below, so the audio-levels endpoint can
// always resolve it and answer with an explanatory 400 rather than failing to construct.
builder.Services.AddSingleton<AudioLevelBroadcaster>();

builder.Services.AddSingleton<Serval.Server.Media.ClipExporter>();
builder.Services.AddSingleton<Serval.Server.Media.CastTranscoder>();

// Saved clips. Registered unconditionally — the summary worker resolves the vision model through
// the provider and does nothing when there is none, so a server with no AI still keeps clips.
builder.Services.AddSingleton<ClipRepository>();
builder.Services.AddSingleton<ClipStorage>();
builder.Services.AddSingleton<ClipMedia>();
builder.Services.AddSingleton<ClipSummaryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClipSummaryWorker>());
builder.Services.AddSingleton<ClipWriteWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClipWriteWorker>());

// Alerts. Registered unconditionally, outside the ServerAi block below, because the queue outlives
// the detection that filled it: a server whose AI is turned off still has last week's alerts to
// show, clear and serve clips for. Only AlertService is reached from the detection pipelines, and
// nothing raises anything when they are not running.
builder.Services.AddSingleton<AlertRepository>();
builder.Services.AddSingleton<AlertStorage>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<AlertClipWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertClipWorker>());
builder.Services.AddHostedService<AlertRetentionWorker>();

// Delivering those alerts to a browser that is not looking at the screen. Registered
// unconditionally alongside the queue, and for the same reason: with no subscriptions it does
// nothing, and Serval:Push:Enabled is checked at enqueue rather than here so turning notifications
// on and off does not need a restart.
//
// The HTTP client is pointed at the public internet — Google, Mozilla and Apple's push services,
// whose addresses come from the browsers themselves, so it has no BaseAddress. What crosses that
// boundary is ciphertext the relay cannot read; see WebPushCrypto. The only other client here that
// leaves the LAN is HomeGraphClient, registered below and used only when a Google Home integration
// has been configured.
builder.Services.AddSingleton<VapidKeyStore>();
builder.Services.AddSingleton<VapidSigner>();
builder.Services.AddHttpClient<WebPushClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<PushSubscriptionRepository>();
builder.Services.AddSingleton<AlertNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertNotifier>());

// Registered unconditionally, like the notifier above and for the same reason: the gate reads
// Serval:GoogleHome:* through IOptionsMonitor at request time, so switching the integration on and
// off is a configuration change rather than a restart.
builder.Services.AddSingleton<GoogleHomeGate>();
builder.Services.AddSingleton<GoogleOAuthStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CameraStreamTicketService>();
builder.Services.AddHostedService<CameraStreamTicketSweepWorker>();
builder.Services.AddScoped<SmartHomeFulfillment>();
builder.Services.AddScoped<GoogleCameraSwitchStore>();

// The second HTTP client in this process pointed at the public internet, after Web Push. What
// crosses is an agent user id and nothing else — no camera name, no image, no telemetry. Absent a
// HomeGraph key the client is registered and never used, which is the ordinary deployment.
builder.Services.AddSingleton<HomeGraphKeyStore>();
builder.Services.AddHttpClient<HomeGraphClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<GoogleHomeSyncWorker>();
builder.Services.AddHostedService<GoogleHomeStateWorker>();

// Where each camera's rolling detect-stream buffer is. In memory rather than in Mongo — see the
// type — so it is a singleton shared by the ingest session that fills it and the worker that cuts
// clips out of it.
builder.Services.AddSingleton<PreviewRingIndex>();

builder.Services.AddHostedService<StreamIngestManager>();
builder.Services.AddHostedService<RetentionWorker>();

// What the server can say about itself: processor, memory, GPU and the media volume. Registered
// unconditionally for the same reason AudioLevelBroadcaster is — the endpoint should always
// resolve and answer with an explanation rather than failing to construct — and the worker returns
// immediately when Serval:Vitals:Enabled is false.
builder.Services.AddSingleton<SystemStatsCollector>();
builder.Services.AddHostedService<SystemStatsWorker>();

// WebRTC low-latency live view via the go2rtc sidecar. The typed client's base address is the
// sidecar URL from config; the sync worker mirrors the camera registry into go2rtc's streams.
builder.Services.AddHttpClient<IGo2RtcClient, Go2RtcClient>((sp, http) =>
{
    WebRtcOptions webrtc = sp.GetRequiredService<IOptions<ServerOptions>>().Value.WebRtc;
    http.BaseAddress = new Uri(webrtc.Go2RtcUrl.TrimEnd('/') + "/");
});
builder.Services.AddHostedService<Go2RtcSyncWorker>();

// Server-side AI detection: the same library the edge CameraModule runs, hosted here so a camera
// without an edge device still gets AI. Off by default — enabling it loads real models into this
// process, and a server that only records should not pay for weights it never uses. Cameras then
// opt in individually via their AiVision / AiAudio flags.
if (serverOptions.ServerAi.Enabled)
{
    // Conversation audio lives under the media root, which is already the volume sized for
    // per-camera data. Resolved once here so the shared library needs no notion of a media root.
    builder.Services.PostConfigure<ServerOptions>(o =>
        o.Ai.Speaker.ConversationAudioDirectory =
            Path.Combine(o.Media.Root, o.ServerAi.ConversationAudioRoot));

    if (serverOptions.Ai.Asr is { } asr && File.Exists(asr.ModelPath))
    {
        builder.Services.AddSingleton(sp => new SenseVoiceAnalyzer(
            sp.GetRequiredService<IOptions<ServerOptions>>().Value.Ai.Asr,
            sp.GetRequiredService<ILogger<SenseVoiceAnalyzer>>()));
    }
    else
    {
        // Refusing to start would take recording down with it, so the server runs without the
        // capability instead — and says so, rather than silently producing no transcripts.
        startupWarnings.Add(
            $"Server AI: ASR model not found at '{serverOptions.Ai.Asr.ModelPath}'. Audio detection "
            + "is disabled; run scripts/fetch-models.sh (or the deploy compose's fetch-models "
            + "one-shot) and point Serval:Ai:Asr at the result.");
    }

    if (serverOptions.Ai.Sound.Enabled)
    {
        SoundOptions sound = serverOptions.Ai.Sound;
        if (File.Exists(sound.ModelPath) && File.Exists(sound.LabelsPath))
        {
            // One instance for every camera, as SenseVoice already is: the model is stateless per
            // stream, so per-camera instances would be the same 26 MB of weights loaded N times.
            // The per-camera state — the level gate, the cooldown — lives in the detector.
            builder.Services.AddSingleton(sp => new SoundEventTagger(
                sp.GetRequiredService<IOptions<ServerOptions>>().Value.Ai.Sound,
                sp.GetRequiredService<ILogger<SoundEventTagger>>()));
        }
        else
        {
            // Same treatment as a missing ASR model: an absent weights file is a capability this
            // server does not have, not a reason to stop recording.
            startupWarnings.Add(
                $"Server AI: audio tagging model or labels not found at '{sound.ModelPath}' / "
                + $"'{sound.LabelsPath}'. Sound event detection is disabled; run "
                + "scripts/fetch-models.sh and point Serval:Ai:Sound at the result.");
        }
    }

    if (serverOptions.Ai.Vision.Enabled)
    {
        VisionOptions vision = serverOptions.Ai.Vision;
        if (File.Exists(vision.ModelPath) && File.Exists(vision.MmprojPath))
        {
            // Built on first resolution, which SceneDescriptionWorker forces as soon as it starts.
            // A model that is present but unusable — a truncated GGUF, no VRAM — is a real fault
            // unlike an absent file, and it still takes the server down: the worker stops the
            // application on a failed load. What resolving it late buys is the socket, since the
            // host constructs every hosted service before starting any of them and this load is
            // measured in tens of seconds.
            builder.Services.AddSingleton<IVisionInferenceRunner>(sp =>
                Qwen3VisionRunner.CreateAsync(
                    sp.GetRequiredService<IOptions<ServerOptions>>().Value.Ai.Vision,
                    sp.GetRequiredService<ILogger<Qwen3VisionRunner>>()).GetAwaiter().GetResult());

            builder.Services.AddSingleton<SceneDescriptionWorker>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SceneDescriptionWorker>());
        }
        else
        {
            // Same treatment as a missing ASR model, for the same reason: an absent weights file is
            // a capability this server does not have, not a reason to stop recording.
            startupWarnings.Add(
                $"Server AI: vision model or projector not found at '{vision.ModelPath}' / "
                + $"'{vision.MmprojPath}'. Scene description is disabled; run "
                + "scripts/fetch-models.sh and point Serval:Ai:Vision at the result.");
        }
    }

    if (serverOptions.Ai.Detection.Enabled)
    {
        DetectionOptions detection = serverOptions.Ai.Detection;

        // Asked of the factory rather than checked here, so the answer is about the device actually
        // selected: a Coral host needs the compiled .tflite at ModelPath and an ONNX host the .onnx,
        // and the warning below should name whichever it is missing.
        if (ObjectDetectorFactory.IsConfigured(detection, out string detectionProblem))
        {
            // Eager, like the vision runner and for the same reason: a model that is present but
            // unusable is a real fault. Here that specifically means an input shape this cannot
            // letterbox for, or an output no postprocessor reads — most often a model exported for
            // the one-to-many head, which every pre-YOLO26 export is.
            //
            // A labels file that does not match the weights is the fault worth catching, since it
            // produces confidently wrong class names forever and nothing else reports a problem. It
            // still cannot be caught outright — no head declares a class count to check a file
            // against — but the factory now warns for any configured class the file cannot provide,
            // which covers the case that actually happens.
            builder.Services.AddSingleton<IObjectDetector>(sp =>
                ObjectDetectorFactory.Create(
                    sp.GetRequiredService<IOptions<ServerOptions>>().Value.Ai.Detection,
                    sp.GetRequiredService<ILoggerFactory>()));
        }
        else
        {
            startupWarnings.Add(
                $"Server AI: object detection is disabled because {detectionProblem}. Cameras fall "
                + "back to the motion gate; export a model with scripts/export-detector.sh (or the "
                + "deploy compose's export-detector one-shot) and point Serval:Ai:Detection at it.");
        }
    }

    builder.Services.AddHostedService<CameraAiCoordinator>();
}

// ONVIF PTZ control. A plain HttpClient (the client sets its own per-request timeout); credentials
// and WS-Security are per-camera, handled inside the client.
builder.Services.AddHttpClient<OnvifClient>();

// Two JWT bearer schemes sharing one signing key: the default scheme validates access tokens
// (issued at login/refresh), "StreamToken" validates the scoped tokens minted by
// POST /api/auth/stream-token for video playback URLs. OnTokenValidated is what actually tells
// them apart — it rejects a stream token presented on the default scheme and vice versa — since a
// JWT's signature alone says nothing about which of the two it was meant to be.
void ConfigureJwtBearer(JwtBearerOptions options, bool requireStreamScope)
{
    // Without this, "sub" and other short claim names are silently remapped to XML-namespace
    // claim types on the way in — TokenService.GetUserId relies on them arriving unchanged.
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(serverOptions.Auth.SigningKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
    };
    options.Events = new JwtBearerEvents
    {
        // Where a stream token actually comes from. A <video>/hls.js request cannot set an
        // Authorization header, so the player puts the token in the query string instead — which
        // is no use unless something reads it back out, and until this handler existed nothing
        // did. Every playlist and segment fetch from a browser 401'd, which surfaces as
        // "Playback failed: manifestLoadError" on the replay stage.
        //
        // Only the "StreamToken" scheme looks at the query string: an access token must always
        // arrive in the header, so it can never be smuggled through a URL. The sockets do the
        // equivalent by hand with ?ticket= (see DashboardEndpoint).
        //
        // A token in a URL lands in server logs, proxy logs and browser history in a way a header
        // does not. That is the reason this one is minted short-lived and scoped to GET media
        // routes rather than being an access token — the design already assumed this trade, it
        // just was not wired up.
        OnMessageReceived = context =>
        {
            if (requireStreamScope)
            {
                string? queryToken = context.Request.Query["stream_token"];
                if (!string.IsNullOrEmpty(queryToken))
                {
                    context.Token = queryToken;
                }
            }

            return Task.CompletedTask;
        },

        OnTokenValidated = context =>
        {
            bool hasStreamScope = context.Principal
                ?.HasClaim(TokenService.ScopeClaimType, TokenService.StreamScope) ?? false;
            if (hasStreamScope != requireStreamScope)
            {
                context.Fail(requireStreamScope
                    ? "This route needs a stream token (POST /api/auth/stream-token), not an access token."
                    : "A stream token cannot be used on this route.");
            }

            return Task.CompletedTask;
        },
    };
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o => ConfigureJwtBearer(o, requireStreamScope: false))
    .AddJwtBearer("StreamToken", o => ConfigureJwtBearer(o, requireStreamScope: true));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireRole(nameof(Role.Admin)))
    // The 4 HLS routes in Media/MediaEndpoints.cs accept either credential: a normal Bearer call
    // (curl, desktop debugging) or the ?stream_token= a browser player attaches, since it cannot
    // set an Authorization header on a <video>/hls.js request. The query parameter is read by
    // OnMessageReceived on the "StreamToken" scheme above.
    .AddPolicy("MediaAccess", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "StreamToken")
        .RequireAuthenticatedUser())
    // Any endpoint added later without an explicit policy fails closed (401) rather than
    // accidentally shipping open.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Naive brute-force scripts stop here before they ever reach Mongo or the password hasher.
    // The per-account lockout in UserRepository is what catches a distributed or low-and-slow
    // attack this can't see.
    //
    // Partitioned by caller: an unpartitioned limiter is one bucket for the whole server, so the
    // ten attempts an attacker is allowed are ten nobody else gets — a single client could lock
    // the household out of its own cameras indefinitely, from one address, without a password.
    // The budget is per address so that spending it only costs the address that spent it.
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        // A null RemoteIpAddress means a transport that never had one (in-memory test host, a unix
        // socket). Those share one bucket rather than each getting their own, which is the safe
        // direction: unattributable requests must not each be handed a fresh budget.
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// Browser access for the App's web build. Named rather than default so the policy is applied
// explicitly below and is visible at the one place it takes effect.
const string AppCorsPolicy = "app";

/// The Google Home camera-stream signalling route, which a gstatic.com page calls directly.
const string GoogleSignalingCorsPolicy = "google-signaling";

builder.Services.AddCors(options => options.AddPolicy(AppCorsPolicy, policy =>
{
    string[] origins = [.. serverOptions.Cors.AllowedOrigins];

    // Any-origin and credentials are mutually exclusive in the spec, and nothing here sends a
    // cookie or an Authorization header — the telemetry key is a request header the App never
    // uses — so neither branch allows credentials and the two behave identically otherwise.
    if (origins.Length == 0)
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        policy.WithOrigins(origins);
    }

    policy.AllowAnyHeader().AllowAnyMethod();

    // AllowAnyHeader governs the *request*; a browser can read no response header beyond the
    // CORS-safelisted six unless it is named here. Without this the web build cannot see the
    // filename it is meant to save a clip under, nor learn that the clip was truncated at a
    // session boundary — both would fail silently, and only on web.
    policy.WithExposedHeaders(
        "Content-Disposition",
        "X-Serval-Clip-From",
        "X-Serval-Clip-To",
        "X-Serval-Clip-Truncated");
}));

// A second, narrow policy for the one route a *Google-hosted page* calls in a browser.
//
// Google's CameraStream signalling is not the server-to-server exchange it looks like: the player
// runs in a web view served from gstatic.com and fetches the signalling URL itself, so the browser
// applies CORS to the answer. Google's documentation asks for that exact origin, and the exactness
// is load-bearing — a credentialed fetch refuses a wildcard and requires the literal origin echoed
// back with Allow-Credentials. The app policy above deliberately never allows credentials, so
// leaving this route on it means the browser silently discards a perfectly good SDP answer: the
// request arrives, the server answers 200, setRemoteDescription is never called, no ICE is ever
// started, and every side of the system reports success while the picture never appears.
//
// Its own policy rather than a widened app policy, so that tightening Serval:Cors:AllowedOrigins —
// which every publicly reachable deployment should do — cannot break Google Home.
builder.Services.AddCors(options => options.AddPolicy(GoogleSignalingCorsPolicy, policy => policy

    // gstatic.com is where Google serves the player that does the signaling, and it is the one
    // origin this route exists for. It cannot be a wildcard: the fetch is credentialed, and a
    // credentialed fetch refuses "*" — which fails as an answer the browser silently discards,
    // with a 200 in every log on this side.
    //
    // The App's own configured origins are allowed alongside it so that this route is reachable
    // from the Serval UI too, on the same terms as every other API route. Without them a browser
    // on the operator's own origin is refused by a policy meant to constrain Google.
    .WithOrigins([.. serverOptions.Cors.AllowedOrigins, "https://www.gstatic.com"])
    .WithMethods("GET", "POST", "OPTIONS")
    .WithHeaders("content-type", "authorization")
    .AllowCredentials()));

builder.Services.AddOpenApi(options =>
{
    // The document is titled after the product rather than the assembly, since it is what the
    // Scalar UI puts at the top of the page.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Serval";
        document.Info.Description =
            "Camera registry, recorded and live media, telemetry, PTZ, and WebRTC signaling.";
        return Task.CompletedTask;
    });

    // Lets Scalar's UI (/scalar/v1) log in and call protected routes from the browser — see
    // BearerSecuritySchemeTransformer for why this doesn't also gate the document itself.
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

// One handler instead of a catch per endpoint: any ValidationException thrown under a request
// becomes a 400 with the message as the body. Everything else falls through to ProblemDetails.
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

foreach (string warning in startupWarnings)
{
    startupLogger.LogWarning("{Warning}", warning);
}

// Which transcode targets this host can actually reach, named up front. Without it, "why was my
// transcode rejected?" costs an ffmpeg invocation and a guess about which encoder the
// HwAccelDevice/Encoder precedence picked.
string transcodeTargets = string.Join(", ", EncoderSelector.SupportedCodecs
    .Order(StringComparer.Ordinal)
    .Select(codec =>
    {
        string encoder = EncoderSelector
            .Select(codec, ingest.HwAccelDevice, ingest.Encoder, ingest.Bitrate).EncoderName;
        return capabilities.CanEncodeVideo(encoder)
            ? $"{codec} ({encoder})"
            : $"{codec} (needs {encoder}, MISSING)";
    }));

startupLogger.LogInformation(
    "ffmpeg at '{FfmpegPath}': {EncoderCount} video encoders. Recorded untouched: {Passthrough}. "
    + "Transcode targets: {Targets}.",
    ingest.FfmpegPath,
    capabilities.VideoEncoders.Count,
    string.Join(", ", ingest.ResolvedVideoPassthroughCodecs),
    transcodeTargets);

// Ensure indexes exist before serving. A Mongo that's down fails startup loudly, which is the
// right behaviour — the server has nothing to do without it.
await app.Services.GetRequiredService<MongoContext>().InitializeAsync();

// Creates the first Admin account when none exists yet — see AdminBootstrap for why this can't
// wait for an authenticated request the way everything else does.
await AdminBootstrap.RunAsync(
    app.Services.GetRequiredService<UserRepository>(),
    app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Auth,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Serval.Server.Auth.Bootstrap"),
    CancellationToken.None);

// Report every camera that will not be ingested, and every one that will but not as intended,
// while an operator is still watching the boot.
await CameraRegistrySweep.RunAsync(
    app.Services.GetRequiredService<CameraRepository>(),
    app.Services.GetRequiredService<IOptions<ServerOptions>>().Value,
    app.Services.GetRequiredService<FfmpegCapabilities>(),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Serval.Server.Cameras.Registry"),
    CancellationToken.None);

// Not gated on Development: the Scalar UI is currently the only way to manage the camera registry
// from a browser, and the deployed container runs as Production. See Serval:OpenApi:Enabled.
if (app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.OpenApi.Enabled)
{
    // Anonymous by design (see BearerSecuritySchemeTransformer): the document and the Scalar UI
    // that renders it stay reachable without logging in, and Scalar's own "Authorize" button is
    // what lets it go on to call the (still-protected) routes it describes.
    app.MapOpenApi().AllowAnonymous();                                        // /openapi/v1.json
    app.MapScalarApiReference(options => options.Title = "Serval API").AllowAnonymous(); // /scalar/v1
}

app.UseExceptionHandler();

// The App's web build (see Dockerfile), if wwwroot holds one. Plain middleware, not an endpoint,
// so it runs unauthenticated and ahead of CORS/auth — same treatment as the OpenApi/Scalar routes
// below. A wwwroot-less image (plain `dotnet run`) just serves nothing here, harmlessly.
app.UseStaticFiles();

// Before UseWebSockets and the endpoints: the CORS middleware has to answer the preflight
// OPTIONS itself, which routing would otherwise turn into a 405.
app.UseCors(AppCorsPolicy);

// Auth after CORS (a preflight OPTIONS carries no credential and must never be challenged), and
// before everything else — WebSockets, rate limiting, and every endpoint all assume
// HttpContext.User is already populated.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseWebSockets();

app.MapGet("/api/health", () => Results.Ok(new { service = "serval-server", status = "ok" }))
    .WithTags("Service")
    .WithSummary("Liveness probe.")
    .AllowAnonymous();

app.MapAuthEndpoints();
app.MapCameraEndpoints();
app.MapMediaEndpoints();
app.MapClipEndpoints();
app.MapAlertEndpoints();
app.MapTelemetryEndpoints();
app.MapDashboardEndpoint();
app.MapLiveEventsEndpoint();
app.MapAudioLevelsEndpoint();
app.MapWebRtcEndpoints();
app.MapCastEndpoints();
app.MapPtzEndpoints();
app.MapOnvifEndpoints();
app.MapSystemEndpoints();
app.MapSettingsEndpoints();
app.MapPreferencesEndpoints();
app.MapPushEndpoints();
app.MapGoogleHomeEndpoints();
app.MapConfigBackupEndpoints();

// The App's web build, when wwwroot holds one (see Dockerfile) — every API route lives under
// /api, so any other unmatched GET is the SPA shell, which handles its own client-side routing
// and auth. AllowAnonymous because the fallback policy above requires a login for anything
// without an explicit policy, and the page has to load before the App can even show a login form.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can boot the app.</summary>
public partial class Program;
