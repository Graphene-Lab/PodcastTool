using System.Text;

namespace AIOrchestrator.API;

/// <summary>Internal: writes the podcast script through the LLM following the narrative style
/// spec, in chunks (providers cap the output length), then applies the deterministic emoji
/// filter. The result is a continuous storytelling text — no bullet lists, no markdown
/// headings, only the [markers] that drive the TTS and the mixing.</summary>
internal static class PodcastScript
{
    /// <summary>The full narrative style spec given to the model on every chunk.</summary>
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
        - Insert musical or silence cues every ten to twelve seconds of speech, like [pausa musicale] or [breve silenzio], to break the artificial voice flow.
        - Repeat key concepts with slightly different words a few minutes apart to emphasize them.
        - Structure the episode in three acts, marking the transitions with [PRIMO ATTO], [SECONDO ATTO], [TERZO ATTO] (these titles are never read aloud).
        - First minutes: the hook and a preview of the theme.
        - First act: expand the problem with an anecdote and a first twist.
        - Second act: deepen with a second revelation and an ironic counterpoint.
        - Third act: build to the climax, then resolve or end on a cliffhanger, and close with a reflection that gives meaning to the journey.
        - Last minutes: the outro — a farewell, a call to action, and a hint at the next episode.
        - Speak as if to a single person: conversational and intimate, without imitating human improvisation.
        - Every sentence must be clear and self-contained: a synthetic voice cannot fix ambiguity with intonation.
        - The script must start with a brief introduction that announces the themes covered, then the marker [SIGLA] (the jingle plays here), then the episode proper.
        - NO bullet lists, NO numbered lists, NO markdown headings, NO bold, NO emoji, NO links: pure continuous prose with the [markers] only.
        """;

    /// <summary>Generates the script in chunks until the target length is reached (or the
    /// model stops producing). Returns null when the first chunk fails.</summary>
    internal static string? Generate(string topic, string lang, string? podcastName, string context, int targetChars = 25000)
    {
        var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
        var welcome = string.IsNullOrWhiteSpace(podcastName)
            ? "Start with a brief welcome to the listeners."
            : $"The introduction must welcome the listeners of the podcast named \"{podcastName}\" by name.";

        var preamble = $"""
            {StyleSpec}

            Episode topic: {topic}
            {welcome}
            Write the script in {LangName(lang)}.
            Target length: about {targetChars} characters in total. Do not write it all at once if it is too long: the continuation will be requested separately.
            Write only the first portion of the script now, ending mid-flow at a natural sentence boundary, so the next portion can continue seamlessly.

            The news context below was gathered for this episode. Use it as factual material; never mention that it is a context or a list — weave it into the narrative.

            {context}
            """;

        var sb = new StringBuilder();
        var (first, firstError) = llm.SendQuery(preamble, useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 9000, temperature: 0.8);
        if (firstError != null || string.IsNullOrWhiteSpace(first))
        {
            Log.LogStep($"PodcastScript: first chunk failed — {(firstError is int h ? $"HTTP {h}" : "empty response")}");
            return null;
        }
        sb.Append(first);

        // Continuation chunks until the target length (3 chunks max keeps the story coherent).
        for (int attempt = 0; attempt < 3 && sb.Length < targetChars; attempt++)
        {
            var tail = sb.ToString()[^Math.Min(400, sb.Length)..];
            var (next, nextError) = llm.SendQuery(
                $"""
                Continue the podcast script exactly where it ended, seamlessly, in the same style and language.
                Target total length: about {targetChars} characters.
                The last portion ended with: "{tail}"
                Write only the next portion, ending mid-flow at a natural sentence boundary.
                """,
                useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 9000, temperature: 0.8);
            if (nextError != null || string.IsNullOrWhiteSpace(next)) break;
            sb.Append('\n').Append(next);
        }

        var script = sb.ToString().Trim();
        if (script.Length == 0) return null;

        // The model occasionally wraps the script in markdown code fences — strip them.
        Utility.RemoveFencesEncapsulationAndFixTrim(ref script);
        return FilterEmoji(script);
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

    private static string LangName(string lang) => lang switch
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
}
