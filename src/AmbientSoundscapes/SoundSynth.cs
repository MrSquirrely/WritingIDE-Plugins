namespace AmbientSoundscapes;

/// <summary>
/// Makes the built-in sounds from noise and sine waves, so the plugin ships no recordings. Every sound is a loop: its
/// end is cross-faded into its start, and slow movements (gusts, waves) repeat a whole number of times in the loop.
/// </summary>
public static class SoundSynth {
    public const int SampleRate = 22050;

    /// <summary>The length of every built-in loop, in seconds.</summary>
    public const double LoopSeconds = 32;

    private const double CrossfadeSeconds = 2;

    public static IReadOnlyList<string> Presets { get; } = ["Rain", "Storm", "Wind", "Ocean", "River", "Fireplace", "Night", "Forest", "Tavern"];

    /// <summary>One loop of a preset, as samples from -1 to 1. Null for a name that is not a preset.</summary>
    public static float[]? Generate(string preset) {
        int seed = preset.Aggregate(17, static (hash, character) => (hash * 31) + char.ToLowerInvariant(character));
        Random random = new(seed);
        int length = (int)((LoopSeconds + CrossfadeSeconds) * SampleRate);
        float[]? samples = preset.ToLowerInvariant() switch {
            "rain" => Rain(random, length, heavy: false),
            "storm" => Storm(random, length),
            "wind" => Wind(random, length, strength: 1),
            "ocean" => Ocean(random, length),
            "river" => River(random, length),
            "fireplace" => Fireplace(random, length),
            "night" => Night(random, length),
            "forest" => Forest(random, length),
            "tavern" => Tavern(random, length),
            _ => null
        };
        return samples is null ? null : Normalize(MakeLoop(samples), 0.7f);
    }

    // ------------------------------------------------------------------
    // The sounds
    // ------------------------------------------------------------------

    private static float[] Rain(Random random, int length, bool heavy) {
        float[] samples = new float[length];
        // The hiss: white noise with the lows taken out.
        float low = 0;
        float smooth = 0;
        for (int i = 0; i < length; i++) {
            float noise = Noise(random);
            low += 0.12f * (noise - low);
            smooth += 0.55f * ((noise - low) - smooth);
            samples[i] = smooth * (heavy ? 0.55f : 0.4f);
        }
        // Drops: short bright ticks, some louder (nearer) than others.
        int drops = (int)(length / (double)SampleRate * (heavy ? 260 : 140));
        for (int n = 0; n < drops; n++) {
            int at = random.Next(length - 400);
            float level = (float)Math.Pow(random.NextDouble(), 3) * 0.5f;
            int duration = random.Next(40, 200);
            float tone = 0;
            for (int i = 0; i < duration; i++) {
                tone += 0.6f * (Noise(random) - tone);
                samples[at + i] += tone * level * (1 - ((float)i / duration));
            }
        }
        return samples;
    }

    private static float[] Storm(Random random, int length) {
        float[] samples = Rain(random, length, heavy: true);
        float[] wind = Wind(random, length, strength: 1.4);
        for (int i = 0; i < length; i++) {
            samples[i] += wind[i] * 0.5f;
        }
        // Thunder: a low rumble that swells and rolls away, three times in the loop.
        for (int n = 0; n < 3; n++) {
            int at = (int)((0.08 + (n * 0.31) + (random.NextDouble() * 0.08)) * (length - (6 * SampleRate)));
            int duration = (int)((3.5 + (random.NextDouble() * 2)) * SampleRate);
            float brown = 0;
            float rumble = 0;
            for (int i = 0; (i < duration) && (at + i < length); i++) {
                brown = (brown * 0.995f) + (Noise(random) * 0.09f);
                rumble += 0.02f * (brown - rumble);
                double t = (double)i / duration;
                // A sharp crack that decays, with slower rolls behind it.
                double envelope = Math.Exp(-t * 4) + (0.5 * Math.Exp(-Math.Pow((t - 0.35) * 5, 2))) + (0.3 * Math.Exp(-Math.Pow((t - 0.65) * 5, 2)));
                samples[at + i] += (float)(rumble * envelope * 5);
            }
        }
        return samples;
    }

    private static float[] Wind(Random random, int length, double strength) {
        float[] samples = new float[length];
        double[] phases = [random.NextDouble() * Math.Tau, random.NextDouble() * Math.Tau, random.NextDouble() * Math.Tau];
        float brown = 0;
        float filtered = 0;
        for (int i = 0; i < length; i++) {
            double t = i / (LoopSeconds * SampleRate);
            // Gusts: slow waves that fit the loop a whole number of times.
            double gust = 0.5 + (0.25 * Math.Sin((Math.Tau * 2 * t) + phases[0])) + (0.15 * Math.Sin((Math.Tau * 5 * t) + phases[1])) + (0.1 * Math.Sin((Math.Tau * 9 * t) + phases[2]));
            brown = (brown * 0.985f) + (Noise(random) * 0.15f);
            // A stronger gust also whistles higher.
            float cutoff = (float)(0.02 + (0.09 * gust));
            filtered += cutoff * (brown - filtered);
            samples[i] = (float)(filtered * (0.35 + gust) * strength);
        }
        return samples;
    }

    private static float[] Ocean(Random random, int length) {
        float[] samples = new float[length];
        float brown = 0;
        float low = 0;
        float hiss = 0;
        double phase = random.NextDouble() * Math.Tau;
        for (int i = 0; i < length; i++) {
            double t = i / (LoopSeconds * SampleRate);
            // Four waves in the loop, each building slowly and breaking fast, and a slower swell under them.
            double wave = (((4 * t) + (phase / Math.Tau)) % 1 + 1) % 1;
            double crest = wave < 0.7 ? Math.Pow(wave / 0.7, 2.2) : Math.Pow(1 - ((wave - 0.7) / 0.3), 1.4);
            double swell = 0.75 + (0.25 * Math.Sin((Math.Tau * t) + phase));
            float noise = Noise(random);
            brown = (brown * 0.99f) + (noise * 0.1f);
            low += 0.05f * (brown - low);
            hiss += 0.4f * (noise - hiss);
            samples[i] = (float)(((low * 2.2 * (0.35 + crest)) + ((noise - hiss) * 0.12 * crest * crest)) * swell);
        }
        return samples;
    }

    private static float[] River(Random random, int length) {
        float[] samples = new float[length];
        float low = 0;
        float band = 0;
        float flutter = 1;
        float target = 1;
        for (int i = 0; i < length; i++) {
            if (i % 300 == 0) {
                target = 0.55f + ((float)random.NextDouble() * 0.9f);
            }
            flutter += 0.004f * (target - flutter);
            float noise = Noise(random);
            low += 0.2f * (noise - low);
            band += 0.5f * ((noise - low) - band);
            samples[i] = band * 0.4f * flutter;
        }
        // Gurgles: brief falling tones as water turns over stones.
        int gurgles = (int)(length / (double)SampleRate * 9);
        for (int n = 0; n < gurgles; n++) {
            int at = random.Next(length - 3000);
            double frequency = 500 + (random.NextDouble() * 900);
            int duration = random.Next(900, 2600);
            float level = 0.05f + ((float)random.NextDouble() * 0.1f);
            double phase = 0;
            for (int i = 0; i < duration; i++) {
                double t = (double)i / duration;
                phase += Math.Tau * frequency * (1.3 - (0.5 * t)) / SampleRate;
                samples[at + i] += (float)(Math.Sin(phase) * level * Math.Sin(Math.PI * t));
            }
        }
        return samples;
    }

    private static float[] Fireplace(Random random, int length) {
        float[] samples = new float[length];
        float brown = 0;
        float low = 0;
        float breath = 1;
        float target = 1;
        for (int i = 0; i < length; i++) {
            if (i % 4000 == 0) {
                target = 0.6f + ((float)random.NextDouble() * 0.8f);
            }
            breath += 0.0006f * (target - breath);
            brown = (brown * 0.992f) + (Noise(random) * 0.09f);
            low += 0.03f * (brown - low);
            samples[i] = low * 2.6f * breath;
        }
        // Crackles and the odd pop.
        int crackles = (int)(length / (double)SampleRate * 16);
        for (int n = 0; n < crackles; n++) {
            int at = random.Next(length - 1200);
            bool pop = random.NextDouble() < 0.12;
            int duration = pop ? random.Next(300, 900) : random.Next(15, 90);
            float level = pop ? 0.5f : 0.12f + ((float)random.NextDouble() * 0.3f);
            float tone = 0;
            for (int i = 0; i < duration; i++) {
                tone += (pop ? 0.25f : 0.7f) * (Noise(random) - tone);
                samples[at + i] += tone * level * (float)Math.Exp(-5.0 * i / duration);
            }
        }
        return samples;
    }

    private static float[] Night(Random random, int length) {
        float[] samples = new float[length];
        // A faint bed of air.
        float brown = 0;
        float low = 0;
        for (int i = 0; i < length; i++) {
            brown = (brown * 0.99f) + (Noise(random) * 0.1f);
            low += 0.04f * (brown - low);
            samples[i] = low * 0.6f;
        }
        // Crickets: each one a steady pitch, trilling in bursts with its own rhythm.
        (double Frequency, double Trill, double Period, double Level)[] crickets = [(4300, 31, 1.0, 0.11), (3700, 26, 1.6, 0.07), (4900, 36, 2.0, 0.045), (4050, 29, 3.2, 0.03)];
        foreach ((double frequency, double trill, double period, double level) in crickets) {
            double offset = random.NextDouble() * period;
            for (int i = 0; i < length; i++) {
                double t = (double)i / SampleRate;
                double inPeriod = ((t + offset) % period) / period;
                if (inPeriod > 0.55) {
                    continue;
                }
                double burst = Math.Sin(Math.PI * inPeriod / 0.55);
                double pulse = Math.Max(0, Math.Sin(Math.Tau * trill * t));
                samples[i] += (float)(Math.Sin(Math.Tau * frequency * t) * pulse * pulse * burst * level);
            }
        }
        return samples;
    }

    private static float[] Forest(Random random, int length) {
        float[] samples = Wind(random, length, strength: 0.45);
        // Leaves: a soft hiss that follows the breeze.
        float low = 0;
        for (int i = 0; i < length; i++) {
            float noise = Noise(random);
            low += 0.3f * (noise - low);
            samples[i] += (noise - low) * 0.05f * (0.4f + Math.Abs(samples[i]) * 4);
        }
        // Birds: phrases of short rising or falling whistles.
        int phrases = (int)(length / (double)SampleRate / 2.2);
        for (int n = 0; n < phrases; n++) {
            int at = random.Next(length - (2 * SampleRate));
            double pitch = 2200 + (random.NextDouble() * 2600);
            double sweep = (random.NextDouble() * 1.2) - 0.4;
            int notes = random.Next(2, 7);
            int noteLength = random.Next(900, 2400);
            int gap = random.Next(500, 1800);
            float level = 0.04f + ((float)random.NextDouble() * 0.09f);
            for (int note = 0; note < notes; note++) {
                int start = at + (note * (noteLength + gap));
                double phase = 0;
                for (int i = 0; (i < noteLength) && (start + i < length); i++) {
                    double t = (double)i / noteLength;
                    phase += Math.Tau * pitch * (1 + (sweep * t)) / SampleRate;
                    samples[start + i] += (float)(Math.Sin(phase) * Math.Sin(Math.PI * t) * level);
                }
            }
        }
        return samples;
    }

    private static float[] Tavern(Random random, int length) {
        float[] samples = new float[length];
        // The murmur: a handful of "voices", each a band of noise that rises and falls at the pace of speech.
        for (int voice = 0; voice < 7; voice++) {
            float low = 0;
            float band = 0;
            float cutoff = 0.05f + ((float)random.NextDouble() * 0.08f);
            float level = 0.5f + ((float)random.NextDouble() * 0.5f);
            float envelope = 0;
            float target = 0;
            int next = 0;
            bool speaking = true;
            int turn = random.Next(SampleRate, 4 * SampleRate);
            for (int i = 0; i < length; i++) {
                if (i >= next) {
                    // A syllable, or the breath between two.
                    target = speaking && (random.NextDouble() < 0.8) ? 0.3f + ((float)random.NextDouble() * 0.7f) : 0;
                    next = i + random.Next(1400, 5200);
                }
                if (i % turn == turn - 1) {
                    speaking = random.NextDouble() < 0.7;
                    turn = random.Next(SampleRate, 5 * SampleRate);
                }
                envelope += 0.002f * (target - envelope);
                float noise = Noise(random);
                low += cutoff * (noise - low);
                band += (cutoff * 0.35f) * (low - band);
                samples[i] += (low - band) * envelope * level * 1.6f;
            }
        }
        // Mugs and plates: a bright ping now and then, and a duller knock.
        int clinks = (int)(length / (double)SampleRate / 1.8);
        for (int n = 0; n < clinks; n++) {
            int at = random.Next(length - 6000);
            bool knock = random.NextDouble() < 0.4;
            double frequency = knock ? 140 + (random.NextDouble() * 120) : 1900 + (random.NextDouble() * 2400);
            int duration = knock ? 2400 : random.Next(2000, 5200);
            float level = knock ? 0.25f : 0.03f + ((float)random.NextDouble() * 0.06f);
            for (int i = 0; i < duration; i++) {
                double t = (double)i / SampleRate;
                samples[at + i] += (float)(Math.Sin(Math.Tau * frequency * t) * Math.Exp(-(knock ? 28 : 9) * t) * level);
            }
        }
        return samples;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static float Noise(Random random) {
        return ((float)random.NextDouble() * 2) - 1;
    }

    /// <summary>Folds the extra tail into the start with an equal-power cross-fade, so the loop has no seam.</summary>
    internal static float[] MakeLoop(float[] samples) {
        int fade = (int)(CrossfadeSeconds * SampleRate);
        int length = samples.Length - fade;
        float[] loop = new float[length];
        Array.Copy(samples, loop, length);
        for (int i = 0; i < fade; i++) {
            double t = (double)i / fade;
            loop[i] = (float)((samples[i] * Math.Sin(t * Math.PI / 2)) + (samples[length + i] * Math.Cos(t * Math.PI / 2)));
        }
        return loop;
    }

    /// <summary>Scales the sound so its loudest moment reaches <paramref name="peak"/>, with a soft ceiling for stray spikes.</summary>
    internal static float[] Normalize(float[] samples, float peak) {
        // The 99.9th percentile, so that one loud pop does not make everything else quiet.
        float[] sorted = samples.Select(Math.Abs).Order().ToArray();
        float reference = Math.Max(1e-6f, sorted[(int)(sorted.Length * 0.999)]);
        float gain = peak / reference;
        for (int i = 0; i < samples.Length; i++) {
            samples[i] = MathF.Tanh(samples[i] * gain);
        }
        return samples;
    }
}

/// <summary>Reads and writes 16-bit PCM WAV files, for the writer's own sounds and for tests.</summary>
public static class WavFile {
    /// <summary>
    /// Reads a PCM WAV file as mono samples at <see cref="SoundSynth.SampleRate"/>. Returns null for a file that is not
    /// 8, 16, 24 or 32-bit PCM (compressed WAVs, and files that are not WAV at all).
    /// </summary>
    public static float[]? Read(string path) {
        try {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            if ((stream.Length < 44) || (new string(reader.ReadChars(4)) != "RIFF")) {
                return null;
            }
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") {
                return null;
            }
            int channels = 0;
            int rate = 0;
            int bits = 0;
            int format = 0;
            while (stream.Position + 8 <= stream.Length) {
                string chunk = new(reader.ReadChars(4));
                int size = reader.ReadInt32();
                long next = stream.Position + size + (size & 1);
                if (chunk == "fmt ") {
                    format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                }
                else if (chunk == "data") {
                    // 1 is PCM; 0xFFFE is the "extensible" wrapper most tools use for PCM too.
                    if ((format is not (1 or -2)) || (channels is < 1 or > 8) || (rate < 4000) || (bits is not (8 or 16 or 24 or 32))) {
                        return null;
                    }
                    size = (int)Math.Min(size, stream.Length - stream.Position);
                    // At most ten minutes: a longer file is not a loop.
                    size = Math.Min(size, rate * channels * (bits / 8) * 600);
                    return Convert(reader.ReadBytes(size), channels, rate, bits);
                }
                stream.Position = Math.Min(next, stream.Length);
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException) {
            return null;
        }
    }

    private static float[] Convert(byte[] data, int channels, int rate, int bits) {
        int bytes = bits / 8;
        int frames = data.Length / (bytes * channels);
        float[] mono = new float[frames];
        for (int frame = 0; frame < frames; frame++) {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++) {
                int at = ((frame * channels) + channel) * bytes;
                sum += bits switch {
                    8 => (data[at] - 128) / 128f,
                    16 => BitConverter.ToInt16(data, at) / 32768f,
                    24 => ((data[at] | (data[at + 1] << 8) | ((sbyte)data[at + 2] << 16))) / 8388608f,
                    _ => BitConverter.ToInt32(data, at) / 2147483648f
                };
            }
            mono[frame] = sum / channels;
        }
        if (rate == SoundSynth.SampleRate) {
            return mono;
        }
        // Linear resampling: good enough for ambience.
        int length = (int)((long)frames * SoundSynth.SampleRate / rate);
        float[] resampled = new float[length];
        double step = (double)rate / SoundSynth.SampleRate;
        for (int i = 0; i < length; i++) {
            double position = i * step;
            int index = (int)position;
            float next = mono[Math.Min(frames - 1, index + 1)];
            resampled[i] = mono[index] + ((next - mono[index]) * (float)(position - index));
        }
        return resampled;
    }

    public static void Write(string path, float[] samples, int sampleRate = SoundSynth.SampleRate) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        int dataLength = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (float sample in samples) {
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }
    }
}