using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

namespace AIOrchestrator.API;

/// <summary>
/// Internal: plugin-local Kokoro TTS engine (lazy, never throws) used while the published
/// Graphene.AIOrchestrator package does not yet expose the shared AIOrchestrator.KokoroTts
/// (the central engine landed on master 2026-08-28 after the same-day package was cut; once
/// Graphene.AIOrchestrator ≥ 1.26.08.29 is published, PodcastMixer can switch back to the
/// shared engine and this class is deleted). The asset discovery mirrors the shared engine:
/// kokoro.onnx + voices/ are resolved from the HOST app base (AgentBridge ships them), so the
/// plugin carries no TTS payload beyond the managed KokoroSharp dlls.
/// </summary>
internal static class PodcastTts
{
    private static readonly object Sync = new();
    private static KokoroWavSynthesizer? _synth;
    private static List<string> _voices = new();
    private static string? _unavailableReason;

    /// <summary>True when the engine could be initialized (model + voices present).</summary>
    internal static bool IsAvailable
    {
        get { EnsureInitialized(); return _synth != null; }
    }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    internal static string UnavailableReason
    {
        get { EnsureInitialized(); return _unavailableReason ?? "TTS not initialized"; }
    }

    /// <summary>Sample rate of the synthesized 16-bit mono PCM.</summary>
    internal static int SampleRate => KokoroPlayback.waveFormat.SampleRate;

    /// <summary>Synthesizes a sentence to raw 16-bit PCM (24 kHz mono). Null when the engine
    /// is unavailable; empty for empty text.</summary>
    internal static byte[]? SynthesizePcm(string text, string? lang)
    {
        EnsureInitialized();
        if (_synth == null) return null;
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();

        var voice = ResolveVoice(lang);
        if (voice == null) return null;

        lock (Sync)
        {
            return _synth.Synthesize(text, voice);
        }
    }

    private static void EnsureInitialized()
    {
        if (_synth != null || _unavailableReason != null) return;
        lock (Sync)
        {
            if (_synth != null || _unavailableReason != null) return;

            var modelPath = FindModel();
            var voicesDir = FindVoicesDir();
            if (modelPath == null)
            {
                _unavailableReason = "kokoro.onnx not found. The host must provision it (AgentBridge DownloadKokoroModel) to enable TTS.";
                return;
            }
            if (voicesDir == null)
            {
                _unavailableReason = "voices/ directory not found. TTS unavailable.";
                return;
            }

            try
            {
                KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                _voices = KokoroVoiceManager.Voices
                    .Select(v => v.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _synth = new KokoroWavSynthesizer(modelPath);
                if (_voices.Count == 0)
                {
                    _unavailableReason = "voices/ contains no Kokoro voices.";
                    _synth.Dispose();
                    _synth = null;
                    return;
                }
                // Warm the first inference so the first real sentence does not pay the ONNX
                // session/JIT + phonemizer warm-up.
                try
                {
                    _synth.Synthesize("Questa è una breve prova di sintesi vocale.", KokoroVoiceManager.GetVoice(_voices[0]));
                }
                catch { }
            }
            catch (Exception ex)
            {
                _unavailableReason = $"TTS initialization failed: {ex.Message}";
                _synth?.Dispose();
                _synth = null;
            }
        }
    }

    /// <summary>Picks the best loaded voice for a language (two-letter ISO), falling back to
    /// the first loaded voice; null when no voice is available.</summary>
    private static KokoroVoice? ResolveVoice(string? lang)
    {
        var target = lang ?? VoiceConversation.ResolveLang(null);
        var prefix = target switch
        {
            "it" => "if_",
            "en" => "af_",
            "es" => "ef_",
            "fr" => "ff_",
            "ja" => "jf_",
            "zh" => "cm_",
            "ko" => "kf_",
            _ => null,
        };
        if (prefix != null)
        {
            var match = _voices.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (match != null) return KokoroVoiceManager.GetVoice(match);
        }
        return _voices.Count > 0 ? KokoroVoiceManager.GetVoice(_voices[0]) : null;
    }

    /// <summary>Locates kokoro.onnx: the app base dir (host bin) or its parent (app root).</summary>
    private static string? FindModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "kokoro.onnx"),
            Path.Combine(Path.GetFullPath(Path.Combine(baseDir, "..")), "kokoro.onnx"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Finds the voices folder next to the engine or in the app base parent.</summary>
    private static string? FindVoicesDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "voices"),
            Path.Combine(Path.GetFullPath(Path.Combine(baseDir, "..", "voices"))),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
