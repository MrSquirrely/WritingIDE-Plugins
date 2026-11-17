using WritingIDE.Core.Grammar;
using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace MapMaker;

/// <summary>
/// Map Maker: a map of the story's world, drawn or imported, with a pin for each place. Distances on the map give
/// travel times, and a continuity rule checks journeys in the text against them.
/// </summary>
public sealed class MapPlugin : IWritingIdePlugin {
    private const string MapKey = "map";

    private IPluginHost _host = null!;
    private volatile MapData _map = new();

    public string Id => "com.mrsquirrely.writingide.mapmaker";

    public string Name => "Map Maker";

    public string Version => "1.0.0";

    public string Description => "A map of your world with a pin for each place, travel times, and a rule that checks journeys against the map.";

    /// <summary>The open project's map. Replaced, never changed in place, so the rule can read it from any thread.</summary>
    public MapData Map => _map;

    /// <summary>The place to select when the map window next opens (set by "Show on Map").</summary>
    public Guid? PlaceToShow { get; set; }

    /// <summary>Raised on the UI thread when another part of the plugin wants a place selected on the map.</summary>
    public event EventHandler? ShowPlaceRequested;

    public void Initialize(IPluginHost host) {
        _host = host;
        host.RegisterRule(new TravelTimeRule(this));
        host.RegisterToolWindow(new PluginToolWindow("map", "Map", () => new MapPanel(this, host)) {
            Description = "Draw or import a map, pin your places on it, and measure journeys."
        });
        host.RegisterEditorCommand(new EditorCommand("Show on Map", context => {
            PlaceToShow = context.EntityId;
            host.ShowToolWindow("map");
            ShowPlaceRequested?.Invoke(this, EventArgs.Empty);
        }) {
            Description = "Opens the map with this place selected.",
            IsAvailable = context => (context.EntityId is Guid id) && _map.Pins.ContainsKey(id)
        });
        host.ProjectOpened += (_, _) => _map = MapData.Parse(host.Storage.GetProjectValue(MapKey));
        host.ProjectClosed += (_, _) => _map = new MapData();
    }

    /// <summary>Stores a changed map in the project and has the open chapters checked against it.</summary>
    public void Save(MapData map) {
        _map = map.Clone();
        _host.Storage.SetProjectValue(MapKey, _map.ToJson());
        _host.Project.Reanalyze();
    }

    /// <summary>
    /// The full path of a file the map names, or null when the name would leave the project folder: a project from
    /// someone else must not make WritingIDE open files elsewhere.
    /// </summary>
    public static string? ResolveInProject(string projectDirectory, string? relative) {
        if (string.IsNullOrWhiteSpace(relative) || (projectDirectory.Length == 0) || Path.IsPathRooted(relative)) {
            return null;
        }
        string root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>MAP001: a journey between two pinned places that takes far less, or far more, time than the map allows.</summary>
public sealed class TravelTimeRule(MapPlugin plugin)
    : GrammarRule("MAP001", "Journey does not fit the map", RuleCategory.Continuity, DiagnosticSeverity.Warning,
        "Compares how long the text says a journey between two places on your map took with the distance between their pins. "
        + "It allows a wide margin for winding roads and rests, so a finding means the time is off by more than a factor of two and a half.") {
    public override IEnumerable<GrammarDiagnostic> Analyze(AnalysisContext context) {
        MapData map = plugin.Map;
        if (map.Pins.Count < 2) {
            return [];
        }
        List<MapPlace> places = context.Options.Entities.Entities
            .Where(entity => (entity.Kind == EntityKind.Place) && map.Pins.ContainsKey(entity.Id))
            .Select(entity => new MapPlace(entity.Id, entity.Name, entity.AllNames.ToList(), map.Pins[entity.Id]))
            .ToList();
        return TravelCheck.Find(context.Text, places, map).Select(finding => Report(finding.Offset, finding.Length, finding.Message)).ToList();
    }
}