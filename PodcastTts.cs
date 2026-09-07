using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

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
        get
        {
            EnsureInitialized(); return _synth != null;
        }
    }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    internal static string UnavailableReason
    {
        get
        {
            EnsureInitialized(); return _unavailableReason ?? "TTS not initialized";
        }
    }

    /// <summary>Sample rate of the synthesized 16-bit mono PCM (24 kHz).</summary>
    internal static int SampleRate => KokoroPlayback.waveFormat.SampleRate;

    /// <summary>Synthesizes a sentence to raw 16-bit PCM (24 kHz mono). Null when the engine
    /// is unavailable; empty for empty text.</summary>
    internal static byte[]? SynthesizePcm(string text, string? lang)
    {
        EnsureInitialized();
        if (_synth == null) return null;
        text = NormalizeForTts(text, lang);
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();

        var voice = ResolveVoice(lang);
        if (voice == null) return null;

        lock (Sync)
        {
            // First-segment budget = the model's full 510-token context: a whole sentence
            // (up to ~470 chars) stays ONE segment — the default 200-token budget cut our
            // ~200-char chunks mid-clause (22% of a typical episode).
            return _synth.Synthesize(text, voice, PipelineConfig);
        }
    }

    /// <summary>Shared pipeline config: first segment up to the model's full context, so the
    /// internal segmentation never cuts a sentence-sized chunk (its cut preference allows
    /// commas/spaces as fallbacks — with our chunks within budget it never fires).</summary>
    private static readonly KokoroTTSPipelineConfig PipelineConfig =
        new(new DefaultSegmentationConfig { MaxFirstSegmentLength = 510 });

    /// <summary>Plugin-local TTS normalization (the shared VoiceConversation.NormalizeForTts
    /// replaces it once Graphene.AIOrchestrator ≥ 1.26.08.29 ships — the packed plugin must
    /// compile against the current package): canonical apostrophes → U+0027 (verified: the
    /// typographic ’ breaks the Italian elision), double quote dropped, space after sentence
    /// punctuation, the Italian elided article "l'" merged with the following word (the Kokoro
    /// espeak-replay reads it as the letter name "elle" before guessed words), and the shared
    /// Phoneme Literals dictionary ("[word](/ipa/)" — KokoroSharp supports it) for English
    /// loanwords ("AI" → /eɪ aɪ/, "source" → /sɔːs/, ...). The language is a parameter here, so
    /// no detection is ever needed in the podcast path.</summary>
    private static string NormalizeForTts(string text, string? lang)
    {
        if (text.IndexOfAny(['\u2019', '\u2018', '\u02BC', '\u2032', '\u2033', '\uFF07']) >= 0)
        {
            text = string.Create(text.Length, text, static (span, src) =>
            {
                for (int i = 0; i < src.Length; i++)
                    span[i] = src[i] is '\u2019' or '\u2018' or '\u02BC' or '\u2032' or '\u2033' or '\uFF07' ? '\'' : src[i];
            });
        }
        // The double quote (the LLM's only quoting mark in practice) dropped; the apostrophe
        // stays (Italian elisions depend on it).
        text = text.Replace("\"", "");
        text = Regex.Replace(text, @"([.!?;])(?=[^\d\s])", "$1 ");
        // Italian elided article "l'" + vowel: merge reproduces the elision ("l'allarme" →
        // "lallarme"); the replay reads the standalone article as the letter name "elle".
        if (string.Equals(lang, "it", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(text, @"(?i:\bl')(?=[a-zàèéìòóù])", "l");
        // Phoneme Literals dictionary (KokoroSharp supports the [word](/ipa/) syntax).
        text = ApplyPhonemeHints(text);
        return text;
    }

    /// <summary>Applies the plugin's embedded Phoneme Literals dictionary (same content as
    /// AIOrchestrator's — the packed plugin cannot read the host's embedded copy). Matching is
    /// case-insensitive ("Source" matches "source") except for all-caps entries ("AI" only).</summary>
    private static string ApplyPhonemeHints(string text)
    {
        var entries = PhonemeEntries.Value;
        if (entries.Count == 0) return text;
        foreach (var (word, phonemes, regex, caseSensitive) in entries)
        {
            if (text.IndexOf(word, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            text = regex.Replace(text, $"[{word}](/{phonemes}/)");
        }
        return text;
    }

    private static readonly Lazy<List<(string Word, string Phonemes, Regex Regex, bool CaseSensitive)>> PhonemeEntries = new(LoadPhonemeEntries);

    private static List<(string Word, string Phonemes, Regex Regex, bool CaseSensitive)> LoadPhonemeEntries()
    {
        var result = new List<(string, string, Regex, bool)>();
        try
        {
            using var stream = typeof(PodcastTts).Assembly.GetManifestResourceStream("PodcastTool.phonemes.txt");
            if (stream == null) return result;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var word = line[..tab].Trim();
                var phonemes = line[(tab + 1)..].Trim();
                if (word.Length == 0 || phonemes.Length == 0) continue;
                var caseSensitive = word == word.ToUpperInvariant() && word != word.ToLowerInvariant();
                result.Add((word, phonemes,
                    new Regex($@"\b{Regex.Escape(word)}\b",
                        caseSensitive ? RegexOptions.CultureInvariant
                                      : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)),
                    caseSensitive));
            }
        }
        catch { /* no dictionary — the TTS falls back to the engine's own pronunciation */ }
        return result;
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
                _synth = CreateSynthesizer(modelPath);
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

    /// <summary>Creates the synthesizer with CUDA acceleration when the GPU provider is
    /// available, falling back to the CPU provider otherwise — the plugin must work on
    /// machines without a CUDA GPU. The CUDA toolkit is enabled AUTOMATICALLY when installed
    /// (no manual PATH setup — <see cref="TryEnableCudaToolkit"/>); the GPU requires the GPU
    /// ONNX Runtime build in the HOST (KokoroSharp.GPU.Windows) — with the CPU runtime the CUDA
    /// EP is not present and the fallback logs the reason.</summary>
    private static KokoroWavSynthesizer CreateSynthesizer(string modelPath)
    {
        TryEnableCudaToolkit();
        try
        {
            var gpu = new SessionOptions();
            gpu.AppendExecutionProvider_CUDA();
            var synth = new KokoroWavSynthesizer(modelPath, gpu);
            Log.LogStep("PodcastTts: CUDA execution provider active");
            return synth;
        }
        catch (Exception ex)
        {
            // CUDA provider missing or CUDA toolkit absent → CPU (the library defaults).
            Log.LogStep($"PodcastTts: CUDA unavailable ({ex.Message}) — CPU fallback");
            return new KokoroWavSynthesizer(modelPath);
        }
    }

    /// <summary>Windows: probes the installed CUDA toolkit + cuDNN directories and prepends
    /// them to the process PATH (the CUDA EP locates the runtime libs via the PATH; the
    /// toolkit installer never adds the cuDNN bin — the classic "cublasLt64_12.dll missing").
    /// A safe no-op when CUDA is absent.</summary>
    private static void TryEnableCudaToolkit()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            var cudnnRoot = Path.Combine(programFiles, "NVIDIA", "CUDNN");
            string? cudaBin = null, cudnnBin = null;
            if (Directory.Exists(cudaRoot))
                cudaBin = Directory.GetDirectories(cudaRoot)
                    .SelectMany(d => Directory.GetDirectories(d, "bin")).FirstOrDefault();
            if (Directory.Exists(cudnnRoot))
                cudnnBin = Directory.GetDirectories(cudnnRoot)
                    .SelectMany(d => Directory.GetDirectories(Path.Combine(d, "bin"))).FirstOrDefault();
            if (cudaBin == null) return;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (path.Contains(cudaBin, StringComparison.OrdinalIgnoreCase)) return;
            var add = cudnnBin != null ? cudaBin + ";" + cudnnBin : cudaBin;
            Environment.SetEnvironmentVariable("PATH", add + ";" + path);
        }
        catch { }
    }

    /// <summary>Picks the best loaded voice for a language (two-letter ISO), falling back to
    /// the first loaded voice; null when no voice is available.</summary>
    private static KokoroVoice? ResolveVoice(string? lang)
    {
        var target = lang ?? MachineLang();
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

    /// <summary>Plugin-local mirror of VoiceConversation.ResolveLang(null): the machine's UI
    /// language (never a hardcoded default) — keeps the voice fallback host-independent.</summary>
    private static string MachineLang()
    {
        try
        {
            var sys = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            if (sys.Length == 2 && char.IsLetter(sys[0]) && char.IsLetter(sys[1]))
                return sys.ToLowerInvariant();
        }
        catch { }
        return "en";
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

    /// <summary>Splits a text chunk into speakable sentences for the mixer. The speakable
    /// conversion and the split are plugin-LOCAL mirrors of the host's VoiceConversation
    /// helpers (frozen at the 2026-08-29 behavior): the audio path must not bind the host
    /// copies — their signatures drifted across Graphene.AIOrchestrator releases
    /// (SplitSentences gained a hardCeiling parameter) and a plugin built against an older
    /// signature died at mix time with "Attempted to access a missing method".</summary>
    internal static IEnumerable<string> SplitSpeakable(string text)
    {
        var speakable = ToSpeakable(text);
        if (speakable.Length == 0) return Array.Empty<string>();
        return SplitSentences(speakable);
    }

    /// <summary>Plugin-local mirror of VoiceConversation.ToSpeakable (markdown stripped,
    /// emoji/symbols removed) — see <see cref="SplitSpeakable"/>.</summary>
    internal static string ToSpeakable(string text) => FilterSpeakableChars(StripMarkdown(text));

    private static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "$1");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"~~(.+?)~~", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[\-\*\+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\d+\.\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[\-\*\s_]{3,}$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\|-+\|", "");
        text = Regex.Replace(text, @"(?<![.?!])\n", ", ");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = FilterSpeakableChars(text);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string FilterSpeakableChars(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.MathSymbol)
            {
                result.Append(text[i]);
            }
            else if (cat == UnicodeCategory.Surrogate)
            {
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>Splits speakable text into sentence chunks (after . ! ? only, never mid-word);
    /// a sentence over <paramref name="maxChunk"/> chars is kept WHOLE up to the
    /// <paramref name="hardCeiling"/> — mirror of VoiceConversation.SplitSentences.</summary>
    internal static IEnumerable<string> SplitSentences(string text, int maxChunk = 200, int hardCeiling = 450)
    {
        var buf = new StringBuilder();
        foreach (var part in Regex.Split(text, @"(?<=[.!?])(?!\d)\s*"))
        {
            var s = part.Trim();
            if (s.Length == 0) continue;

            if (s.Length > maxChunk)
            {
                if (buf.Length > 0) { yield return buf.ToString().Trim(); buf.Clear(); }
                if (s.Length > hardCeiling)
                {
                    var cut = s.LastIndexOf(' ', hardCeiling);
                    var at = cut > hardCeiling / 2 ? cut : hardCeiling;
                    yield return s[..at].Trim();
                    s = s[at..].TrimStart();
                }
                if (s.Length > 0) buf.Append(s);
                continue;
            }

            if (buf.Length > 0 && buf.Length + s.Length + 1 > maxChunk)
            {
                yield return buf.ToString().Trim();
                buf.Clear();
            }
            if (buf.Length > 0) buf.Append(' ');
            buf.Append(s);
        }
        if (buf.Length > 0) yield return buf.ToString().Trim();
    }
}
