using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using Microsoft.Win32;

using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace MapMaker;

/// <summary>
/// The Map window. The map is a 1000-unit-wide surface inside a view box, so the picture, the drawing and the pins
/// scale together. Left: the tools and the list of places. Bottom: the journey between the two selected pins.
/// </summary>
public sealed class MapPanel : UserControl {
    private const double SurfaceWidth = 1000;
    private const string MapsFolder = "Maps";

    private static readonly (string Name, Color Color)[] Pens = [
        ("Coast and borders", Color.FromRgb(0x3B, 0x3B, 0x3B)),
        ("Water", Color.FromRgb(0x2E, 0x7D, 0xC2)),
        ("Forest", Color.FromRgb(0x3F, 0x8F, 0x4F)),
        ("Mountains", Color.FromRgb(0x8A, 0x5A, 0x33)),
        ("Roads", Color.FromRgb(0xB0, 0x3A, 0x2E))
    ];

    private readonly MapPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly MapData _map;
    private readonly Grid _surface = new() { Width = SurfaceWidth };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly InkCanvas _ink = new() { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.None };
    private readonly Canvas _pins = new() { Background = Brushes.Transparent };
    private readonly Line _route = new() { StrokeThickness = 2.5, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _placeList = new();
    private readonly TextBlock _journey = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 10) };
    private readonly TextBox _width = new() { Width = 58, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _unit = new() { Width = 78, ItemsSource = MapMath.Units.Keys.ToList() };
    private readonly TextBox _newPlace = new() { MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint;
    private readonly Dictionary<Guid, FrameworkElement> _pinViews = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private IReadOnlyList<EntityInfo> _places = [];
    private Guid? _selected;
    private Guid? _previous;
    private Guid? _placing;
    private Guid? _dragging;
    private bool _dragMoved;
    private bool _loading = true;
    private bool _inkDirty;

    public MapPanel(MapPlugin plugin, IPluginHost host) {
        _plugin = plugin;
        _host = host;
        _map = plugin.Map.Clone();
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        _hint = Subtle("", new Thickness(0, 6, 0, 0));
        _route.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _journey.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        // The map: paper, the picture, the drawing, then the pins on top.
        Border paper = new() { Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD8)) };
        _surface.Children.Add(paper);
        _surface.Children.Add(_image);
        _surface.Children.Add(_ink);
        _pins.Children.Add(_route);
        _surface.Children.Add(_pins);
        _pins.MouseLeftButtonDown += OnMapClicked;
        _pins.MouseMove += OnPinDragged;
        _pins.MouseLeftButtonUp += (_, _) => EndDrag();
        _ink.StrokeCollected += (_, _) => OnInkChanged();
        _ink.StrokeErased += (_, _) => OnInkChanged();
        Border frame = new() { Child = _surface, BorderThickness = new Thickness(1), Margin = new Thickness(12) };
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Viewbox view = new() { Child = frame, Stretch = Stretch.Uniform };

        DockPanel side = new() { Width = 250 };
        StackPanel tools = BuildTools();
        DockPanel.SetDock(tools, Dock.Top);
        side.Children.Add(tools);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _placeList, Margin = new Thickness(12, 0, 0, 12) });
        Border sideBorder = new() { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        sideBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush");

        Border journeyBar = new() { Child = _journey, BorderThickness = new Thickness(0, 1, 0, 0) };
        journeyBar.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");

        DockPanel root = new();
        DockPanel.SetDock(sideBorder, Dock.Left);
        root.Children.Add(sideBorder);
        DockPanel.SetDock(journeyBar, Dock.Bottom);
        root.Children.Add(journeyBar);
        root.Children.Add(view);
        Content = root;

        _saveTimer.Tick += (_, _) => Save();
        Loaded += (_, _) => {
            _plugin.ShowPlaceRequested += OnShowPlaceRequested;
            LoadMap();
            OnShowPlaceRequested(this, EventArgs.Empty);
        };
        Unloaded += (_, _) => {
            _plugin.ShowPlaceRequested -= OnShowPlaceRequested;
            if (_saveTimer.IsEnabled) {
                Save();
            }
        };
    }

    // ------------------------------------------------------------------
    // The side panel
    // ------------------------------------------------------------------

    private StackPanel BuildTools() {
        StackPanel tools = new() { Margin = new Thickness(12, 12, 12, 8) };
        tools.Children.Add(new TextBlock { Text = "Map", FontSize = 20, FontWeight = FontWeights.SemiBold });

        tools.Children.Add(Heading("Picture"));
        WrapPanel picture = new();
        picture.Children.Add(TileButton("Import image...", ImportImage, "Use a picture (PNG or JPEG) as the map. It is copied into the project's Maps folder."));
        picture.Children.Add(TileButton("Remove", () => {
            _map.Image = null;
            ShowImage();
            SaveSoon();
        }, "Take the picture off the map (the file stays in the Maps folder)"));
        tools.Children.Add(picture);

        tools.Children.Add(Heading("Draw"));
        WrapPanel modes = new();
        modes.Children.Add(ModeButton("Pins", InkCanvasEditingMode.None, isChecked: true, "Move pins and measure journeys"));
        modes.Children.Add(ModeButton("Pen", InkCanvasEditingMode.Ink, isChecked: false, "Draw on the map"));
        modes.Children.Add(ModeButton("Eraser", InkCanvasEditingMode.EraseByStroke, isChecked: false, "Click a line to remove it"));
        tools.Children.Add(modes);
        WrapPanel colours = new() { Margin = new Thickness(0, 6, 0, 0) };
        foreach ((string name, Color color) in Pens) {
            Button swatch = new() { Width = 26, Height = 22, Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(color), ToolTip = name, BorderThickness = new Thickness(1) };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Pen: {name}");
            swatch.Click += (_, _) => SetPen(color, name == "Coast and borders" ? 3 : (name == "Roads" ? 2 : 6));
            colours.Children.Add(swatch);
        }
        tools.Children.Add(colours);
        Button clear = TileButton("Clear drawing", () => {
            if ((_ink.Strokes.Count > 0) && (MessageBox.Show(Window.GetWindow(this)!, "Remove everything you drew on the map? Pins and the picture stay.", "Clear drawing", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)) {
                _ink.Strokes.Clear();
                OnInkChanged();
            }
        });
        clear.Margin = new Thickness(0, 6, 0, 0);
        tools.Children.Add(clear);

        tools.Children.Add(Heading("Scale"));
        StackPanel scale = new() { Orientation = Orientation.Horizontal };
        scale.Children.Add(new TextBlock { Text = "Map is", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        System.Windows.Automation.AutomationProperties.SetName(_width, "Map width");
        System.Windows.Automation.AutomationProperties.SetName(_unit, "Unit");
        scale.Children.Add(_width);
        _unit.Margin = new Thickness(6, 0, 6, 0);
        scale.Children.Add(_unit);
        scale.Children.Add(new TextBlock { Text = "wide", VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(scale);
        _width.LostFocus += (_, _) => OnScaleChanged();
        _width.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) {
                OnScaleChanged();
            }
        };
        _unit.SelectionChanged += (_, _) => OnScaleChanged();

        tools.Children.Add(Heading("Places"));
        DockPanel add = new();
        Button addButton = TileButton("Add", AddPlace, "Create a place with this name, then click the map to pin it");
        addButton.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(addButton, Dock.Right);
        add.Children.Add(addButton);
        System.Windows.Automation.AutomationProperties.SetName(_newPlace, "New place name");
        _newPlace.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) {
                AddPlace();
            }
        };
        add.Children.Add(_newPlace);
        tools.Children.Add(add);
        tools.Children.Add(_hint);
        return tools;
    }

    private RadioButton ModeButton(string text, InkCanvasEditingMode mode, bool isChecked, string toolTip) {
        RadioButton button = new() { Content = text, GroupName = "MapMode", IsChecked = isChecked, Margin = new Thickness(0, 0, 12, 0), ToolTip = toolTip };
        button.Checked += (_, _) => {
            _ink.EditingMode = mode;
            // While drawing, the pins must not catch the pen.
            _pins.IsHitTestVisible = mode == InkCanvasEditingMode.None;
            _placing = null;
            ShowHint();
        };
        return button;
    }

    private void SetPen(Color color, double width) {
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = color, Width = width, Height = width, FitToCurve = true };
    }

    // ------------------------------------------------------------------
    // Loading and saving
    // ------------------------------------------------------------------

    private void LoadMap() {
        _loading = true;
        _width.Text = _map.Width.ToString("0.##", CultureInfo.CurrentCulture);
        _unit.SelectedItem = _map.Unit;
        SetPen(Pens[0].Color, 3);
        ShowImage();
        _ink.Strokes.Clear();
        if (MapPlugin.ResolveInProject(_host.Project.DirectoryPath, _map.Ink) is string inkPath && File.Exists(inkPath)) {
            try {
                using FileStream stream = File.OpenRead(inkPath);
                _ink.Strokes = new StrokeCollection(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
                _host.Log($"The map's drawing could not be read: {exception.Message}");
            }
        }
        _loading = false;
        RefreshPlaces();
    }

    private void ShowImage() {
        _image.Source = null;
        if (MapPlugin.ResolveInProject(_host.Project.DirectoryPath, _map.Image) is string path && File.Exists(path)) {
            try {
                BitmapImage bitmap = new();
                bitmap.BeginInit();
                // Read it all now, so the file is not kept open.
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                _image.Source = bitmap;
                _map.Aspect = Math.Clamp((double)bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth), 0.1, 10);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
                _host.Notify($"The map picture could not be shown: {exception.Message}");
            }
        }
        _surface.Height = SurfaceWidth * _map.Aspect;
        foreach ((Guid id, FrameworkElement view) in _pinViews) {
            PlacePin(view, _map.Pins[id]);
        }
        ShowJourney();
    }

    private void ImportImage() {
        if (!_host.Project.IsOpen) {
            return;
        }
        OpenFileDialog dialog = new() { Title = "Import map image", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) {
            return;
        }
        try {
            string folder = System.IO.Path.Combine(_host.Project.DirectoryPath, MapsFolder);
            Directory.CreateDirectory(folder);
            string name = "map" + System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
            File.Copy(dialog.FileName, System.IO.Path.Combine(folder, name), overwrite: true);
            _map.Image = $"{MapsFolder}/{name}";
            ShowImage();
            SaveSoon();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _host.Notify($"The picture could not be copied into the project: {exception.Message}");
        }
    }

    private void OnInkChanged() {
        if (!_loading) {
            _inkDirty = true;
            SaveSoon();
        }
    }

    private void OnScaleChanged() {
        if (_loading) {
            return;
        }
        if (double.TryParse(_width.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double width) && (width > 0)) {
            _map.Width = width;
        }
        _map.Unit = _unit.SelectedItem as string ?? "km";
        _width.Text = _map.Width.ToString("0.##", CultureInfo.CurrentCulture);
        ShowJourney();
        SaveSoon();
    }

    private void SaveSoon() {
        if (_loading) {
            return;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save() {
        _saveTimer.Stop();
        if (!_host.Project.IsOpen) {
            return;
        }
        if (_inkDirty) {
            try {
                string folder = System.IO.Path.Combine(_host.Project.DirectoryPath, MapsFolder);
                string file = System.IO.Path.Combine(folder, "drawing.isf");
                if (_ink.Strokes.Count == 0) {
                    File.Delete(file);
                    _map.Ink = null;
                }
                else {
                    Directory.CreateDirectory(folder);
                    using FileStream stream = File.Create(file);
                    _ink.Strokes.Save(stream);
                    _map.Ink = $"{MapsFolder}/drawing.isf";
                }
                _inkDirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _host.Notify($"The map's drawing could not be saved: {exception.Message}");
            }
        }
        _plugin.Save(_map);
    }

    // ------------------------------------------------------------------
    // Places and pins
    // ------------------------------------------------------------------

    private void RefreshPlaces() {
        _places = _host.Project.IsOpen
            ? _host.Project.GetEntities().Where(static entity => entity.Kind == EntityKind.Place).OrderBy(static place => place.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
        // A pin whose place was deleted goes too.
        foreach (Guid orphan in _map.Pins.Keys.Where(id => _places.All(place => place.Id != id)).ToList()) {
            _map.Pins.Remove(orphan);
        }
        foreach (FrameworkElement view in _pinViews.Values) {
            _pins.Children.Remove(view);
        }
        _pinViews.Clear();
        foreach (EntityInfo place in _places.Where(place => _map.Pins.ContainsKey(place.Id))) {
            AddPinView(place);
        }
        BuildPlaceList();
        ShowHint();
        ShowJourney();
    }

    private void BuildPlaceList() {
        _placeList.Children.Clear();
        if (_places.Count == 0) {
            _placeList.Children.Add(Subtle("No places yet. Type a name above and press Add, or add places in WritingIDE.", new Thickness(0, 4, 12, 0)));
            return;
        }
        foreach (EntityInfo place in _places) {
            bool pinned = _map.Pins.ContainsKey(place.Id);
            DockPanel row = new() { Margin = new Thickness(0, 2, 12, 2) };
            Button action = TileButton(pinned ? "Unpin" : "Pin", () => {
                if (pinned) {
                    _map.Pins.Remove(place.Id);
                    if (_selected == place.Id) {
                        _selected = null;
                    }
                    if (_previous == place.Id) {
                        _previous = null;
                    }
                    SaveSoon();
                    RefreshPlaces();
                }
                else {
                    StartPlacing(place.Id);
                }
            }, pinned ? "Take this place off the map" : "Then click the map where this place is");
            action.Margin = new Thickness(6, 0, 0, 0);
            action.Padding = new Thickness(6, 2, 6, 2);
            System.Windows.Automation.AutomationProperties.SetName(action, $"{(pinned ? "Unpin" : "Pin")} {place.Name}");
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
            TextBlock name = new() { Text = place.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = place.Summary.Length > 0 ? place.Summary : null };
            if (!pinned) {
                name.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            }
            else if ((place.Id == _selected) || (place.Id == _previous)) {
                name.FontWeight = FontWeights.SemiBold;
            }
            row.Children.Add(name);
            if (pinned) {
                row.Cursor = Cursors.Hand;
                row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => Select(place.Id);
            }
            _placeList.Children.Add(row);
        }
    }

    private void AddPlace() {
        string name = _newPlace.Text.Trim();
        if ((name.Length == 0) || !_host.Project.IsOpen) {
            return;
        }
        EntityInfo? existing = _places.FirstOrDefault(place => place.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        Guid id = existing?.Id ?? _host.Project.AddEntity(EntityKind.Place, name);
        _newPlace.Text = "";
        RefreshPlaces();
        StartPlacing(id);
    }

    private void StartPlacing(Guid placeId) {
        _placing = placeId;
        _ink.EditingMode = InkCanvasEditingMode.None;
        _pins.IsHitTestVisible = true;
        _pins.Cursor = Cursors.Cross;
        ShowHint();
    }

    private void ShowHint() {
        _hint.Text = _placing is Guid placing
            ? $"Click the map where '{_places.FirstOrDefault(place => place.Id == placing)?.Name}' is. Esc cancels."
            : (_ink.EditingMode != InkCanvasEditingMode.None
                ? "Drawing: pick a colour and draw. Choose Pins to move pins again."
                : "Click two pins to measure the journey between them. Drag a pin to move it; double-click it to open the place.");
        if (_placing is null) {
            _pins.Cursor = null;
        }
    }

    private void AddPinView(EntityInfo place) {
        StackPanel view = new() { Tag = place.Id, Cursor = Cursors.Hand, ToolTip = place.Summary.Length > 0 ? $"{place.Name}: {place.Summary}" : place.Name };
        Ellipse dot = new() { Width = 16, Height = 16, StrokeThickness = 2.5, Stroke = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "PlaceBrush");
        view.Children.Add(dot);
        Border label = new() { Background = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(0, 2, 0, 0) };
        label.Child = new TextBlock { Text = place.Name, FontSize = 13, Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold };
        view.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(view, $"Pin: {place.Name}");
        // The dot's centre sits on the pin's position, whatever the label's width.
        view.SizeChanged += (_, _) => PlacePin(view, _map.Pins.GetValueOrDefault(place.Id) ?? new MapPin(0, 0));
        view.MouseLeftButtonDown += (_, e) => {
            if (_placing is not null) {
                return;
            }
            if (e.ClickCount == 2) {
                _host.Project.OpenEntity(place.Id);
            }
            else {
                _dragging = place.Id;
                _dragMoved = false;
                _pins.CaptureMouse();
            }
            e.Handled = true;
        };
        _pinViews[place.Id] = view;
        _pins.Children.Add(view);
        PlacePin(view, _map.Pins[place.Id]);
    }

    private void PlacePin(FrameworkElement view, MapPin pin) {
        Canvas.SetLeft(view, (pin.X * SurfaceWidth) - (view.ActualWidth / 2));
        Canvas.SetTop(view, (pin.Y * SurfaceWidth) - 8);
    }

    private MapPin PinAt(Point point) {
        return new MapPin(Math.Clamp(point.X / SurfaceWidth, 0, 1), Math.Clamp(point.Y / SurfaceWidth, 0, _map.Aspect));
    }

    private void OnMapClicked(object sender, MouseButtonEventArgs e) {
        if (_placing is Guid placing) {
            _map.Pins[placing] = PinAt(e.GetPosition(_pins));
            _placing = null;
            SaveSoon();
            RefreshPlaces();
            Select(placing);
            e.Handled = true;
        }
    }

    private void OnPinDragged(object sender, MouseEventArgs e) {
        if ((_dragging is not Guid id) || (e.LeftButton != MouseButtonState.Pressed)) {
            return;
        }
        _dragMoved = true;
        _map.Pins[id] = PinAt(e.GetPosition(_pins));
        PlacePin(_pinViews[id], _map.Pins[id]);
        ShowJourney();
    }

    private void EndDrag() {
        if (_dragging is not Guid id) {
            return;
        }
        _pins.ReleaseMouseCapture();
        _dragging = null;
        if (_dragMoved) {
            SaveSoon();
        }
        else {
            Select(id);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) {
        if ((e.Key == Key.Escape) && (_placing is not null)) {
            _placing = null;
            ShowHint();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Selects a pin; the pin selected before it becomes the other end of the journey.</summary>
    private void Select(Guid placeId) {
        if (_selected != placeId) {
            _previous = _selected;
            _selected = placeId;
        }
        BuildPlaceList();
        ShowJourney();
    }

    private void OnShowPlaceRequested(object? sender, EventArgs e) {
        if ((_plugin.PlaceToShow is Guid id) && _map.Pins.ContainsKey(id)) {
            _plugin.PlaceToShow = null;
            _previous = null;
            _selected = null;
            Select(id);
        }
    }

    /// <summary>The line between the two selected pins, and the distance and travel times under the map.</summary>
    private void ShowJourney() {
        foreach ((Guid id, FrameworkElement view) in _pinViews) {
            bool selected = (id == _selected) || (id == _previous);
            if (((StackPanel)view).Children[0] is Ellipse dot) {
                dot.Width = dot.Height = selected ? 20 : 16;
                dot.SetResourceReference(Shape.FillProperty, selected ? "AccentBrush" : "PlaceBrush");
            }
        }
        if ((_selected is Guid to) && (_previous is Guid from) && _map.Pins.TryGetValue(from, out MapPin? start) && _map.Pins.TryGetValue(to, out MapPin? end)) {
            _route.X1 = start.X * SurfaceWidth;
            _route.Y1 = start.Y * SurfaceWidth;
            _route.X2 = end.X * SurfaceWidth;
            _route.Y2 = end.Y * SurfaceWidth;
            _route.Visibility = Visibility.Visible;
            double distance = MapMath.Distance(_map, start, end);
            string fromName = _places.FirstOrDefault(place => place.Id == from)?.Name ?? "?";
            string toName = _places.FirstOrDefault(place => place.Id == to)?.Name ?? "?";
            _journey.Text = $"{fromName} to {toName}: {MapMath.FormatDistance(_map, distance)} in a straight line.  "
                + string.Join(",  ", MapMath.Estimate(MapMath.ToKilometres(_map, distance)).Select(static estimate => estimate.ToString())) + ".";
        }
        else {
            _route.Visibility = Visibility.Collapsed;
            _journey.Text = _map.Pins.Count < 2
                ? "Pin two places to measure the journey between them. The continuity rule MAP001 then checks travel times in your chapters against the map."
                : "Click two pins to measure the journey between them.";
        }
    }

    // ------------------------------------------------------------------
    // Small pieces
    // ------------------------------------------------------------------

    private static TextBlock Heading(string text) {
        return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5) };
    }

    private static Button TileButton(string text, Action click, string? toolTip = null) {
        Button button = new() { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = toolTip };
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