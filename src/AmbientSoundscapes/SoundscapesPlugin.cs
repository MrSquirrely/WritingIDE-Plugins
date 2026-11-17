using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace AmbientSoundscapes;

/// <summary>
/// Ambient Soundscapes: looping background sound (rain, wind, a fire, a tavern) mixed to taste, which can follow the
/// story: a chapter plays the mix saved for it, or the soundscape of the place it is set in.
/// </summary>
public sealed class SoundscapesPlugin : IWritingIdePlugin {
    private const string MasterKey = "master";
    private const string FollowKey = "follow";
    private const string MixKey = "mix";
    private const string ChapterMixPrefix = "mix:";

    /// <summary>The attribute on a place that names its sounds: "Soundscape: Ocean 70, Wind 30".</summary>
    public const string PlaceAttribute = "Soundscape";

    private readonly AudioMixer _mixer = new();
    private readonly List<SoundLayer> _layers = [];
    private IPluginHost _host = null!;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _saveTimer;
    private int _silentSeconds;

    public string Id => "com.mrsquirrely.writingide.soundscapes";

    public string Name => "Ambient Soundscapes";

    public string Version => "1.0.0";

    public string Description => "Looping background sound that can follow the place a chapter is set in.";

    public IReadOnlyList<SoundLayer> Layers => _layers;

    public bool IsPlaying { get; private set; }

    /// <summary>True when the mix changes with the chapter in front.</summary>
    public bool Follow { get; private set; }

    /// <summary>What the current mix came from ("Port Ashby", "this chapter's own mix"), for the panel.</summary>
    public string Source { get; private set; } = "";

    public float Master {
        get => _mixer.Master;
        set {
            _mixer.Master = value;
            SaveSoon();
        }
    }

    public string? Problem => _mixer.Problem;

    /// <summary>Raised on the UI thread when the mix, the source or the playing state changed.</summary>
    public event EventHandler? Changed;

    public string SoundsFolder => Path.Combine(_host.Storage.DataDirectory, "Sounds");

    public void Initialize(IPluginHost host) {
        _host = host;
        _mixer.Master = float.TryParse(host.Storage.GetSetting(MasterKey), System.Globalization.CultureInfo.InvariantCulture, out float master) ? master : 0.6f;
        Follow = host.Storage.GetSetting(FollowKey) != "false";
        LoadLayers();
        ApplyMix(MixText.Parse(host.Storage.GetSetting(MixKey) ?? "", _layers.Select(static layer => layer.Name)), "", start: false);

        host.RegisterToolWindow(new PluginToolWindow("mixer", "Soundscape", () => new SoundscapePanel(this, host)) {
            Description = "Mix background sounds, and let them follow the place a chapter is set in."
        });
        host.RegisterCommand(new PluginCommand("Soundscape: Play or Pause", _ => SetPlaying(!IsPlaying)) {
            Description = "Starts or stops the background sound."
        });
        host.ActiveChapterChanged += (_, e) => {
            if (Follow && IsPlaying && (e.ChapterId is Guid chapterId)) {
                FollowChapter(chapterId);
            }
        };
        host.ProjectClosed += (_, _) => SetPlaying(false);
    }

    /// <summary>The built-in sounds, then the WAV files in the writer's Sounds folder.</summary>
    public void LoadLayers() {
        Dictionary<string, float> volumes = _layers.ToDictionary(static layer => layer.Name, static layer => layer.Volume, StringComparer.OrdinalIgnoreCase);
        _layers.Clear();
        _layers.AddRange(SoundSynth.Presets.Select(static preset => new SoundLayer(preset, isCustom: false)));
        try {
            Directory.CreateDirectory(SoundsFolder);
            foreach (string file in Directory.EnumerateFiles(SoundsFolder, "*.wav").Order(StringComparer.OrdinalIgnoreCase)) {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!_layers.Any(layer => layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) {
                    _layers.Add(new SoundLayer(name, isCustom: true));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _host.Log($"The Sounds folder could not be read: {exception.Message}");
        }
        foreach (SoundLayer layer in _layers) {
            layer.Volume = volumes.GetValueOrDefault(layer.Name);
            if (layer.Volume > 0) {
                EnsureLoaded(layer);
            }
        }
        _mixer.SetLayers(_layers);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes (or reads) a layer's loop in the background, the first time it is wanted.</summary>
    private void EnsureLoaded(SoundLayer layer) {
        if (layer.Samples is not null) {
            return;
        }
        // An empty loop marks "being made", so it is only made once.
        layer.Samples = [];
        string folder = SoundsFolder;
        Task.Run(() => {
            float[]? samples = layer.IsCustom ? WavFile.Read(Path.Combine(folder, layer.Name + ".wav")) : SoundSynth.Generate(layer.Name);
            if (samples is null) {
                _host.Log($"'{layer.Name}.wav' could not be played: only uncompressed (PCM) WAV files are supported.");
                _host.Notify($"'{layer.Name}.wav' could not be played. Only uncompressed (PCM) WAV files work as custom sounds.");
                return;
            }
            layer.Samples = samples;
        });
    }

    public void SetVolume(SoundLayer layer, float volume) {
        layer.Volume = volume;
        if (volume > 0) {
            EnsureLoaded(layer);
            Source = "";
            SetPlaying(true);
        }
        SaveSoon();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlaying(bool playing) {
        if (playing == IsPlaying) {
            return;
        }
        IsPlaying = playing;
        if (playing) {
            _mixer.Master = Master;
            foreach (SoundLayer layer in _layers.Where(static layer => layer.Volume > 0)) {
                EnsureLoaded(layer);
            }
            _mixer.SetLayers(_layers);
            _mixer.Start();
            _silentSeconds = 0;
            _idleTimer ??= CreateIdleTimer();
            _idleTimer.Start();
            if (Follow && (_host.Editor.ActiveChapterId is Guid chapterId)) {
                FollowChapter(chapterId);
            }
        }
        else {
            // Fade out: the mixer plays silence until the idle timer stops the device.
            _mixer.SetLayers(_layers.Select(static layer => new SoundLayer(layer.Name, layer.IsCustom) { Samples = layer.Samples, Current = layer.Current, Position = layer.Position }).ToList());
        }
        if (_mixer.Problem is string problem) {
            _host.Notify(problem);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases the sound device once nothing has been audible for a few seconds.</summary>
    private DispatcherTimer CreateIdleTimer() {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => {
            bool audible = IsPlaying && _layers.Any(static layer => layer.Volume > 0);
            _silentSeconds = audible ? 0 : _silentSeconds + 1;
            if (_silentSeconds >= 3) {
                _mixer.Stop();
                timer.Stop();
                if (IsPlaying) {
                    IsPlaying = false;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        };
        return timer;
    }

    public void SetFollow(bool follow) {
        Follow = follow;
        _host.Storage.SetSetting(FollowKey, follow ? "true" : "false");
        if (follow && IsPlaying && (_host.Editor.ActiveChapterId is Guid chapterId)) {
            FollowChapter(chapterId);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets every layer from a mix; sounds the mix does not name fade out.</summary>
    private void ApplyMix(Dictionary<string, float> mix, string source, bool start) {
        foreach (SoundLayer layer in _layers) {
            layer.Volume = mix.GetValueOrDefault(layer.Name);
            if (layer.Volume > 0) {
                EnsureLoaded(layer);
            }
        }
        Source = source;
        if (start && (mix.Count > 0)) {
            SetPlaying(true);
        }
        SaveSoon();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Switches to the chapter's own mix if it has one, otherwise to the soundscape of the place the chapter mentions
    /// most. A chapter with neither leaves the sound as it is.
    /// </summary>
    private void FollowChapter(Guid chapterId) {
        IEnumerable<string> names = _layers.Select(static layer => layer.Name);
        try {
            if (_host.Storage.GetProjectValue(ChapterMixPrefix + chapterId) is string saved) {
                ApplyMix(MixText.Parse(saved, names), "this chapter's own mix", start: false);
                return;
            }
            string text = _host.Project.GetChapterText(chapterId);
            (EntityInfo? place, string mix) = FindPlace(_host.Project.GetEntities(), text);
            if (place is not null) {
                ApplyMix(MixText.Parse(mix, names), place.Name, start: false);
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) {
            // The chapter or project went away while switching.
        }
    }

    /// <summary>The place with a Soundscape attribute that the text names most often.</summary>
    internal static (EntityInfo? Place, string Mix) FindPlace(IEnumerable<EntityInfo> entities, string text) {
        EntityInfo? best = null;
        string bestMix = "";
        int bestCount = 0;
        foreach (EntityInfo place in entities.Where(static entity => entity.Kind == EntityKind.Place)) {
            string? mix = place.Attributes.FirstOrDefault(static attribute => attribute.Key.Equals(PlaceAttribute, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(mix)) {
                continue;
            }
            int count = place.Aliases.Prepend(place.Name)
                .Where(static name => name.Trim().Length > 1)
                .Sum(name => Regex.Count(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name.Trim())}(?![\p{{L}}\p{{N}}])"));
            if (count > bestCount) {
                best = place;
                bestMix = mix;
                bestCount = count;
            }
        }
        return (best, bestMix);
    }

    public string CurrentMix => MixText.Format(_layers.Select(static layer => (layer.Name, layer.Volume)));

    /// <summary>Saves the current mix for the chapter in front (or removes it, with an empty mix).</summary>
    public bool SaveChapterMix(bool clear) {
        if (_host.Editor.ActiveChapterId is not Guid chapterId) {
            return false;
        }
        _host.Storage.SetProjectValue(ChapterMixPrefix + chapterId, clear ? null : CurrentMix);
        Source = clear ? "" : "this chapter's own mix";
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SavePlaceMix(Guid placeId) {
        _host.Project.SetEntityAttribute(placeId, PlaceAttribute, CurrentMix.Length > 0 ? CurrentMix : null);
    }

    /// <summary>Remembers the mix and volume for next time, a moment after the last change (sliders move a lot).</summary>
    private void SaveSoon() {
        if (_saveTimer is null) {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _saveTimer.Tick += (_, _) => {
                _saveTimer.Stop();
                _host.Storage.SetSetting(MixKey, CurrentMix);
                _host.Storage.SetSetting(MasterKey, Master.ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}

/// <summary>The Soundscape window: play and pause, the overall volume, a slider for every sound, and what the mix follows.</summary>
public sealed class SoundscapePanel : UserControl {
    private readonly SoundscapesPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly Button _play = new() { Padding = new Thickness(14, 5, 14, 5), MinWidth = 90 };
    private readonly Slider _master = new() { Minimum = 0, Maximum = 100, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _follow = new() { Content = "Follow the story: play each chapter's own mix, or the soundscape of the place it is set in", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _source;
    private readonly StackPanel _sliders = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _places = new() { Width = 220, DisplayMemberPath = nameof(EntityInfo.Name) };
    private readonly Dictionary<SoundLayer, (Slider Slider, TextBlock Percent)> _rows = [];
    private bool _loading;

    public SoundscapePanel(SoundscapesPlugin plugin, IPluginHost host) {
        _plugin = plugin;
        _host = host;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        _source = Subtle("", new Thickness(0, 4, 0, 0));

        StackPanel body = new() { Margin = new Thickness(24, 16, 24, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = "Soundscape", FontSize = 20, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Subtle("Background sound while you write. Move a slider to add a sound to the mix. The sounds are made on your PC; nothing is downloaded.", new Thickness(0, 2, 0, 12)));

        StackPanel top = new() { Orientation = Orientation.Horizontal };
        _play.SetResourceReference(StyleProperty, "TileButton");
        _play.Click += (_, _) => _plugin.SetPlaying(!_plugin.IsPlaying);
        top.Children.Add(_play);
        top.Children.Add(new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 8, 0) });
        System.Windows.Automation.AutomationProperties.SetName(_master, "Overall volume");
        _master.ValueChanged += (_, _) => {
            if (!_loading) {
                _plugin.Master = (float)(_master.Value / 100);
            }
        };
        top.Children.Add(_master);
        body.Children.Add(top);

        _follow.Checked += (_, _) => OnFollowChanged();
        _follow.Unchecked += (_, _) => OnFollowChanged();
        body.Children.Add(_follow);
        body.Children.Add(_source);

        body.Children.Add(new TextBlock { Text = "Sounds", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
        body.Children.Add(_sliders);

        body.Children.Add(new TextBlock { Text = "Tie this mix to the story", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        WrapPanel story = new();
        story.Children.Add(TileButton("Use for this chapter", () => {
            if (!_plugin.SaveChapterMix(clear: false)) {
                _host.Notify("Open a chapter first: the mix is saved for the chapter in front.");
            }
        }, "Whenever this chapter is in front, this mix plays"));
        story.Children.Add(TileButton("Forget this chapter's mix", () => _plugin.SaveChapterMix(clear: true)));
        body.Children.Add(story);
        StackPanel place = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_places, "Place");
        place.Children.Add(_places);
        place.Children.Add(TileButton("Use as this place's soundscape", () => {
            if (_places.SelectedItem is EntityInfo selected) {
                _plugin.SavePlaceMix(selected.Id);
                _host.SetStatusText($"'{selected.Name}' now has the soundscape: {_plugin.CurrentMix}.");
            }
        }, $"Writes the mix to the place's {SoundscapesPlugin.PlaceAttribute} attribute; chapters set there play it"));
        body.Children.Add(place);
        body.Children.Add(Subtle($"A place's soundscape is its '{SoundscapesPlugin.PlaceAttribute}' attribute, such as \"Ocean 70, Wind 30\": you can also type it on the place's page.", new Thickness(0, 6, 0, 0)));

        body.Children.Add(new TextBlock { Text = "Your own sounds", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        WrapPanel own = new();
        own.Children.Add(TileButton("Open Sounds folder", () => {
            Directory.CreateDirectory(_plugin.SoundsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_plugin.SoundsFolder}\"") { UseShellExecute = true });
        }));
        own.Children.Add(TileButton("Reload", () => _plugin.LoadLayers(), "Look in the Sounds folder again"));
        body.Children.Add(own);
        body.Children.Add(Subtle("Put WAV files (uncompressed PCM) in the Sounds folder and press Reload: each becomes a sound with its own slider, looped from end to start.", new Thickness(0, 6, 0, 0)));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Loaded += (_, _) => {
            _plugin.Changed += OnChanged;
            BuildSliders();
            LoadPlaces();
            Show();
        };
        Unloaded += (_, _) => _plugin.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) {
        if (_rows.Count != _plugin.Layers.Count || _plugin.Layers.Any(layer => !_rows.ContainsKey(layer))) {
            BuildSliders();
        }
        Show();
    }

    private void OnFollowChanged() {
        if (!_loading) {
            _plugin.SetFollow(_follow.IsChecked == true);
        }
    }

    private void BuildSliders() {
        _sliders.Children.Clear();
        _rows.Clear();
        foreach (SoundLayer layer in _plugin.Layers) {
            DockPanel row = new() { Margin = new Thickness(0, 3, 0, 3) };
            TextBlock name = new() { Text = layer.IsCustom ? $"{layer.Name} (yours)" : layer.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            TextBlock percent = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(percent, Dock.Right);
            row.Children.Add(percent);
            Slider slider = new() { Minimum = 0, Maximum = 100, Width = 320, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{layer.Name} volume");
            slider.ValueChanged += (_, _) => {
                if (!_loading) {
                    _plugin.SetVolume(layer, (float)(slider.Value / 100));
                }
            };
            row.Children.Add(slider);
            _sliders.Children.Add(row);
            _rows[layer] = (slider, percent);
        }
    }

    private void LoadPlaces() {
        if (!_host.Project.IsOpen) {
            return;
        }
        _places.ItemsSource = _host.Project.GetEntities().Where(static entity => entity.Kind == EntityKind.Place).OrderBy(static place => place.Name).ToList();
        _places.SelectedIndex = _places.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>Sets the controls from the plugin's state.</summary>
    private void Show() {
        _loading = true;
        _play.Content = _plugin.IsPlaying ? "Pause" : "Play";
        _master.Value = _plugin.Master * 100;
        _follow.IsChecked = _plugin.Follow;
        foreach ((SoundLayer layer, (Slider slider, TextBlock percent)) in _rows) {
            slider.Value = layer.Volume * 100;
            percent.Text = layer.Volume > 0.005f ? $"{Math.Round(layer.Volume * 100)}%" : "off";
        }
        _source.Text = _plugin.Problem
            ?? (_plugin.Source.Length > 0 ? $"Now playing: {_plugin.Source} ({_plugin.CurrentMix})." : (_plugin.CurrentMix.Length > 0 ? $"Mix: {_plugin.CurrentMix}." : "Nothing in the mix yet."));
        _loading = false;
    }

    private static Button TileButton(string text, Action click, string? toolTip = null) {
        Button button = new() { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), ToolTip = toolTip };
        button.SetResourceReference(StyleProperty, "TileButton");
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Subtle(string text, Thickness margin) {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        return block;
    }
}