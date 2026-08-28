using System.ServiceModel.Syndication;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ReverseMarkdown;

namespace AIOrchestrator.API;

/// <summary>Internal: gathers the news context for a podcast episode. Combines the shared
/// web-search summaries (today + this week) with international RSS news feeds (aggregated and
/// filtered by topic keywords) and the full text of the top articles. Every source fails
/// independently — a broken feed never aborts the episode.</summary>
internal static partial class PodcastContext
{
    // Verified working international feeds (2026-08-28): broad + independent journalism mix.
    // Reuters/AP no longer publish public RSS — their content is reached via the Google News
    // per-query feed below.
    private static readonly (string Name, string Url)[] Feeds =
    {
        ("BBC World", "https://feeds.bbci.co.uk/news/world/rss.xml"),
        ("The Guardian", "https://www.theguardian.com/world/rss"),
        ("DW", "https://rss.dw.com/rdf/rss-en-world"),
        ("France24", "https://www.france24.com/en/rss"),
        ("Al Jazeera", "https://www.aljazeera.com/xml/rss/all.xml"),
        ("NHK World", "https://www3.nhk.or.jp/rss/news/cat0.xml"),
        ("UN News", "https://news.un.org/feed/subscribe/en/news/all/rss.xml"),
        ("NDTV World", "https://feeds.feedburner.com/ndtvnews-world-news"),
        ("The Intercept", "https://theintercept.com/feed/"),
        ("Drop Site News", "https://www.dropsitenews.com/feed"),
        ("ProPublica", "https://www.propublica.org/feeds/propublica/main"),
        ("Project Censored", "https://projectcensored.org/feed/"),
        ("Global Voices", "https://globalvoices.org/feed/"),
        ("Zero Hedge", "https://cms.zerohedge.com/fullrss2.xml"),
        ("CounterPunch", "https://www.counterpunch.org/feed/"),
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly Converter HtmlToMd = new(new Config
    {
        Base64Images = Config.Base64ImageHandling.Skip,
        UnknownTags = Config.UnknownTagsOption.Bypass,
    });

    private static readonly string[] StopWords =
    {
        "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "with", "by",
        "from", "as", "is", "are", "was", "were", "be", "been", "it", "its", "this", "that",
        "these", "those", "about", "after", "before", "between", "over", "under", "not", "but",
        "di", "del", "della", "dei", "delle", "il", "lo", "la", "gli", "le", "un", "una", "uno",
        "e", "ed", "o", "a", "in", "con", "per", "su", "tra", "fra", "da", "che", "come",
        "what", "why", "how", "when", "who", "which",
    };

    /// <summary>Gathers the episode context: today + this-week web summaries, the top topic
    /// items of every international feed, and the full text of the best articles. Returns null
    /// when NOTHING could be gathered (every source failed).</summary>
    internal static string? Gather(string topic, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# NEWS CONTEXT — topic: {topic} (gathered {DateTime.UtcNow:yyyy-MM-dd})");
        sb.AppendLine();

        // 1) Shared web-search summaries: today's news, then this week's news.
        var today = WebSearchHiLevel.SearchAndSummarize(topic, WebSearchEngine.SearchTimeRestriction.Today, 5);
        if (!string.IsNullOrWhiteSpace(today))
        {
            sb.AppendLine("## NEWS FROM THE LAST 24 HOURS");
            sb.AppendLine(today);
            sb.AppendLine();
        }
        var week = WebSearchHiLevel.SearchAndSummarize(topic, WebSearchEngine.SearchTimeRestriction.Week, 5);
        if (!string.IsNullOrWhiteSpace(week))
        {
            sb.AppendLine("## NEWS FROM THIS WEEK");
            sb.AppendLine(week);
            sb.AppendLine();
        }

        // 2) International feeds: every feed is fetched, scored against the topic keywords
        //    (English keywords when the topic is not English), the best items collected.
        var keywords = TopicKeywords(topic, lang);
        var feedItems = FetchFeedItems(topic, keywords);

        if (feedItems.Count > 0)
        {
            sb.AppendLine("## INTERNATIONAL SOURCES");
            foreach (var item in feedItems.Take(40))
            {
                var date = item.Published.HasValue ? item.Published.Value.ToString("yyyy-MM-dd") : "";
                sb.AppendLine($"- [{item.Source}] {item.Title} ({date}) — {item.Url}");
            }
            sb.AppendLine();
        }

        // 3) Full text of the best articles (the feed only carries the summary).
        var toRead = feedItems.OrderByDescending(i => i.Score).Take(6).ToArray();
        foreach (var item in toRead)
        {
            var body = ReadArticle(item.Url);
            if (string.IsNullOrWhiteSpace(body)) continue;
            sb.AppendLine($"## ARTICLE: {item.Title} — {item.Source} ({item.Url})");
            sb.AppendLine(body.Length > 12000 ? body[..12000] : body);
            sb.AppendLine();
        }

        var total = sb.ToString();
        return total.Length > 500 ? total : null;
    }

    /// <summary>Keyword set for feed scoring: the topic's own tokens plus (when the topic is
    /// not English) an English translation of the topic via the LLM — feeds are English.</summary>
    private static string[] TopicKeywords(string topic, string lang)
    {
        var tokens = Tokenize(topic);
        if (lang == "en" || tokens.Length == 0) return tokens;
        try
        {
            var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var (response, error) = llm.SendQuery(
                $"Translate this news topic to English for searching English news: \"{topic}\". " +
                "Reply with only the English translation, no quotes, no explanation.",
                useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 200, temperature: 0);
            if (error == null && !string.IsNullOrWhiteSpace(response))
            {
                var translated = Tokenize(response);
                if (translated.Length > 0) return translated;
            }
        }
        catch { }
        return tokens;
    }

    /// <summary>Tokenizes text into lowercase topic words (stop words and short words removed).</summary>
    private static string[] Tokenize(string text)
    {
        var words = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var w in words)
        {
            if (w.Length < 3 || StopWords.Contains(w) || !seen.Add(w)) continue;
            result.Add(w);
        }
        return result.ToArray();
    }

    private sealed record FeedItem(string Source, string Title, string Url, DateTimeOffset? Published, int Score);

    /// <summary>Fetches every international feed plus the Google News per-query feed, scores
    /// each item against the topic keywords and returns the best ones (capped).</summary>
    private static List<FeedItem> FetchFeedItems(string topic, string[] keywords)
    {
        var items = new List<FeedItem>();
        var googleUrl = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(topic)}&hl=en&gl=US&ceid=US:en";
        var sources = Feeds.Append((Name: "Google News", Url: googleUrl)).ToArray();

        // All feeds in parallel; each fails independently.
        var tasks = sources.Select(feed => Task.Run(() => FetchOne(feed.Name, feed.Url, keywords))).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(40)); }
        catch (AggregateException) { }

        foreach (var t in tasks)
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result == null) continue;
            items.AddRange(t.Result);
        }
        return items.OrderByDescending(i => i.Score).Take(80).ToList();
    }

    /// <summary>Fetches one feed and returns its items scored by keyword overlap.</summary>
    private static List<FeedItem>? FetchOne(string source, string url, string[] keywords)
    {
        try
        {
            using var stream = Http.GetStreamAsync(url).GetAwaiter().GetResult();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            var feed = SyndicationFeed.Load(reader);
            if (feed == null) return null;

            var list = new List<FeedItem>();
            foreach (var item in feed.Items)
            {
                var title = item.Title?.Text ?? "";
                var summary = item.Summary?.Text ?? "";
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? "";
                if (title.Length == 0 || link.Length == 0) continue;
                var score = Score(title + " " + summary, keywords);
                if (score == 0) continue;
                list.Add(new FeedItem(source, title, link, item.PublishDate, score));
            }
            return list.OrderByDescending(i => i.Score).Take(3).ToList();
        }
        catch
        {
            return null;   // a broken feed never aborts the episode
        }
    }

    /// <summary>Counts keyword hits in a title/summary (longer matches weigh more).</summary>
    private static int Score(string text, string[] keywords)
    {
        var lower = text.ToLowerInvariant();
        int score = 0;
        foreach (var kw in keywords)
        {
            if (lower.Contains(kw)) score += kw.Length >= 5 ? 2 : 1;
        }
        return score;
    }

    /// <summary>Reads an article page and converts it to Markdown text (best-effort).</summary>
    private static string? ReadArticle(string url)
    {
        try
        {
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var html = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(html)) return null;
            var md = HtmlToMd.Convert(html, includeInteractive: false);
            return Regex.Replace(md, @"\s+", " ").Trim();
        }
        catch
        {
            return null;
        }
    }
}
