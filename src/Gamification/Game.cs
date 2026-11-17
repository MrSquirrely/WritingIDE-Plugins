using System.Text.Json;

namespace Gamification;

/// <summary>The writer's profile: experience, achievements and counters. Kept per user, across projects.</summary>
public sealed class GameState {
    public long Xp { get; set; }

    /// <summary>Achievement id to the moment it was unlocked.</summary>
    public Dictionary<string, DateTime> Unlocked { get; set; } = [];

    public int SprintsCompleted { get; set; }

    public int BestSprintWords { get; set; }

    public int GoalsReached { get; set; }

    /// <summary>Words written for experience since the profile began.</summary>
    public long LifetimeWords { get; set; }

    public bool WroteAtNight { get; set; }

    public bool WroteEarly { get; set; }

    /// <summary>Per project: the day and today's word count last credited, so words are never counted twice.</summary>
    public Dictionary<string, CreditedDay> Credited { get; set; } = [];

    public static GameState Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return new GameState();
        }
        try {
            return JsonSerializer.Deserialize<GameState>(json) ?? new GameState();
        }
        catch (JsonException) {
            return new GameState();
        }
    }

    public string ToJson() {
        return JsonSerializer.Serialize(this);
    }
}

public sealed record CreditedDay(DateOnly Date, int Words);

/// <summary>What the achievements look at: the open project and the writer's progress in it.</summary>
/// <param name="FinalChapters">Chapters whose status is Final.</param>
/// <param name="DeadCharacters">Characters with a Died attribute.</param>
public sealed record GameSnapshot(int TotalWords, int TargetWords, int WordsToday, int Streak, int Chapters, int FinalChapters,
    int Characters, int Places, int Lore, int DeadCharacters, int Relationships);

/// <summary>Something to unlock.</summary>
/// <param name="Glyph">A Segoe MDL2 Assets character.</param>
public sealed record Achievement(string Id, string Name, string Description, string Glyph, int Xp, Func<GameSnapshot, GameState, bool> IsEarned);

/// <summary>A level and where the writer stands in it.</summary>
/// <param name="Floor">Experience at which the level starts.</param>
/// <param name="Ceiling">Experience at which the next level starts.</param>
public sealed record LevelInfo(int Level, string Title, long Floor, long Ceiling) {
    public double Progress(long xp) {
        return Math.Clamp((double)(xp - Floor) / Math.Max(1, Ceiling - Floor), 0, 1);
    }
}

/// <summary>The rules of the game: experience, levels and achievements. No UI and no WritingIDE types, so it can be tested.</summary>
public static class GameEngine {
    public const int GoalXp = 100;
    public const int SprintXp = 50;

    private static readonly string[] Titles = [
        "Scribbler", "Note-Taker", "Apprentice", "Journeyman", "Wordsmith", "Storyteller", "Chronicler", "Novelist",
        "Author", "Bard", "Master of the Craft", "Legend"
    ];

    public static IReadOnlyList<Achievement> Achievements { get; } = [
        new("words-100", "First Words", "Have 100 words in a manuscript.", "", 25, static (s, _) => s.TotalWords >= 100),
        new("words-1k", "Getting Somewhere", "Reach 1,000 words in a manuscript.", "", 50, static (s, _) => s.TotalWords >= 1_000),
        new("words-10k", "Double Digits", "Reach 10,000 words in a manuscript.", "", 150, static (s, _) => s.TotalWords >= 10_000),
        new("words-25k", "Novella", "Reach 25,000 words in a manuscript.", "", 300, static (s, _) => s.TotalWords >= 25_000),
        new("words-50k", "Fifty Thousand", "Reach 50,000 words in a manuscript: a short novel.", "", 500, static (s, _) => s.TotalWords >= 50_000),
        new("words-100k", "Doorstopper", "Reach 100,000 words in a manuscript.", "", 1000, static (s, _) => s.TotalWords >= 100_000),
        new("target", "The Finish Line", "Reach a manuscript's target word count.", "", 1000, static (s, _) => (s.TargetWords > 0) && (s.TotalWords >= s.TargetWords)),

        new("day-2k", "On a Roll", "Write 2,000 words in a day.", "", 150, static (s, _) => s.WordsToday >= 2_000),
        new("day-5k", "Marathon", "Write 5,000 words in a day.", "", 400, static (s, _) => s.WordsToday >= 5_000),
        new("goal-1", "A Day's Work", "Reach your daily goal.", "", 50, static (_, g) => g.GoalsReached >= 1),
        new("goal-10", "Reliable", "Reach your daily goal 10 times.", "", 200, static (_, g) => g.GoalsReached >= 10),
        new("goal-50", "Professional", "Reach your daily goal 50 times.", "", 750, static (_, g) => g.GoalsReached >= 50),

        new("streak-3", "Hat Trick", "Meet your goal 3 days in a row.", "", 100, static (s, _) => s.Streak >= 3),
        new("streak-7", "A Week of It", "Meet your goal 7 days in a row.", "", 250, static (s, _) => s.Streak >= 7),
        new("streak-30", "Habit", "Meet your goal 30 days in a row.", "", 1000, static (s, _) => s.Streak >= 30),
        new("streak-100", "Unstoppable", "Meet your goal 100 days in a row.", "", 3000, static (s, _) => s.Streak >= 100),

        new("sprint-1", "Off the Blocks", "Finish a writing sprint.", "", 50, static (_, g) => g.SprintsCompleted >= 1),
        new("sprint-10", "Sprinter", "Finish 10 writing sprints.", "", 200, static (_, g) => g.SprintsCompleted >= 10),
        new("sprint-500", "Quick Hands", "Write 500 words in one sprint.", "", 200, static (_, g) => g.BestSprintWords >= 500),

        new("night-owl", "Night Owl", "Write between midnight and four in the morning.", "", 50, static (_, g) => g.WroteAtNight),
        new("early-bird", "Early Bird", "Write between five and seven in the morning.", "", 50, static (_, g) => g.WroteEarly),

        new("cast-5", "Ensemble", "Create 5 characters.", "", 50, static (s, _) => s.Characters >= 5),
        new("cast-20", "Cast of Thousands", "Create 20 characters.", "", 200, static (s, _) => s.Characters >= 20),
        new("places-5", "World Traveller", "Create 5 places.", "", 50, static (s, _) => s.Places >= 5),
        new("lore-3", "Lorekeeper", "Write 3 lore entries.", "", 75, static (s, _) => s.Lore >= 3),
        new("relationships-5", "It's Complicated", "Define 5 relationships between story elements.", "", 75, static (s, _) => s.Relationships >= 5),
        new("darling", "Kill Your Darlings", "Give a character a Died attribute.", "", 75, static (s, _) => s.DeadCharacters >= 1),

        new("chapters-10", "Ten Chapters In", "Have 10 chapters.", "", 100, static (s, _) => s.Chapters >= 10),
        new("final-1", "Polished", "Mark a chapter as Final.", "", 100, static (s, _) => s.FinalChapters >= 1),
        new("the-end", "The End", "Mark every chapter as Final (in a book of at least three).", "", 1500, static (s, _) => (s.Chapters >= 3) && (s.FinalChapters == s.Chapters))
    ];

    /// <summary>Experience at which a level starts: 0, 500, 1,500, 3,000, 5,000 and so on.</summary>
    public static long FloorOf(int level) {
        return 250L * level * (level - 1);
    }

    public static LevelInfo LevelFor(long xp) {
        int level = 1;
        while (FloorOf(level + 1) <= xp) {
            level++;
        }
        // A new title every three levels.
        string title = Titles[Math.Min(Titles.Length - 1, (level - 1) / 3)];
        return new LevelInfo(level, title, FloorOf(level), FloorOf(level + 1));
    }

    /// <summary>
    /// Credits the words written today in one project: one experience point a word. Only the growth since the last call
    /// counts, so deleting and retyping earns nothing twice. Returns the experience gained.
    /// </summary>
    public static int CreditWords(GameState state, string projectKey, DateOnly today, int wordsToday, TimeOnly time) {
        state.Credited.TryGetValue(projectKey, out CreditedDay? last);
        int already = last?.Date == today ? last.Words : 0;
        int gained = Math.Max(0, wordsToday - already);
        if ((last is null) || (last.Date != today) || (wordsToday > already)) {
            state.Credited[projectKey] = new CreditedDay(today, Math.Max(wordsToday, already));
        }
        if (gained > 0) {
            state.Xp += gained;
            state.LifetimeWords += gained;
            state.WroteAtNight |= time.Hour < 4;
            state.WroteEarly |= time.Hour is >= 5 and < 7;
        }
        return gained;
    }

    public static void CreditGoal(GameState state) {
        state.GoalsReached++;
        state.Xp += GoalXp;
    }

    /// <summary>A finished sprint earns experience; one stopped early only counts towards the best sprint.</summary>
    public static void CreditSprint(GameState state, int words, bool completed) {
        state.BestSprintWords = Math.Max(state.BestSprintWords, words);
        if (completed) {
            state.SprintsCompleted++;
            state.Xp += SprintXp;
        }
    }

    /// <summary>Unlocks every achievement that is now earned, adds its experience, and returns the new ones.</summary>
    public static IReadOnlyList<Achievement> Evaluate(GameState state, GameSnapshot snapshot, DateTime now) {
        List<Achievement> unlocked = [];
        foreach (Achievement achievement in Achievements) {
            if (!state.Unlocked.ContainsKey(achievement.Id) && achievement.IsEarned(snapshot, state)) {
                state.Unlocked[achievement.Id] = now;
                state.Xp += achievement.Xp;
                unlocked.Add(achievement);
            }
        }
        return unlocked;
    }
}