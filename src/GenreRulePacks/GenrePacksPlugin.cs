using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using WritingIDE.Sdk;

namespace GenreRulePacks;

/// <summary>
/// Genre Rule Packs: rules and manuscript checks for romance, mystery, fantasy, science fiction and thrillers. The
/// packs follow the project's genre unless the writer picks them in the Genre Packs window.
/// </summary>
public sealed class GenrePacksPlugin : IWritingIdePlugin {
    private const string PacksKey = "packs";

    private readonly PackState _state = new();
    private IPluginHost _host = null!;
    private int _factsVersion;

    public string Id => "com.mrsquirrely.writingide.genrepacks";

    public string Name => "Genre Rule Packs";

    public string Version => "1.0.0";

    public string Description => "Rules and manuscript checks for romance, mystery, fantasy, science fiction and thrillers.";

    internal PackState State => _state;

    public void Initialize(IPluginHost host) {
        _host = host;
        foreach (PackRule rule in GenreRules.Create(_state)) {
            host.RegisterRule(rule);
        }
        host.RegisterToolWindow(new PluginToolWindow("packs", "Genre Packs", () => new PacksPanel(this, host)) {
            Description = "Choose the genre packs for this project and run the genre report."
        });
        host.RegisterCommand(new PluginCommand("Genre Report", _ => host.ShowToolWindow("packs")) {
            Description = "Checks the whole manuscript against the genre packs that are on."
        });
        host.ProjectOpened += (_, _) => {
            LoadPacks();
            RefreshFacts();
        };
        host.ProjectSaved += (_, _) => RefreshFacts();
        host.ProjectClosed += (_, _) => {
            _state.Load("", "");
            _state.Facts = ManuscriptFacts.Empty;
        };
    }

    /// <summary>Raised on the UI thread when the packs that are on changed.</summary>
    public event EventHandler? PacksChanged;

    private void LoadPacks() {
        _state.Load(_host.Storage.GetProjectValue(PacksKey), _host.Project.Genre);
        PacksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Follows the project's genre again (<paramref name="packs"/> null), or turns on exactly these packs.</summary>
    public void SetPacks(IEnumerable<GenrePack>? packs) {
        _host.Storage.SetProjectValue(PacksKey, packs is null ? null : string.Join(",", packs.Select(static pack => pack.Id)));
        LoadPacks();
        RefreshFacts();
        _host.Project.Reanalyze();
    }

    /// <summary>
    /// Works out where every story element first appears, off the UI thread: the late-arrival rules need it, and it
    /// means reading the whole manuscript.
    /// </summary>
    private void RefreshFacts() {
        int version = Interlocked.Increment(ref _factsVersion);
        if (!_host.Project.IsOpen || (_state.Enabled.Count == 0)) {
            return;
        }
        Task.Run(() => {
            try {
                ManuscriptFacts facts = ManuscriptSnapshot.Read(_host.Project).ToFacts();
                if (version == Volatile.Read(ref _factsVersion)) {
                    _state.Facts = facts;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or TaskCanceledException) {
                // The project closed or a chapter was removed while reading: the next save tries again.
            }
        });
    }

    public Task<IReadOnlyList<ReportLine>> BuildReportAsync() {
        IReadOnlyList<GenrePack> packs = _state.Enabled;
        return Task.Run(() => {
            ManuscriptSnapshot manuscript = ManuscriptSnapshot.Read(_host.Project);
            _state.Facts = manuscript.ToFacts();
            return GenreReport.Build(packs, manuscript);
        });
    }
}

/// <summary>The Genre Packs window: which packs are on, what each checks, and the genre report.</summary>
public sealed class PacksPanel : UserControl {
    private readonly GenrePacksPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly CheckBox _automatic = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly Dictionary<GenrePack, CheckBox> _boxes = [];
    private readonly StackPanel _report = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _run = new() { Content = "Run genre report", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _loading;

    public PacksPanel(GenrePacksPlugin plugin, IPluginHost host) {
        _plugin = plugin;
        _host = host;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");

        StackPanel body = new() { Margin = new Thickness(24, 16, 24, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = "Genre Packs", FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Subtle("Each pack adds rules to the Error List (press F6 to check the whole manuscript) and checks to the genre report below. "
            + "The rules give advice, not errors: switch off any one of them in Tools > Options > Grammar Rules.", new Thickness(0, 2, 0, 14)));

        _automatic.Checked += (_, _) => OnAutomaticChanged();
        _automatic.Unchecked += (_, _) => OnAutomaticChanged();
        body.Children.Add(_automatic);

        IReadOnlyList<PackRule> rules = GenreRules.Create(plugin.State);
        foreach (GenrePack pack in GenrePack.All) {
            CheckBox box = new() { Content = new TextBlock { Text = pack.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 } };
            System.Windows.Automation.AutomationProperties.SetName(box, $"{pack.Name} pack");
            box.Checked += (_, _) => OnPackChanged();
            box.Unchecked += (_, _) => OnPackChanged();
            _boxes[pack] = box;
            StackPanel panel = new() { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(box);
            panel.Children.Add(Subtle(pack.Description, new Thickness(24, 2, 0, 2)));
            panel.Children.Add(Subtle(string.Join("   ", rules.Where(rule => rule.Pack == pack).Select(static rule => $"{rule.Id} {rule.Title}")), new Thickness(24, 0, 0, 0), 11));
            body.Children.Add(panel);
        }

        body.Children.Add(new TextBlock { Text = "Genre report", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        body.Children.Add(Subtle("Checks that need the whole book: who arrives when, whether clues are planted, how the chapters are paced. "
            + "It reads roles from your characters' Role attribute (Love interest, Culprit) and clues from items with a Clue attribute.", new Thickness(0, 0, 0, 8)));
        _run.SetResourceReference(StyleProperty, "TileButton");
        _run.Click += async (_, _) => await RunReportAsync();
        body.Children.Add(_run);
        body.Children.Add(_report);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Loaded += (_, _) => {
            _plugin.PacksChanged += OnPacksChanged;
            Show();
        };
        Unloaded += (_, _) => _plugin.PacksChanged -= OnPacksChanged;
    }

    private void OnPacksChanged(object? sender, EventArgs e) {
        Show();
    }

    /// <summary>Sets the check boxes from the plugin's state.</summary>
    private void Show() {
        _loading = true;
        string genre = _host.Project.Genre;
        _automatic.Content = genre.Length > 0
            ? $"Choose the packs from the project's genre (\"{genre}\")"
            : "Choose the packs from the project's genre (none set: Project > Properties)";
        _automatic.IsChecked = !_plugin.State.IsManual;
        foreach ((GenrePack pack, CheckBox box) in _boxes) {
            box.IsChecked = _plugin.State.IsEnabled(pack);
            box.IsEnabled = _plugin.State.IsManual;
        }
        _loading = false;
    }

    private void OnAutomaticChanged() {
        if (_loading) {
            return;
        }
        // Switching to manual keeps the packs that were on; switching back follows the genre.
        _plugin.SetPacks(_automatic.IsChecked == true ? null : _plugin.State.Enabled);
    }

    private void OnPackChanged() {
        if (_loading) {
            return;
        }
        _plugin.SetPacks(_boxes.Where(static pair => pair.Value.IsChecked == true).Select(static pair => pair.Key).ToList());
    }

    private async Task RunReportAsync() {
        _report.Children.Clear();
        if (_plugin.State.Enabled.Count == 0) {
            _report.Children.Add(Subtle("No pack is on. Tick one above, or set the project's genre in Project > Properties.", default));
            return;
        }
        _run.IsEnabled = false;
        try {
            IReadOnlyList<ReportLine> lines = await _plugin.BuildReportAsync();
            foreach (IGrouping<GenrePack, ReportLine> group in lines.GroupBy(static line => line.Pack)) {
                _report.Children.Add(new TextBlock { Text = group.Key.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
                foreach (ReportLine line in group) {
                    DockPanel row = new() { Margin = new Thickness(0, 0, 0, 4) };
                    TextBlock mark = new() { Text = line.Passed ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 22, Margin = new Thickness(0, 2, 0, 0) };
                    mark.SetResourceReference(TextBlock.ForegroundProperty, line.Passed ? "SuccessBrush" : "WarningBrush");
                    System.Windows.Automation.AutomationProperties.SetName(mark, line.Passed ? "Passed" : "To look at");
                    DockPanel.SetDock(mark, Dock.Left);
                    row.Children.Add(mark);
                    row.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap });
                    _report.Children.Add(row);
                }
            }
            int open = lines.Count(static line => !line.Passed);
            _host.SetStatusText(open == 0 ? "Genre report: nothing to look at." : $"Genre report: {open} thing{(open == 1 ? "" : "s")} to look at.");
            _host.Project.Reanalyze();
        }
        catch (Exception exception) {
            _report.Children.Add(Subtle($"The report could not be built: {exception.Message}", default));
        }
        finally {
            _run.IsEnabled = true;
        }
    }

    private static TextBlock Subtle(string text, Thickness margin, double size = 12) {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = size };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        return block;
    }
}