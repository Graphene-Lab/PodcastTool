using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PodcastTool.Harness")]

namespace AIOrchestrator.API;

/// <summary>Creates narrated podcast episodes: plans the episode (recent-facts judgment +
/// English research queries + three-act outline), researches the topic from recent or general
/// sources accordingly, compresses the research into a brief, writes the narrative body and a
/// separate welcome intro, then mixes the audio (intro → jingle → body, soft ducked background)
/// and saves it as an MP3 under /podcast/. Files are saved under /podcast/.</summary>
public class PodcastTool : BaseAgentTool, IFileTool
{
    /// <summary>
    /// Generates a complete podcast episode about <paramref name="topic"/>: a narrative script
    /// of about 25,000 characters (three acts, continuous storytelling — no bullet lists, no
    /// emoji) and the mixed audio (voice, jingle, soft background music) as an MP3 file. The
    /// episode starts with a short welcome introduction announcing the covered themes, then
    /// the jingle, then the episode proper.
    /// </summary>
    /// <param name="topic">The episode subject. When the subject depends on recent facts or
    /// events, recent news about it is gathered; otherwise general background material is used.</param>
    /// <param name="language">Optional two-letter ISO language for the script and voice
    /// (e.g. "en", "it", "fr", "es"). When omitted, the language of <paramref name="topic"/>
    /// is detected. An invalid value (not a two-letter ISO code) returns an error.</param>
    /// <param name="podcastName">Optional name of the podcast. When provided, the introduction
    /// welcomes the listeners of that podcast by name and the files are saved under
    /// /podcast/&lt;podcastName&gt;/.</param>
    /// <returns>The sandbox-relative paths of the saved MP3 (to attach to the user reply),
    /// script and podcast RSS feed, with their lengths; failures are returned as
    /// "Error: ..." with the cause.</returns>
    public string GeneratePodcast(string topic, string? language = null, string? podcastName = null)
    {
        var sw = Stopwatch.StartNew();
        Log.LogStep($"PodcastTool: generating podcast about '{topic}' (lang={language ?? "auto"}, name={podcastName ?? "none"})");
        try
        {
            if (string.IsNullOrWhiteSpace(topic))
                return "Error: the topic is empty. Provide a topic to generate the podcast.";
            if (topic.Length > 300)
                topic = topic.Trim()[..300];

            var lang = Utility.DetectLanguage(topic) ?? "en";
            if (!string.IsNullOrWhiteSpace(language))
            {
                var normalized = PodcastScript.NormalizeLanguage(language);
                if (normalized == null)
                    return $"Error: the 'language' parameter must be a valid two-letter ISO code (e.g. \"en\", \"it\", \"fr\", \"es\") — received \"{language}\", which is not valid. Omit it to use the language of the topic.";
                lang = normalized;
            }

            // 1) Plan: recent-facts judgment + English queries + three-act outline.
            const int targetChars = 25000;               // X: ~30 minutes of speech
            var budgetChars = targetChars * 3;           // empirical context budget: 3X
            var plan = PodcastPlanner.Plan(topic, lang, targetChars);
            if (plan == null)
                return "Error: the language model did not produce an episode plan. Retry later.";

            // 2) Research + 3) brief (≤ X) — the raw context never enters the writing prompt.
            var brief = PodcastContext.Gather(topic, plan, budgetChars, targetChars);
            if (string.IsNullOrWhiteSpace(brief))
                return "Error: no information about this topic could be gathered. Retry later or choose a broader topic.";

            // 4) Body: act by act (enrichment fallback is internal).
            var body = PodcastScript.Generate(topic, lang, brief, plan, targetChars);
            if (string.IsNullOrWhiteSpace(body))
                return "Error: the language model did not produce the episode script. Retry later.";

            // 5) Intro: generated AFTER the body so it announces the themes actually covered.
            var intro = PodcastScript.GenerateIntro(topic, lang, podcastName, body);

            // Save: /podcast/{podcastName or topic}/{topic}-{date}.md + .mp3
            var folderName = Slug(string.IsNullOrWhiteSpace(podcastName) ? topic : podcastName);
            var dir = SandboxPath.Resolve($"/podcast/{folderName}");
            Directory.CreateDirectory(dir);
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            var fileBase = $"{Slug(topic)}-{date}";

            var script = $"{intro}\n\n[SIGLA]\n\n{body}";
            var chars = script.Length;
            var estMinutes = (int)Math.Round(chars / 830.0);   // ~830 chars/min of speech
            var scriptPath = Path.Combine(dir, fileBase + ".md");
            File.WriteAllText(scriptPath, script);
            GitSupport.Snapshot(scriptPath, $"PodcastTool: script for '{topic}'");

            var mp3Agent = "";
            var feedAgent = "";
            var audioNote = "";
            try
            {
                var wavPath = Path.Combine(dir, fileBase + ".wav");
                var mp3Path = Path.Combine(dir, fileBase + ".mp3");
                if (PodcastMixer.Mix(intro, body, lang, wavPath, mp3Path))
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
                            DurationSeconds: estMinutes * 60),
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
            result += $". Length: {chars} characters (about {estMinutes} minutes of speech).";
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
