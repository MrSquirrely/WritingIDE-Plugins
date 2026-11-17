using Gamification;

namespace Plugins.Tests;

public sealed class GamificationTests {
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly TimeOnly Noon = new(12, 0);

    private static GameSnapshot Snapshot(int totalWords = 0, int wordsToday = 0, int streak = 0, int chapters = 0, int finalChapters = 0, int characters = 0, int dead = 0) {
        return new GameSnapshot(totalWords, 80000, wordsToday, streak, chapters, finalChapters, characters, 0, 0, dead, 0);
    }

    [Fact]
    public void WordsAreCreditedOnceHoweverOftenTheyAreCounted() {
        GameState state = new();

        Assert.Equal(300, GameEngine.CreditWords(state, "novel", Today, 300, Noon));
        Assert.Equal(0, GameEngine.CreditWords(state, "novel", Today, 300, Noon));
        // Cutting 100 words and writing them again earns nothing.
        Assert.Equal(0, GameEngine.CreditWords(state, "novel", Today, 200, Noon));
        Assert.Equal(0, GameEngine.CreditWords(state, "novel", Today, 300, Noon));
        Assert.Equal(50, GameEngine.CreditWords(state, "novel", Today, 350, Noon));
        // Another project and another day each start from zero.
        Assert.Equal(120, GameEngine.CreditWords(state, "other", Today, 120, Noon));
        Assert.Equal(80, GameEngine.CreditWords(state, "novel", Today.AddDays(1), 80, Noon));

        Assert.Equal(550, state.Xp);
        Assert.Equal(550, state.LifetimeWords);
    }

    [Fact]
    public void LevelsGetLongerAndTitlesChangeEveryThreeLevels() {
        Assert.Equal(1, GameEngine.LevelFor(0).Level);
        Assert.Equal(1, GameEngine.LevelFor(499).Level);
        Assert.Equal(2, GameEngine.LevelFor(500).Level);
        Assert.Equal(3, GameEngine.LevelFor(1500).Level);
        Assert.Equal(5, GameEngine.LevelFor(5000).Level);
        Assert.Equal("Scribbler", GameEngine.LevelFor(1500).Title);
        Assert.Equal("Note-Taker", GameEngine.LevelFor(3000).Title);
        Assert.Equal("Legend", GameEngine.LevelFor(10_000_000).Title);

        LevelInfo level = GameEngine.LevelFor(1000);
        Assert.Equal((500, 1500), (level.Floor, level.Ceiling));
        Assert.Equal(0.5, level.Progress(1000));
    }

    [Fact]
    public void AchievementsUnlockOnceAndAddTheirExperience() {
        GameState state = new();
        DateTime now = new(2026, 11, 17, 12, 0, 0);

        IReadOnlyList<Achievement> first = GameEngine.Evaluate(state, Snapshot(totalWords: 1200, characters: 5), now);
        IReadOnlyList<Achievement> again = GameEngine.Evaluate(state, Snapshot(totalWords: 1200, characters: 5), now);

        Assert.Equal(["words-100", "words-1k", "cast-5"], first.Select(static achievement => achievement.Id));
        Assert.Empty(again);
        Assert.Equal(first.Sum(static achievement => achievement.Xp), state.Xp);
        Assert.Equal(now, state.Unlocked["cast-5"]);
    }

    [Fact]
    public void GoalsSprintsAndWritingHoursCount() {
        GameState state = new();
        DateTime now = new(2026, 11, 17, 2, 0, 0);

        GameEngine.CreditGoal(state);
        GameEngine.CreditSprint(state, 520, completed: true);
        GameEngine.CreditSprint(state, 90, completed: false);
        GameEngine.CreditWords(state, "novel", Today, 10, new TimeOnly(2, 0));
        List<string> unlocked = GameEngine.Evaluate(state, Snapshot(streak: 3), now).Select(static achievement => achievement.Id).ToList();

        Assert.Equal(1, state.SprintsCompleted);
        Assert.Equal(520, state.BestSprintWords);
        Assert.Equal(["goal-1", "streak-3", "sprint-1", "sprint-500", "night-owl"], unlocked);
        Assert.DoesNotContain("early-bird", unlocked);
    }

    [Fact]
    public void TheEndNeedsEveryChapterFinalInABookOfAtLeastThree() {
        Assert.DoesNotContain(GameEngine.Evaluate(new GameState(), Snapshot(chapters: 2, finalChapters: 2), DateTime.Now), static achievement => achievement.Id == "the-end");
        Assert.DoesNotContain(GameEngine.Evaluate(new GameState(), Snapshot(chapters: 5, finalChapters: 4), DateTime.Now), static achievement => achievement.Id == "the-end");
        Assert.Contains(GameEngine.Evaluate(new GameState(), Snapshot(chapters: 5, finalChapters: 5, dead: 1), DateTime.Now), static achievement => achievement.Id == "the-end");
    }

    [Fact]
    public void TheProfileSurvivesSavingAndABrokenFile() {
        GameState state = new() { Xp = 1234, GoalsReached = 3 };
        state.Unlocked["words-1k"] = new DateTime(2026, 11, 1);
        GameEngine.CreditWords(state, @"C:\Novels\Tide", Today, 200, Noon);

        GameState loaded = GameState.Parse(state.ToJson());

        Assert.Equal(1434, loaded.Xp);
        Assert.Equal(3, loaded.GoalsReached);
        Assert.Equal(new DateTime(2026, 11, 1), loaded.Unlocked["words-1k"]);
        Assert.Equal(new CreditedDay(Today, 200), loaded.Credited[@"C:\Novels\Tide"]);
        Assert.Equal(0, GameState.Parse("{ not json").Xp);
        Assert.Equal(0, GameState.Parse(null).Xp);
    }

    [Fact]
    public void AchievementIdsAreUnique() {
        Assert.Equal(GameEngine.Achievements.Count, GameEngine.Achievements.Select(static achievement => achievement.Id).Distinct().Count());
    }
}