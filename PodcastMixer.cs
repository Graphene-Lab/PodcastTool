using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AIOrchestrator.API;

/// <summary>Internal: renders the podcast audio. The narration is synthesized sentence by
/// sentence with the shared <see cref="KokoroTts"/> engine; the CC0 jingle and background
/// loop are decoded and resampled to 48 kHz stereo with OwnAudioSharp (FileSource); the three
/// streams are then mixed sample by sample (deterministic, fast, cross-platform) and written
/// as a 48 kHz stereo 16-bit WAV.</summary>
internal static partial class PodcastMixer
{
    /// <summary>Human-readable reason when <see cref="Mix"/> fails (kept for the tool result).</summary>
    internal static string LastError { get; private set; } = "";

    // Background volume under the narration (a quiet bed, standard for spoken podcasts).
    private const float BackgroundGain = 0.12f;

    /// <summary>Mixes the episode audio from the finished script. Returns false when the TTS
    /// engine or the music assets are unavailable (LastError explains why).</summary>
    internal static bool Mix(string script, string lang, string wavPath)
    {
        LastError = "";
        if (!KokoroTts.IsAvailable)
        {
            LastError = KokoroTts.UnavailableReason;
            return false;
        }

        EnsureNativeAvailable();
        if (!TryLoadMusic(out var jingle, out var background))
            return false;

        var sampleRate = KokoroTts.SampleRate;             // 24 kHz mono narration
        const int outRate = 48000;                          // output: 48 kHz stereo
        var jingleDuration = (double)jingle.Length / (2 * 2) / outRate;   // stereo int16 frames → seconds

        // 1) Build the narration PCM: TTS segments + silence for the [markers] (the [SIGLA]
        //    marker leaves a gap exactly as long as the jingle).
        var narration = new List<byte>(sampleRate * 2 * 60);   // int16 mono
        var jingleStartSec = -1.0;
        var jingleGapInserted = false;
        var currentSec = 0.0;

        void AppendSilence(double seconds)
        {
            var n = (int)(seconds * sampleRate);
            for (int i = 0; i < n; i++)
            {
                narration.Add(0);
                narration.Add(0);
            }
            currentSec += seconds;
        }

        foreach (var token in Regex.Split(script, @"(\[[^\]]+\])"))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var marker = Regex.Match(token, @"^\[([^\]]+)\]$");
            if (marker.Success)
            {
                var name = marker.Groups[1].Value.Trim().ToLowerInvariant();
                switch (name)
                {
                    case "sigla":
                        if (jingleGapInserted) break;
                        jingleStartSec = currentSec;
                        AppendSilence(jingleDuration);
                        jingleGapInserted = true;
                        break;
                    case "pausa musicale":
                        AppendSilence(0.8);
                        break;
                    case "breve silenzio":
                        AppendSilence(0.4);
                        break;
                    case "primo atto":
                    case "secondo atto":
                    case "terzo atto":
                    case "outro":
                        AppendSilence(1.0);
                        break;
                    default:
                        AppendSilence(0.3);   // any other [marker]: unread, short pause
                        break;
                }
                continue;
            }

            var speakable = VoiceConversation.ToSpeakable(token);
            if (speakable.Length == 0) continue;
            foreach (var sentence in VoiceConversation.SplitSentences(speakable))
            {
                var pcm = KokoroTts.SynthesizePcm(sentence, lang);
                if (pcm == null || pcm.Length == 0) continue;
                narration.AddRange(pcm);
                currentSec += (double)pcm.Length / 2 / sampleRate;
            }
        }

        if (narration.Count == 0)
        {
            LastError = "the script contains no speakable text.";
            return false;
        }

        // 2) Mix: narration (int16 mono 24 kHz → 48 kHz stereo, gain 1.0) + jingle (at its
        //    slot, fade out) + background loop (quiet bed, fade in/out at the edges).
        var narration16 = MemoryMarshal.Cast<byte, short>(CollectionsMarshal.AsSpan(narration));
        int narrationFrames24 = narration16.Length;               // 24 kHz mono frames
        long totalFrames48 = (long)narrationFrames24 * 2;         // 48 kHz stereo frames
        var jingleStartFrame = jingleStartSec >= 0 ? (long)(jingleStartSec * outRate) : -1L;
        var fadeOutSamples = (int)(outRate * 0.6);                // jingle fade-out, 0.6 s
        var bgFadeIn = (int)(outRate * 0.5);
        var bgFadeOut = (int)(outRate * 1.0);

        using var fs = File.Create(wavPath);
        using var bw = new BinaryWriter(fs);
        WriteWavHeader(bw, totalFrames48, outRate);

        var block = new short[outRate * 2];   // one second of stereo output (samples)
        for (long f = 0; f < totalFrames48;)
        {
            var n = (int)Math.Min(outRate, totalFrames48 - f);   // stereo frames this block
            for (int k = 0; k < n; k++)
            {
                var idx = f + k;                       // 48 kHz output frame index
                var nIdx = (int)(idx >> 1);            // 24 kHz narration frame (×2 upmix)
                if (nIdx >= narration16.Length) break;
                var voice = narration16[nIdx] / 32768f;

                // Background loop (48 kHz stereo int16 → float), quiet bed.
                var bf = (int)(idx % (background.Length / 2));
                var bgGain = BackgroundGain;
                if (idx < bgFadeIn) bgGain *= idx / (float)bgFadeIn;
                if (idx > totalFrames48 - bgFadeOut) bgGain *= (totalFrames48 - idx) / (float)bgFadeOut;
                float bL = background[bf * 2] / 32768f * bgGain;
                float bR = background[bf * 2 + 1] / 32768f * bgGain;

                // Jingle at its slot, with a fade-out so it hands off to the body cleanly.
                float jL = 0f, jR = 0f;
                if (jingleStartFrame >= 0)
                {
                    var jf = idx - jingleStartFrame;
                    if (jf >= 0 && jf < jingle.Length / 2)
                    {
                        var g = 1f;
                        if (jf > jingle.Length / 2 - fadeOutSamples)
                            g = (jingle.Length / 2 - jf) / (float)fadeOutSamples;
                        jL = jingle[(int)jf * 2] / 32768f * g;
                        jR = jingle[(int)jf * 2 + 1] / 32768f * g;
                    }
                }

                var l = voice + bL + jL;
                var r = voice + bR + jR;
                block[k * 2] = (short)Math.Clamp((int)(l * 32768f), short.MinValue, short.MaxValue);
                block[k * 2 + 1] = (short)Math.Clamp((int)(r * 32768f), short.MinValue, short.MaxValue);
            }
            for (int i = 0; i < n * 2; i++) bw.Write(block[i]);
            f += n;
        }
        return true;
    }

    /// <summary>Decodes the jingle and the background loop to 48 kHz stereo int16 with
    /// OwnAudioSharp, returning them as flat interleaved arrays (jingle: first 12 seconds —
    /// a full CC0 track is longer than a sigla).</summary>
    private static bool TryLoadMusic(out short[] jingle, out short[] background)
    {
        jingle = Array.Empty<short>();
        background = Array.Empty<short>();

        var jinglePath = FindAsset("jingle.mp3");
        var bgPath = FindAsset("background.mp3");
        if (jinglePath == null || bgPath == null)
        {
            LastError = "the CC0 music assets (jingle/background) are not deployed. Reinstall the plugin.";
            return false;
        }

        try
        {
            var jingleFile = new OwnaudioNET.Sources.FileSource(jinglePath, 8192, 48000, 2);
            jingle = ReadAllSamples(jingleFile, maxSeconds: 12);
            jingleFile.Dispose();
            var bgFile = new OwnaudioNET.Sources.FileSource(bgPath, 8192, 48000, 2);
            background = ReadAllSamples(bgFile, maxSeconds: 0);
            bgFile.Dispose();
            if (jingle.Length < 48000 || background.Length < 48000)
            {
                LastError = "the music assets are empty or unreadable.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"the music assets failed to load: {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads an entire source into a flat stereo int16 array (resampled to 48 kHz
    /// stereo by the FileSource). Cap the jingle length; the background loop is bounded too
    /// (a hard frame cap keeps the read terminating even if the decoder never signals the
    /// end of stream).</summary>
    private static short[] ReadAllSamples(OwnaudioNET.Sources.FileSource source, int maxSeconds)
    {
        var capSeconds = maxSeconds > 0 ? maxSeconds : 180;   // 3 min hard cap for the loop
        var samples = new List<short>(capSeconds * 48000 * 2);
        var buffer = new float[4096 * 2];
        var maxFrames = capSeconds * 48000;
        var totalFrames = 0;
        source.Play();
        while (!source.IsEndOfStream && totalFrames < maxFrames)
        {
            var frames = source.ReadSamples(buffer.AsSpan(), 4096);
            if (frames <= 0) break;
            for (int i = 0; i < frames * 2; i++)
                samples.Add((short)Math.Clamp((int)(buffer[i] * 32768f), short.MinValue, short.MaxValue));
            totalFrames += frames;
        }
        return samples.ToArray();
    }

    /// <summary>Locates a music asset: the host assets/ folder (PluginUpdater merge target)
    /// or the plugin's own assets/audio/ folder.</summary>
    private static string? FindAsset(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "audio", fileName),
            Path.Combine(AppContext.BaseDirectory, "assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        // Plugin subfolder layout: Tools/<Plugin>/assets/audio/...
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (Directory.Exists(toolsDir))
        {
            foreach (var dir in Directory.GetDirectories(toolsDir, "*", SearchOption.AllDirectories))
            {
                var candidate = Path.Combine(dir, "assets", "audio", fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>OwnAudioSharp's native FFI must sit in the app base directory (verified
    /// empirically: the byte-loaded shim does not see the plugin folder). Copies the matching
    /// per-RID native from the plugin payload on first use — idempotent, cross-platform.</summary>
    private static void EnsureNativeAvailable()
    {
        var appBase = AppContext.BaseDirectory;
        var nativeName = OperatingSystem.IsWindows() ? "ownaudio_ffi.dll"
            : OperatingSystem.IsMacOS() ? "libownaudio_ffi.dylib"
            : "libownaudio_ffi.so";
        if (File.Exists(Path.Combine(appBase, nativeName))) return;

        var rid = OperatingSystem.IsWindows()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
            : OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

        var dirs = new List<string>();
        var toolsDir = Path.Combine(appBase, "Tools");
        if (Directory.Exists(toolsDir))
        {
            dirs.Add(toolsDir);
            dirs.AddRange(Directory.GetDirectories(toolsDir, "*", SearchOption.AllDirectories));
        }
        dirs.Add(appBase);   // harness/dev scenario: the SDK copied runtimes/ into the bin

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, "runtimes", rid, "native", nativeName);
            if (!File.Exists(candidate)) continue;
            try
            {
                File.Copy(candidate, Path.Combine(appBase, nativeName), overwrite: true);
                return;
            }
            catch { }
        }
    }

    private static void WriteWavHeader(BinaryWriter bw, long totalFrames48, int sampleRate)
    {
        var dataLen = (int)(totalFrames48 * 2 * 2);   // stereo int16 — 4-byte fields in the header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataLen);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)2);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2 * 2);
        bw.Write((short)4);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataLen);
    }
}
