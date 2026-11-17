using GenreRulePacks;

using WritingIDE.Core.Analysis;
using WritingIDE.Core.Grammar;
using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace Plugins.Tests;

public sealed class GenreRulePacksTests {
    private static IReadOnlyList<GrammarDiagnostic> Check(PackState state, string text, GrammarOptions? options = null) {
        AnalysisContext context = new(text, options ?? GrammarOptions.Default, null);
        return GenreRules.Create(state).SelectMany(rule => rule.Analyze(context)).ToList();
    }

    private static PackState StateFor(string genre, string? stored = null) {
        PackState state = new();
        state.Load(stored, genre);
        return state;
    }

    [Fact]
    public void PacksFollowTheGenreUnlessTheWriterChoosesThem() {
        Assert.Equal(["Romance", "Fantasy"], StateFor("Romantic fantasy").Enabled.Select(static pack => pack.Name));
        Assert.Equal(["Mystery"], StateFor("Cozy mystery").Enabled.Select(static pack => pack.Name));
        Assert.Equal(["Science Fiction"], StateFor("Space opera").Enabled.Select(static pack => pack.Name));
        Assert.Empty(StateFor("Literary").Enabled);

        PackState manual = StateFor("Romance", "thriller, scifi");
        Assert.True(manual.IsManual);
        Assert.Equal(["Science Fiction", "Thriller"], manual.Enabled.Select(static pack => pack.Name));
        Assert.Equal("scifi,thriller", manual.Serialize());
        // An empty choice is still a choice: no packs, whatever the genre.
        Assert.Empty(StateFor("Romance", "").Enabled);
    }

    [Fact]
    public void RulesStaySilentUntilTheirPackIsOn() {
        const string text = "She let out a breath she didn't know she was holding. It was love at first sight.";

        Assert.Empty(Check(StateFor("Thriller"), text));
        IReadOnlyList<GrammarDiagnostic> found = Check(StateFor("Romance"), text);

        Assert.Equal(["ROM001", "ROM002"], found.Select(static diagnostic => diagnostic.RuleId));
        Assert.Equal("let out a breath she didn't know", text.Substring(found[0].Offset, found[0].Length));
    }

    [Theory]
    [InlineData("mystery", "As luck would have it, the key was in the drawer.", "MYS002")]
    [InlineData("mystery", "Little did she know, the butler had seen it all.", "MYS003")]
    [InlineData("fantasy", "He was the chosen one, as it was foretold.", "FAN001")]
    [InlineData("fantasy", "\"Okay,\" said the knight.", "FAN002")]
    [InlineData("scifi", "The explosion roared through the vacuum.", "SCI002")]
    [InlineData("scifi", "That war ended light-years ago.", "SCI003")]
    public void EachPackFindsItsPhrases(string pack, string text, string expected) {
        Assert.Contains(Check(StateFor("", pack), text), diagnostic => diagnostic.RuleId == expected);
    }

    [Fact]
    public void SuddenlyIsOnlyAHabitFromTheThirdTime() {
        PackState state = StateFor("Thriller");

        Assert.Empty(Check(state, "Suddenly the door opened. Without warning, he fell."));
        IReadOnlyList<GrammarDiagnostic> found = Check(state, "Suddenly the door opened. Without warning, he fell. All of a sudden it was dark.");

        Assert.Equal(3, found.Count);
        Assert.All(found, static diagnostic => Assert.Equal("THR002", diagnostic.RuleId));
        Assert.Contains("3 times", found[0].Message);
    }

    [Fact]
    public void MixedUnitsReportsTheSystemUsedLess() {
        PackState state = StateFor("Sci-fi");
        const string text = "The station was 40 kilometers out. He walked 200 meters, then another 3 km, and found a hatch 6 feet wide. Her feet hurt.";

        GrammarDiagnostic found = Assert.Single(Check(state, text));

        Assert.Equal("SCI001", found.RuleId);
        Assert.Equal("6 feet", text.Substring(found.Offset, found.Length));
        Assert.Empty(Check(state, "It was 40 kilometers out, and he pounds the door with both feet."));
    }

    [Fact]
    public void ACulpritWhoArrivesInTheLastThirdIsReportedWhereTheyAppear() {
        StoryEntity culprit = new(EntityKind.Character, "Silas Crane");
        culprit.Attributes.Add(new EntityAttribute("Role", "Culprit"));
        StoryEntity sleuth = new(EntityKind.Character, "Elena Marsh");
        PackState state = StateFor("Mystery");
        // Ten chapters of equal length; the culprit first appears in the ninth.
        state.Facts = new ManuscriptFacts(new Dictionary<Guid, int> { [culprit.Id] = 8, [sleuth.Id] = 0 }, Enumerable.Repeat(1000, 10).ToList());
        GrammarOptions Options(int chapter) {
            return new GrammarOptions(new EntityIndex([culprit, sleuth]), new HashSet<string>(), new HashSet<string>()) {
                Position = new ManuscriptPosition(chapter, Enumerable.Range(1, 10).Select(static i => $"Chapter {i}").ToList())
            };
        }
        const string text = "Elena Marsh opened the door. Silas Crane stood there.";

        GrammarDiagnostic found = Assert.Single(Check(state, text, Options(8)));

        Assert.Equal("MYS001", found.RuleId);
        Assert.Equal("Silas Crane", text.Substring(found.Offset, found.Length));
        Assert.Contains("80%", found.Message.Replace(" ", "").Replace(" %", "%"));
        // In a later chapter he is no longer new; and an early culprit is fine.
        Assert.Empty(Check(state, text, Options(9)));
        state.Facts = new ManuscriptFacts(new Dictionary<Guid, int> { [culprit.Id] = 2 }, Enumerable.Repeat(1000, 10).ToList());
        Assert.Empty(Check(state, text, Options(2)));
    }

    [Fact]
    public void TheReportChecksWhoArrivesWhenAndWhetherCluesArePlanted() {
        EntityInfo Entity(EntityKind kind, string name, params (string Key, string Value)[] attributes) {
            return new EntityInfo(Guid.NewGuid(), kind, name, [], "", attributes.ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value));
        }
        ManuscriptSnapshot manuscript = new(
            [("One", "Elena found the brass compass on the sill."), ("Two", "Elena rowed to town."), ("Three", "Elena read the logbook."), ("Four", "Crane confessed. The ledger proved it.")],
            [
                Entity(EntityKind.Character, "Crane", ("Role", "Culprit")),
                Entity(EntityKind.Item, "Brass Compass", ("Clue", "points to the wreck")),
                Entity(EntityKind.Item, "Ledger", ("Clue", "the payments")),
                Entity(EntityKind.Item, "Torn Letter", ("Clue", "the motive"))
            ]);

        IReadOnlyList<ReportLine> lines = GenreReport.Build([GenrePack.Mystery], manuscript);

        Assert.Contains(lines, static line => !line.Passed && line.Text.StartsWith("Crane (the culprit) first appears 73%", StringComparison.Ordinal));
        // Names match whatever their case.
        Assert.Contains(lines, static line => line.Passed && line.Text.Contains("'Brass Compass' is planted in 'One'"));
        Assert.Contains(lines, static line => !line.Passed && line.Text.Contains("'Torn Letter' is never mentioned"));
        Assert.Contains(lines, static line => !line.Passed && line.Text.Contains("'Ledger' first appears in the last chapter"));
    }

    [Fact]
    public void TheReportAsksForRolesItCannotFind() {
        ManuscriptSnapshot manuscript = new([("One", "They met at the harbour.")], []);

        IReadOnlyList<ReportLine> lines = GenreReport.Build([GenrePack.Romance, GenrePack.Fantasy], manuscript);

        Assert.Contains(lines, static line => (line.Pack == GenrePack.Romance) && line.Text.Contains("Love interest"));
        Assert.Contains(lines, static line => (line.Pack == GenrePack.Fantasy) && line.Text.Contains("No Lore element describes the magic"));
    }
}