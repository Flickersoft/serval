namespace Serval.Ai;

/// <summary>
/// Every knob the shared detection library exposes, aggregated for a host that wants to bind the
/// lot from one config section (the Server binds <c>Serval:Ai</c>).
///
/// The implementations themselves never take this type — each takes only its own slice, so a
/// component cannot quietly start reading a setting that isn't its concern. The CameraModule
/// keeps these as flat properties on <c>CameraModuleOptions</c> so its existing
/// <c>CameraModule:Vad:*</c> style config paths are unchanged.
/// </summary>
public sealed class AiOptions
{
    public VadOptions Vad { get; set; } = new();
    public AudioGateOptions AudioGate { get; set; } = new();
    public AsrOptions Asr { get; set; } = new();
    public VisionOptions Vision { get; set; } = new();
    public MotionOptions Motion { get; set; } = new();
    public DetectionOptions Detection { get; set; } = new();
    public SpeakerOptions Speaker { get; set; } = new();
    public SoundOptions Sound { get; set; } = new();
}

/// <summary>
/// Non-speech sound tagging: AudioSet's 527 classes over a small zipformer.
///
/// This path is parallel to the VAD, not behind it. The speech path is gated on Silero, which
/// rejects everything that is not speech, so a car horn could never reach it. Here the only gate
/// is level — <see cref="Gate"/> — and whatever gets through is classified as-is.
/// </summary>
public sealed record SoundOptions
{
    /// <summary>
    /// Off by default. It is a 26 MB model and a second onnxruntime session per host, and a
    /// deployment that only wants transcription should not pay for either.
    /// </summary>
    public bool Enabled { get; set; }

    public string ModelPath { get; set; } =
        "models/sherpa-onnx-zipformer-small-audio-tagging-2024-04-15/model.int8.onnx";

    /// <summary>The model's own class list. Ships beside the weights; the two must match.</summary>
    public string LabelsPath { get; set; } =
        "models/sherpa-onnx-zipformer-small-audio-tagging-2024-04-15/class_labels_indices.csv";

    /// <summary>
    /// Deliberately below <see cref="AsrOptions.NumThreads"/>. What bites on the Pi is not this
    /// model's latency — it is a few tens of milliseconds — but contention with the vision model,
    /// which saturates every core for seconds at a time. Raising this trades a fast classification
    /// nobody is waiting for against dropped samples on the capture thread.
    /// </summary>
    public int NumThreads { get; set; } = 2;

    public string Provider { get; set; } = "cpu";

    /// <summary>How many scored labels to keep. The winner decides the record; the rest are stored
    /// so a threshold can be re-derived later from real recordings rather than guessed again.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Segments shorter than this are discarded — a single loud click classifies as noise.</summary>
    public double MinSegmentSeconds { get; set; } = 0.5;

    /// <summary>
    /// Hard cut for a sound that never stops. AudioSet models are trained on 10-second clips, and
    /// without this a running lawnmower would hold the gate open and never produce a record at all.
    /// </summary>
    public double MaxSegmentSeconds { get; set; } = 10.0;

    /// <summary>
    /// This path's own level gate. A separate instance from the VAD's, not a shared one:
    /// <see cref="AudioLevelGate"/> is stateful, and the two want different tuning anyway.
    /// </summary>
    public AudioGateOptions Gate { get; set; } = new()
    {
        // Longer than the VAD's, and for a different reason. There the hangover exists to feed
        // Silero the trailing silence it needs to declare an utterance over; here it defines the
        // tail of the clip being classified, and a bark cut to 0.3s is a bark the model has too
        // little of to place.
        HangoverSeconds = 1.5,

        // More than the VAD's 10, because a transient's attack is the most informative part of it.
        // 16 windows ≈ 512 ms at 16 kHz.
        PreRollWindows = 16,
    };

    /// <summary>
    /// Floor for an ordinary sound. Low on purpose: a wrong label here costs one row in a feed.
    /// </summary>
    public float MinConfidence { get; set; } = 0.35f;

    /// <summary>
    /// Floor for anything in <see cref="AlertLabels"/>, and higher than <see cref="MinConfidence"/>
    /// on purpose. These are not the same bet: a false "Dog" costs a feed row, a false
    /// "Gunshot, gunfire" costs trust in every alert after it. Expect to raise this after measuring
    /// against real recordings.
    /// </summary>
    public float AlertMinConfidence { get; set; } = 0.60f;

    /// <summary>
    /// Minimum gap between two records carrying the same label. Keyed per label, so a dog and a
    /// siren never silence each other.
    ///
    /// This is load-bearing rather than cosmetic. A noisy outdoor camera can hold the level gate
    /// open indefinitely, and then this is the only thing between the deployment and an outbox
    /// filling at one record per <see cref="MaxSegmentSeconds"/>, forever.
    /// </summary>
    public double CooldownSeconds { get; set; } = 60.0;

    /// <summary>Shorter for alerts: a repeated alarm is information, a repeated dog is not.</summary>
    public double AlertCooldownSeconds { get; set; } = 15.0;

    /// <summary>
    /// Labels to drop from the scored shortlist before picking a winner. Empty by default.
    ///
    /// Sound and speech are detected independently over the same audio and are expected to
    /// overlap — a conversation produces both an utterance and a "Speech" sound record, and that
    /// is not a bug. This exists only as an escape hatch if a particular deployment finds a label
    /// noisy. Note it filters the *shortlist*, not the segment, so adding "Speech" here still lets
    /// glass breaking behind a conversation through.
    /// </summary>
    public string[] IgnoredLabels { get; set; } = [];

    /// <summary>
    /// The labels used when <see cref="AlertLabels"/> is left unset.
    ///
    /// <para><b>Prefer the general class over the specific one.</b> AudioSet's labels are
    /// hierarchical and the parent usually scores higher, so a list naming only the specific one
    /// never matches: measured against the model's own test clips, two sirens came back "Siren" at
    /// 0.88 and 0.98 with "Civil defense siren" second at 0.74 and 0.82. Since the highest-scoring
    /// label is the one published, listing only the child would have let both pass silently.</para>
    /// </summary>
    public static readonly string[] DefaultAlertLabels =
    [
        "Glass",
        "Shatter",
        "Gunshot, gunfire",
        "Smoke detector, smoke alarm",
        "Fire alarm",
        "Alarm",
        "Screaming",
        "Car alarm",
        "Siren",
        "Civil defense siren",
    ];

    /// <summary>
    /// Labels that set <c>is_alert</c> on the published record, spelled exactly as the model spells
    /// them. Kept in configuration rather than code because which sounds matter is a property of
    /// the site, not of the software — and because this list should be pruned after measurement,
    /// not extended. Empty means <see cref="DefaultAlertLabels"/>; read
    /// <see cref="EffectiveAlertLabels"/> rather than this.
    ///
    /// <para>Empty rather than pre-populated, for the reason
    /// <see cref="DetectionOptions.Classes"/> gives: the binder appends to a list that already has
    /// entries, so a pre-populated default would make a settings overlay that names three labels
    /// produce thirteen — pruning this list, which is what the doc above asks for, would silently
    /// do nothing.</para>
    /// </summary>
    public string[] AlertLabels { get; set; } = [];

    /// <summary>The alert labels in force, resolving the unset case.</summary>
    public IReadOnlyList<string> EffectiveAlertLabels =>
        AlertLabels.Length > 0 ? AlertLabels : DefaultAlertLabels;

    /// <summary>
    /// A copy, for composing one camera's overrides. A plain <c>with</c> would alias
    /// <see cref="Gate"/> and the label arrays, letting one camera's override be written through
    /// into every other camera's — so the reference-typed members are cloned explicitly.
    /// </summary>
    public SoundOptions Copy() => this with
    {
        Gate = Gate with { },
        IgnoredLabels = [.. IgnoredLabels],
        AlertLabels = [.. AlertLabels],
    };
}

/// <summary>
/// Cheap RMS gate in front of the VAD. Silero is an ONNX forward pass on every 512-sample window;
/// in a quiet room that is almost all wasted, and on the Pi it competes with vision for cores.
/// </summary>
public sealed record AudioGateOptions
{
    /// <summary>On by default: it only ever saves work, and the pre-roll means it cannot clip an onset.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Window RMS below which audio is considered silence, measured *after*
    /// <c>AudioOptions.InputGain</c>. 0.01 is roughly -40 dBFS, comfortably under speech at a
    /// normal distance while still above a typical noise floor. Raise it if the VAD is being woken
    /// constantly by room tone; lower it if quiet speech is being missed.
    /// </summary>
    public float RmsThreshold { get; set; } = 0.01f;

    /// <summary>
    /// Windows retained while closed and replayed into the detector on open, so the attack of the
    /// first word is never cut. 10 windows ≈ 320 ms at 16 kHz, which covers a soft onset.
    /// </summary>
    public int PreRollWindows { get; set; } = 10;

    /// <summary>
    /// How long the gate stays open after the last loud window. This is what makes skipping windows
    /// safe: the gate only ever closes well after speech has ended, so the detector is never cut
    /// off mid-utterance and never resumes mid-word.
    /// </summary>
    public double HangoverSeconds { get; set; } = 1.0;

}

/// <summary>
/// Frame-difference motion gate in front of the vision model. A description costs seconds of CPU
/// (tens on the Pi), so it must not run on a still scene.
/// </summary>
public sealed record MotionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Frames are compared at this size, not their capture size. Motion is a low-frequency signal:
    /// downscaling costs almost nothing, removes sensor noise for free, and makes the comparison
    /// independent of the camera's resolution.
    /// </summary>
    public int CompareWidth { get; set; } = 64;

    public int CompareHeight { get; set; } = 48;

    /// <summary>Per-pixel luma difference counted as "changed". Below this is sensor noise and JPEG ringing.</summary>
    public int PixelDelta { get; set; } = 25;

    /// <summary>Fraction of changed pixels at or above which motion is declared.</summary>
    public double MinChangedFraction { get; set; } = 0.02;

    /// <summary>
    /// Upper bound, and not a redundant one: when nearly every pixel changes at once it is almost
    /// never motion. It is the IR-cut filter flipping to night mode, a light being switched on, or
    /// auto-exposure hunting — all of which would otherwise trigger a description of a scene that
    /// did not actually change.
    /// </summary>
    public double MaxChangedFraction { get; set; } = 0.5;

}

/// <summary>
/// Object detection, which is the other thing that can decide whether the vision model runs.
///
/// It answers a different question than <see cref="MotionOptions"/> does, and the difference is
/// the point. A frame difference reports *change*; a detector reports *state*. Only the second can
/// support "a person has been at the door for 40 seconds", because on a change-driven sample
/// "nothing is there" and "nothing was looked at" are the same observation. It also sees the case
/// frame differencing structurally cannot: something that is present but not moving.
///
/// Off by default, so a host that does not configure it keeps the motion gate it has today.
/// </summary>
public sealed record DetectionOptions
{
    /// <summary>
    /// When on, this replaces <see cref="MotionOptions"/> as the gate in front of the vision model
    /// rather than sitting behind it. Both are not run: a pre-filter tuned loosely enough not to
    /// miss a distant figure admits nearly every frame of an outdoor camera at night anyway, so it
    /// would cost the blind spots without reliably buying the compute.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What runs detection, as one <c>runtime-device</c> name (<c>onnx-cpu</c>,
    /// <c>tflite-edgetpu</c>, ...). The runtime prefix decides how <see cref="ModelPath"/> is
    /// read; <see cref="ObjectDetectorFactory"/> maps the name to an implementation and carries
    /// the rationale for the naming and the fallback rules.
    /// </summary>
    public string Device { get; set; } = "onnx-cpu";

    /// <summary>
    /// An explicit <c>libonnxruntime.so</c> to load instead of the one beside the app. Null means
    /// default probing, which is what finds the runtime the build already ships.
    ///
    /// This exists so the execution provider is a property of the *image* rather than of the
    /// package graph: a GPU provider is a different native library at the same managed API, and
    /// pinning it here keeps the arm64 CameraModule's restore graph out of it entirely.
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>
    /// The weights. One path for both runtimes: <see cref="Device"/>'s prefix says how to read it,
    /// and <see cref="ObjectDetectorFactory"/> checks the file against the device before either
    /// detector opens it.
    /// </summary>
    public string ModelPath { get; set; } = "models/detect/model.onnx";

    /// <summary>
    /// Directory holding <c>libedgetpu.so.1</c> and <c>libtensorflowlite_c.so</c>. Null means default
    /// probing, which finds them when the image puts them on the loader path.
    ///
    /// <para>Deliberately not in the settings catalogue, following <see cref="NativeLibraryPath"/>: a
    /// native library's location is a property of the *image*, not a choice an operator makes in a
    /// form. Two libraries and not one because libedgetpu is only a TFLite <em>delegate</em> — the
    /// model loading, tensor access and Invoke are all the TFLite C API.</para>
    /// </summary>
    public string? EdgeTpuLibraryDirectory { get; set; }

    /// <summary>The model's own class list, one label per line, or <c>index name</c> per line as
    /// Coral's own labelmaps are written — <see cref="LabelFile"/> reads both. Ships beside the
    /// weights, and the two must match: the heads this repo reads declare no class count, so nothing
    /// checks this at load and a file that disagrees becomes confidently wrong labels forever. Only a
    /// class index past the end of the file is noticed, as a warning on the first frame that decodes
    /// one.
    ///
    /// <para><b>Indices are not portable between vocabularies.</b> A COCO-90 labelmap puts <c>cat</c>
    /// at 16 where COCO-80 puts it at 15, so a labels file from the wrong family renames most of the
    /// vocabulary without changing a single box.</para></summary>
    public string LabelsPath { get; set; } = "models/detect/labels.txt";

    /// <summary>
    /// How many pixels one inference may cost, for a model exported with dynamic axes — each
    /// camera gets the stride-32 rectangle nearest its own aspect at about this many pixels (see
    /// <see cref="DetectorShapes"/>). A fixed-shape export ignores this: the shape is in the file,
    /// and the startup line prints what the model reported rather than this.
    /// </summary>
    public int InputPixels { get; set; } = 640 * 384;

    /// <summary>
    /// Confidence floor applied by the detector itself, as a coarse filter on what it bothers to
    /// return. A camera that wants to be *stricter* raises its own floor in its detection tuning;
    /// nothing downstream can go below this one, so keep it permissive.
    /// </summary>
    public float ScoreThreshold { get; set; } = 0.25f;

    /// <summary>
    /// Smallest a detection may be, as a fraction of the frame's *area*, before it is dropped
    /// whatever the model claimed about it — the one filter confidence cannot express, since
    /// magnifying an artefact in a region crop makes the model <em>more</em> sure of it. Zero by
    /// default, meaning no floor.
    /// </summary>
    public double MinObjectFraction { get; set; }

    /// <summary>
    /// The classes used when <see cref="Classes"/> is left unset. COCO's 80 include a great many —
    /// toasters, giraffes — that a security camera reporting would only be reporting a mistake.
    /// </summary>
    public static readonly string[] DefaultClasses =
        ["person", "bicycle", "car", "motorcycle", "bus", "truck", "cat", "dog"];

    /// <summary>Used when <see cref="DescribeClasses"/> is unset.</summary>
    public static readonly string[] DefaultDescribeClasses = ["person"];

    /// <summary>Used when <see cref="AlertClasses"/> is unset.</summary>
    public static readonly string[] DefaultAlertClasses = ["person"];

    /// <summary>
    /// Which of the model's classes are worth acting on, as the default for every camera. Empty
    /// means <see cref="DefaultClasses"/>; read <see cref="EffectiveClasses"/> rather than this.
    ///
    /// <para><b>Empty rather than pre-populated, and that is load-bearing.</b> Configuration
    /// binding <em>appends</em> to a list that already has entries, so a default here would make
    /// <c>Serval__Ai__Detection__Classes__0=person</c> produce the eight defaults <em>plus</em>
    /// person — a setting that can only ever add, doing the opposite of what an operator narrowing
    /// a list intends, silently. The Cors origins list is left undeclared for the same reason.</para>
    /// </summary>
    public string[] Classes { get; set; } = [];

    /// <summary>The classes in force, resolving the unset case.</summary>
    public IReadOnlyList<string> EffectiveClasses =>
        Classes.Length > 0 ? Classes : DefaultClasses;

    /// <summary>
    /// How long an object must be *out of sight* before its episode is closed. The outer of two
    /// absence windows: <see cref="TrackingOptions.CoastSeconds"/> is how long a position keeps
    /// being predicted, this is how long the record waits before saying the object left, and
    /// between the two the episode has no position at all — see <see cref="ObjectEventPolicy"/>.
    /// </summary>
    public double AbsenceSeconds { get; set; } = 30.0;

    /// <summary>
    /// How long something must have been *absent* before it turning up counts as an arrival —
    /// what separates an event from the furniture. Asked of the object rather than its class, and
    /// it bounds how long <see cref="ObjectEventPolicy"/> remembers where an episode ended, since
    /// that memory exists only to answer this question.
    /// </summary>
    public double NoveltySeconds { get; set; } = 120.0;

    /// <summary>
    /// Hard cut for something that never leaves, for the same reason
    /// <see cref="SoundOptions.MaxSegmentSeconds"/> exists. A car parked in view is genuinely
    /// present for days, and without this it would be one episode that never closes and therefore
    /// never becomes a complete record of anything.
    ///
    /// An hour rather than minutes because a cut is pure bookkeeping — the continuation it opens is
    /// never an arrival and never asks for a description. At five minutes, a car that has not moved
    /// all evening produces a record every five minutes.
    /// </summary>
    public double MaxEpisodeSeconds { get; set; } = 3600.0;

    /// <summary>
    /// Ceiling on how often any one camera is examined, regardless of how fast frames arrive.
    /// Zero by default, meaning no ceiling at all — <c>Ingest:DetectFps</c> decides. Below about
    /// 2 fps the tracker stops associating boxes, so a lower ceiling silently fragments tracks;
    /// see <see cref="ExamineRateGate"/> for the equality edge.
    /// </summary>
    public double MaxFps { get; set; }

    /// <summary>
    /// Threads per inference. Kept low: a model this small parallelises poorly within one
    /// inference, and <see cref="MaxConcurrency"/> is the lever that actually scales.
    /// </summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>
    /// How many detections may run at once, each on its own session — the lever that actually
    /// scales, since ORT's intra-op pool belongs to the session and concurrent calls into one
    /// contend for the same threads. Zero derives it from the host
    /// (<c>ProcessorCount / NumThreads</c>, capped at 4 for memory: each session holds its own
    /// weights and arena). The EdgeTPU backend ignores this and reports one lane per device.
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>How many detections will actually run at once, resolving the auto case.</summary>
    public int EffectiveConcurrency => MaxConcurrency > 0
        ? MaxConcurrency
        : Math.Clamp(Environment.ProcessorCount / Math.Max(NumThreads, 1), 1, 4);

    /// <summary>
    /// Which classes are worth waking the vision model for. Empty means every class in
    /// <see cref="Classes"/>.
    ///
    /// Separate from <see cref="Classes"/> because detecting and describing are different bets.
    /// Knowing a car has been on the driveway since 18:00 is worth recording; spending seconds of
    /// inference to be told about it is not. This is the list that decides what a description is
    /// spent on, and it should be shorter than the one that decides what is written down.
    ///
    /// <para>Empty rather than pre-populated, for the reason <see cref="Classes"/> gives.</para>
    /// </summary>
    public string[] DescribeClasses { get; set; } = [];

    /// <summary>The describable classes in force, resolving the unset case.</summary>
    public IReadOnlyList<string> EffectiveDescribeClasses =>
        DescribeClasses.Length > 0 ? DescribeClasses : DefaultDescribeClasses;

    /// <summary>
    /// How fast a detection's centre must be travelling to count as moving, as a fraction of the
    /// frame <em>per second</em>.
    ///
    /// Movement is the other reason to describe something, and it catches what arrival cannot: a
    /// car that has been parked since before the camera started watching, and then drives off, was
    /// never an arrival but is certainly an event. It is also what keeps furniture quiet — a couch
    /// scores zero here forever.
    ///
    /// Boxes jitter by a percent or so on a static object at this resolution, so this sits above
    /// that noise floor rather than at zero.
    ///
    /// <para><b>Per second, not per frame</b> — the difference between a speed and an accident of
    /// the detect rate. Measured per frame, the same subject at the same speed crosses half as much
    /// ground at 2 fps as at 1, so raising the rate would silently stop movement being reported.</para>
    /// </summary>
    public double MinMovementFraction { get; set; } = 0.02;

    /// <summary>
    /// How far any edge of a box must shift, as a fraction of the frame, before the episode's
    /// track records a new sample.
    ///
    /// Its own setting rather than a second use of <see cref="MinMovementFraction"/>, which gates
    /// seconds of vision inference: raising that one to quiet a busy describer would silently
    /// coarsen every overlay, and the two want opposite defaults for the same view.
    ///
    /// Edges rather than the centre, which is where the movement gate can afford to differ. Someone
    /// walking straight at the camera holds their centre still while their box doubles, and a
    /// centre test would replay them frozen at the size they arrived.
    /// </summary>
    public double TrackMinMovementFraction { get; set; } = 0.01;

    /// <summary>
    /// How many samples one episode's track may hold. Past it, every other sample is dropped,
    /// rather than the track being truncated and the rest of the episode replaying without a box.
    /// Repeated passes leave the oldest stretches coarsest and the most recent minutes detailed.
    ///
    /// Run-length encoding means this is only reached by something that moves continuously for a
    /// very long time, so the cap is a bound on the pathological case rather than a budget the
    /// ordinary one spends.
    /// </summary>
    public int TrackMaxSamples { get; set; } = 300;

    /// <summary>
    /// Minimum gap between descriptions requested for the same class, keyed per class so a person
    /// and a car never silence each other. The same guard <see cref="SoundOptions.CooldownSeconds"/>
    /// provides, and load-bearing for the same reason: without it, one person walking across the
    /// view for a minute is a description every second.
    /// </summary>
    public double DescribeCooldownSeconds { get; set; } = 60.0;

    /// <summary>
    /// Regions of this camera's view to ignore, as polygons in normalised coordinates.
    ///
    /// No confidence threshold separates a public road past the driveway from the drive itself,
    /// because the detector is right about both. Only geometry does.
    ///
    /// Really a per-camera setting; a value here is the default for cameras that specify none.
    /// </summary>
    public DetectionMask[] Masks { get; set; } = [];

    /// <summary>Classes worth raising an alert for, held to <see cref="AlertMinConfidence"/>
    /// instead of the ordinary floor. Empty rather than pre-populated, for the reason
    /// <see cref="Classes"/> gives.</summary>
    public string[] AlertClasses { get; set; } = [];

    /// <summary>The alert classes in force, resolving the unset case.</summary>
    public IReadOnlyList<string> EffectiveAlertClasses =>
        AlertClasses.Length > 0 ? AlertClasses : DefaultAlertClasses;

    /// <summary>
    /// Deliberately above the ordinary floor, following the position
    /// <see cref="SoundOptions.AlertMinConfidence"/> takes: a false alert costs trust in every
    /// alert after it, so the bar for claiming one is higher than the bar for recording one.
    /// </summary>
    public float AlertMinConfidence { get; set; } = 0.6f;

    /// <summary>
    /// A copy, for composing one camera's overrides. A plain <c>with</c> would alias the class
    /// lists, the masks and the nested options, letting a per-camera allowlist be written through
    /// into every other camera's — so the reference-typed members are cloned explicitly.
    /// </summary>
    public DetectionOptions Copy() => this with
    {
        Classes = [.. Classes],
        DescribeClasses = [.. DescribeClasses],
        Masks = [.. Masks],
        AlertClasses = [.. AlertClasses],
        Regions = Regions with { },
        Tracking = Tracking with { },
    };

    /// <summary>Where in the frame the detector is pointed, and how often at all of it.</summary>
    public RegionOptions Regions { get; set; } = new();

    /// <summary>How boxes are followed from frame to frame into objects with identity.</summary>
    public TrackingOptions Tracking { get; set; } = new();
}

/// <summary>Whether crops are cut around motion, or the whole frame is examined each time.</summary>
public enum RegionMode
{
    /// <summary>Decide from the ratio of frame size to model input — see
    /// <see cref="RegionOptions.Mode"/>.</summary>
    Auto,

    Off,

    On,
}

/// <summary>
/// Region proposal: cutting crops around motion and around what is already being tracked, instead of
/// shrinking the whole frame into the model every time.
///
/// <para>It buys recall rather than speed, and it is not a motion gate — frames are examined on a
/// fixed interval whatever motion says, see <see cref="FloorSeconds"/>. Both arguments, with the
/// ratios that decide when crops pay, are in <c>Docs/detection.md</c> under *Where the detector
/// looks*.</para>
/// </summary>
public sealed record RegionOptions
{
    /// <summary>
    /// Whether to cut crops at all.
    ///
    /// <para><see cref="RegionMode.Auto"/> decides from one ratio, and it is the only thing that
    /// matters: how much larger a distant subject arrives in a native-resolution crop than in a
    /// shrunk whole frame. At 1280 into 320 it is 4x and clearly worth it; at 720 into 640 it is
    /// 1.1x and worth nothing, and the deployment should not pay the per-camera tuning that regions
    /// bring. The resolved answer is logged with its reason, because the alternative is a setting
    /// that silently stops earning its keep the day somebody lowers
    /// <see cref="DetectionOptions.InputPixels"/>.</para>
    /// </summary>
    public RegionMode Mode { get; set; } = RegionMode.Auto;

    /// <summary>The ratio at or above which <see cref="RegionMode.Auto"/> turns regions on.</summary>
    public double AutoMinRatio { get; set; } = 1.5;

    /// <summary>
    /// Whether the whole-frame pass is made as a sweep of native-scale tiles instead of one shrunken
    /// look.
    ///
    /// <para><b>Off by default, and independent of <see cref="Mode"/> on purpose.</b> Region cropping
    /// decides where to look *opportunistically*; this changes how the coverage guarantee itself is
    /// made, which is a much bigger behavioural change and not one that should arrive as a side effect
    /// of anything. A working deployment must be able to take an accelerator's code without its
    /// detection behaviour moving.</para>
    ///
    /// <para><b>What it is for.</b> A backend with one compiled input shape gives every camera the same
    /// one, so a 32:9 panoramic is squeezed to a fraction of its scale — the floor covers every pixel at
    /// a scale that has already discarded the far field. Covering is not examining. Tiles examine the
    /// same area at the scale the picture has.</para>
    ///
    /// <para><b>What it costs.</b> Covering a wide frame at native scale is several inferences where the
    /// squeeze was one, and those are reserved rather than discretionary — so they come out of the
    /// host's headroom before motion crops do. That is the right priority for a guarantee, and it is why
    /// this wants an accelerator behind it rather than four CPU cores.</para>
    /// </summary>
    public bool TiledFloor { get; set; }

    /// <summary>
    /// How badly the whole frame has to be shrunk before <see cref="TiledFloor"/> actually tiles.
    ///
    /// The same <see cref="Gain"/> ratio <see cref="RegionMode.Auto"/> uses, so a camera whose frame
    /// already arrives at close to native scale keeps the single cheap floor pass it does not need
    /// replacing. Two rather than the 1.5 that turns cropping on: cropping is nearly free and tiling
    /// is not.
    /// </summary>
    public double TiledFloorMinGain { get; set; } = 2.0;

    /// <summary>
    /// Fraction of a tile that neighbouring tiles share.
    ///
    /// <para><b>Not optional and not a tuning nicety.</b> An object lying across a tile boundary is cut
    /// in two and detected as neither half, so this has to exceed the largest thing worth finding as a
    /// share of a tile. A fifth covers a person or a car in a tile the size of a detector input; a
    /// camera watching something larger, closer, wants more.</para>
    /// </summary>
    public double TileOverlapFraction { get; set; } = 0.2;

    /// <summary>
    /// How often the whole frame is examined regardless of motion or tracks.
    ///
    /// The floor that keeps presence honest. It catches what a pure motion gate cannot — something
    /// arriving while the gate is blind, during an IR-cut flip, and then staying still — and it is
    /// how a restarted camera finds the car already parked in front of it. Five seconds costs a
    /// fifth of an inference per second per camera.
    /// </summary>
    public double FloorSeconds { get; set; } = 5.0;

    /// <summary>
    /// Crops examined in one frame, at most.
    ///
    /// What makes the per-frame cost predictable: without it a windy hedge proposes a region per
    /// gust, and the inference budget for every other camera goes with it.
    /// </summary>
    public int MaxPerFrame { get; set; } = 3;

    /// <summary>
    /// Changed cells a cluster needs before it is worth a crop. Below this it is sensor noise, a
    /// leaf, or rain.
    /// </summary>
    public int MinCells { get; set; } = 4;

    /// <summary>
    /// How much of the frame is added around a cluster on each side.
    ///
    /// The compare grid is coarse and an object's edges routinely sit outside the cells that
    /// actually changed — a walking person's legs move while their torso does not. Without padding
    /// the crop is of a waist.
    /// </summary>
    public double PaddingFraction { get; set; } = 0.04;

    /// <summary>
    /// The smallest a crop may be, as a fraction of the frame's own width and height.
    ///
    /// <para><b>Measured, not guessed.</b> A 200x200 crop taken tightly around a landscaper beside a
    /// truck — native 4K, subject a comfortable 53 pixels tall — returned <em>nothing at all</em>,
    /// not even the truck filling half of it. The same scene in a 640-pixel crop found the truck at
    /// 0.84. A detector needs the whole of an object and some of its surroundings; a crop tight
    /// enough to cut a vehicle in half destroys more evidence than the magnification recovers.</para>
    ///
    /// <para>So a cluster smaller than this is grown around its own centre rather than cropped to
    /// its bounds. A quarter of the frame still magnifies a distant subject four times over.</para>
    /// </summary>
    public double MinSizeFraction { get; set; } = 0.25;

    /// <summary>
    /// The least a region may be shrunk to reach the detector's input, as a fraction of the
    /// frame's own pixels — the ceiling <see cref="MinSizeFraction"/> is the floor of. Below it a
    /// small detector does not merely miss things, it invents them; the default is the guard
    /// against the merge-chain runaway on <see cref="MotionRegions.AddMerged"/>, and the measured
    /// per-model boundaries are in <c>Docs/detection.md</c>.
    /// </summary>
    public double MinRegionScale { get; set; } = 0.5;

    /// <summary>
    /// Whether crops are cut, for a given frame and the input shape that frame resolves to.
    ///
    /// <para>Takes both shapes rather than reading them from settings, because the decision belongs
    /// to the pair and neither half means anything alone.</para>
    ///
    /// <para><b>The gain is whichever axis is squeezed hardest</b>, not the width. Both shapes now
    /// vary in aspect, and a portrait camera's width says nothing useful about either: a 480x640
    /// doorbell into a 416x576 input is 1.15x across and 1.11x down, while the same frame into a
    /// landscape 640x384 is 0.75x across and 1.67x down. Reading width alone calls the second one
    /// "no magnification available" and declines to crop the very camera being squeezed to 60%.</para>
    /// </summary>
    public bool ShouldCrop(int frameWidth, int frameHeight, DetectorInput input) => Mode switch
    {
        RegionMode.On => true,
        RegionMode.Off => false,
        _ => Gain(frameWidth, frameHeight, input) >= AutoMinRatio,
    };

    /// <summary>
    /// Whether this camera's whole-frame pass should be a tile sweep rather than one shrunken look.
    ///
    /// <para>Two conditions, both required: the operator asked for it, and this camera is actually being
    /// squeezed enough to be worth it. The second is what keeps a 16:9 stream that already arrives at
    /// native scale on the single cheap floor pass it does not need replacing.</para>
    /// </summary>
    public bool ShouldTileFloor(int frameWidth, int frameHeight, DetectorInput input) =>
        TiledFloor && Gain(frameWidth, frameHeight, input) >= TiledFloorMinGain;

    /// <summary>
    /// How much larger a subject arrives in a native-resolution crop than in the whole frame shrunk
    /// to fit — the ratio <see cref="RegionMode.Auto"/> decides on, and what the startup line reports.
    ///
    /// The reciprocal of the whole-frame fit: a frame fitted at 0.6 scale gives back 1/0.6 = 1.67x
    /// by cropping instead. Expressed once here so the decision and the log cannot drift apart.
    /// </summary>
    public static double Gain(int frameWidth, int frameHeight, DetectorInput input)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || input.Width <= 0 || input.Height <= 0)
        {
            return 0;
        }

        double fit = Math.Min(
            (double)input.Width / frameWidth, (double)input.Height / frameHeight);

        return fit > 0 ? 1.0 / fit : 0;
    }

    /// <summary>
    /// The largest region that can still reach this input at <see cref="MinRegionScale"/>, in frame
    /// pixels.
    ///
    /// <para>Resolved here, from the pair, for the reason <see cref="ShouldCrop"/> is: the answer means
    /// nothing without both halves, and <see cref="RegionPlanner"/> is deliberately given resolved
    /// answers rather than a detector to interrogate.</para>
    ///
    /// <para>Both axes, because a region is fitted by whichever is squeezed hardest and a bound on one
    /// alone would leave the other free to grow.</para>
    /// </summary>
    /// <returns>The bound, or null when it cannot apply — no input, or a scale of zero, which is how an
    /// operator switches this off.</returns>
    public (int Width, int Height)? MaxRegion(DetectorInput input)
    {
        if (MinRegionScale <= 0 || input.Width <= 0 || input.Height <= 0)
        {
            return null;
        }

        return (
            Math.Max(input.Width, (int)(input.Width / MinRegionScale)),
            Math.Max(input.Height, (int)(input.Height / MinRegionScale)));
    }
}

/// <summary>
/// How <see cref="ObjectTracker"/> decides that two boxes in two frames are the same object.
///
/// <para><b>Everything is in seconds or in frame fractions, and nothing is in frames or pixels.</b>
/// Tuned in frame counts, a tracker is retuned by any change to the detect rate without anybody
/// touching it; tuned in pixels, it means something different on every camera.</para>
///
/// <para>The defaults are for 2 fps, and hold unchanged at 5 fps on an accelerator. They are unusable
/// at 1 fps, where boxes move too far between frames to overlap at all and no setting rescues it —
/// which makes 2 fps a floor on the tracker's terms rather than a budget, so a host that cannot hold
/// it wants a cheaper <see cref="DetectionOptions.InputPixels"/> or an accelerator rather than a
/// lower rate.</para>
/// </summary>
public sealed record TrackingOptions
{
    /// <summary>
    /// How much a box must overlap a track's prediction to be considered the same object.
    ///
    /// <para>Low on purpose. This is overlap against a *predicted* position, not a previous one, so
    /// a well-tracked subject scores high and the cases sitting near the floor are the ones where
    /// the prediction is poor — a subject that just changed direction, or one whose box the detector
    /// resized. Raising it fragments those into new tracks; lowering it starts claiming that two
    /// subjects passing each other are one.</para>
    ///
    /// <para>It does a second job at a much longer range: <see cref="ObjectEventPolicy"/> uses it to
    /// decide whether a track it has never seen before is an object it is already writing an episode
    /// about, coming back. Raising it there means a flickering distant object gets a fresh episode
    /// every time it drops out.</para>
    /// </summary>
    public float MinIou { get; set; } = 0.2f;

    /// <summary>
    /// How long a new object must be seen before it is believed, on top of needing at least two
    /// separate sightings.
    ///
    /// <para>The gate on the confident one-frame ghost — an IR-lit bush read as a person — which is
    /// a small detector's dominant failure. A ghost rarely repeats in the same place while a real
    /// subject stays put, so the cheapest way to remove nearly all of them is to insist on being
    /// shown twice.</para>
    ///
    /// <para>In seconds, so raising the frame rate leaves confirmation latency alone and raises the
    /// evidence behind it — at 1 s a subject needs two sightings at 2 fps and six at 5 fps. Counted
    /// in frames instead, the same change would quietly cut the delay to a fifth and let a ghost
    /// through on far less.</para>
    /// </summary>
    public double ConfirmSeconds { get; set; } = 1.0;

    /// <summary>
    /// How long a confirmed track survives without being matched.
    ///
    /// <para>What carries a subject behind a parked car, through a frame the detector missed, and
    /// across a region plan that did not happen to look there. Too short and one occlusion becomes
    /// two episodes; too long and a subject that has genuinely left is still claimed to be present,
    /// which is the more damaging error because an episode's duration is what gets stored.</para>
    /// </summary>
    public double CoastSeconds { get; set; } = 2.0;

    /// <summary>
    /// How much the estimate is allowed to drift per second when nothing is measured, as a fraction
    /// of the frame.
    ///
    /// <para>The dial between trusting the motion model and trusting the detector. Raised, the
    /// filter follows a subject that changes speed and jitters with the detector's own box noise;
    /// lowered, it smooths hard and lags anything that turns.</para>
    /// </summary>
    public float ProcessNoise { get; set; } = 0.05f;

    /// <summary>
    /// How much a single box is doubted, as a fraction of the frame.
    ///
    /// <para>Detector boxes on a real subject wobble by a few percent between frames with nothing
    /// actually moving, and this is what keeps that out of the velocity estimate.</para>
    /// </summary>
    public float MeasurementNoise { get; set; } = 0.02f;

    /// <summary>
    /// A ceiling on live tracks per camera, so a pathological frame cannot grow the list without
    /// bound. Not a tuning dial: a scene genuinely holding this many objects has other problems.
    /// </summary>
    public int MaxTracks { get; set; } = 64;

}
