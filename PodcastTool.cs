using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PodcastTool.Harness")]

namespace AIOrchestrator.API;

/// <summary>Creates narrated podcast episodes: researches the topic from recent news, writes a
/// continuous narrative script (~30 minutes of speech, no bullet lists), and mixes the spoken
/// audio with a jingle and a quiet background loop. Files are saved under /podcasts/.</summary>
public class PodcastTool : BaseAgentTool, IFileTool
{
    /// <summary>
    /// Generates a complete podcast episode about <paramref name="topic"/>: a narrative script
    /// of about 25,000 characters and the mixed audio (voice, jingle, background music) in
    /// WAV format. The script starts with a short introduction announcing the covered themes,
    /// then a jingle, then the episode; emoji are removed and the text follows a continuous
    /// storytelling style (no bullet lists, no markdown headings).
    /// </summary>
    /// <param name="topic">The episode subject. Recent news about it is gathered automatically.</param>
    /// <param name="language">Optional two-letter ISO language for the script and voice
    /// (e.g. "en", "it"). When omitted, the language of <paramref name="topic"/> is detected.</param>
    /// <param name="podcastName">Optional name of the podcast. When provided, the introduction
    /// welcomes the listeners of that podcast by name.</param>
    /// <returns>The sandbox-relative paths of the saved script and audio files with their
    /// lengths; failures are returned as "Error: ..." with the cause.</returns>
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
                var l = language.Trim().ToLowerInvariant();
                if (l.Length >= 2 && char.IsLetter(l[0]) && char.IsLetter(l[1]))
                    lang = l[..2];
            }

            var context = PodcastContext.Gather(topic, lang);
            if (string.IsNullOrWhiteSpace(context))
                return "Error: no recent information about this topic could be gathered (web search and news feeds unavailable). Retry later or choose a broader topic.";

            var script = PodcastScript.Generate(topic, lang, podcastName, context, targetChars: 25000);
            if (string.IsNullOrWhiteSpace(script))
                return "Error: the language model did not produce a script. Retry later.";

            var slug = Slug(topic);
            var dir = SandboxPath.Resolve($"/podcasts/{slug}");
            Directory.CreateDirectory(dir);

            var scriptPath = Path.Combine(dir, $"{slug}-{DateTime.UtcNow:yyyyMMdd}.md");
            File.WriteAllText(scriptPath, script);
            GitSupport.Snapshot(scriptPath, $"PodcastTool: script for '{topic}'");

            var wavAgent = "";
            var audioNote = "";
            try
            {
                var wavPath = Path.Combine(dir, $"{slug}-{DateTime.UtcNow:yyyyMMdd}.wav");
                if (PodcastMixer.Mix(script, lang, wavPath))
                {
                    GitSupport.Snapshot(wavPath, $"PodcastTool: audio for '{topic}'");
                    wavAgent = SandboxPath.ToAgent(wavPath);
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

            var chars = script.Length;
            var estMinutes = (int)Math.Round(chars / 830.0);   // ~830 chars/min of speech
            var result = $"Podcast created: script at {SandboxPath.ToAgent(scriptPath)}";
            if (wavAgent.Length > 0) result += $", audio at {wavAgent}";
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

    /// <summary>Builds a filesystem-safe name from the topic (lowercase words, dashes).</summary>
    internal static string Slug(string topic)
    {
        var words = topic.ToLowerInvariant()
            .Split([' ', ',', ';', ':', '/', '\\', '(', ')', '?', '!', '.', '\'', '"'])
            .Where(w => w.Length > 0 && w.All(char.IsLetterOrDigit));
        var slug = string.Join("-", words.Take(6));
        return slug.Length == 0 ? "podcast" : slug;
    }
}
