using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapMaker;

/// <summary>A place's pin. Both coordinates are fractions of the map's width, so the map can be shown at any size.</summary>
public sealed record MapPin(double X, double Y);

/// <summary>The project's map: an optional picture, a drawing, the scale, and a pin for each place.</summary>
public sealed class MapData {
    /// <summary>The picture, relative to the project folder ("Maps/world.png"); null for a map that is only drawn.</summary>
    public string? Image { get; set; }

    /// <summary>The drawing (ink strokes), relative to the project folder; null when nothing is drawn.</summary>
    public string? Ink { get; set; }

    /// <summary>The map's height as a fraction of its width.</summary>
    public double Aspect { get; set; } = 0.65;

    /// <summary>How far it is from the map's left edge to its right edge, in <see cref="Unit"/>.</summary>
    public double Width { get; set; } = 1000;

    /// <summary>"km", "miles" or "leagues".</summary>
    public string Unit { get; set; } = "km";

    /// <summary>Story element id (a place) to its pin.</summary>
    public Dictionary<Guid, MapPin> Pins { get; set; } = [];

    public static MapData Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return new MapData();
        }
        try {
            MapData data = JsonSerializer.Deserialize<MapData>(json) ?? new MapData();
            data.Width = double.IsFinite(data.Width) && (data.Width > 0) ? data.Width : 1000;
            data.Aspect = double.IsFinite(data.Aspect) ? Math.Clamp(data.Aspect, 0.1, 10) : 0.65;
            data.Unit = MapMath.Units.ContainsKey(data.Unit) ? data.Unit : "km";
            return data;
        }
        catch (JsonException) {
            return new MapData();
        }
    }

    public string ToJson() {
        return JsonSerializer.Serialize(this);
    }

    public MapData Clone() {
        return Parse(ToJson());
    }
}

/// <summary>A way of travelling and how far it gets in a day.</summary>
/// <param name="Phrase">How a message names it: "on foot".</param>
public sealed record TravelMode(string Name, string Phrase, double KilometresPerDay, bool ByLand);

/// <summary>How long a journey takes by one mode.</summary>
public sealed record TravelEstimate(TravelMode Mode, double Days) {
    public override string ToString() {
        return $"{MapMath.FormatDays(Days)} {Mode.Phrase}";
    }
}

/// <summary>Distances on the map and how long they take to travel.</summary>
public static class MapMath {
    /// <summary>Hours on the road in a day of travel.</summary>
    public const double HoursPerDay = 8;

    /// <summary>Kilometres in one of each unit.</summary>
    public static IReadOnlyDictionary<string, double> Units { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) {
        ["km"] = 1,
        ["miles"] = 1.609344,
        ["leagues"] = 4.828032
    };

    public static TravelMode Foot { get; } = new("Foot", "on foot", 30, ByLand: true);

    public static TravelMode Horse { get; } = new("Horse", "on horseback", 60, ByLand: true);

    public static TravelMode Cart { get; } = new("Cart", "by cart or wagon", 25, ByLand: true);

    public static TravelMode Ship { get; } = new("Ship", "by sailing ship", 160, ByLand: false);

    public static IReadOnlyList<TravelMode> Modes { get; } = [Foot, Horse, Cart, Ship];

    /// <summary>The straight-line distance between two pins, in the map's unit.</summary>
    public static double Distance(MapData map, MapPin from, MapPin to) {
        return Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)) * map.Width;
    }

    public static double ToKilometres(MapData map, double distance) {
        return distance * Units.GetValueOrDefault(map.Unit, 1);
    }

    public static IReadOnlyList<TravelEstimate> Estimate(double kilometres) {
        return Modes.Select(mode => new TravelEstimate(mode, kilometres / mode.KilometresPerDay)).ToList();
    }

    /// <summary>"about 3 hours", "1 day", "2.5 days", "3 weeks".</summary>
    public static string FormatDays(double days) {
        if (days < 0.75) {
            double hours = Math.Max(1, Math.Round(days * HoursPerDay));
            return $"about {hours:0} hour{(hours == 1 ? "" : "s")}";
        }
        if (days < 21) {
            double rounded = days < 5 ? Math.Round(days * 2) / 2 : Math.Round(days);
            return $"{rounded.ToString("0.#", CultureInfo.InvariantCulture)} day{(rounded == 1 ? "" : "s")}";
        }
        double weeks = Math.Round(days / 7);
        return $"{weeks:0} weeks";
    }

    public static string FormatDistance(MapData map, double distance) {
        return $"{(distance < 10 ? distance.ToString("0.#", CultureInfo.InvariantCulture) : distance.ToString("N0", CultureInfo.InvariantCulture))} {map.Unit}";
    }
}

/// <summary>A place the travel check knows: its pin and the names the text may call it.</summary>
public sealed record MapPlace(Guid Id, string Name, IReadOnlyList<string> Names, MapPin Pin);

/// <summary>A journey in the text whose stated time does not fit the map.</summary>
public sealed record TravelFinding(int Offset, int Length, string Message);

/// <summary>
/// Finds sentences that say how long a journey between two pinned places took ("They rode from Greyhaven to Port
/// Ashby in a day") and compares the time with the distance on the map.
/// </summary>
public static partial class TravelCheck {
    /// <summary>A stated time within this factor of the estimate passes: roads wind, travellers rest, horses differ.</summary>
    public const double Slack = 2.5;

    public static IReadOnlyList<TravelFinding> Find(string text, IReadOnlyList<MapPlace> places, MapData map) {
        List<TravelFinding> findings = [];
        if (places.Count < 2) {
            return findings;
        }
        List<(Regex Pattern, MapPlace Place)> names = places
            .SelectMany(place => place.Names.Where(static name => name.Trim().Length > 1).Select(name => (Name: name.Trim(), Place: place)))
            .OrderByDescending(static entry => entry.Name.Length)
            .Select(static entry => (new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(entry.Name)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant), entry.Place))
            .ToList();

        foreach (Match sentence in Sentence().Matches(text)) {
            string value = sentence.Value;
            Match duration = Duration().Match(value);
            if (!duration.Success || !TravelWord().IsMatch(value) || Air().IsMatch(value)) {
                continue;
            }
            // The first two different places the sentence names.
            List<(int Index, MapPlace Place)> mentioned = [];
            foreach ((Regex pattern, MapPlace place) in names) {
                Match match = pattern.Match(value);
                if (match.Success && mentioned.All(found => found.Place.Id != place.Id)) {
                    mentioned.Add((match.Index, place));
                }
            }
            if (mentioned.Count < 2) {
                continue;
            }
            mentioned.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            MapPlace from = mentioned[0].Place;
            MapPlace to = mentioned[1].Place;
            if (ParseDays(duration) is not double stated) {
                continue;
            }
            double distance = MapMath.Distance(map, from.Pin, to.Pin);
            double kilometres = MapMath.ToKilometres(map, distance);
            if (kilometres < 1) {
                continue;
            }
            TravelMode? mode = ModeOf(value);
            IReadOnlyList<TravelMode> candidates = mode is not null ? [mode] : [MapMath.Horse, MapMath.Foot];
            double fastest = candidates.Min(candidate => kilometres / candidate.KilometresPerDay);
            double slowest = candidates.Max(candidate => kilometres / candidate.KilometresPerDay);
            if ((stated >= fastest / Slack) && (stated <= slowest * Slack)) {
                continue;
            }
            string estimates = string.Join(" or ", candidates.Select(candidate => new TravelEstimate(candidate, kilometres / candidate.KilometresPerDay).ToString()));
            string verdict = stated < fastest / Slack ? "too fast" : "too slow";
            findings.Add(new TravelFinding(sentence.Index + duration.Index, duration.Length,
                $"'{from.Name}' to '{to.Name}' is about {MapMath.FormatDistance(map, distance)} on the map: {estimates}. The text says {duration.Value.Trim()}, which is {verdict}."));
        }
        return findings;
    }

    /// <summary>The length of time a match of <see cref="Duration"/> states, in days of travel.</summary>
    internal static double? ParseDays(Match duration) {
        string unit = duration.Groups["unit"].Value.ToLowerInvariant();
        if (unit.StartsWith("fortnight", StringComparison.Ordinal)) {
            return 14;
        }
        string number = duration.Groups["number"].Value.ToLowerInvariant().Trim();
        double count;
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double digits)) {
            count = digits;
        }
        else if (number.StartsWith("half", StringComparison.Ordinal)) {
            count = 0.5;
        }
        else if (number.Contains("couple", StringComparison.Ordinal)) {
            count = 2;
        }
        else if (NumberWords.TryGetValue(number, out int word)) {
            count = word;
        }
        else {
            return null;
        }
        return unit switch {
            _ when unit.StartsWith("hour", StringComparison.Ordinal) => count / MapMath.HoursPerDay,
            _ when unit.StartsWith("week", StringComparison.Ordinal) => count * 7,
            _ when unit.StartsWith("month", StringComparison.Ordinal) => count * 30,
            _ => count
        };
    }

    private static TravelMode? ModeOf(string sentence) {
        if (ByShip().IsMatch(sentence)) {
            return MapMath.Ship;
        }
        if (ByHorse().IsMatch(sentence)) {
            return MapMath.Horse;
        }
        if (ByCart().IsMatch(sentence)) {
            return MapMath.Cart;
        }
        return OnFoot().IsMatch(sentence) ? MapMath.Foot : null;
    }

    private static readonly Dictionary<string, int> NumberWords = new() {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40
    };

    [GeneratedRegex(@"[^.!?\r\n]+[.!?]*")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\b(?:(?<number>\d+(?:\.\d+)?|half an?|a couple of|a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty)[\s-]+(?<unit>hours?|days?|nights?|weeks?|months?)|a (?<unit>fortnight))\b", RegexOptions.IgnoreCase)]
    internal static partial Regex Duration();

    [GeneratedRegex(@"\b(rode|ride|rides|riding|ridden|walk\w*|march\w*|sail\w*|journey\w*|travel\w*|trek\w*|hik(e|ed|ing)|voyage\w*|trip|gallop\w*|drove|drive|driven|reach\w*|arriv\w*|crossing|crossed|made (it|their way|his way|her way)|from)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TravelWord();

    [GeneratedRegex(@"\b(flew|fly|flying|flight|flown|dragon|griffin|gryphon|wing(s|ed)?|airship|teleport\w*|portal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Air();

    [GeneratedRegex(@"\b(sail\w*|ship|boat|voyage\w*|by sea|rowed|rowing|galley|ferry)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ByShip();

    [GeneratedRegex(@"\b(rode|ride|rides|riding|ridden|gallop\w*|horse(s|back)?|mount(s|ed)?|saddle\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ByHorse();

    [GeneratedRegex(@"\b(cart|wagon|caravan|carriage|coach|oxen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ByCart();

    [GeneratedRegex(@"\b(walk\w*|march\w*|on foot|trek\w*|hik(e|ed|ing)|trudg\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnFoot();
}