using System.Text;

namespace AIOrchestrator.API;

/// <summary>Internal: writes the podcast body act by act from the plan's outline (length
/// predictable, arc coherent) with a bounded per-act enrichment fallback, then the separate
/// welcome intro that announces the themes the body ACTUALLY covers. The text follows the
/// narrative style spec — continuous prose, no bullet lists, only the [markers] that drive the
/// TTS and the mixing; emoji are removed deterministically.</summary>
internal static class PodcastScript
{
    /// <summary>The full narrative style spec given to the model on every act.</summary>
    private const string StyleSpec = """
        Write the script for a podcast episode of about thirty minutes, meant to be read by a text-to-speech voice.
        The style must be narrative and engaging, structured as a dramatic arc — never as a list of facts.
        - Start with a strong hook: a question, a mystery, or an image that grabs attention immediately.
        - Develop the story in layers, alternating tension and lighter pauses, revealing information progressively.
        - Use short, direct sentences: one idea per sentence.
        - Use punctuation intentionally: periods build anticipation, commas give breath, question and exclamation marks guide the TTS intonation.
        - Make irony and self-irony explicit with phrases like "as absurd as it sounds" or "picture the scene".
        - Write rhetorical questions as real questions, ending with a question mark.
        - Alternate dense factual sections with reflective, descriptive ones to create rhythm.
        - Insert musical or silence cues like [pausa musicale] or [breve silenzio] every 150-200
          characters of speech (about every 10-12 seconds) to break the artificial voice flow —
          the cues are a REQUIREMENT, place them densely and regularly.
        - Repeat key concepts with slightly different words a few minutes apart to emphasize them.
        - Speak as if to a single person: conversational and intimate, without imitating human improvisation.
        - Every sentence must be clear and self-contained: a synthetic voice cannot fix ambiguity with intonation.
        - NO bullet lists, NO numbered lists, NO markdown headings, NO bold, NO emoji, NO links: pure continuous prose with the [markers] only.
        """;

    /// <summary>Writes the body act by act (the act markers are added deterministically by the
    /// caller). Returns null when the first act fails.</summary>
    internal static string? Generate(string topic, string lang, string brief, PodcastPlan plan, int targetChars)
    {
        var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
        var langName = LangName(lang);
        var sb = new StringBuilder();
        string[] markers = { "[PRIMO ATTO]", "[SECONDO ATTO]", "[TERZO ATTO]" };

        for (int i = 0; i < plan.Acts.Length; i++)
        {
            var act = plan.Acts[i];
            var seam = sb.Length > 0 ? sb.ToString()[^Math.Min(300, sb.Length)..] : "";
            var beats = act.Beats.Length > 0 ? string.Join("; ", act.Beats) : "(follow the act's narrative arc)";
            var (text, error) = llm.SendQuery(
                $"""
                Today's date is {DateTime.UtcNow:yyyy-MM-dd} (UTC) — use it as the temporal
                reference for any event mentioned (an event "on 28 August" is today, a past or
                upcoming date is anchored to this day).

                {StyleSpec}

                Episode topic: {topic}.
                You are writing ACT {act.Act} of the episode, in {langName}.
                Act title: {act.Title}.
                Story beats for this act: {beats}.
                Target length for this act: at most {act.TargetChars} characters — aim slightly
                below the target.
                {(i == plan.Acts.Length - 1 ? "This is the final act: write concisely — DON'T BE VERBOSE." : "")}
                {seamInstruction(seam)}
                Write only this act. Do not add act markers, do not write the intro or the outro.

                Research brief for the whole episode (use its facts woven into the narrative):
                {brief}
                """,
                useHistory: false, role: LLMUtility.SystemRole.None,
                // Generous safety cap only: episodes are NEVER cut for length, so the cap must
                // not be tight enough to sever a full act mid-narrative (the provider's strict
                // enforcement proved unreliable anyway).
                maxToken: 9000,
                temperature: 0.8);

            if (error != null || string.IsNullOrWhiteSpace(text))
            {
                Log.LogStep($"PodcastScript: act {act.Act} failed — {(error is int h ? $"HTTP {h}" : "empty response")}");
                return null;
            }
            text = TrimToSentenceEnd(text.Trim());
            if (i > 0) sb.Append('\n').Append(markers[i]).Append('\n');
            else sb.Append(markers[i]).Append('\n');
            sb.Append(text);
            Log.LogStep($"PodcastScript: act {act.Act} '{act.Title}' → {text.Length:N0} chars (target {act.TargetChars:N0})");
        }

        // Episodes are NEVER cut (the operating rule): the length follows the writer — the
        // "at most N characters" + "don't be verbose" instructions prevent verbosity at the
        // source. No deterministic trim, no outro repair: the narrative always stays whole.
        var body = FilterEmoji(sb.ToString()).Trim();
        if (body.Length == 0) return null;

        // Bounded enrichment: acts far below their target are expanded inline (narrative
        // considerations woven in, same style), at most two rounds over the body.
        for (int round = 0; round < 2; round++)
        {
            var expanded = false;
            for (int i = 0; i < plan.Acts.Length; i++)
            {
                var act = plan.Acts[i];
                if (act.TargetChars <= 0 || MeasureAct(body, i) >= act.TargetChars * 0.8) continue;
                var (enriched, err) = llm.SendQuery(
                    $"""
                    {StyleSpec}

                    The {Ordinal(act.Act)} act of the podcast episode about "{topic}" (in {langName}) is
                    shorter than its target ({MeasureAct(body, i)} vs ~{act.TargetChars} characters).
                    Expand it INLINE: add narrative considerations, anecdotes, context and reflection that
                    deepen the existing story beats — keep the same style, do not repeat sentences, do not
                    add act markers, do not write the intro or outro. The act must end up AT MOST about
                    {act.TargetChars} characters — do not overshoot.
                    Write the full expanded act.

                    Current act text:
                    {ExtractAct(body, i)}
                    """,
                    useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 9000, temperature: 0.8);
                if (err == null && !string.IsNullOrWhiteSpace(enriched))
                {
                    var before = MeasureAct(body, i);
                    body = ReplaceAct(body, i, StripActMarkers(enriched.Trim()));
                    Log.LogStep($"PodcastScript: act {act.Act} enriched {before:N0} → {MeasureAct(body, i):N0} chars");
                    expanded = true;
                }
            }
            if (!expanded) break;
        }

        body = FilterEmoji(body).Trim();
        Log.LogStep($"PodcastScript: body → {body.Length:N0} chars (target {targetChars:N0}); enrichment rounds logged per act");
        return body.Length > 0 ? body : null;
    }

    /// <summary>Writes the short welcome intro announcing the themes the body actually covers
    /// (given the body's opening) — generated AFTER the body, in the episode language.</summary>
    internal static string GenerateIntro(string topic, string lang, string? podcastName, string body)
    {
        var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
        var welcome = string.IsNullOrWhiteSpace(podcastName)
            ? "Welcome the listeners briefly."
            : $"Welcome the listeners of the podcast named \"{podcastName}\" by name.";
        var opening = body.Length > 600 ? body[..600] : body;
        var (text, error) = llm.SendQuery(
            $"""
            Today's date is {DateTime.UtcNow:yyyy-MM-dd} (UTC) — use it as the temporal reference.

            Write a SHORT welcome intro (3 to 5 sentences, at most 700 characters) for a podcast
            episode in {LangName(lang)}. {welcome} Announce the themes that the episode covers.
            The episode opens like this: "{opening}"
            Plain flowing prose, no emoji, no markers, no bullets.
            """,
            useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 800, temperature: 0.7);
        if (error == null && !string.IsNullOrWhiteSpace(text))
        {
            var intro = FilterEmoji(text).Trim();
            if (intro.Length > 0)
            {
                Log.LogStep($"PodcastScript: intro → {intro.Length:N0} chars");
                return intro;
            }
        }
        // Language-aware fallback (the model call failed — never leave an English podcast with
        // an Italian welcome).
        return lang == "it"
            ? $"Benvenuti. Oggi parliamo di {topic}."
            : $"Welcome. Today we talk about {topic}.";
    }

    /// <summary>Removes emoji and symbols deterministically (no LLM) while keeping the ASCII
    /// [markers] and the punctuation the TTS needs. Same filter as the voice pipeline.</summary>
    internal static string FilterEmoji(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var cat = char.GetUnicodeCategory(text, i);
            if (cat is System.Globalization.UnicodeCategory.UppercaseLetter
                or System.Globalization.UnicodeCategory.LowercaseLetter
                or System.Globalization.UnicodeCategory.TitlecaseLetter
                or System.Globalization.UnicodeCategory.ModifierLetter
                or System.Globalization.UnicodeCategory.OtherLetter
                or System.Globalization.UnicodeCategory.DecimalDigitNumber
                or System.Globalization.UnicodeCategory.LetterNumber
                or System.Globalization.UnicodeCategory.OtherNumber
                or System.Globalization.UnicodeCategory.SpaceSeparator
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.DashPunctuation
                or System.Globalization.UnicodeCategory.OpenPunctuation
                or System.Globalization.UnicodeCategory.ClosePunctuation
                or System.Globalization.UnicodeCategory.InitialQuotePunctuation
                or System.Globalization.UnicodeCategory.FinalQuotePunctuation
                or System.Globalization.UnicodeCategory.OtherPunctuation
                or System.Globalization.UnicodeCategory.CurrencySymbol
                or System.Globalization.UnicodeCategory.MathSymbol)
            {
                result.Append(text[i]);
            }
            else if (cat == System.Globalization.UnicodeCategory.Surrogate)
            {
                i++;   // skip the emoji surrogate pair whole
            }
        }
        return result.ToString();
    }

    /// <summary>The two-letter languages the tool can write and speak.</summary>
    internal static readonly string[] KnownLanguages = { "en", "it", "fr", "es", "de", "pt", "ja", "zh", "ko" };

    /// <summary>Full language names → two-letter code (defensive: some agents send "Spanish"
    /// instead of "es", and a naive 2-char truncation would produce the wrong "sp").</summary>
    private static readonly Dictionary<string, string> LanguageNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["italian"] = "it", ["english"] = "en", ["french"] = "fr", ["spanish"] = "es",
        ["german"] = "de", ["portuguese"] = "pt", ["japanese"] = "ja", ["chinese"] = "zh",
        ["korean"] = "ko",
    };

    /// <summary>Normalizes a language argument to a two-letter code (case-insensitive,
    /// full names accepted); null when not recognized.</summary>
    internal static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (LanguageNameToCode.TryGetValue(v, out var full)) return full;
        var code = v.ToLowerInvariant();
        if (code.Length >= 2 && char.IsLetter(code[0]) && char.IsLetter(code[1]))
        {
            var two = code[..2];
            if (KnownLanguages.Contains(two)) return two;
        }
        return null;
    }

    internal static string LangName(string lang) => lang switch
    {
        "it" => "Italian",
        "en" => "English",
        "fr" => "French",
        "es" => "Spanish",
        "de" => "German",
        "pt" => "Portuguese",
        "ja" => "Japanese",
        "zh" => "Chinese",
        "ko" => "Korean",
        _ => "English",
    };

    // ── act measurement / extraction (the act markers are the deterministic boundaries) ──

    private static readonly string[] ActMarkers = { "[PRIMO ATTO]", "[SECONDO ATTO]", "[TERZO ATTO]" };

    private static string ExtractAct(string body, int actIndex)
    {
        var start = body.IndexOf(ActMarkers[actIndex], StringComparison.Ordinal);
        if (start < 0) return body;
        start += ActMarkers[actIndex].Length;
        var end = actIndex + 1 < ActMarkers.Length
            ? body.IndexOf(ActMarkers[actIndex + 1], StringComparison.Ordinal)
            : body.Length;
        return end < 0 ? body[start..] : body[start..end];
    }

    private static int MeasureAct(string body, int actIndex)
    {
        var act = ExtractAct(body, actIndex);
        // Exclude the markers themselves from the measurement.
        foreach (var m in ActMarkers) act = act.Replace(m, "");
        return act.Length;
    }

    private static string ReplaceAct(string body, int actIndex, string newAct)
    {
        var start = body.IndexOf(ActMarkers[actIndex], StringComparison.Ordinal);
        if (start < 0) return body;
        var end = actIndex + 1 < ActMarkers.Length
            ? body.IndexOf(ActMarkers[actIndex + 1], StringComparison.Ordinal)
            : body.Length;
        if (end < 0) end = body.Length;
        return body[..start] + ActMarkers[actIndex] + "\n" + newAct + body[end..];
    }

    private static string StripActMarkers(string text)
    {
        foreach (var m in ActMarkers) text = text.Replace(m, "");
        return text.Trim();
    }

    /// <summary>When the output hits the token cap mid-flow, cuts at the last sentence
    /// boundary so the act ends cleanly (the next act continues from the seam).</summary>
    private static string TrimToSentenceEnd(string text)
    {
        if (text.Length == 0) return text;
        var last = text[^1];
        if (last is '.' or '!' or '?' or '…' or '"' or '”' or '»') return text;
        var cut = text.LastIndexOfAny(['.', '!', '?']);
        return cut >= text.Length / 2 ? text[..(cut + 1)] : text;
    }

    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => n + "th" };

    private static string seamInstruction(string seam) =>
        seam.Length == 0
            ? "This is the opening act: start with the strong hook."
            : $"Continue seamlessly from where the previous act ended (its last words: \"{seam}\") — the transition must feel like one continuous story.";
}
