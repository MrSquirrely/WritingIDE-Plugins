using System.Text.RegularExpressions;

namespace GenreRulePacks;

/// <summary>A set of rules and manuscript checks for one genre.</summary>
/// <param name="Id">Stored in the project, so it never changes.</param>
/// <param name="GenrePattern">Matches the genre a writer types in the project's properties ("Cozy mystery").</param>
public sealed partial record GenrePack(string Id, string Name, string Description, Regex GenrePattern) {
    public static GenrePack Romance { get; } = new("romance", "Romance",
        "Worn-out romance phrases, love at first sight, a love interest who arrives late, and whether both leads are there at the end.", RomancePattern());

    public static GenrePack Mystery { get; } = new("mystery", "Mystery",
        "Fair play: the culprit and the clues must be on the page early enough, and luck must not solve the case.", MysteryPattern());

    public static GenrePack Fantasy { get; } = new("fantasy", "Fantasy",
        "Tired fantasy tropes, modern words that break a pre-industrial world, and magic without a cost.", FantasyPattern());

    public static GenrePack ScienceFiction { get; } = new("scifi", "Science Fiction",
        "Mixed metric and imperial units, sound in a vacuum, and light-years or parsecs used as time.", ScienceFictionPattern());

    public static GenrePack Thriller { get; } = new("thriller", "Thriller",
        "Pace: chapters that run long, 'suddenly' doing the work of suspense, and chapters that end on a rest.", ThrillerPattern());

    public static IReadOnlyList<GenrePack> All { get; } = [Romance, Mystery, Fantasy, ScienceFiction, Thriller];

    public static GenrePack? Find(string id) {
        return All.FirstOrDefault(pack => pack.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The packs that fit a genre as the writer typed it. "Romantic fantasy" gets two.</summary>
    public static IReadOnlyList<GenrePack> ForGenre(string genre) {
        return All.Where(pack => pack.GenrePattern.IsMatch(genre)).ToList();
    }

    [GeneratedRegex(@"\b(romance|romantic|rom-?com|love story)", RegexOptions.IgnoreCase)]
    private static partial Regex RomancePattern();

    [GeneratedRegex(@"\b(mystery|mysteries|whodunn?it|detective|crime|noir|cozy|cosy)", RegexOptions.IgnoreCase)]
    private static partial Regex MysteryPattern();

    [GeneratedRegex(@"\b(fantasy|sword|sorcery|fairy ?tale|grimdark|romantasy)", RegexOptions.IgnoreCase)]
    private static partial Regex FantasyPattern();

    [GeneratedRegex(@"\b(sci-?fi|science fiction|space opera|cyberpunk|dystopia|sf)\b|\bspace\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScienceFictionPattern();

    [GeneratedRegex(@"\b(thriller|suspense|horror|action|espionage|spy)", RegexOptions.IgnoreCase)]
    private static partial Regex ThrillerPattern();
}

/// <summary>
/// Which packs are on for the open project, and what the rules need to know about the whole manuscript. Rules run on
/// background threads, one chapter at a time, so they read this snapshot instead of asking WritingIDE.
/// </summary>
public sealed class PackState {
    private volatile IReadOnlySet<string> _enabled = new HashSet<string>();
    private volatile ManuscriptFacts _facts = ManuscriptFacts.Empty;

    /// <summary>True when the writer chose the packs; false when they follow the project's genre.</summary>
    public bool IsManual { get; private set; }

    public ManuscriptFacts Facts {
        get => _facts;
        set => _facts = value;
    }

    public bool IsEnabled(GenrePack pack) {
        return _enabled.Contains(pack.Id);
    }

    public IReadOnlyList<GenrePack> Enabled => GenrePack.All.Where(IsEnabled).ToList();

    /// <summary>
    /// Sets the packs from what the project stores (<paramref name="stored"/>: null to follow the genre, otherwise a
    /// comma-separated list of pack ids, possibly empty).
    /// </summary>
    public void Load(string? stored, string genre) {
        IsManual = stored is not null;
        IEnumerable<GenrePack> packs = stored is null
            ? GenrePack.ForGenre(genre)
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GenrePack.Find).OfType<GenrePack>();
        _enabled = packs.Select(static pack => pack.Id).ToHashSet();
    }

    /// <summary>What <see cref="Load"/> reads back for the packs that are on now.</summary>
    public string Serialize() {
        return string.Join(",", Enabled.Select(static pack => pack.Id));
    }
}

/// <summary>Where each story element first appears, by chapter, across the whole manuscript.</summary>
/// <param name="FirstChapter">Story element id to the index of the first chapter that mentions it.</param>
/// <param name="ChapterWords">Each chapter's length in words, in reading order.</param>
public sealed record ManuscriptFacts(IReadOnlyDictionary<Guid, int> FirstChapter, IReadOnlyList<int> ChapterWords) {
    public static ManuscriptFacts Empty { get; } = new(new Dictionary<Guid, int>(), []);

    public int ChapterCount => ChapterWords.Count;

    /// <summary>How far into the story a chapter starts, from 0 to 1, by words.</summary>
    public double PositionOf(int chapterIndex) {
        int total = ChapterWords.Sum();
        return (total == 0) || (chapterIndex <= 0) ? 0 : (double)ChapterWords.Take(chapterIndex).Sum() / total;
    }
}