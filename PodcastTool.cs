using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PodcastTool.Harness")]

namespace AIOrchestrator.API;

/// <summary>Create narrated podcast episodes: plan, research, write and mix an audio episode and save it as an MP3 under /podcast/.
/// The tool plans the episode (recent-facts judgment + English research queries + three-act outline), researches the
/// topic from recent or general sources accordingly, compresses the research into a brief, writes the narrative body
/// and a separate welcome intro, then mixes the audio (intro → jingle → body, soft ducked background).
/// Files are saved under /podcast/.</summary>
public class PodcastTool : BaseAgentTool, IFileTool
{
    /// <summary>
    /// Generates a complete podcast episode about <paramref name="topic"/>: a narrative script
    /// of a duration set in the tool config (podcast.json, "durationMinutes", default 30 —
    /// the per-act lengths are derived deterministically from it) and the mixed audio (voice,
    /// jingle, soft background music) as an MP3 file. The episode starts with a short welcome
    /// introduction announcing the covered themes, then the jingle, then the episode proper.
    /// Episodes are kept WHOLE: the per-act targets are corrected deterministically after each
    /// act so the total converges to the configured duration, and the narrative is never cut
    /// to hit an exact duration.
    /// </summary>
    /// <param name="topic">The episode subject. When the subject depends on recent facts or
    /// events, recent news about it is gathered; otherwise general background material is used.</param>
    /// <param name="language">Optional two-letter ISO language for the script and voice
    /// (e.g. "en", "it", "fr", "es"). When omitted, the language of <paramref name="topic"/>
    /// is detected. An invalid value (not a two-letter ISO code) returns an error.</param>
    /// <param name="podcastName">Optional name of the podcast. When provided, the introduction
    /// welcomes the listeners of that podcast by name and the files are saved under
    /// /podcast/&lt;podcastName&gt;/.</param>
    /// <param name="contextualMaterial">If necessary you can pass some text that will be used
    /// as material to create the podcast (it can be provided by the user as a result of a
    /// search), it is an optional parameter. The text joins the gathered research context.</param>
    /// <param name="minimizeAcronyms">When true (default) the writer expands every acronym and
    /// abbreviation to its full form ("Bitcoin" not "BTC") — TTS engines often mispronounce
    /// them. Set false when the script may keep the compact forms.</param>
    /// <param name="durationMinutes">Optional episode length in minutes. When omitted, the tool
    /// config (podcast.json, "durationMinutes", default 30) is used. Pass a small value (5 is
    /// the minimum) for a short episode — without it the default 30-minute episode is produced,
    /// which on a CPU-only machine takes a long time to synthesize. The per-act lengths are
    /// derived from this value exactly as from the config.</param>
    /// <returns>The sandbox-relative paths of the saved MP3 (to attach to the user reply),
    /// script and podcast RSS feed, with their lengths; failures are returned as
    /// "Error: ..." with the cause.</returns>
    public string GeneratePodcast(string topic, string? language = null, string? podcastName = null, string? contextualMaterial = null, bool minimizeAcronyms = true, int? durationMinutes = null)
    {
        var sw = Stopwatch.StartNew();
        Log.LogStep($"PodcastTool: generating podcast about '{topic}' (lang={language ?? "auto"}, name={podcastName ?? "none"})");
        try
        {
            if (string.IsNullOrWhiteSpace(topic))
                return "Error: the topic is empty. Provide a topic to generate the podcast.";
            if (topic.Length > 300)
                topic = topic.Trim()[..300];

            // The episode language: the explicit parameter wins (no detection needed — the
            // lingua detection below is only a fallback and must never be able to fail the
            // episode when the caller already chose the language).
            var lang = string.Empty;
            if (!string.IsNullOrWhiteSpace(language))
            {
                var normalized = PodcastScript.NormalizeLanguage(language);
                if (normalized == null)
                    return $"Error: the 'language' parameter must be a valid two-letter ISO code (e.g. \"en\", \"it\", \"fr\", \"es\") — received \"{language}\", which is not valid. Omit it to use the language of the topic.";
                lang = normalized;
            }
            else
            {
                // Topic-language detection first; when it is unavailable or ambiguous (e.g. the
                // lingua models are missing on an install) fall back to the MACHINE's UI language
                // — never a hardcoded English default — so a podcast on a localized machine is
                // narrated in that language whenever the engine supports it.
                lang = Utility.DetectLanguage(topic) ?? VoiceConversation.ResolveLang(null);
            }

            // 1) Deterministic length model: the duration comes from the tool config
            //    (podcast.json, "durationMinutes", default 30 = 2 min intro+closing + 28 min
            //    of acts). The per-act lengths and the research budget are DERIVED — the LLM
            //    never decides them. Changing the duration needs no code changes. The
            //    durationMinutes parameter overrides the config when the caller asks for a
            //    specific length (floor 5 minutes, the model's minimum).
            var minutes = durationMinutes.HasValue ? Math.Max(5, durationMinutes.Value) : PodcastConfig.DurationMinutes;
            var (actCount, actChars, targetChars) = PodcastLengths.Compute(minutes);
            var budgetChars = targetChars * 3;           // empirical context budget: 3X
            var plan = PodcastPlanner.Plan(topic, lang, actCount, actChars);
            if (plan == null)
                return "Error: the language model did not produce an episode plan. Retry later.";

            // 2) Research + 3) brief (≤ X) — the raw context never enters the writing prompt.
            //    The user-provided contextualMaterial joins the research as a first-class source.
            var brief = PodcastContext.Gather(topic, plan, budgetChars, targetChars, contextualMaterial);
            if (string.IsNullOrWhiteSpace(brief))
                return "Error: no information about this topic could be gathered. Retry later or choose a broader topic.";

            // 4) Body: act by act (deterministic per-act targets + cumulative correction;
            //    enrichment fallback is internal). minimizeAcronyms defaults true: the writer
            //    expands acronyms/abbreviations (TTS pronounces them wrongly).
            var body = PodcastScript.Generate(topic, lang, brief, plan, actChars, minimizeAcronyms);
            if (string.IsNullOrWhiteSpace(body))
                return "Error: the language model did not produce the episode script. Retry later.";

            // 5) Intro + outro: generated AFTER the body (the intro announces the themes
            //    actually covered; the outro is the host farewell — "Avete ascoltato <podcast>"
            //    — followed in the mix by a very short closing musical stinger).
            var intro = PodcastScript.GenerateIntro(topic, lang, podcastName, body);
            var outro = PodcastScript.GenerateOutro(topic, lang, podcastName);

            // Save: /podcast/{podcastName or topic}/{topic}-{date}.md + .mp3
            var folderName = Slug(string.IsNullOrWhiteSpace(podcastName) ? topic : podcastName);
            var dir = SandboxPath.Resolve($"/podcast/{folderName}");
            Directory.CreateDirectory(dir);
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            var fileBase = $"{Slug(topic)}-{date}";

            var script = $"{intro}\n\n[SIGLA]\n\n{body}\n\n[OUTRO]\n\n{outro}";
            var chars = script.Length;
            var estMinutes = (int)Math.Round(chars / 830.0);   // ~830 chars/min of speech
            var scriptPath = Path.Combine(dir, fileBase + ".md");
            File.WriteAllText(scriptPath, script);
            GitSupport.Snapshot(scriptPath, $"PodcastTool: script for '{topic}'");

            var mp3Agent = "";
            var feedAgent = "";
            var audioNote = "";
            double audioSeconds = 0;
            try
            {
                var wavPath = Path.Combine(dir, fileBase + ".wav");
                var mp3Path = Path.Combine(dir, fileBase + ".mp3");
                // The outro rides in the body stream ([OUTRO] marker → gap → farewell), after
                // which the mixer appends the very short closing stinger.
                var mixBody = $"{body}\n\n[OUTRO]\n\n{outro}";
                if (PodcastMixer.Mix(intro, mixBody, lang, wavPath, mp3Path, out audioSeconds))
                {
                    GitSupport.Snapshot(mp3Path, $"PodcastTool: audio for '{topic}'");
                    mp3Agent = SandboxPath.ToAgent(mp3Path);

                    // The podcast RSS feed: one <item> per episode, appended/updated next to
                    // the MP3s (channel metadata from podcast.json when present).
                    var feedPath = PodcastFeed.Update(dir,
                        new PodcastFeedEntry(
                            Title: $"Episodio {CountEpisodes(dir) + 1}: {topic}",
                            Description: intro,
                            Mp3FileName: Path.GetFileName(mp3Path),
                            Mp3Bytes: new FileInfo(mp3Path).Length,
                            DurationSeconds: (int)Math.Round(audioSeconds)),
                        podcastTitle: string.IsNullOrWhiteSpace(podcastName) ? topic : podcastName,
                        language: lang);
                    GitSupport.Snapshot(feedPath, $"PodcastTool: feed for '{topic}'");
                    feedAgent = SandboxPath.ToAgent(feedPath);
                }
                else
                {
                    audioNote = $" Audio skipped: {PodcastMixer.LastError}";
                }
            }
            catch (Exception ex)
            {
                Log.LogStep($"PodcastTool: audio failed — {ex.Message}");
                audioNote = $" Audio skipped: {ex.Message}";
            }

            var result = mp3Agent.Length > 0
                ? $"Podcast created: audio at {mp3Agent} (attach it to the reply)"
                : "Podcast created: script only (audio unavailable)";
            result += $", script at {SandboxPath.ToAgent(scriptPath)}";
            if (feedAgent.Length > 0) result += $", RSS feed at {feedAgent}";
            result += $". Length: {chars} characters";
            result += audioSeconds > 0
                ? $" ({audioSeconds / 60:F0} minutes of audio)."
                : $" (about {estMinutes} minutes of speech).";
            result += audioNote;
            result += $" Generated in {sw.Elapsed.TotalSeconds:F0} seconds.";
            Log.LogStep($"PodcastTool: done — {chars} chars in {sw.Elapsed.TotalSeconds:F0}s");
            return result;
        }
        catch (Exception ex)
        {
            Log.LogStep($"PodcastTool: failed — {ex.Message}");
            return $"Error: podcast generation failed: {ex.Message}";
        }
    }

    /// <summary>Builds a filesystem-safe name from the text (lowercase words, dashes).</summary>
    internal static string Slug(string text)
    {
        var words = text.ToLowerInvariant()
            .Split([' ', ',', ';', ':', '/', '\\', '(', ')', '?', '!', '.', '\'', '"'])
            .Where(w => w.Length > 0 && w.All(char.IsLetterOrDigit));
        var slug = string.Join("-", words.Take(6));
        return slug.Length == 0 ? "podcast" : slug;
    }

    /// <summary>Number of episodes already in the folder's feed.xml (0 when none yet).</summary>
    private static int CountEpisodes(string folder)
    {
        var feedPath = Path.Combine(folder, "feed.xml");
        if (!File.Exists(feedPath)) return 0;
        try
        {
            var doc = new System.Xml.XmlDocument { XmlResolver = null };
            doc.Load(feedPath);
            return doc.GetElementsByTagName("item").Count;
        }
        catch { return 0; }
    }
}
