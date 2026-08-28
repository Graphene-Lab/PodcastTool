using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GroovyCodecs.Mp3;
using GroovyCodecs.Types;

namespace AIOrchestrator.API;

/// <summary>Internal: renders the podcast audio. The narration (welcome intro first, then the
/// body with its [markers] as silences/pauses) is synthesized sentence by sentence with the
/// plugin-local <see cref="PodcastTts"/> engine; the CC0 jingle and background loop are
/// decoded and resampled to 48 kHz stereo with OwnAudioSharp (FileSource); the streams are
/// mixed sample by sample (deterministic, fast, cross-platform) with a DUCKED background bed
/// (barely perceptible under the voice, rising in the pauses), then encoded to MP3 with the
/// pure-managed GroovyMp3 (LAME) encoder.</summary>
internal static partial class PodcastMixer
{
    /// <summary>Human-readable reason when <see cref="Mix"/> fails (kept for the tool result).</summary>
    internal static string LastError { get; private set; } = "";

    // Background levels: a soft bed that is "barely perceptible" under the voice and rises
    // slightly during the silences (ducking, deterministic at sample level).
    private const float BackgroundWhileSpeaking = 0.05f;
    private const float BackgroundInPause = 0.12f;

    /// <summary>Mixes the episode (intro → jingle → body) and encodes the MP3. Returns false
    /// when the TTS engine or the music assets are unavailable (LastError explains why).</summary>
    internal static bool Mix(string intro, string body, string lang, string wavPath, string mp3Path)
    {
        LastError = "";
        if (!PodcastTts.IsAvailable)
        {
            LastError = PodcastTts.UnavailableReason;
            return false;
        }

        EnsureNativeAvailable();
        if (!TryLoadMusic(out var jingle, out var background))
            return false;

        var sampleRate = PodcastTts.SampleRate;              // 24 kHz mono narration
        const int outRate = 48000;                           // output: 48 kHz stereo
        var jingleDuration = (double)jingle.Length / (2 * 2) / outRate;   // stereo int16 frames → seconds

        // 1) Narration = welcome intro + 0.6 s breath + the body (markers become silences).
        //    The jingle slot is the gap right after the intro.
        var narration = new List<byte>(sampleRate * 2 * 60);   // int16 mono
        var currentSec = 0.0;
        var jingleStartSec = -1.0;

        void AppendPcm(byte[] pcm)
        {
            narration.AddRange(pcm);
            currentSec += (double)pcm.Length / 2 / sampleRate;
        }

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

        AppendPcm(Speak(intro, lang));
        AppendSilence(0.6);
        jingleStartSec = currentSec;
        AppendSilence(jingleDuration);

        foreach (var token in Regex.Split(body, @"(\[[^\]]+\])"))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var marker = Regex.Match(token, @"^\[([^\]]+)\]$");
            if (marker.Success)
            {
                var name = marker.Groups[1].Value.Trim().ToLowerInvariant();
                switch (name)
                {
                    case "sigla":
                        AppendSilence(0.8);   // the jingle is placed by the mixer, not the marker
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
                        AppendSilence(0.3);
                        break;
                }
                continue;
            }
            AppendPcm(Speak(token, lang));
        }

        if (narration.Count == 0)
        {
            LastError = "the script contains no speakable text.";
            return false;
        }
        Log.LogStep($"PodcastMixer: narration {(double)narration.Count / 2 / sampleRate:F0}s, jingle at {jingleStartSec:F0}s (jingle {jingleDuration:F0}s)");

        // 2) Mix: narration (int16 mono 24 kHz → 48 kHz stereo) + jingle at its slot (fade
        //    out) + ducked background loop.
        var narration16 = MemoryMarshal.Cast<byte, short>(CollectionsMarshal.AsSpan(narration));
        int narrationFrames24 = narration16.Length;
        long totalFrames48 = (long)narrationFrames24 * 2;
        var jingleStartFrame = jingleStartSec >= 0 ? (long)(jingleStartSec * outRate) : -1L;
        var bodyStartFrame = jingleStartFrame >= 0 ? jingleStartFrame + jingle.Length / 2 : 0L;   // the bed starts with the body
        var fadeOutSamples = (int)(outRate * 0.6);
        var bgFadeIn = (int)(outRate * 0.5);
        var bgFadeOut = (int)(outRate * 1.0);
        const float voiceThreshold = 0.008f;                  // what counts as "speaking"

        using (var fs = File.Create(wavPath))
        using (var bw = new BinaryWriter(fs))
        {
            WriteWavHeader(bw, totalFrames48, outRate);

            var block = new short[outRate * 2];
            var bgGain = BackgroundWhileSpeaking;             // smoothed duck level
            for (long f = 0; f < totalFrames48;)
            {
                var n = (int)Math.Min(outRate, totalFrames48 - f);
                for (int k = 0; k < n; k++)
                {
                    var idx = f + k;
                    var nIdx = (int)(idx >> 1);
                    if (nIdx >= narration16.Length) break;
                    var voice = narration16[nIdx] / 32768f;

                    // Ducking: the bed exists only under the body — the intro and the jingle
                    // are clean. Under the voice it drops to "barely perceptible", rising in
                    // the pauses.
                    var g = 0f;
                    if (idx >= bodyStartFrame)
                    {
                        var target = Math.Abs(voice) > voiceThreshold ? BackgroundWhileSpeaking : BackgroundInPause;
                        bgGain += (target - bgGain) * 0.01f;  // smooth attack/release
                        g = bgGain;
                        var t = idx - bodyStartFrame;
                        if (t < bgFadeIn) g *= t / (float)bgFadeIn;
                        if (idx > totalFrames48 - bgFadeOut) g *= (totalFrames48 - idx) / (float)bgFadeOut;
                    }

                    var bf = (int)(idx % (background.Length / 2));
                    float bL = background[bf * 2] / 32768f * g;
                    float bR = background[bf * 2 + 1] / 32768f * g;

                    float jL = 0f, jR = 0f;
                    if (jingleStartFrame >= 0)
                    {
                        var jf = idx - jingleStartFrame;
                        if (jf >= 0 && jf < jingle.Length / 2)
                        {
                            var jg = 1f;
                            if (jf > jingle.Length / 2 - fadeOutSamples)
                                jg = (jingle.Length / 2 - jf) / (float)fadeOutSamples;
                            jL = jingle[(int)jf * 2] / 32768f * jg;
                            jR = jingle[(int)jf * 2 + 1] / 32768f * jg;
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
            bw.Flush();
        }   // WAV closed here — the MP3 encoder can now read it

        // 3) MP3: encode the mixed WAV with the pure-managed GroovyMp3 (LAME) encoder, then
        //    drop the bulky WAV intermediate (the MP3 is the deliverable).
        if (!EncodeMp3(wavPath, mp3Path)) return false;
        try { File.Delete(wavPath); } catch { }
        return true;
    }

    /// <summary>Synthesizes a text chunk to int16 PCM (never null — empty for unreadable text).</summary>
    private static byte[] Speak(string text, string lang)
    {
        var speakable = VoiceConversation.ToSpeakable(text);
        if (speakable.Length == 0) return Array.Empty<byte>();
        var pcm = new List<byte>();
        foreach (var sentence in VoiceConversation.SplitSentences(speakable))
        {
            var bytes = PodcastTts.SynthesizePcm(sentence, lang);
            if (bytes != null && bytes.Length > 0) pcm.AddRange(bytes);
        }
        return pcm.ToArray();
    }

    /// <summary>Encodes a 48 kHz stereo 16-bit WAV to MP3 with GroovyMp3 (pure managed LAME
    /// port — cross-platform, no natives), feeding the PCM in chunks and flushing at the end.</summary>
    private static bool EncodeMp3(string wavPath, string mp3Path)
    {
        try
        {
            var pcm = File.ReadAllBytes(wavPath);
            if (pcm.Length <= 44) return Fail("the mixed WAV is empty.");

            var encoder = new Mp3Encoder();
            var format = new AudioFormat
            {
                SampleRate = 48000,
                Channels = 2,
                BitsPerSample = 16,
                BigEndian = false,
                IsFloatingPoint = false,
                BlockAlign = 4,
                AverageBytesPerSecond = 48000 * 2 * 2,
            };
            encoder.SetFormat(format, format);

            using var outFile = File.Create(mp3Path);
            var chunkSize = Math.Min(encoder.InputBufferSize > 0 ? encoder.InputBufferSize : 46080, 46080);
            var inBuf = new byte[chunkSize];
            var outBuf = new byte[encoder.OutputBufferSize > 0 ? encoder.OutputBufferSize : chunkSize + 4096];

            int offset = 44;   // skip the WAV header
            while (offset < pcm.Length)
            {
                var take = Math.Min(chunkSize, pcm.Length - offset);
                Buffer.BlockCopy(pcm, offset, inBuf, 0, take);
                var encoded = encoder.EncodeBuffer(inBuf, 0, take, outBuf);
                if (encoded > 0) outFile.Write(outBuf, 0, encoded);
                offset += take;
            }
            var tail = encoder.EncodeFinish(outBuf);
            if (tail > 0) outFile.Write(outBuf, 0, tail);
            encoder.Close();

            var size = new FileInfo(mp3Path).Length;
            if (size < 1024) return Fail($"the MP3 encode produced no audio ({size} bytes).");
            Log.LogStep($"PodcastMixer: MP3 encoded — {size / 1024d / 1024d:F1} MB");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"the MP3 encode failed: {ex.Message}");
        }
    }

    private static bool Fail(string reason)
    {
        LastError = reason;
        return false;
    }

    /// <summary>Decodes the jingle and the background loop to 48 kHz stereo int16 with
    /// OwnAudioSharp (jingle: first 12 seconds — a full CC0 track is longer than a sigla).</summary>
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

    /// <summary>Locates a music asset: the host assets/ folder (PluginUpdater merge target),
    /// the plugin's own assets/audio/ folder, or — last resort — the embedded resource copy
    /// (the dev ship target excludes assets/, so the embedded copy is the guarantee).</summary>
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
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (Directory.Exists(toolsDir))
        {
            foreach (var dir in Directory.GetDirectories(toolsDir, "*", SearchOption.AllDirectories))
            {
                var candidate = Path.Combine(dir, "assets", "audio", fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        try
        {
            var asm = typeof(PodcastMixer).Assembly;
            var logical = "PodcastTool.assets.audio." + fileName;
            using var stream = asm.GetManifestResourceStream(logical);
            if (stream == null) return null;
            var dir = Path.Combine(Path.GetTempPath(), "podcasttool-assets");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, fileName);
            if (!File.Exists(target))
            {
                using var dst = File.Create(target);
                stream.CopyTo(dst);
            }
            return target;
        }
        catch
        {
            return null;
        }
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
            // Probe both the runtimes/{rid}/native layout AND the flat plugin root (some
            // packages drop the native flat into the output).
            var candidates = new[]
            {
                Path.Combine(dir, "runtimes", rid, "native", nativeName),
                Path.Combine(dir, nativeName),
            };
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    File.Copy(candidate, Path.Combine(appBase, nativeName), overwrite: true);
                    return;
                }
                catch { }
            }
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
