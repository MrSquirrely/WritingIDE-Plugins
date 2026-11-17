using System.Runtime.InteropServices;

namespace AmbientSoundscapes;

/// <summary>One looping sound in the mix.</summary>
public sealed class SoundLayer(string name, bool isCustom) {
    private float[]? _samples;
    private float _volume;

    public string Name { get; } = name;

    /// <summary>True for a sound from the writer's own Sounds folder.</summary>
    public bool IsCustom { get; } = isCustom;

    /// <summary>The loop; null until it has been made or loaded.</summary>
    public float[]? Samples {
        get => Volatile.Read(ref _samples);
        set => Volatile.Write(ref _samples, value);
    }

    /// <summary>The volume the writer asked for, 0 to 1. The mixer glides towards it.</summary>
    public float Volume {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 1));
    }

    /// <summary>Only the mixer thread touches these.</summary>
    internal float Current;

    internal int Position;
}

/// <summary>
/// Mixes the layers and plays them through the Windows wave-out device. A small mixer of its own, because looping
/// sounds need to repeat without a gap and fade smoothly, which the media player cannot do.
/// </summary>
public sealed class AudioMixer : IDisposable {
    private const int BufferSamples = SoundSynth.SampleRate / 10;
    private const int BufferCount = 4;

    /// <summary>How fast a layer's volume moves, per sample: a full fade takes about a second and a half.</summary>
    private const float Glide = 1f / (SoundSynth.SampleRate * 1.5f);

    private readonly Lock _lock = new();
    private volatile SoundLayer[] _layers = [];
    private float _master = 0.6f;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>The overall volume, 0 to 1.</summary>
    public float Master {
        get => Volatile.Read(ref _master);
        set => Volatile.Write(ref _master, Math.Clamp(value, 0, 1));
    }

    /// <summary>Why nothing can be heard (no sound device, say); null while all is well.</summary>
    public string? Problem { get; private set; }

    public bool IsRunning => _running;

    public void SetLayers(IEnumerable<SoundLayer> layers) {
        _layers = layers.ToArray();
    }

    /// <summary>Starts the device if it is not playing. Safe to call again.</summary>
    public void Start() {
        lock (_lock) {
            if (_running) {
                return;
            }
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "Ambient Soundscapes mixer" };
            _thread.Start();
        }
    }

    /// <summary>Stops the device and waits for the mixer to finish.</summary>
    public void Stop() {
        Thread? thread;
        lock (_lock) {
            _running = false;
            thread = _thread;
            _thread = null;
        }
        thread?.Join(2000);
    }

    public void Dispose() {
        Stop();
    }

    /// <summary>
    /// Fills a buffer with the next stretch of the mix and moves every layer on. Returns true while anything can be
    /// heard (a layer still fading out counts).
    /// </summary>
    internal static bool Mix(IReadOnlyList<SoundLayer> layers, float master, float[] buffer) {
        Array.Clear(buffer);
        bool audible = false;
        foreach (SoundLayer layer in layers) {
            float[]? samples = layer.Samples;
            float target = samples is null ? 0 : layer.Volume;
            if ((samples is null) || (samples.Length == 0) || ((target == 0) && (layer.Current == 0))) {
                continue;
            }
            audible = true;
            int position = layer.Position % samples.Length;
            float current = layer.Current;
            for (int i = 0; i < buffer.Length; i++) {
                if (current < target) {
                    current = Math.Min(target, current + Glide);
                }
                else if (current > target) {
                    current = Math.Max(target, current - Glide);
                }
                // The volume slider feels even when the gain follows its square.
                buffer[i] += samples[position] * current * current;
                if (++position == samples.Length) {
                    position = 0;
                }
            }
            layer.Current = current;
            layer.Position = position;
        }
        for (int i = 0; i < buffer.Length; i++) {
            buffer[i] = MathF.Tanh(buffer[i] * master * 1.4f);
        }
        return audible;
    }

    private void Run() {
        WaveFormat format = new() {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = SoundSynth.SampleRate,
            AvgBytesPerSec = SoundSynth.SampleRate * 2,
            BlockAlign = 2,
            BitsPerSample = 16
        };
        int result = waveOutOpen(out IntPtr device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) {
            Problem = "No sound device could be opened.";
            _running = false;
            return;
        }
        Problem = null;
        int headerSize = Marshal.SizeOf<WaveHeader>();
        IntPtr[] headers = new IntPtr[BufferCount];
        IntPtr[] data = new IntPtr[BufferCount];
        bool[] queued = new bool[BufferCount];
        float[] mix = new float[BufferSamples];
        short[] pcm = new short[BufferSamples];
        try {
            for (int i = 0; i < BufferCount; i++) {
                data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
                headers[i] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WaveHeader { Data = data[i], BufferLength = BufferSamples * 2 }, headers[i], false);
                waveOutPrepareHeader(device, headers[i], headerSize);
            }
            while (_running) {
                bool wrote = false;
                for (int i = 0; i < BufferCount; i++) {
                    if (queued[i] && ((Marshal.PtrToStructure<WaveHeader>(headers[i]).Flags & HeaderDone) == 0)) {
                        continue;
                    }
                    Mix(_layers, Master, mix);
                    for (int sample = 0; sample < BufferSamples; sample++) {
                        pcm[sample] = (short)(mix[sample] * 32000);
                    }
                    Marshal.Copy(pcm, 0, data[i], BufferSamples);
                    waveOutWrite(device, headers[i], headerSize);
                    queued[i] = true;
                    wrote = true;
                }
                if (!wrote) {
                    Thread.Sleep(15);
                }
            }
        }
        finally {
            waveOutReset(device);
            for (int i = 0; i < BufferCount; i++) {
                if (headers[i] != IntPtr.Zero) {
                    waveOutUnprepareHeader(device, headers[i], headerSize);
                    Marshal.FreeHGlobal(headers[i]);
                }
                if (data[i] != IntPtr.Zero) {
                    Marshal.FreeHGlobal(data[i]);
                }
            }
            waveOutClose(device);
        }
    }

    // ------------------------------------------------------------------
    // winmm.dll
    // ------------------------------------------------------------------

    private const int WaveMapper = -1;
    private const uint HeaderDone = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}

/// <summary>A mix written as text: "Rain 70, Wind 30". This is what a place's Soundscape attribute holds.</summary>
public static class MixText {
    /// <summary>The volume a sound gets when the text gives none ("Rain").</summary>
    public const int DefaultPercent = 60;

    /// <summary>
    /// Reads "Rain 70, Wind 30" as sound name to volume (0 to 1). Names match <paramref name="known"/> whatever their
    /// case; names that are not known are left out.
    /// </summary>
    public static Dictionary<string, float> Parse(string text, IEnumerable<string> known) {
        Dictionary<string, float> mix = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = known.OrderByDescending(static name => name.Length).ToList();
        foreach (string part in text.Split([',', ';', '+', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            string? name = names.FirstOrDefault(candidate => part.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (name is null) {
                continue;
            }
            string rest = part[name.Length..].Trim(' ', ':', '=', '%');
            mix[name] = int.TryParse(rest, out int percent) ? Math.Clamp(percent, 0, 100) / 100f : DefaultPercent / 100f;
        }
        return mix;
    }

    public static string Format(IEnumerable<(string Name, float Volume)> layers) {
        return string.Join(", ", layers.Where(static layer => layer.Volume > 0.005f).Select(static layer => $"{layer.Name} {Math.Round(layer.Volume * 100)}"));
    }
}