using System.Globalization;
using System.Text.RegularExpressions;

namespace Serval.Ai;

/// <summary>How a label file numbers its classes.</summary>
public enum LabelFileFormat
{
    /// <summary>One name per line; the line's position is the class index.</summary>
    Positional,

    /// <summary>Every line is <c>index name</c>, as Coral's own <c>coco_labels.txt</c> is written.
    /// The stated index is the class index, so the file may skip numbers.</summary>
    Indexed,
}

/// <summary>
/// A detection model's class list, read so that a class index always lands on the name the model meant.
///
/// <para><b>This exists because dropping blank lines is silently catastrophic.</b> Reading a label file
/// as <c>ReadAllLines().Where(line.Length > 0)</c> — which is what the ONNX path did — shifts every
/// index after a gap up by one, so every class past the gap is renamed to its neighbour. Nothing
/// throws, no box is missing, and the only symptom is that the wrong words are attached to the right
/// rectangles, permanently and in stored history. The head this repo reads declares no class count, so
/// there is nothing to check the length against and no chance of catching it at load.</para>
///
/// <para>Two formats are recognised because both are in circulation: Ultralytics exports a positional
/// list, and Coral's model zoo ships an index-prefixed one. A COCO-90 labelmap padded with <c>???</c>
/// rows works under either, since those rows are not empty and so keep their positions.</para>
/// </summary>
public sealed partial class LabelFile
{
    /// <summary>An index this far out is a corrupt file rather than a sparse one.</summary>
    private const int IndexCeiling = 10_000;

    [GeneratedRegex(@"^\s*(\d+)\s+(\S.*)$")]
    private static partial Regex IndexedLine();

    private LabelFile(IReadOnlyList<string> labels, LabelFileFormat format, int placeholders)
    {
        Labels = labels;
        Format = format;
        PlaceholderCount = placeholders;
    }

    /// <summary>The class names, indexed as the model indexes them.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Which numbering the file turned out to use.</summary>
    public LabelFileFormat Format { get; }

    /// <summary>How many slots were filled in for gaps. Non-zero is not an error — a sparse labelmap is
    /// legitimate — but it is worth logging, because it is also what a truncated file looks like.
    /// </summary>
    public int PlaceholderCount { get; }

    /// <summary>Reads a label file from disk.</summary>
    /// <exception cref="FileNotFoundException">The file is not there.</exception>
    public static LabelFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Detection labels not found: {path}", path);
        }

        return Parse(File.ReadAllLines(path), path);
    }

    /// <summary>
    /// Parses label lines. Separate from <see cref="Load"/> so the rules can be tested without a file.
    /// </summary>
    /// <param name="lines">The file's lines, in order.</param>
    /// <param name="origin">What to name in an error message.</param>
    public static LabelFile Parse(IEnumerable<string> lines, string origin = "labels")
    {
        // Trailing blanks carry no positional information — there is no line after them whose index
        // they could shift — so they are dropped. Interior blanks are load-bearing and are kept.
        List<string> trimmed = [.. lines.Select(static line => line.Trim())];

        while (trimmed.Count > 0 && trimmed[^1].Length == 0)
        {
            trimmed.RemoveAt(trimmed.Count - 1);
        }

        if (trimmed.Count == 0)
        {
            throw new InvalidOperationException($"'{origin}' contains no labels.");
        }

        List<string> present = [.. trimmed.Where(static line => line.Length > 0)];

        // Indexed only when *every* content line states an index. One unprefixed line means the file is
        // positional and a number at the start of a name is just part of the name.
        bool indexed = present.Count > 0 && present.All(static line => IndexedLine().IsMatch(line));

        return indexed
            ? ParseIndexed(present, origin)
            : ParsePositional(trimmed, origin);
    }

    private static LabelFile ParsePositional(List<string> lines, string origin)
    {
        var labels = new string[lines.Count];
        int placeholders = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                // Named for its index rather than left empty, so a downstream string match cannot
                // accidentally succeed against "".
                labels[i] = Placeholder(i);
                placeholders++;
                continue;
            }

            labels[i] = lines[i];
        }

        if (placeholders == labels.Length)
        {
            throw new InvalidOperationException($"'{origin}' contains no labels.");
        }

        return new LabelFile(labels, LabelFileFormat.Positional, placeholders);
    }

    private static LabelFile ParseIndexed(List<string> lines, string origin)
    {
        var byIndex = new Dictionary<int, string>();

        foreach (string line in lines)
        {
            Match match = IndexedLine().Match(line);
            int index = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);

            if (index > IndexCeiling)
            {
                throw new InvalidOperationException(
                    $"'{origin}' states class index {index}, past the {IndexCeiling} ceiling; the file "
                    + "is more likely corrupt than that sparse.");
            }

            if (!byIndex.TryAdd(index, match.Groups[2].Value))
            {
                throw new InvalidOperationException(
                    $"'{origin}' states class index {index} twice ('{byIndex[index]}' and "
                    + $"'{match.Groups[2].Value}'); which one a detection means is undecidable.");
            }
        }

        int count = byIndex.Keys.Max() + 1;
        var labels = new string[count];
        int placeholders = 0;

        for (int i = 0; i < count; i++)
        {
            if (byIndex.TryGetValue(i, out string? name))
            {
                labels[i] = name;
                continue;
            }

            labels[i] = Placeholder(i);
            placeholders++;
        }

        return new LabelFile(labels, LabelFileFormat.Indexed, placeholders);
    }

    private static string Placeholder(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"class_{index}");
}
