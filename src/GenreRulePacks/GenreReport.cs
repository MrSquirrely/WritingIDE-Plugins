using System.Text.RegularExpressions;

using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace GenreRulePacks;

/// <summary>The manuscript as the report needs it: chapters in reading order with their text, and the story elements.</summary>
public sealed record ManuscriptSnapshot(IReadOnlyList<(string Title, string Text)> Chapters, IReadOnlyList<EntityInfo> Entities) {
    /// <summary>Reads the open project. Safe from any thread.</summary>
    public static ManuscriptSnapshot Read(IProjectApi project) {
        List<(string Title, string Text)> chapters = project.GetChapters().Select(chapter => (chapter.Title, project.GetChapterText(chapter.Id))).ToList();
        return new ManuscriptSnapshot(chapters, project.GetEntities());
    }

    /// <summary>Where each story element first appears, and how long each chapter is.</summary>
    public ManuscriptFacts ToFacts() {
        Dictionary<Guid, int> first = [];
        foreach (EntityInfo entity in Entities) {
            int index = FirstChapterOf(entity);
            if (index >= 0) {
                first[entity.Id] = index;
            }
        }
        return new ManuscriptFacts(first, Chapters.Select(static chapter => CountWords(chapter.Text)).ToList());
    }

    /// <summary>The index of the first chapter that mentions the element by name or alias; -1 when none does.</summary>
    public int FirstChapterOf(EntityInfo entity) {
        List<Regex> names = NamePatterns(entity);
        for (int i = 0; i < Chapters.Count; i++) {
            if (names.Any(name => name.IsMatch(Chapters[i].Text))) {
                return i;
            }
        }
        return -1;
    }

    public bool Mentions(int chapterIndex, EntityInfo entity) {
        return NamePatterns(entity).Any(name => name.IsMatch(Chapters[chapterIndex].Text));
    }

    private static List<Regex> NamePatterns(EntityInfo entity) {
        return entity.Aliases.Prepend(entity.Name)
            .Where(static name => name.Trim().Length > 1)
            .Select(static name => new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name.Trim())}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .ToList();
    }

    internal static int CountWords(string text) {
        return WordPattern.Count(text);
    }

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*", RegexOptions.Compiled);
}

/// <summary>One finding of the genre report.</summary>
/// <param name="Passed">True for a check the manuscript meets; false for something to look at.</param>
public sealed record ReportLine(GenrePack Pack, bool Passed, string Text);

/// <summary>Checks that need the whole manuscript: who appears when, and how the book is paced.</summary>
public static class GenreReport {
    public static IReadOnlyList<ReportLine> Build(IEnumerable<GenrePack> packs, ManuscriptSnapshot manuscript) {
        List<ReportLine> lines = [];
        ManuscriptFacts facts = manuscript.ToFacts();
        foreach (GenrePack pack in packs) {
            if (manuscript.Chapters.Count == 0) {
                lines.Add(new ReportLine(pack, false, "There are no chapters to check yet."));
                continue;
            }
            if (pack == GenrePack.Romance) {
                Romance(lines, manuscript, facts);
            }
            else if (pack == GenrePack.Mystery) {
                Mystery(lines, manuscript, facts);
            }
            else if (pack == GenrePack.Fantasy) {
                Fantasy(lines, manuscript);
            }
            else if (pack == GenrePack.ScienceFiction) {
                ScienceFiction(lines, manuscript);
            }
            else if (pack == GenrePack.Thriller) {
                Thriller(lines, manuscript, facts);
            }
        }
        return lines;
    }

    private static void Romance(List<ReportLine> lines, ManuscriptSnapshot manuscript, ManuscriptFacts facts) {
        GenrePack pack = GenrePack.Romance;
        List<EntityInfo> interests = WithRole(manuscript, GenreRules.LoveInterestRoles());
        if (interests.Count == 0) {
            lines.Add(new ReportLine(pack, false, "No character has 'Love interest' in their Role attribute. Give the love interest that role, so the pack can check when they arrive and whether they are there at the end."));
            return;
        }
        int last = manuscript.Chapters.Count - 1;
        foreach (EntityInfo interest in interests) {
            Arrival(lines, pack, facts, interest, "the love interest", 0.25, "Romance readers expect to meet both leads in the first quarter.");
            lines.Add(manuscript.Mentions(last, interest)
                ? new ReportLine(pack, true, $"{interest.Name} is in the last chapter: the couple can have their ending on the page.")
                : new ReportLine(pack, false, $"{interest.Name} is not in the last chapter ('{manuscript.Chapters[last].Title}'). A romance ends with the couple together (happily ever after, or happy for now), and readers want to see it."));
        }
    }

    private static void Mystery(List<ReportLine> lines, ManuscriptSnapshot manuscript, ManuscriptFacts facts) {
        GenrePack pack = GenrePack.Mystery;
        List<EntityInfo> culprits = WithRole(manuscript, GenreRules.CulpritRoles());
        if (culprits.Count == 0) {
            lines.Add(new ReportLine(pack, false, "No character has 'Culprit' (or 'Murderer', 'Killer') in their Role attribute. Give the guilty party that role, so the pack can check that the reader meets them in time."));
        }
        foreach (EntityInfo culprit in culprits) {
            Arrival(lines, pack, facts, culprit, "the culprit", 2.0 / 3, "The reader must meet the culprit in the first two thirds to have a fair chance.");
        }

        // Clues: items with a Clue attribute, or 'clue' in their Role or summary.
        List<EntityInfo> clues = manuscript.Entities
            .Where(static entity => (entity.Kind == EntityKind.Item)
                && (entity.Attributes.Keys.Any(static key => key.Equals("Clue", StringComparison.OrdinalIgnoreCase))
                    || (entity.Attributes.TryGetValue("Role", out string? role) && role.Contains("clue", StringComparison.OrdinalIgnoreCase))
                    || entity.Summary.Contains("clue", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (clues.Count == 0) {
            lines.Add(new ReportLine(pack, false, "No item is marked as a clue. Add a 'Clue' attribute to the items that point to the solution, and the pack checks that each is on the page before the reveal."));
        }
        foreach (EntityInfo clue in clues) {
            if (!facts.FirstChapter.TryGetValue(clue.Id, out int first)) {
                lines.Add(new ReportLine(pack, false, $"The clue '{clue.Name}' is never mentioned in the manuscript."));
            }
            else if ((manuscript.Chapters.Count > 1) && (first == manuscript.Chapters.Count - 1)) {
                lines.Add(new ReportLine(pack, false, $"The clue '{clue.Name}' first appears in the last chapter. Plant it earlier, so the solution does not rest on something the reader never saw."));
            }
            else {
                lines.Add(new ReportLine(pack, true, $"The clue '{clue.Name}' is planted in '{manuscript.Chapters[first].Title}', before the ending."));
            }
        }
    }

    private static void Fantasy(List<ReportLine> lines, ManuscriptSnapshot manuscript) {
        GenrePack pack = GenrePack.Fantasy;
        List<EntityInfo> magic = manuscript.Entities
            .Where(static entity => (entity.Kind == EntityKind.Lore) && MagicPattern.IsMatch($"{entity.Name} {entity.Summary}"))
            .ToList();
        if (magic.Count == 0) {
            lines.Add(new ReportLine(pack, false, "No Lore element describes the magic. Writing its rules down (a Lore element with 'magic' in its name or summary) keeps it consistent."));
            return;
        }
        foreach (EntityInfo system in magic) {
            bool hasCost = system.Attributes.Keys.Any(static key => CostPattern.IsMatch(key));
            lines.Add(hasCost
                ? new ReportLine(pack, true, $"'{system.Name}' has a cost or a limit. Magic that costs something creates stakes.")
                : new ReportLine(pack, false, $"'{system.Name}' has no Cost or Limits attribute. Magic that can do anything for free removes the story's tension: say what it costs, or what it cannot do."));
        }
    }

    private static void ScienceFiction(List<ReportLine> lines, ManuscriptSnapshot manuscript) {
        GenrePack pack = GenrePack.ScienceFiction;
        int metric = manuscript.Chapters.Sum(static chapter => MixedUnitsRule.Metric().Count(chapter.Text));
        int imperial = manuscript.Chapters.Sum(static chapter => MixedUnitsRule.Imperial().Count(chapter.Text));
        if ((metric == 0) && (imperial == 0)) {
            lines.Add(new ReportLine(pack, true, "No measurements with units found."));
        }
        else if ((metric > 0) && (imperial > 0)) {
            lines.Add(new ReportLine(pack, false, $"The manuscript measures in both systems: {metric} metric and {imperial} imperial measurement{(imperial == 1 ? "" : "s")}. Pick one for the setting (SCI001 marks the odd ones chapter by chapter)."));
        }
        else {
            lines.Add(new ReportLine(pack, true, $"Units are consistent: {(metric > 0 ? "metric" : "imperial")} throughout ({metric + imperial} measurements)."));
        }
    }

    private static void Thriller(List<ReportLine> lines, ManuscriptSnapshot manuscript, ManuscriptFacts facts) {
        GenrePack pack = GenrePack.Thriller;
        List<int> words = facts.ChapterWords.Where(static count => count > 0).ToList();
        if (words.Count < 3) {
            lines.Add(new ReportLine(pack, true, "Too few written chapters to judge the pace yet."));
            return;
        }
        List<int> sorted = words.Order().ToList();
        int median = sorted[sorted.Count / 2];
        lines.Add(new ReportLine(pack, median <= 3000, $"A typical chapter is {median:N0} words. {(median <= 3000 ? "That is a thriller's pace." : "Thrillers usually run under 3,000 words a chapter; shorter chapters read faster.")}"));
        for (int i = 0; i < facts.ChapterWords.Count; i++) {
            if ((facts.ChapterWords[i] > LongChapterRule.LongWords) && (facts.ChapterWords[i] >= median * 1.5)) {
                lines.Add(new ReportLine(pack, false, $"'{manuscript.Chapters[i].Title}' is {facts.ChapterWords[i]:N0} words, well over the typical {median:N0}."));
            }
        }
        // The last third should not be slower than the first.
        int third = Math.Max(1, facts.ChapterWords.Count / 3);
        double opening = facts.ChapterWords.Take(third).Average();
        double closing = facts.ChapterWords.Skip(facts.ChapterWords.Count - third).Where(static count => count > 0).DefaultIfEmpty(0).Average();
        if ((closing > 0) && (closing > opening * 1.25)) {
            lines.Add(new ReportLine(pack, false, $"Chapters in the last third average {closing:N0} words, against {opening:N0} in the first. Thrillers usually speed up towards the end, with shorter chapters."));
        }
    }

    private static void Arrival(List<ReportLine> lines, GenrePack pack, ManuscriptFacts facts, EntityInfo entity, string role, double latest, string advice) {
        if (!facts.FirstChapter.TryGetValue(entity.Id, out int first)) {
            lines.Add(new ReportLine(pack, false, $"{entity.Name} ({role}) is never mentioned in the manuscript."));
            return;
        }
        double position = facts.PositionOf(first);
        lines.Add(position <= latest
            ? new ReportLine(pack, true, $"{entity.Name} ({role}) first appears {position:P0} of the way in.")
            : new ReportLine(pack, false, $"{entity.Name} ({role}) first appears {position:P0} of the way in. {advice}"));
    }

    private static List<EntityInfo> WithRole(ManuscriptSnapshot manuscript, Regex roles) {
        return manuscript.Entities
            .Where(entity => (entity.Kind == EntityKind.Character) && entity.Attributes.TryGetValue("Role", out string? role) && roles.IsMatch(role))
            .ToList();
    }

    private static readonly Regex MagicPattern = new(@"\b(magic|magical|sorcery|spell|spells|arcane|witchcraft|enchant\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CostPattern = new(@"cost|limit|price|weakness|drawback|rule", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}