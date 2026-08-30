using System.Text.Json;

namespace AIOrchestrator.API;

/// <summary>
/// Deterministic podcast length model. The episode duration is the ONLY user knob (the tool
/// config, "podcast.json" — "durationMinutes", default 30 = 2 minutes of intro + closing
/// stinger + 28 minutes of acts). Everything else is DERIVED, never left to the LLM:
///   actCount    = round(actsMinutes / standardActMinutes), clamped to [3, 6]
///   actChars    = actsMinutes × charsPerMinute / actCount   (equal acts — the division is
///                 forced to an integer count so no act ends up shorter/longer as a leftover)
///   totalChars  = actCount × actChars                       (≈ durationMinutes of speech)
/// The per-act lengths told to the writer are corrected deterministically at write time
/// (PodcastScript): after each act the cumulative difference vs the cumulative prediction is
/// applied to the NEXT act's target, so the total always converges to totalChars.
/// </summary>
internal static class PodcastLengths
{
    /// <summary>Speech rate used to convert minutes to characters (≈ the observed rate).</summary>
    internal const double CharsPerMinute = 833;

    /// <summary>Intro + closing musical stinger — not part of the acts.</summary>
    internal const double IntroOutroMinutes = 2;

    /// <summary>Standard act length for the default 30-minute episode: 28/3 ≈ 9.333 minutes.</summary>
    private const double StandardActMinutes = (30 - IntroOutroMinutes) / 3.0;

    /// <summary>Minimum/maximum number of acts.</summary>
    internal const int MinActs = 3;
    internal const int MaxActs = 6;

    /// <summary>Computes the deterministic length model for an episode duration in minutes.</summary>
    internal static (int ActCount, int ActChars, int TotalChars) Compute(int durationMinutes)
    {
        var actsMinutes = Math.Max(1, durationMinutes - IntroOutroMinutes);
        var actCount = Math.Clamp((int)Math.Round(actsMinutes / StandardActMinutes), MinActs, MaxActs);
        var actChars = (int)Math.Round(actsMinutes * CharsPerMinute / actCount);
        return (actCount, actChars, actChars * actCount);
    }
}

/// <summary>
/// Tool configuration ("podcast.json" next to the executable, under assets/ — the same
/// host-level assets convention used by the other tools). The plugin NEVER overwrites an
/// existing file: the default is embedded and materialized only when the file is absent.
/// Plugin updates preserve the file (PluginUpdater skips .json files), so the user's
/// settings survive every update. The file is editable by hand or by the agent.
/// </summary>
internal static class PodcastConfig
{
    /// <summary>Embedded default configuration (written only when the file is missing).</summary>
    private const string DefaultJson = """
        {
          "durationMinutes": 30,
          "_comment": "Episode duration in minutes (default 30 = 2 min intro+closing stinger + 28 min of acts). The per-act lengths and the research budget are derived deterministically from this value — changing it needs no code changes.",
          "qwenTts": {
            "_comment": "Qwen3-TTS voice behavior (the 0.6B model has no instruction control, so neutrality comes from the sampling settings). temperature: lower = calmer, more neutral prosody (0.9 = model default; 0.5 = flat newsreader). seed: fixed = deterministic voice (same script → same audio every run); change it for a different but still reproducible prosody; set null for stochastic (the model's default behavior). The 1.7B model additionally applies a neutral 'newsreader' style instruction.",
            "temperature": 0.5,
            "seed": 20260830
          }
        }
        """;

    /// <summary>Configured episode duration in minutes (default 30).</summary>
    internal static int DurationMinutes
    {
        get
        {
            try
            {
                var json = TryLoad();
                if (json != null && json.RootElement.TryGetProperty("durationMinutes", out var d)
                    && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var minutes) && minutes >= 5)
                    return minutes;
            }
            catch { }
            return 30;
        }
    }

    /// <summary>Qwen3-TTS sampling temperature (default 0.5 — a flat, neutral newsreader
    /// prosody; 0.9 is the model default and sounds more emotional/random).</summary>
    internal static float QwenTemperature
    {
        get
        {
            try
            {
                var json = TryLoad();
                if (json != null && json.RootElement.TryGetProperty("qwenTts", out var q)
                    && q.ValueKind == JsonValueKind.Object && q.TryGetProperty("temperature", out var t)
                    && t.ValueKind == JsonValueKind.Number && t.TryGetSingle(out var temperature)
                    && temperature is > 0f and <= 2f)
                    return temperature;
            }
            catch { }
            return 0.5f;
        }
    }

    /// <summary>Qwen3-TTS sampling seed (default 20260830 — deterministic voice: the same
    /// script always produces the same audio). Null (JSON <c>null</c>) keeps the model's
    /// stochastic prosody.</summary>
    internal static int? QwenSeed
    {
        get
        {
            try
            {
                var json = TryLoad();
                if (json != null && json.RootElement.TryGetProperty("qwenTts", out var q)
                    && q.ValueKind == JsonValueKind.Object && q.TryGetProperty("seed", out var s))
                {
                    if (s.ValueKind == JsonValueKind.Null) return null;
                    if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var seed)) return seed;
                }
            }
            catch { }
            return 20260830;
        }
    }

    /// <summary>Loads podcast.json from the host assets dir (first) or the app base; writes the
    /// embedded default when absent. Null when neither the file nor the default is available.</summary>
    private static JsonDocument? TryLoad()
    {
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "assets"), AppContext.BaseDirectory })
        {
            var path = Path.Combine(dir, "podcast.json");
            if (File.Exists(path))
                return JsonDocument.Parse(File.ReadAllText(path));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, DefaultJson);
                return JsonDocument.Parse(DefaultJson);
            }
            catch { }
        }
        return null;
    }
}
