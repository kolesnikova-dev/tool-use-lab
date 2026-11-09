namespace ToolUseLab.Tests;

public class MarkdownChunkerTests
{
    private static List<DocChunk> Chunk(string text, int max = 500, int overlap = 0) =>
        new MarkdownChunker(max, overlap).Chunk("a.md", text);

    [Fact]
    public void Sections_become_chunks_with_a_heading_breadcrumb()
    {
        var chunks = Chunk("Intro text.\n\n# Setup\nInstall it.\n\n## Windows\nUse winget.\n\n# Usage\nRun it.");

        Assert.Equal(["", "Setup", "Setup > Windows", "Usage"], chunks.Select(c => c.Heading));
        Assert.Equal("Use winget.", chunks[2].Text);
        Assert.Equal("a.md > Setup > Windows", chunks[2].Citation);
        Assert.Equal("a.md", chunks[0].Citation);
    }

    [Fact]
    public void A_sibling_heading_replaces_the_previous_one_in_the_breadcrumb()
    {
        var chunks = Chunk("# A\n## B\nb text\n## C\nc text");

        Assert.Equal(["A > B", "A > C"], chunks.Select(c => c.Heading));
    }

    [Fact]
    public void Hash_lines_inside_code_fences_are_not_headings()
    {
        var chunks = Chunk("# Real\n```bash\n# just a comment\necho hi\n```");

        Assert.Single(chunks);
        Assert.Contains("# just a comment", chunks[0].Text);
    }

    [Fact]
    public void A_long_section_is_split_within_the_size_limit()
    {
        string text = "# Big\n" + string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragraph {i} " + new string('x', 80)));

        var chunks = Chunk(text, max: 300);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 300));
        Assert.All(chunks, c => Assert.Equal("Big", c.Heading));
    }

    [Fact]
    public void Overlap_repeats_the_last_paragraph_in_the_next_chunk()
    {
        string text = "# T\n" + string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"P{i} " + new string('y', 90)));

        var chunks = Chunk(text, max: 250, overlap: 120);

        Assert.True(chunks.Count > 1);
        string lastOfFirst = chunks[0].Text.Split("\n\n")[^1];
        Assert.StartsWith(lastOfFirst, chunks[1].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("# Only a heading")]
    public void Empty_or_heading_only_input_gives_no_chunks(string text) => Assert.Empty(Chunk(text));

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var chunks = Chunk("# A\r\nline one\r\n\r\nline two\r\n");

        Assert.Single(chunks);
        Assert.Equal("A", chunks[0].Heading);
        Assert.DoesNotContain('\r', chunks[0].Text);
    }
}

public class SentenceChunkerTests
{
    private static List<DocChunk> Chunk(string text, int max = 500, int overlap = 0) =>
        new SentenceChunker(max, overlap).Chunk("a.txt", text);

    [Fact]
    public void Splits_on_sentence_ends_but_not_on_decimals_or_lowercase_abbreviations()
    {
        var sentences = SentenceChunker.Sentences("Version 3.5 is out. See e.g. the notes! Is it good? Yes.").ToList();

        Assert.Equal(["Version 3.5 is out.", "See e.g. the notes!", "Is it good?", "Yes."], sentences);
    }

    [Fact]
    public void Blank_lines_also_end_a_sentence()
    {
        Assert.Equal(["Title without period", "Next line."], SentenceChunker.Sentences("Title without period\n\nNext line.").ToList());
    }

    [Fact]
    public void Whole_sentences_are_packed_and_never_cut()
    {
        string text = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"This is sentence number {i}."));

        var chunks = Chunk(text, max: 120);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 120));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text));
        Assert.All(chunks, c => Assert.Equal("", c.Heading));
    }

    [Fact]
    public void Overlap_repeats_the_last_sentence()
    {
        string text = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"Sentence {i} is here."));

        var chunks = Chunk(text, max: 70, overlap: 30);

        string lastOfFirst = chunks[0].Text[(chunks[0].Text.LastIndexOf("Sentence") - 0)..];
        Assert.StartsWith(lastOfFirst, chunks[1].Text);
    }

    [Fact]
    public void One_sentence_longer_than_the_limit_is_still_split()
    {
        string text = string.Join(" ", Enumerable.Repeat("word", 100)) + ".";

        var chunks = Chunk(text, max: 100);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
    }

    [Fact]
    public void Empty_input_gives_no_chunks() => Assert.Empty(Chunk("  \n "));
}

public class SizeChunkerTests
{
    [Fact]
    public void Windows_respect_the_limit_and_do_not_cut_words()
    {
        string text = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));

        var chunks = new SizeChunker(100, 20).Chunk("a.txt", text);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
        var words = text.Split(' ').ToHashSet();
        Assert.All(chunks.SelectMany(c => c.Text.Split(' ')), w => Assert.Contains(w, words));
    }

    [Fact]
    public void Overlap_repeats_text_and_nothing_is_lost()
    {
        string text = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"w{i}"));

        var chunks = new SizeChunker(60, 20).Chunk("a.txt", text);

        var seen = chunks.SelectMany(c => c.Text.Split(' ')).ToHashSet();
        Assert.All(Enumerable.Range(0, 100), i => Assert.Contains($"w{i}", seen));
        Assert.True(chunks.Sum(c => c.Text.Length) > text.Length); // overlap means repeated text
    }

    [Fact]
    public void A_single_word_longer_than_the_limit_is_cut_hard_and_terminates()
    {
        var chunks = new SizeChunker(50, 10).Chunk("a.txt", new string('z', 205));

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 50));
    }

    [Fact]
    public void Short_text_is_one_chunk() =>
        Assert.Equal("hello there", Assert.Single(new SizeChunker(100, 10).Chunk("a.txt", "  hello there \n")).Text);

    [Fact]
    public void Empty_text_gives_no_chunks() => Assert.Empty(new SizeChunker(100, 10).Chunk("a.txt", ""));
}

public class ChunkerFactoryTests
{
    [Theory]
    [InlineData("Auto", "a.md", typeof(MarkdownChunker))]
    [InlineData("Auto", "A.MD", typeof(MarkdownChunker))]
    [InlineData("Auto", "a.txt", typeof(SentenceChunker))]
    [InlineData("Size", "a.md", typeof(SizeChunker))]
    [InlineData("Sentence", "a.md", typeof(SentenceChunker))]
    [InlineData("Markdown", "a.txt", typeof(MarkdownChunker))]
    public void Picks_the_chunker_for_the_strategy_and_extension(string strategy, string path, Type expected) =>
        Assert.IsType(expected, ChunkerFactory.For(Enum.Parse<ChunkingStrategy>(strategy), path, 500, 50));
}
