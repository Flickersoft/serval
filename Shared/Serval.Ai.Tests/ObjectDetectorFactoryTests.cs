using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Choosing a device, and refusing to choose one that cannot work.
///
/// <para>Every assertion here runs on a machine with no Coral runtime, no ONNX Runtime provider and no
/// weights — which is the property the factory's ordering exists to give. A rejection that needed a
/// native library loaded to report itself could not be tested on a build agent, and would report the
/// wrong cause on a real host: "the delegate would not open" instead of "the model file is not there".
/// </para>
///
/// <para>The compatibility rule this also guards: <c>Device</c> defaults to <c>onnx-cpu</c>, so a host
/// that has never heard of an accelerator keeps behaving exactly as it did.</para>
/// </summary>
public class ObjectDetectorFactoryTests
{
    private static DetectionOptions Options(string device = "onnx-cpu") => new()
    {
        Device = device,
        ModelPath = "/nonexistent/model.onnx",
        LabelsPath = "/nonexistent/labels.txt",
    };

    [Fact]
    public void The_default_device_is_onnx_cpu_so_an_existing_host_is_unaffected()
    {
        Assert.Equal("onnx-cpu", new DetectionOptions().Device);
    }

    [Theory]
    [InlineData("hailo")]
    [InlineData("rocm")]
    [InlineData("auto")]
    [InlineData("cpu")]
    public void An_unknown_device_is_refused_and_names_the_valid_ones(string device)
    {
        // Refused rather than defaulted: an operator who wrote "coral" or "tpu" has a belief about what
        // is running, and silently giving them the CPU detector confirms it falsely. 'rocm' and 'auto'
        // are here because they were once accepted — a retired name has to fail loudly rather than
        // quietly meaning something else now.
        Assert.False(ObjectDetectorFactory.IsConfigured(Options(device), out string reason));

        Assert.Contains(device, reason);
        foreach (string valid in ObjectDetectorFactory.Devices)
        {
            Assert.Contains(valid, reason);
        }
    }

    [Fact]
    public void A_missing_labels_file_is_reported_before_any_model_is_considered()
    {
        // Labels first because every device needs them, and because a labels failure is the one whose
        // consequences are silent if it slips through.
        Assert.False(ObjectDetectorFactory.IsConfigured(Options(), out string reason));
        Assert.Contains("labels", reason);
    }

    [Fact]
    public void A_missing_model_names_the_file_and_the_device_that_wanted_it()
    {
        using var temporary = new TemporaryLabels();
        DetectionOptions options = Options();
        options.LabelsPath = temporary.Path;

        Assert.False(ObjectDetectorFactory.IsConfigured(options, out string reason));
        Assert.Contains("model.onnx", reason);
        Assert.Contains("onnx-cpu", reason);
    }

    [Fact]
    public void An_onnx_device_pointed_at_a_tflite_model_is_refused_by_the_file_not_its_name()
    {
        // The mistake one shared model path makes possible, and the reason the check reads the header
        // rather than the extension: the name is what somebody got wrong.
        using var temporary = new TemporaryLabels();
        using var model = new TemporaryTflite(".onnx");
        DetectionOptions options = Options();
        options.LabelsPath = temporary.Path;
        options.ModelPath = model.Path;

        Assert.False(ObjectDetectorFactory.IsConfigured(options, out string reason));
        Assert.Contains("TFLite", reason);
    }

    [Fact]
    public void The_edgetpu_device_pointed_at_onnx_weights_is_refused_and_says_what_it_needed()
    {
        using var temporary = new TemporaryLabels();
        using var model = new TemporaryFile(".tflite");
        DetectionOptions options = Options("tflite-edgetpu");
        options.LabelsPath = temporary.Path;
        options.ModelPath = model.Path;

        Assert.False(ObjectDetectorFactory.IsConfigured(options, out string reason));
        Assert.Contains("edgetpu_compiler", reason);
    }

    [Fact]
    public void A_matching_model_and_device_is_configured()
    {
        using var temporary = new TemporaryLabels();
        using var model = new TemporaryTflite(".tflite");
        DetectionOptions options = Options("tflite-edgetpu");
        options.LabelsPath = temporary.Path;
        options.ModelPath = model.Path;

        Assert.True(ObjectDetectorFactory.IsConfigured(options, out string reason));
        Assert.Equal("", reason);
    }

    [Theory]
    [InlineData("ONNX-CPU")]
    [InlineData("  onnx-cpu  ")]
    [InlineData("TFLite-EdgeTpu")]
    public void A_device_name_is_read_case_and_whitespace_insensitively(string device)
    {
        // These arrive from environment variables and a settings form, so casing and stray whitespace
        // are the operator's, not a mistake worth failing over.
        using var temporary = new TemporaryLabels();
        DetectionOptions options = Options(device);
        options.LabelsPath = temporary.Path;

        // Configured or not depends on the model file; what matters is that it was recognised, so the
        // reason never complains about the name itself.
        ObjectDetectorFactory.IsConfigured(options, out string reason);
        Assert.DoesNotContain("not one of", reason);
    }

    [Fact]
    public void An_empty_device_is_read_as_onnx_cpu()
    {
        // Configuration binding can produce an empty string where a value was cleared rather than unset.
        using var temporary = new TemporaryLabels();
        DetectionOptions options = Options("");
        options.LabelsPath = temporary.Path;

        Assert.False(ObjectDetectorFactory.IsConfigured(options, out string reason));
        Assert.Contains("onnx-cpu", reason);
    }

    [Fact]
    public void Counting_devices_never_throws_on_a_host_with_no_runtime()
    {
        // A CPU-only deployment stays supported, and the settings page asks this question of every host
        // to decide whether to offer tflite-edgetpu. On a machine with no libedgetpu the P/Invoke cannot
        // bind — which must be "no devices", never a crash.
        Assert.Equal(0, EdgeTpuObjectDetector.CountDevices(Options()));
    }

    [Fact]
    public void The_catalogued_devices_are_the_ones_the_factory_builds()
    {
        // The settings catalogue takes its choices from this list, so it cannot drift from the dispatch.
        Assert.Equal(
            ["onnx-cpu", "onnx-cuda", "onnx-openvino", "onnx-tensorrt", "tflite-edgetpu"],
            ObjectDetectorFactory.Devices);
    }

    [Fact]
    public void The_two_runtime_families_partition_the_devices()
    {
        // The catalogue's "applies only to these" rules are built from the families, so a device in
        // neither would silently be a device no tuning setting admits to belonging to.
        Assert.Empty(ObjectDetectorFactory.OnnxDevices.Intersect(ObjectDetectorFactory.TfliteDevices));
        Assert.Equal(
            [.. ObjectDetectorFactory.Devices.Order()],
            [.. ObjectDetectorFactory.OnnxDevices.Concat(ObjectDetectorFactory.TfliteDevices).Order()]);
    }

    [Theory]
    [InlineData("onnx-cpu", "cpu")]
    [InlineData("onnx-openvino", "openvino")]
    [InlineData("ONNX-TensorRT", "tensorrt")]
    public void The_execution_provider_is_the_half_after_the_runtime(string device, string expected)
    {
        // What OnnxObjectDetector is handed instead of reading a setting of its own.
        Assert.Equal(expected, ObjectDetectorFactory.Silicon(device));
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string extension)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + extension);
            File.WriteAllText(Path, "");
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    /// <summary>A file carrying the FlatBuffer identifier a real .tflite has at byte 4, which is what
    /// the factory sniffs for. Four bytes of root-offset padding first, as the format specifies.</summary>
    private sealed class TemporaryTflite : IDisposable
    {
        private readonly TemporaryFile _file;

        public TemporaryTflite(string extension)
        {
            _file = new TemporaryFile(extension);
            File.WriteAllBytes(_file.Path, [0x18, 0x00, 0x00, 0x00, (byte)'T', (byte)'F', (byte)'L', (byte)'3']);
        }

        public string Path => _file.Path;

        public void Dispose() => _file.Dispose();
    }

    private sealed class TemporaryLabels : IDisposable
    {
        private readonly TemporaryFile _file = new(".txt");

        public TemporaryLabels() => File.WriteAllLines(_file.Path, ["person", "car"]);

        public string Path => _file.Path;

        public void Dispose() => _file.Dispose();
    }
}
