using AmbientSoundscapes;

using MapMaker;

using WritingIDE.Core.Model;
using WritingIDE.Sdk;

namespace Plugins.Tests;

public sealed class SoundscapesTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WritingIdePlugins.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryPresetIsAnAudibleLoopOfTheSameLength() {
        foreach (string preset in SoundSynth.Presets) {
            float[]? samples = SoundSynth.Generate(preset);

            Assert.NotNull(samples);
            Assert.Equal((int)(SoundSynth.LoopSeconds * SoundSynth.SampleRate), samples.Length);
            Assert.All(samples, static sample => Assert.InRange(sample, -1f, 1f));
            double loudness = Math.Sqrt(samples.Average(static sample => (double)sample * sample));
            Assert.True(loudness > 0.02, $"{preset} is nearly silent ({loudness:0.000}).");
            // The same name gives the same sound, so a mix sounds the same every day.
            Assert.Equal(samples.Take(2000), SoundSynth.Generate(preset.ToUpperInvariant())!.Take(2000));
        }
        Assert.Null(SoundSynth.Generate("Vuvuzela"));
    }

    [Fact]
    public void ALoopEndsWhereItBegins() {
        // A ramp makes any seam obvious: without the cross-fade the jump from end to start would be 0.9.
        float[] ramp = Enumerable.Range(0, 6 * SoundSynth.SampleRate).Select(static i => 0.9f * i / (6 * SoundSynth.SampleRate)).ToArray();

        float[] loop = SoundSynth.MakeLoop(ramp);

        Assert.Equal(4 * SoundSynth.SampleRate, loop.Length);
        Assert.InRange(Math.Abs(loop[0] - loop[^1]), 0, 0.01);
    }

    [Fact]
    public void MixTextReadsNamesAndVolumes() {
        string[] known = ["Rain", "Wind", "Fireplace", "Sea Wind"];

        Dictionary<string, float> mix = MixText.Parse("rain 70, Wind, sea wind: 25%; Kazoo 90 + fireplace=40", known);

        Assert.Equal(4, mix.Count);
        Assert.Equal(0.7f, mix["Rain"]);
        Assert.Equal(0.6f, mix["Wind"]);
        Assert.Equal(0.25f, mix["Sea Wind"]);
        Assert.Equal(0.4f, mix["Fireplace"]);
        Assert.Equal("Rain 70, Wind 60", MixText.Format([("Rain", 0.7f), ("Wind", 0.6f), ("Ocean", 0f)]));
        Assert.Empty(MixText.Parse("", known));
    }

    [Fact]
    public void TheMixerFadesALayerInAndLoopsIt() {
        SoundLayer layer = new("Tone", isCustom: false) { Samples = Enumerable.Repeat(0.5f, 1000).ToArray(), Volume = 1 };
        float[] buffer = new float[SoundSynth.SampleRate / 10];

        Assert.True(AudioMixer.Mix([layer], 1, buffer));
        // It starts silent and rises: no click when a sound is switched on.
        Assert.Equal(0, buffer[0], 3);
        Assert.True(buffer[^1] > buffer[100]);
        // 2,205 samples through a 1,000-sample loop.
        Assert.Equal(205, layer.Position);

        layer.Volume = 0;
        for (int i = 0; i < 40; i++) {
            AudioMixer.Mix([layer], 1, buffer);
        }
        Assert.False(AudioMixer.Mix([layer], 1, buffer));
        Assert.All(buffer, static sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void WavFilesRoundTripAndOddFilesAreRefused() {
        Directory.CreateDirectory(_directory);
        float[] tone = Enumerable.Range(0, 4410).Select(static i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
        string path = Path.Combine(_directory, "tone.wav");

        WavFile.Write(path, tone, 44100);
        float[]? read = WavFile.Read(path);

        // Read back at the mixer's rate: half the samples.
        Assert.NotNull(read);
        Assert.Equal(2205, read.Length);
        Assert.Equal(tone[200], read[100], 2);

        string notWav = Path.Combine(_directory, "song.wav");
        File.WriteAllText(notWav, "This is an MP3 that someone renamed, or not audio at all.");
        Assert.Null(WavFile.Read(notWav));
        Assert.Null(WavFile.Read(Path.Combine(_directory, "missing.wav")));
    }

    [Fact]
    public void TheChapterFollowsThePlaceItNamesMost() {
        EntityInfo Place(string name, string? soundscape, params string[] aliases) {
            Dictionary<string, string> attributes = soundscape is null ? [] : new Dictionary<string, string> { ["soundscape"] = soundscape };
            return new EntityInfo(Guid.NewGuid(), EntityKind.Place, name, aliases, "", attributes);
        }
        EntityInfo[] places = [Place("Greyhaven Lighthouse", "Ocean 70, Wind 30", "the light"), Place("Port Ashby", "Tavern 50"), Place("The Marsh", null)];

        (EntityInfo? place, string mix) = SoundscapesPlugin.FindPlace(places, "From the light she saw Port Ashby. The Marsh, The Marsh, The Marsh. Back at the light, the lamp was out.");

        Assert.Equal("Greyhaven Lighthouse", place?.Name);
        Assert.Equal("Ocean 70, Wind 30", mix);
        Assert.Null(SoundscapesPlugin.FindPlace(places, "Nowhere in particular; a lighter mood.").Place);
    }

    public void Dispose() {
        if (Directory.Exists(_directory)) {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class MapMakerTests {
    private static readonly MapPlace Greyhaven = new(Guid.NewGuid(), "Greyhaven Lighthouse", ["Greyhaven Lighthouse", "Greyhaven"], new MapPin(0.1, 0.1));
    private static readonly MapPlace Ashby = new(Guid.NewGuid(), "Port Ashby", ["Port Ashby"], new MapPin(0.4, 0.5));

    /// <summary>A map 600 km wide: Greyhaven to Port Ashby is 300 km, ten days on foot and five on horseback.</summary>
    private static MapData Map() {
        return new MapData { Width = 600, Unit = "km" };
    }

    [Fact]
    public void DistanceUsesTheMapsScaleAndUnit() {
        MapData map = Map();

        Assert.Equal(300, MapMath.Distance(map, Greyhaven.Pin, Ashby.Pin), 6);
        map.Unit = "miles";
        Assert.Equal(482.8, MapMath.ToKilometres(map, 300), 1);
        Assert.Equal("300 miles", MapMath.FormatDistance(map, 300));
        Assert.Equal(["10 days on foot", "5 days on horseback", "12 days by cart or wagon", "2 days by sailing ship"], MapMath.Estimate(300).Select(static estimate => estimate.ToString()));
        Assert.Equal("about 2 hours", MapMath.FormatDays(0.25));
        Assert.Equal("1.5 days", MapMath.FormatDays(1.6));
        Assert.Equal("4 weeks", MapMath.FormatDays(28));
    }

    [Theory]
    [InlineData("They rode from Greyhaven to Port Ashby in a day.", "a day", "too fast")]
    [InlineData("She walked from Port Ashby to Greyhaven Lighthouse in three hours.", "three hours", "too fast")]
    [InlineData("The ride from Greyhaven to Port Ashby took two months.", "two months", "too slow")]
    [InlineData("It was a two-day march from Greyhaven to Port Ashby.", "two-day", "too fast")]
    public void AJourneyThatDoesNotFitTheMapIsReported(string text, string marked, string verdict) {
        TravelFinding finding = Assert.Single(TravelCheck.Find(text, [Greyhaven, Ashby], Map()));

        Assert.Equal(marked, text.Substring(finding.Offset, finding.Length));
        Assert.Contains(verdict, finding.Message);
        Assert.Contains("300 km", finding.Message);
    }

    [Theory]
    // Five days on horseback, ten on foot: both fit, and so does anything within the margin.
    [InlineData("They rode from Greyhaven to Port Ashby in five days.")]
    [InlineData("She walked from Greyhaven to Port Ashby in a fortnight.")]
    [InlineData("They travelled from Greyhaven to Port Ashby in a week.")]
    // By ship it is two days.
    [InlineData("They sailed from Greyhaven to Port Ashby in a day.")]
    // Not a journey, only one place, or by air.
    [InlineData("It had rained for three hours in Greyhaven and in Port Ashby.")]
    [InlineData("They rode out of Greyhaven for an hour.")]
    [InlineData("The dragon flew from Greyhaven to Port Ashby in an hour.")]
    public void AJourneyThatFitsIsLeftAlone(string text) {
        Assert.Empty(TravelCheck.Find(text, [Greyhaven, Ashby], Map()));
    }

    [Fact]
    public void TheMapSurvivesSavingAndRepairsBadValues() {
        MapData map = Map();
        map.Image = "Maps/map.png";
        map.Pins[Greyhaven.Id] = Greyhaven.Pin;

        MapData loaded = MapData.Parse(map.ToJson());

        Assert.Equal("Maps/map.png", loaded.Image);
        Assert.Equal(600, loaded.Width);
        Assert.Equal(Greyhaven.Pin, loaded.Pins[Greyhaven.Id]);
        MapData repaired = MapData.Parse("""{ "Width": -5, "Unit": "parsecs", "Aspect": 500 }""");
        Assert.Equal((1000, "km", 10), (repaired.Width, repaired.Unit, repaired.Aspect));
        Assert.Empty(MapData.Parse("not json").Pins);
    }

    [Fact]
    public void MapFilesMustStayInsideTheProject() {
        const string project = @"C:\Novels\Tide";

        Assert.Equal(@"C:\Novels\Tide\Maps\map.png", MapPlugin.ResolveInProject(project, "Maps/map.png"));
        Assert.Null(MapPlugin.ResolveInProject(project, @"..\Secrets\map.png"));
        Assert.Null(MapPlugin.ResolveInProject(project, @"Maps\..\..\other.png"));
        Assert.Null(MapPlugin.ResolveInProject(project, @"C:\Windows\win.ini"));
        Assert.Null(MapPlugin.ResolveInProject(project, null));
        Assert.Null(MapPlugin.ResolveInProject("", "Maps/map.png"));
    }
}