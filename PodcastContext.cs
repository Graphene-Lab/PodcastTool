using System.ServiceModel.Syndication;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ReverseMarkdown;

namespace AIOrchestrator.API;

/// <summary>Internal: researches the episode context. English queries from the plan (boolean
/// combinations + variants) drive web searches whose time filter is ON only when the plan
/// judged the episode news/event-sensitive; international RSS feeds and a Google News
/// per-query feed add headlines (filtered by the topic keywords). The raw material is
/// deduplicated, relevance-ranked, bounded by the 3X context budget and then compressed by a
/// single LLM call into a research brief (≤ X) that the script writer consumes — the raw
/// context never enters the writing prompt. Every source fails independently.</summary>
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
        "what", "why", "how", "when", "who", "which", "traveling", "travelling", "vacation",
    };

    /// <summary>Researches the topic and returns the research brief (≤ <paramref name="targetChars"/>).
    /// The raw material is bounded by the 3X budget (empirical: three times the target text).
    /// Null when NOTHING could be gathered.</summary>
    internal static string? Gather(string topic, PodcastPlan plan, int budgetChars, int targetChars)
    {
        var raw = new StringBuilder(Math.Min(budgetChars, 120_000));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = Keywords(plan.SearchQueries);

        // 1) Web searches: one per English query. For news/event episodes the FIRST query
        //    targets today, the SECOND this week, and the rest are unrestricted — the episode
        //    needs recent facts AND general background for its reflective sections. Evergreen
        //    episodes never use a time filter.
        for (int i = 0; i < plan.SearchQueries.Length; i++)
        {
            if (raw.Length >= budgetChars) break;
            var restriction = plan.NeedsRecentFacts
                ? (i switch
                {
                    0 => WebSearchEngine.SearchTimeRestriction.Today,
                    1 => WebSearchEngine.SearchTimeRestriction.Week,
                    _ => (WebSearchEngine.SearchTimeRestriction?)null,
                })
                : (WebSearchEngine.SearchTimeRestriction?)null;
            var results = WebSearchEngine.Search(plan.SearchQueries[i], filetype: null,
                provider: WebSearchEngine.SearchProvider.DuckDuckGo, timeRestriction: restriction);
            Log.LogStep($"PodcastContext: query {i + 1} '{plan.SearchQueries[i]}' ({(restriction?.ToString() ?? "no filter")}) → {results.Length} results");
            foreach (var r in results)
            {
                if (raw.Length >= budgetChars) break;
                if (string.IsNullOrWhiteSpace(r.Url) || !seen.Add(NormalizeUrl(r.Url))) continue;
                var line = r.MdDescription;
                if (line.Length == 0) continue;
                // The URL rides WITH the description so the full-page pass can find it.
                raw.Append(line).Append(" (").Append(r.Url).Append(")\n");
                if (raw.Length >= budgetChars) break;
            }
        }

        // 2) International feeds + Google News per-query (headlines only — cheap), scored by
        //    the topic keywords; a news-less topic (e.g. recipes) simply scores nothing here.
        foreach (var item in FetchFeedItems(plan.SearchQueries, keywords))
        {
            if (raw.Length >= budgetChars) break;
            if (!seen.Add(NormalizeUrl(item.Url))) continue;
            var date = item.Published.HasValue ? item.Published.Value.ToString("yyyy-MM-dd") : "";
            raw.Append($"[{item.Source}] {item.Title} ({date}) — {item.Url}\n");
        }

        // 3) Full text of the top-ranked pages (search results first, then the best feed
        //    items). When the accumulated material is still thin (below the episode target),
        //    read more pages so the brief has enough facts to weave into 30 minutes.
        var pagesToRead = raw.Length < targetChars ? 6 : 3;
        foreach (var url in TopUrls(raw.ToString(), keywords, pagesToRead))
        {
            if (raw.Length >= budgetChars) break;
            if (!seen.Add(NormalizeUrl(url))) continue;
            var body = ReadArticle(url);
            if (string.IsNullOrWhiteSpace(body)) continue;
            raw.Append("\n### ").Append(url).Append('\n');
            raw.Append(body.Length > 8000 ? body[..8000] : body).Append('\n');
        }

        var material = raw.ToString();
        if (material.Length < 500) return null;
        Log.LogStep($"PodcastContext: {material.Length:N0} raw chars gathered (budget {budgetChars:N0})");

        // 4) The research brief: one LLM call compresses the raw material (deduplicated,
        //    facts preserved) into ≤ targetChars. The script writer sees ONLY the brief.
        try
        {
            var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
            var (brief, err) = llm.SendQuery(
                $"""
                Today's date is {DateTime.UtcNow:yyyy-MM-dd}.
                A podcast episode about "{topic}" is being researched. Below is the raw material
                gathered from web searches, international news feeds and articles.

                Condense it into a research brief of at most {targetChars} characters that a script
                writer will use to write a narrative podcast:
                - Keep every concrete fact, date, number, name, quote and statistic relevant to the topic.
                - Merge duplicate information across sources.
                - Keep the distinction between very recent facts and general background.
                - Plain flowing text, no preamble, no headings, no lists, no emoji.

                Raw material:
                {material}
                """,
                useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 9000, temperature: 0.3);
            if (err == null && !string.IsNullOrWhiteSpace(brief))
            {
                var b = brief.Trim();
                Log.LogStep($"PodcastContext: brief {b.Length:N0} chars");
                return b.Length > 200 ? b : null;
            }
        }
        catch { }

        // Fallback: the deduplicated raw material, capped.
        return material.Length > targetChars ? material[..targetChars] : material;
    }

    /// <summary>Scoring tokens: the meaningful words of all English queries.</summary>
    private static string[] Keywords(string[] queries)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in queries)
        {
            foreach (var w in Regex.Split(q.ToLowerInvariant(), @"[^a-z0-9]+"))
            {
                if (w.Length < 3 || StopWords.Contains(w) || !seen.Add(w)) continue;
                result.Add(w);
            }
        }
        return result.ToArray();
    }

    /// <summary>Returns the best <paramref name="count"/> URLs from the raw material, ranked by
    /// keyword overlap with the topic (URLs already carrying their title line score higher).</summary>
    private static IEnumerable<string> TopUrls(string material, string[] keywords, int count)
    {
        var entries = new List<(int Score, string Url)>();
        foreach (var line in material.Split('\n'))
        {
            var m = Regex.Match(line, @"https?://\S+");
            if (!m.Success) continue;
            entries.Add((Score(line, keywords), m.Value));
        }
        return entries
            .Where(e => !Regex.IsMatch(e.Url, @"duckduckgo\.com|google\.com|\.gov"))
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Url)
            .Select(e => e.Url)
            .Distinct()
            .Take(count);
    }

    private static string NormalizeUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        var q = lower.IndexOf('?');
        if (q >= 0) lower = lower[..q];
        return lower.TrimEnd('/');
    }

    private sealed record FeedItem(string Source, string Title, string Url, DateTimeOffset? Published, int Score);

    /// <summary>Fetches the international feeds plus one Google News per-query feed, scores
    /// each item against the topic keywords and returns the best ones (capped).</summary>
    private static List<FeedItem> FetchFeedItems(string[] queries, string[] keywords)
    {
        var items = new List<FeedItem>();
        var sources = Feeds.ToList();
        foreach (var q in queries.Take(2))
            sources.Add(("Google News: " + q, $"https://news.google.com/rss/search?q={Uri.EscapeDataString(q)}&hl=en&gl=US&ceid=US:en"));

        var tasks = sources.Select(feed => Task.Run(() => FetchOne(feed.Name, feed.Url, keywords))).ToArray();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(40)); }
        catch (AggregateException) { }

        foreach (var t in tasks)
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result == null) continue;
            items.AddRange(t.Result);
        }
        return items.OrderByDescending(i => i.Score).Take(40).ToList();
    }

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

    /// <summary>Counts keyword hits (longer matches weigh more).</summary>
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
