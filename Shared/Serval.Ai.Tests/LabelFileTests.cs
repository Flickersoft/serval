using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Reading a model's class list so an index lands on the name the model meant.
///
/// The failure this guards is the quietest in the whole detection path. Drop a blank line and every
/// index after it shifts by one, so every class past the gap is renamed to its neighbour — boxes are
/// still found, nothing throws, and the wrong words go into append-only storage forever. The head
/// declares no class count, so nothing downstream can notice.
/// </summary>
public class LabelFileTests
{
    [Fact]
    public void A_blank_line_keeps_the_index_of_every_label_after_it()
    {
        // The bug. Dropping the gap would make index 2 "car" instead of "bicycle" and index 3 "dog"
        // instead of "car", quietly renaming most of the vocabulary.
        LabelFile labels = LabelFile.Parse(["person", "bicycle", "", "car", "dog"]);

        Assert.Equal(LabelFileFormat.Positional, labels.Format);
        Assert.Equal(5, labels.Labels.Count);
        Assert.Equal("person", labels.Labels[0]);
        Assert.Equal("bicycle", labels.Labels[1]);
        Assert.Equal("class_2", labels.Labels[2]);
        Assert.Equal("car", labels.Labels[3]);
        Assert.Equal("dog", labels.Labels[4]);
        Assert.Equal(1, labels.PlaceholderCount);
    }

    [Fact]
    public void A_gap_is_named_rather_than_left_empty()
    {
        // An empty string would match an empty configured class, and the three class sets are matched
        // with StringComparer.Ordinal against exactly this list.
        LabelFile labels = LabelFile.Parse(["person", "", "car"]);

        Assert.NotEqual("", labels.Labels[1]);
        Assert.Equal("class_1", labels.Labels[1]);
    }

    [Fact]
    public void Trailing_blank_lines_do_not_become_classes()
    {
        // Unlike an interior gap, a trailing blank shifts nothing — there is no line after it. Keeping
        // them would invent classes that the model cannot emit.
        LabelFile labels = LabelFile.Parse(["person", "car", "", "", ""]);

        Assert.Equal(2, labels.Labels.Count);
        Assert.Equal(0, labels.PlaceholderCount);
    }

    [Fact]
    public void Filler_rows_are_kept_verbatim_so_the_indices_stay_right()
    {
        // A COCO-90 labelmap pads unused ids with "???". Those rows are not empty, so they already
        // hold their positions — and they must, because that padding is what makes 90-slot indexing
        // line up.
        LabelFile labels = LabelFile.Parse(["person", "???", "car"]);

        Assert.Equal(3, labels.Labels.Count);
        Assert.Equal("???", labels.Labels[1]);
        Assert.Equal("car", labels.Labels[2]);
        Assert.Equal(0, labels.PlaceholderCount);
    }

    [Fact]
    public void An_indexed_coral_labelmap_puts_each_name_at_its_stated_index()
    {
        // Coral's own coco_labels.txt is written this way. Read positionally, every name would land one
        // slot early and the index prefix would be part of the name.
        LabelFile labels = LabelFile.Parse(["0  person", "1  bicycle", "2  car"]);

        Assert.Equal(LabelFileFormat.Indexed, labels.Format);
        Assert.Equal(["person", "bicycle", "car"], labels.Labels);
    }

    [Fact]
    public void An_indexed_labelmap_may_skip_numbers()
    {
        // Sparse is legitimate for COCO-90, and the skipped slots have to stay occupied or everything
        // after them shifts.
        LabelFile labels = LabelFile.Parse(["0 person", "3 car"]);

        Assert.Equal(4, labels.Labels.Count);
        Assert.Equal("person", labels.Labels[0]);
        Assert.Equal("class_1", labels.Labels[1]);
        Assert.Equal("class_2", labels.Labels[2]);
        Assert.Equal("car", labels.Labels[3]);
        Assert.Equal(2, labels.PlaceholderCount);
    }

    [Fact]
    public void A_name_that_merely_starts_with_a_digit_stays_positional()
    {
        // Indexed is only claimed when *every* line states an index. One unprefixed line means the
        // digits belong to the name, and guessing otherwise would eat part of it.
        LabelFile labels = LabelFile.Parse(["0 person", "bicycle", "2 car"]);

        Assert.Equal(LabelFileFormat.Positional, labels.Format);
        Assert.Equal(["0 person", "bicycle", "2 car"], labels.Labels);
    }

    [Fact]
    public void A_duplicated_index_is_refused_rather_than_resolved()
    {
        // Which name a detection means is genuinely undecidable, and picking one silently would attach
        // the wrong word to a real object.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LabelFile.Parse(["0 person", "0 pedestrian"], "coco_labels.txt"));

        Assert.Contains("twice", error.Message);
        Assert.Contains("coco_labels.txt", error.Message);
    }

    [Fact]
    public void An_index_past_the_ceiling_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LabelFile.Parse(["0 person", "999999 car"]));

        Assert.Contains("ceiling", error.Message);
    }

    [Fact]
    public void An_empty_file_is_refused_by_name()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LabelFile.Parse(["", "   ", ""], "labels.txt"));

        Assert.Contains("labels.txt", error.Message);
    }

    [Fact]
    public void Surrounding_whitespace_is_not_part_of_a_name()
    {
        // Names are matched with StringComparer.Ordinal downstream, so a stray space is a class that
        // can never be selected.
        LabelFile labels = LabelFile.Parse(["  person  ", "\tcar\t"]);

        Assert.Equal(["person", "car"], labels.Labels);
    }

    [Theory]
    [InlineData("person", "bicycle", "car")]
    [InlineData("person", "traffic_light", "fire_hydrant", "teddy_bear", "hair_drier", "toothbrush")]
    [InlineData("single")]
    public void A_file_with_no_gaps_reads_exactly_as_filtering_blank_lines_used_to(params string[] lines)
    {
        // The compatibility guarantee, stated as a test. LabelFile replaced
        // ReadAllLines().Select(Trim).Where(len > 0) in OnnxObjectDetector, and the live deployment's
        // labels file has no blank lines, no index prefixes and no stray whitespace — so the new reader
        // must return the identical list for it. Anything else would silently relabel a running system.
        string[] asTheOldCodeRead = [.. lines.Select(static l => l.Trim()).Where(static l => l.Length > 0)];

        LabelFile labels = LabelFile.Parse(lines);

        Assert.Equal(asTheOldCodeRead, labels.Labels);
        Assert.Equal(LabelFileFormat.Positional, labels.Format);
        Assert.Equal(0, labels.PlaceholderCount);
    }

    [Fact]
    public void The_coco_90_shape_survives_a_round_trip()
    {
        // The real file: 90 positional entries, no gaps, person first. Pinned because this is the
        // vocabulary the EdgeTPU backend ships with, and its indices differ from COCO-80 — cat is 16
        // here and 15 there — so the file and the weights have to travel together.
        string[] lines = [.. Enumerable.Range(0, 90).Select(static i => i == 0 ? "person" : $"class{i}")];

        LabelFile labels = LabelFile.Parse(lines);

        Assert.Equal(LabelFileFormat.Positional, labels.Format);
        Assert.Equal(90, labels.Labels.Count);
        Assert.Equal("person", labels.Labels[0]);
        Assert.Equal(0, labels.PlaceholderCount);
    }
}
