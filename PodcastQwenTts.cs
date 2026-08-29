using System.Diagnostics;
using System.Reflection;
using ElBruno.QwenTTS.Pipeline;

namespace AIOrchestrator.API;

/// <summary>
/// Internal: alternative plugin-local TTS engine based on Qwen3-TTS (ElBruno.QwenTTS) —
/// used when the preferred TTS engine is "qwen". The model (~5.5 GB, 0.6B) downloads
/// automatically on first use into the shared cache (%LOCALAPPDATA%/ElBruno/QwenTTS).
/// Output format matches Kokoro: 24 kHz mono int16 PCM, so the mixer pipeline is unchanged.
/// Language: the official Qwen3-TTS supports 10 languages including Italian; the wrapper's
/// catalog is smaller — the explicit language name is used when the catalog lists it,
/// otherwise "auto" lets the model detect the language itself (elbruno/ElBruno.QwenTTS#72
/// tracks adding Italian to the catalog; the resolution switches automatically).
/// </summary>
internal static class PodcastQwenTts
{
    private static readonly object Sync = new();
    private static TtsPipeline? _pipeline;
    private static string? _unavailableReason;
    private static string _speaker = "ryan";

    /// <summary>True when the engine could be initialized (model downloaded + pipeline up).</summary>
    internal static bool IsAvailable
    {
        get { EnsureInitialized(); return _pipeline != null; }
    }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    internal static string UnavailableReason
    {
        get { EnsureInitialized(); return _unavailableReason ?? "QwenTTS not initialized"; }
    }

    /// <summary>Preferred preset voice (one of the model's 9 speakers).</summary>
    internal static string Speaker
    {
        get => _speaker;
        set { lock (Sync) _speaker = string.IsNullOrWhiteSpace(value) ? "ryan" : value.Trim(); }
    }

    /// <summary>Synthesizes a text chunk to raw 16-bit PCM (24 kHz mono — same as Kokoro,
    /// so the mixer accepts it unchanged). Null when the engine is unavailable.</summary>
    internal static byte[]? SynthesizePcm(string text, string? lang)
    {
        EnsureInitialized();
        if (_pipeline == null) return null;
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();
        try
        {
            var result = _pipeline.SynthesizeToPcmAsync(text, Speaker, ResolveLanguage(lang)).GetAwaiter().GetResult();
            var samples = result.Samples.Span;
            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var s = (short)Math.Clamp((int)(samples[i] * 32768f), short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }
        catch (Exception ex)
        {
            _unavailableReason = $"QwenTTS synthesis failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>The model supports 10 languages (incl. Italian) but the wrapper catalog lists
    /// fewer: use the explicit English language name when the catalog has it, otherwise "auto"
    /// (the model detects the language from the text). When Italian is added to the catalog
    /// (elbruno/ElBruno.QwenTTS#72) this switches from "auto" to "italian" automatically.</summary>
    private static string ResolveLanguage(string? lang)
    {
        var name = LangName(lang);
        return QwenLanguageCatalog.SupportedLanguages.Contains(name) ? name : "auto";
    }

    private static string LangName(string? lang) => lang?.ToLowerInvariant() switch
    {
        "it" => "italian",
        "en" => "english",
        "es" => "spanish",
        "fr" => "french",
        "de" => "german",
        "pt" => "portuguese",
        "ja" => "japanese",
        "zh" => "chinese",
        "ko" => "korean",
        "ru" => "russian",
        _ => "auto",
    };

    private static void EnsureInitialized()
    {
        if (_pipeline != null || _unavailableReason != null) return;
        lock (Sync)
        {
            if (_pipeline != null || _unavailableReason != null) return;
            // Release gate (plugin-local copy of the shared TtsEngineSupport — the packed
            // plugin compiles against the published Graphene.AIOrchestrator): Qwen3-TTS is
            // ~0.1x realtime on CPU, so in release builds it is offered only on NVIDIA GPUs
            // with ≥16 GB VRAM; debug builds (the test harness) always allow it. Gating here
            // also avoids triggering the ~5.5 GB model download on unsupported machines.
            if (!QwenTtsSupported(out var gateReason))
            {
                _unavailableReason = gateReason;
                return;
            }
            try
            {
                _pipeline = TtsPipeline.CreateAsync(modelDir: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _unavailableReason = $"QwenTTS initialization failed: {ex.Message}";
            }
        }
    }

    /// <summary>True when the RUNNING app (entry assembly) is a Debug build — the gate follows
    /// the launched app, not the plugin's own configuration.</summary>
    private static bool IsDebugBuild =>
        string.Equals(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>Total VRAM of the primary NVIDIA GPU in bytes (0 = undetectable).</summary>
    private static long NvidiaVramBytes
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=memory.total --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return 0;
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return 0; }
                var line = p.StandardOutput.ReadLine()?.Trim();
                return long.TryParse(line, out var mb) && mb > 0 ? mb * 1024 * 1024 : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>Release builds require an NVIDIA GPU with ≥16 GB VRAM for Qwen3-TTS (the engine
    /// is ~0.1x realtime on CPU); debug builds allow it for testing.</summary>
    private static bool QwenTtsSupported(out string reason)
    {
        if (IsDebugBuild)
        {
            reason = "Debug build — Qwen3-TTS allowed for testing";
            return true;
        }
        var vramGb = NvidiaVramBytes / 1024d / 1024d / 1024d;
        if (vramGb <= 0)
        {
            reason = "Qwen3-TTS needs an NVIDIA GPU with ≥16 GB VRAM (none detected)";
            return false;
        }
        if (vramGb < 16)
        {
            reason = $"Qwen3-TTS needs an NVIDIA GPU with ≥16 GB VRAM (this GPU has {vramGb:0.#} GB)";
            return false;
        }
        reason = "NVIDIA GPU with ≥16 GB VRAM detected";
        return true;
    }
}
