using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace Gamification;

/// <summary>
/// Gamification: experience for the words you write, levels, and achievements for milestones, streaks, sprints and
/// worldbuilding. The profile belongs to the writer, not to one project.
/// </summary>
public sealed class GamificationPlugin : IWritingIdePlugin {
    private const string StateKey = "state";

    private IPluginHost _host = null!;
    private GameState _state = new();
    private DispatcherTimer? _timer;
    private bool _quiet;

    public string Id => "com.mrsquirrely.writingide.gamification";

    public string Name => "Gamification";

    public string Version => "1.0.0";

    public string Description => "Experience, levels and achievements for writing, streaks, sprints and worldbuilding.";

    internal GameState State => _state;

    /// <summary>Raised on the UI thread when experience or achievements changed.</summary>
    public event EventHandler? Changed;

    public void Initialize(IPluginHost host) {
        _host = host;
        _state = GameState.Parse(host.Storage.GetSetting(StateKey));
        host.RegisterToolWindow(new PluginToolWindow("achievements", "Achievements", () => new AchievementsPanel(this)) {
            Description = "Your level, experience and achievements."
        });
        // What an existing project has already earned unlocks quietly, as one notification, when it opens.
        host.ProjectOpened += (_, _) => Update(opening: true);
        host.ProjectSaved += (_, _) => Update(opening: false);
        host.DailyGoalReached += (_, _) => {
            GameEngine.CreditGoal(_state);
            Update(opening: false);
        };
        host.SprintFinished += (_, e) => {
            GameEngine.CreditSprint(_state, e.Words, e.Completed);
            Update(opening: false);
        };
        // Typing: wait until the writer pauses, then credit the words.
        host.ChapterTextChanged += (_, _) => {
            if (_timer is null) {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _timer.Tick += (_, _) => {
                    _timer.Stop();
                    Update(opening: false);
                };
            }
            _timer.Stop();
            _timer.Start();
        };
    }

    /// <summary>Credits new words, unlocks what is earned, and tells the writer. Runs on the UI thread.</summary>
    private void Update(bool opening) {
        if (_quiet || !_host.Project.IsOpen) {
            return;
        }
        _quiet = true;
        try {
            int levelBefore = GameEngine.LevelFor(_state.Xp).Level;
            long xpBefore = _state.Xp;
            WritingStats stats = _host.Project.GetWritingStats();
            DateTime now = DateTime.Now;
            GameEngine.CreditWords(_state, _host.Project.DirectoryPath, DateOnly.FromDateTime(now), stats.WordsToday, TimeOnly.FromDateTime(now));
            IReadOnlyList<Achievement> unlocked = GameEngine.Evaluate(_state, ReadSnapshot(stats), now);
            if ((_state.Xp == xpBefore) && (unlocked.Count == 0)) {
                return;
            }
            _host.Storage.SetSetting(StateKey, _state.ToJson());

            if (unlocked.Count == 1) {
                _host.Notify($"Achievement unlocked: {unlocked[0].Name}. {unlocked[0].Description} (+{unlocked[0].Xp} XP)");
            }
            else if (unlocked.Count > 1) {
                _host.Notify(opening
                    ? $"{unlocked.Count} achievements for what you have already written: {string.Join(", ", unlocked.Select(static achievement => achievement.Name))}."
                    : $"{unlocked.Count} achievements unlocked: {string.Join(", ", unlocked.Select(static achievement => achievement.Name))}.");
            }
            foreach (Achievement achievement in unlocked) {
                _host.Log($"Achievement unlocked: {achievement.Name} (+{achievement.Xp} XP)");
            }
            LevelInfo level = GameEngine.LevelFor(_state.Xp);
            if (level.Level > levelBefore) {
                _host.Notify($"Level {level.Level}: {level.Title}.");
                _host.SetStatusText($"Level up! You are now level {level.Level}, {level.Title}.");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally {
            _quiet = false;
        }
    }

    private GameSnapshot ReadSnapshot(WritingStats stats) {
        IProjectApi project = _host.Project;
        IReadOnlyList<ChapterInfo> chapters = project.GetChapters();
        IReadOnlyList<EntityInfo> entities = project.GetEntities();
        List<EntityInfo> characters = entities.Where(static entity => entity.Kind == EntityKind.Character).ToList();
        return new GameSnapshot(
            TotalWords: stats.TotalWords,
            TargetWords: stats.TargetWords,
            WordsToday: stats.WordsToday,
            Streak: stats.Streak,
            Chapters: chapters.Count,
            FinalChapters: chapters.Count(static chapter => chapter.Status.Equals("Final", StringComparison.OrdinalIgnoreCase)),
            Characters: characters.Count,
            Places: entities.Count(static entity => entity.Kind == EntityKind.Place),
            Lore: entities.Count(static entity => entity.Kind == EntityKind.Lore),
            DeadCharacters: characters.Count(static character => character.Attributes.Keys.Any(static key => key.Equals("Died", StringComparison.OrdinalIgnoreCase))),
            Relationships: entities.Sum(entity => project.GetEntityRelationships(entity.Id).Count));
    }
}

/// <summary>The Achievements window: level, experience, and every achievement, unlocked or not.</summary>
public sealed class AchievementsPanel : UserControl {
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private readonly GamificationPlugin _plugin;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 16, 24, 24) };

    public AchievementsPanel(GamificationPlugin plugin) {
        _plugin = plugin;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Loaded += (_, _) => {
            _plugin.Changed += OnChanged;
            Build();
        };
        Unloaded += (_, _) => _plugin.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) {
        Build();
    }

    private void Build() {
        GameState state = _plugin.State;
        LevelInfo level = GameEngine.LevelFor(state.Xp);
        _body.Children.Clear();

        _body.Children.Add(new TextBlock { Text = $"Level {level.Level}: {level.Title}", FontSize = 22, FontWeight = FontWeights.SemiBold });
        _body.Children.Add(Subtle($"{state.Xp:N0} XP. {level.Ceiling - state.Xp:N0} to level {level.Level + 1}. "
            + "One XP for every word you write, more for reaching your daily goal, finishing sprints and unlocking achievements.", new Thickness(0, 2, 0, 8)));

        // A plain two-part bar: the styled progress bar shows a percentage this does not need.
        Grid bar = new() { Height = 10, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Width = 560 };
        double progress = level.Progress(state.Xp);
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(progress, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - progress, GridUnitType.Star) });
        Border track = new() { CornerRadius = new CornerRadius(5) };
        track.SetResourceReference(Border.BackgroundProperty, "BadgeBrush");
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        Border filled = new() { CornerRadius = new CornerRadius(5) };
        filled.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        bar.Children.Add(filled);
        System.Windows.Automation.AutomationProperties.SetName(bar, $"Progress to level {level.Level + 1}: {progress:P0}");
        _body.Children.Add(bar);

        _body.Children.Add(Subtle($"{state.LifetimeWords:N0} words written for XP   ·   {state.GoalsReached} daily goal{(state.GoalsReached == 1 ? "" : "s")} reached   ·   "
            + $"{state.SprintsCompleted} sprint{(state.SprintsCompleted == 1 ? "" : "s")} finished{(state.BestSprintWords > 0 ? $" (best: {state.BestSprintWords:N0} words)" : "")}", new Thickness(0, 8, 0, 0)));

        int unlocked = GameEngine.Achievements.Count(achievement => state.Unlocked.ContainsKey(achievement.Id));
        _body.Children.Add(new TextBlock { Text = $"Achievements ({unlocked} of {GameEngine.Achievements.Count})", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
        WrapPanel badges = new() { Margin = new Thickness(-6, 0, 0, 0) };
        // Unlocked first, newest at the front.
        foreach (Achievement achievement in GameEngine.Achievements
            .OrderByDescending(achievement => state.Unlocked.ContainsKey(achievement.Id))
            .ThenByDescending(achievement => state.Unlocked.GetValueOrDefault(achievement.Id))) {
            badges.Children.Add(Badge(achievement, state.Unlocked.TryGetValue(achievement.Id, out DateTime when) ? when : null));
        }
        _body.Children.Add(badges);
    }

    private static Border Badge(Achievement achievement, DateTime? unlocked) {
        TextBlock glyph = new() { Text = achievement.Glyph, FontFamily = IconFont, FontSize = 26, Width = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, unlocked is null ? "SubtleTextBrush" : "AccentBrush");
        DockPanel.SetDock(glyph, Dock.Left);

        StackPanel text = new() { Margin = new Thickness(6, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = achievement.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Subtle(achievement.Description, new Thickness(0, 1, 0, 2), 11));
        text.Children.Add(Subtle(unlocked is DateTime when ? $"Unlocked {when:d MMM yyyy}   ·   +{achievement.Xp} XP" : $"Locked   ·   {achievement.Xp} XP", default, 11));

        DockPanel content = new();
        content.Children.Add(glyph);
        content.Children.Add(text);
        Border card = new() { Child = content, Width = 270, MinHeight = 78, Padding = new Thickness(8, 8, 10, 8), Margin = new Thickness(6), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Opacity = unlocked is null ? 0.6 : 1 };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, unlocked is null ? "DividerBrush" : "AccentBrush");
        System.Windows.Automation.AutomationProperties.SetName(card, $"{achievement.Name}: {(unlocked is null ? "locked" : "unlocked")}. {achievement.Description}");
        return card;
    }

    private static TextBlock Subtle(string text, Thickness margin, double size = 12) {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = size };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        return block;
    }
}