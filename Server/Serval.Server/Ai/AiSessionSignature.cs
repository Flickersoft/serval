using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serval.Ai;

namespace Serval.Server.Ai;

/// <summary>
/// A digest of everything a camera's detection session reads <em>when it is built</em>, so the
/// coordinator can notice a change and restart it.
///
/// <para><b>Computed over the effective options, not the camera's overrides.</b> Every setting here
/// has two possible sources — a server-wide value and a per-camera override — and a session cannot
/// tell which it got. Digesting the merged result covers both with one rule: retuning one camera
/// restarts that camera, retuning the server restarts every camera the change reaches.</para>
///
/// <para><b>Serialized whole, with the exceptions listed, rather than field-by-field.</b> A new
/// dial added to any options class is digested automatically; a field a hand-written digest
/// forgets is a value that is stored, reported, and silently not in use until an unrelated
/// restart. The exception list is the part that still needs a human: anything a session does
/// <em>not</em> build — model paths, thread counts, execution providers, input sizes — belongs to
/// singletons loaded once at process start, and digesting those would tear down every camera's
/// session to arrive at exactly the same models.</para>
/// </summary>
internal static class AiSessionSignature
{
    /// <summary>
    /// The restart-gated fields — read once at process start, never by a session. The raw class
    /// and label lists are here too, for a different reason: their <c>Effective*</c> counterparts
    /// are what a session reads, and digesting both would restart a camera for moving between an
    /// empty list and the typed-out built-in set, which are the same instruction.
    /// </summary>
    private static readonly HashSet<(Type, string)> Excluded =
    [
        (typeof(DetectionOptions), nameof(DetectionOptions.Device)),
        (typeof(DetectionOptions), nameof(DetectionOptions.NativeLibraryPath)),
        (typeof(DetectionOptions), nameof(DetectionOptions.EdgeTpuLibraryDirectory)),
        (typeof(DetectionOptions), nameof(DetectionOptions.ModelPath)),
        (typeof(DetectionOptions), nameof(DetectionOptions.LabelsPath)),
        (typeof(DetectionOptions), nameof(DetectionOptions.InputPixels)),
        (typeof(DetectionOptions), nameof(DetectionOptions.NumThreads)),
        (typeof(DetectionOptions), nameof(DetectionOptions.MaxConcurrency)),
        (typeof(DetectionOptions), nameof(DetectionOptions.EffectiveConcurrency)),
        (typeof(DetectionOptions), nameof(DetectionOptions.Classes)),
        (typeof(DetectionOptions), nameof(DetectionOptions.DescribeClasses)),
        (typeof(DetectionOptions), nameof(DetectionOptions.AlertClasses)),
        (typeof(VadOptions), nameof(VadOptions.ModelPath)),
        (typeof(SoundOptions), nameof(SoundOptions.ModelPath)),
        (typeof(SoundOptions), nameof(SoundOptions.LabelsPath)),
        (typeof(SoundOptions), nameof(SoundOptions.NumThreads)),
        (typeof(SoundOptions), nameof(SoundOptions.Provider)),
        (typeof(SoundOptions), nameof(SoundOptions.AlertLabels)),
        (typeof(SpeakerOptions), nameof(SpeakerOptions.EmbeddingModelPath)),
        (typeof(SpeakerOptions), nameof(SpeakerOptions.SegmentationModelPath)),
        (typeof(SpeakerOptions), nameof(SpeakerOptions.ConversationAudioDirectory)),
        (typeof(SpeakerOptions), nameof(SpeakerOptions.NumThreads)),

        // A mask's name is for logs and the UI; only the geometry and the class filter affect
        // what the detector does, and a rename must not restart the session.
        (typeof(DetectionMask), nameof(DetectionMask.Name)),
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static info =>
                {
                    foreach (JsonPropertyInfo property in info.Properties)
                    {
                        if (Excluded.Contains((info.Type, property.Name)))
                        {
                            property.ShouldSerialize = static (_, _) => false;
                        }
                    }
                },
            },
        },
        Converters = { new SortedWords(), new SortedWordList() },
    };

    /// <summary>
    /// <see cref="AsrOptions"/> and <see cref="VisionOptions"/> are left out whole: both configure
    /// singletons shared across every camera, so nothing in them is a per-session read.
    /// </summary>
    public static string Of(AiOptions ai) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { ai.Detection, ai.Motion, ai.AudioGate, ai.Vad, ai.Sound, ai.Speaker },
            Json)));

    /// <summary>Word lists digest sorted, so the same words in a different order are correctly
    /// seen as no change rather than restarting a camera's session for a reordered list.</summary>
    private sealed class SortedWords : JsonConverter<string[]>
    {
        public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The signature only writes.");

        public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal));
    }

    /// <inheritdoc cref="SortedWords"/>
    private sealed class SortedWordList : JsonConverter<IReadOnlyList<string>>
    {
        public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The signature only writes.");

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal));
    }
}
