using System.Text.RegularExpressions;

using WritingIDE.Core.Analysis;
using WritingIDE.Core.Grammar;
using WritingIDE.Core.Model;

namespace GenreRulePacks;

/// <summary>A rule that belongs to a genre pack: it stays silent unless its pack is on for the project.</summary>
public abstract class PackRule(GenrePack pack, PackState state, string id, string title, DiagnosticSeverity severity, string description)
    : GrammarRule(id, title, RuleCategory.Story, severity, description) {
    public GenrePack Pack { get; } = pack;

    protected PackState State { get; } = state;

    public sealed override IEnumerable<GrammarDiagnostic> Analyze(AnalysisContext context) {
        return State.IsEnabled(Pack) ? Check(context) : [];
    }

    protected abstract IEnumerable<GrammarDiagnostic> Check(AnalysisContext context);

    /// <summary>True when a character's Role attribute contains one of the words ("Role: Love interest").</summary>
    internal static bool HasRole(StoryEntity entity, Regex roles) {
        return (entity.Kind == EntityKind.Character)
            && entity.Attributes.Any(attribute => attribute.Key.Equals("Role", StringComparison.OrdinalIgnoreCase) && roles.IsMatch(attribute.Value));
    }
}

/// <summary>
/// A rule made of phrases, each with its own advice. With <paramref name="minimum"/> above 1 it only speaks up when
/// the phrases turn up that often in one chapter: a word that is fine once and a habit by the third time.
/// </summary>
public sealed class PhraseRule(GenrePack pack, PackState state, string id, string title, DiagnosticSeverity severity, string description,
    IReadOnlyList<(Regex Pattern, string Message)> phrases, int minimum = 1)
    : PackRule(pack, state, id, title, severity, description) {
    protected override IEnumerable<GrammarDiagnostic> Check(AnalysisContext context) {
        List<GrammarDiagnostic> found = [];
        foreach ((Regex pattern, string message) in phrases) {
            context.CancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in pattern.Matches(context.Text)) {
                found.Add(Report(match.Index, match.Length, string.Format(message, match.Value, 0)));
            }
        }
        if (found.Count < minimum) {
            return [];
        }
        return minimum > 1
            ? found.Select(diagnostic => diagnostic with { Message = diagnostic.Message.Replace("{count}", found.Count.ToString()) }).OrderBy(static diagnostic => diagnostic.Offset).ToList()
            : found.OrderBy(static diagnostic => diagnostic.Offset).ToList();
    }

    internal static (Regex Pattern, string Message) Phrase(string pattern, string message) {
        return (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), message);
    }
}

/// <summary>
/// A character with a given role who first walks on late in the book: the culprit of a mystery after two thirds, the
/// love interest of a romance after a quarter. Reported where they first appear.
/// </summary>
public sealed class LateArrivalRule(GenrePack pack, PackState state, string id, string title, string description, Regex roles, double latest, string message)
    : PackRule(pack, state, id, title, DiagnosticSeverity.Warning, description) {
    protected override IEnumerable<GrammarDiagnostic> Check(AnalysisContext context) {
        ManuscriptFacts facts = State.Facts;
        if ((context.Options.Position is not ManuscriptPosition position) || (facts.ChapterCount < 3)) {
            yield break;
        }
        double here = facts.PositionOf(position.Index);
        if (here <= latest) {
            yield break;
        }
        foreach (StoryEntity entity in context.Options.Entities.Entities.Where(entity => HasRole(entity, roles))) {
            if (!facts.FirstChapter.TryGetValue(entity.Id, out int first) || (first != position.Index)) {
                continue;
            }
            EntityMention? mention = EntityIndex.FindMentions(context.Text, entity).OrderBy(static found => found.Offset).FirstOrDefault();
            if (mention is not null) {
                yield return Report(mention.Offset, mention.Length, string.Format(message, entity.Name, $"{here:P0}"));
            }
        }
    }
}

/// <summary>SCI001: metric and imperial units in the same chapter. The system used less is reported.</summary>
public sealed partial class MixedUnitsRule(PackState state)
    : PackRule(GenrePack.ScienceFiction, state, "SCI001", "Mixed unit systems", DiagnosticSeverity.Warning,
        "A setting usually measures in one system. Metric and imperial units in the same chapter read as a slip, unless two cultures are talking.") {
    protected override IEnumerable<GrammarDiagnostic> Check(AnalysisContext context) {
        MatchCollection metric = Metric().Matches(context.Text);
        MatchCollection imperial = Imperial().Matches(context.Text);
        if ((metric.Count == 0) || (imperial.Count == 0)) {
            yield break;
        }
        bool metricIsMain = metric.Count >= imperial.Count;
        foreach (Match match in metricIsMain ? imperial : metric) {
            yield return Report(match.Index, match.Length,
                $"'{match.Value}' is {(metricIsMain ? "imperial" : "metric")}, but this chapter mostly measures in {(metricIsMain ? "metric" : "imperial")} units.");
        }
    }

    // A number before the unit keeps "feet" (the body part) and "pounds" (the verb) out of it.
    [GeneratedRegex(@"\b\d[\d,.]*\s*(?:kilomet(?:er|re)s?|km|met(?:er|re)s?|centimet(?:er|re)s?|cm|kilograms?|kilos?|kg|lit(?:er|re)s?|degrees Celsius)\b|\b(?:a|an|one|two|three|four|five|six|seven|eight|nine|ten|twenty|thirty|fifty|hundred|thousand|million|few|several|many)\s+(?:kilomet(?:er|re)s?|met(?:er|re)s?|centimet(?:er|re)s?|kilograms?|kilos?|lit(?:er|re)s?)\b", RegexOptions.IgnoreCase)]
    internal static partial Regex Metric();

    [GeneratedRegex(@"\b\d[\d,.]*\s*(?:miles?|feet|foot|ft|inch(?:es)?|yards?|pounds?|lbs?|gallons?|degrees Fahrenheit)\b|\b(?:a|an|one|two|three|four|five|six|seven|eight|nine|ten|twenty|thirty|fifty|hundred|thousand|million|few|several|many)\s+(?:miles?|feet|inch(?:es)?|yards?|pounds?|gallons?)\b", RegexOptions.IgnoreCase)]
    internal static partial Regex Imperial();
}

/// <summary>THR001: a chapter much longer than the others slows a thriller down.</summary>
public sealed class LongChapterRule(PackState state)
    : PackRule(GenrePack.Thriller, state, "THR001", "Long chapter", DiagnosticSeverity.Message,
        "Thrillers keep chapters short to keep the reader turning pages. A chapter far longer than the rest is a place to cut or split.") {
    /// <summary>Longer than this, and at least half as long again as the typical chapter.</summary>
    internal const int LongWords = 3500;

    protected override IEnumerable<GrammarDiagnostic> Check(AnalysisContext context) {
        int words = context.Tokens.Count(static token => token.IsWord);
        if (words <= LongWords) {
            yield break;
        }
        List<int> others = State.Facts.ChapterWords.Where(static count => count > 0).Order().ToList();
        int median = others.Count == 0 ? 0 : others[others.Count / 2];
        if ((median > 0) && (words < median * 1.5)) {
            yield break;
        }
        int end = Math.Min(context.Text.Length, context.Text.IndexOfAny(['\r', '\n']) is int line and > 0 ? line : Math.Min(40, context.Text.Length));
        yield return Report(0, end, $"This chapter is {words:N0} words{(median > 0 ? $", against a typical {median:N0}" : "")}. Consider splitting it at a turn or a cliffhanger.");
    }
}

/// <summary>THR003: a chapter whose last line is someone falling asleep or the day ending gives the reader a place to stop.</summary>
public sealed partial class RestfulEndingRule(PackState state)
    : PackRule(GenrePack.Thriller, state, "THR003", "Chapter ends on a rest", DiagnosticSeverity.Message,
        "A chapter that ends with sleep or a quiet close invites the reader to put the book down. End on a question, a threat or a decision instead.") {
    protected override IEnumerable<GrammarDiagnostic> Check(AnalysisContext context) {
        string text = context.Text.TrimEnd();
        if (text.Length == 0) {
            yield break;
        }
        int start = text.LastIndexOf('\n') + 1;
        Match match = Rest().Match(text, start);
        if (match.Success) {
            yield return Report(start, text.Length - start, "The chapter ends at rest. A thriller chapter usually ends on a hook: cut the last beat, or end a moment earlier.");
        }
    }

    [GeneratedRegex(@"\b(fell asleep|drifted off|went to (bed|sleep)|closed (his|her|their|my) eyes and slept|slept (soundly|deeply|at last)|sleep (took|claimed) (him|her|them|me)|turned out the light|and that was that|all was (well|quiet|calm))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rest();
}

/// <summary>Every rule of every pack.</summary>
public static partial class GenreRules {
    public static IReadOnlyList<PackRule> Create(PackState state) {
        return [
            new PhraseRule(GenrePack.Romance, state, "ROM001", "Worn-out romance phrase", DiagnosticSeverity.Message,
                "Phrases romance readers have met many times. A fresh, specific detail lands harder.", [
                    PhraseRule.Phrase(@"\b(let|letting|released?) out a breath (s?he|they|I) (didn['’]t|hadn['’]t|did not|had not) (know|known|realize|realized|realise|realised)", "'{0}': one of the most-used lines in the genre. What does the relief feel like for this character?"),
                    PhraseRule.Phrase(@"\b(heart|pulse) skipped a beat\b", "'{0}' is a stock reaction. Show what this character does when startled or smitten."),
                    PhraseRule.Phrase(@"\bbutterflies in (his|her|their|my) stomach\b", "'{0}' is a stock phrase. Find this character's own physical tell."),
                    PhraseRule.Phrase(@"\b(electricity|a spark|sparks|a jolt) (shot|ran|flew|raced|passed) (through|between|up)\b", "'{0}': touch-as-electricity is very common. Try a detail only these two would notice."),
                    PhraseRule.Phrase(@"\b(smou?ldering|piercing|steely) (gaze|eyes|stare|look)\b", "'{0}' is a stock description. What do the eyes actually do?"),
                    PhraseRule.Phrase(@"\b(chiseled|chiselled) (jaw|features|abs|chest|face)\b", "'{0}' is a stock description of a hero."),
                    PhraseRule.Phrase(@"\bruggedly handsome\b", "'{0}' is a stock description of a hero."),
                    PhraseRule.Phrase(@"\b(his|her|their) orbs\b", "'{0}': 'orbs' for eyes pulls readers out of the scene. 'Eyes' is invisible."),
                    PhraseRule.Phrase(@"\bheaving (bosom|breasts|chest)\b", "'{0}' is a parody of the genre by now."),
                    PhraseRule.Phrase(@"\bundressed (him|her|them|me) with (his|her|their) eyes\b", "'{0}' is a stock phrase.")
                ]),
            new PhraseRule(GenrePack.Romance, state, "ROM002", "Love at first sight", DiagnosticSeverity.Message,
                "Readers come for the falling, not the having fallen. Instant love skips the part they enjoy.", [
                    PhraseRule.Phrase(@"\blove at first sight\b", "'{0}': if they are in love already, what is left for the book to do? Attraction at first sight leaves room to fall."),
                    PhraseRule.Phrase(@"\b(knew|realized|realised) (right then|in that (moment|instant)|at once|instantly|immediately) (that )?(s?he|they) (was|were) (the one|in love)\b", "'{0}': certainty this early leaves the romance nowhere to go.")
                ]),
            new LateArrivalRule(GenrePack.Romance, state, "ROM003", "Love interest arrives late",
                "A romance promises the reader the couple. A love interest who first appears after a quarter of the book makes them wait too long.",
                LoveInterestRoles(), 0.25, "{0} (the love interest) first appears {1} of the way in. Romance readers expect to meet both leads in the first quarter."),

            new LateArrivalRule(GenrePack.Mystery, state, "MYS001", "Culprit appears late",
                "Fair play: the reader must have met the culprit in time to suspect them. A culprit introduced in the last third feels like a cheat.",
                CulpritRoles(), 2.0 / 3, "{0} (the culprit) first appears {1} of the way in. Introduce them in the first two thirds, so the reader has a fair chance."),
            new PhraseRule(GenrePack.Mystery, state, "MYS002", "Luck solves it", DiagnosticSeverity.Message,
                "The detective should earn the solution. Coincidence may get a sleuth into trouble, but not out of it.", [
                    PhraseRule.Phrase(@"\b(by|through) (sheer|pure|a stroke of|mere|happy) (coincidence|chance|luck)\b", "'{0}': if this moves the case forward, let it come from something the sleuth did or noticed."),
                    PhraseRule.Phrase(@"\bas luck would have it\b", "'{0}': if this moves the case forward, let it come from something the sleuth did or noticed."),
                    PhraseRule.Phrase(@"\b(just|simply) happened to (find|see|notice|overhear|spot|discover|be passing|come across)\b", "'{0}': a clue found by accident is weaker than one found by reasoning.")
                ]),
            new PhraseRule(GenrePack.Mystery, state, "MYS003", "Clue kept from the reader", DiagnosticSeverity.Message,
                "Fair play: what the detective sees, the reader should see. Hiding it is the oldest cheat in the genre.", [
                    PhraseRule.Phrase(@"\blittle did (s?he|they|I|we) know\b", "'{0}' tells the reader something is being kept from them. Show the clue and let them miss it."),
                    PhraseRule.Phrase(@"\b(what|something) (s?he|they|I) (saw|found|read|noticed)( there| inside| on it)? (made|changed|explained) (everything|it all)\b", "'{0}': if the sleuth learns it here, the reader should too."),
                    PhraseRule.Phrase(@"\b(but|and) (s?he|they|I) (kept|would keep) (that|it|this) to (him|her|them|my)sel(f|ves)\b", "'{0}': a narrator who hides what the sleuth knows is not playing fair.")
                ]),

            new PhraseRule(GenrePack.Fantasy, state, "FAN001", "Worn-out fantasy trope", DiagnosticSeverity.Message,
                "Tropes the genre has leaned on for decades. They can still work, but only on purpose.", [
                    PhraseRule.Phrase(@"\bthe chosen one\b", "'{0}': a hero chosen by fate does less than one who chooses. Who decided, and why them?"),
                    PhraseRule.Phrase(@"\b(an?|the) ancient prophecy\b", "'{0}': prophecies hand the plot to fate. If you keep it, let it be wrong, vague or costly."),
                    PhraseRule.Phrase(@"\b(it|as) (was|had been) (foretold|prophesied|written)\b", "'{0}': prophecies hand the plot to fate. If you keep it, let it be wrong, vague or costly."),
                    PhraseRule.Phrase(@"\bthe dark lord\b", "'{0}': an evil with no name and no reason is hard to fear. What does this one want?"),
                    PhraseRule.Phrase(@"\b(simple|humble|mere) (farm ?(boy|girl|hand)|stable ?(boy|girl|hand)|shepherd)\b", "'{0}': the humble farmhand with a destiny is the genre's most familiar opening.")
                ]),
            new PhraseRule(GenrePack.Fantasy, state, "FAN002", "Modern word in a pre-industrial world", DiagnosticSeverity.Message,
                "Words that come from machines, psychology or the twentieth century break the spell of a pre-industrial setting. Ignore this rule if your world has them.", [
                    PhraseRule.Phrase(@"\b(okay|ok)\b", "'{0}' dates from the 1830s and sounds modern. Try 'all right', 'very well' or 'aye'."),
                    PhraseRule.Phrase(@"\byou guys\b", "'{0}' is modern American speech."),
                    PhraseRule.Phrase(@"\badrenaline\b", "'{0}' was named in 1901. Describe what the body does instead: the shaking hands, the narrowed sight."),
                    PhraseRule.Phrase(@"\b(paranoid|neurotic|psycho|sociopath|trauma|traumati[sz]ed|subconscious(ly)?)\b", "'{0}' is a term from modern psychology."),
                    PhraseRule.Phrase(@"\b(full throttle|hit the brakes|short[- ]circuit(ed)?|on the same wavelength|blow(ing)? off steam|ran out of steam|derail(ed|ing)?|fast[- ]track(ed)?|autopilot|hardwired|bandwidth)\b", "'{0}' is a figure of speech from machines your world may not have."),
                    PhraseRule.Phrase(@"\b(minutes?|seconds?) later\b", "'{0}': without clocks, people count in heartbeats, breaths or the time it takes to do something.")
                ]),

            new MixedUnitsRule(state),
            new PhraseRule(GenrePack.ScienceFiction, state, "SCI002", "Sound in a vacuum", DiagnosticSeverity.Message,
                "There is no sound in a vacuum. Readers of hard SF notice; if you want the noise, let it travel through a hull or a suit.", [
                    PhraseRule.Phrase(@"\b(roar|boom|thunder|rumble|scream|screech|whine|bang|echo)(ed|ing|s)? (across|through|in|into) (the )?(vacuum|void|(silence|blackness|emptiness|depths) of space|space)\b", "'{0}': sound does not carry in a vacuum. Let the character feel it through the hull, or see it in silence.")
                ]),
            new PhraseRule(GenrePack.ScienceFiction, state, "SCI003", "Distance used as time", DiagnosticSeverity.Warning,
                "A light-year and a parsec are distances. Using one as a length of time is the best-known slip in the genre.", [
                    PhraseRule.Phrase(@"\blight[- ]years? (ago|later|earlier|before|from now|since)\b", "'{0}': a light-year is a distance (about 9.5 trillion km), not a time."),
                    PhraseRule.Phrase(@"\b(took|lasted|waited|for|in under|in less than) (\w+ ){0,2}(light[- ]years|parsecs)\b", "'{0}': light-years and parsecs measure distance, not time.")
                ]),

            new LongChapterRule(state),
            new PhraseRule(GenrePack.Thriller, state, "THR002", "'Suddenly' announces the surprise", DiagnosticSeverity.Message,
                "'Suddenly' warns the reader that something sudden is coming, which makes it less sudden. Once is fine; three times in a chapter is a habit.", [
                    PhraseRule.Phrase(@"\b(suddenly|all of a sudden|without warning|out of nowhere)\b", "'{0}' softens the shock it announces, and this chapter leans on it {{count}} times. Cut it and start with the event.")
                ], minimum: 3),
            new RestfulEndingRule(state)
        ];
    }

    [GeneratedRegex(@"love ?interest|romantic lead|\bhero(ine)?\b.*\b(romance|romantic)|\b(romance|romantic)\b.*\bhero(ine)?", RegexOptions.IgnoreCase)]
    internal static partial Regex LoveInterestRoles();

    [GeneratedRegex(@"culprit|murderer|killer|perpetrator|\bthe guilty\b|\bguilty party\b", RegexOptions.IgnoreCase)]
    internal static partial Regex CulpritRoles();
}