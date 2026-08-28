using System.Text;
using System.Xml;

namespace AIOrchestrator.API;

/// <summary>One episode entry in the podcast RSS feed.</summary>
internal sealed record PodcastFeedEntry(string Title, string Description, string Mp3FileName, long Mp3Bytes, int DurationSeconds);

/// <summary>Internal: builds/updates the podcast RSS 2.0 feed (with the iTunes namespace)
/// next to the episodes. Channel metadata comes from an optional podcast.json in the same
/// folder (host-configurable: title, description, link, language, author, imageUrl, category,
/// explicit); when absent, sensible defaults are used. Each generated episode appends (or
/// replaces) one &lt;item&gt; with the MP3 enclosure.</summary>
internal static class PodcastFeed
{
    private const string FeedFileName = "feed.xml";
    private const string ConfigFileName = "podcast.json";

    /// <summary>Creates or updates the feed for the given episode and returns its host path.</summary>
    internal static string Update(string folder, PodcastFeedEntry entry, string podcastTitle, string language)
    {
        var feedPath = Path.Combine(folder, FeedFileName);
        var config = LoadConfig(folder);

        var title = FirstNonEmpty(config.GetValueOrDefault("title"), podcastTitle, "Podcast");
        var description = FirstNonEmpty(config.GetValueOrDefault("description"), $"Podcast about {entry.Title}");
        var link = config.GetValueOrDefault("link") ?? "";
        var lang = FirstNonEmpty(config.GetValueOrDefault("language"), language, "en");
        var author = config.GetValueOrDefault("author") ?? "";
        var imageUrl = config.GetValueOrDefault("imageUrl") ?? "";
        var category = FirstNonEmpty(config.GetValueOrDefault("category"), "Society & Culture");
        var explicitFlag = config.GetValueOrDefault("explicit") ?? "false";
        if (explicitFlag != "true") explicitFlag = "false";

        var doc = new XmlDocument();
        doc.XmlResolver = null;
        XmlElement channel;
        if (File.Exists(feedPath))
        {
            doc.Load(feedPath);
            channel = (XmlElement?)doc.GetElementsByTagName("channel")[0] ?? BuildChannel(doc);
            // Channel metadata is refreshed from the config each time.
            SetChannel(doc, channel, title, description, link, lang, author, imageUrl, category, explicitFlag);
        }
        else
        {
            channel = BuildChannel(doc);
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));
            var rss = doc.CreateElement("rss");
            rss.SetAttribute("version", "2.0");
            rss.SetAttribute("xmlns:itunes", "http://www.itunes.com/dtds/podcast-1.0.dtd");
            rss.SetAttribute("xmlns:podcast", "https://podcastnamespace.org/");
            rss.AppendChild(channel);
            doc.AppendChild(rss);
            SetChannel(doc, channel, title, description, link, lang, author, imageUrl, category, explicitFlag);
        }

        // Replace the item with the same guid, or append a new episode item.
        var guid = entry.Mp3FileName;
        XmlElement? existing = null;
        foreach (XmlNode node in doc.GetElementsByTagName("guid"))
        {
            if (node.InnerText == guid) { existing = node.ParentNode as XmlElement; break; }
        }
        if (existing != null)
        {
            var parent = existing.ParentNode;
            parent?.RemoveChild(existing);
        }
        var item = BuildItem(doc, entry, link);
        channel.AppendChild(item);

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(feedPath, settings))
            doc.Save(writer);
        var episodes = doc.GetElementsByTagName("item").Count;
        Log.LogStep($"PodcastFeed: '{feedPath}' updated — {episodes} episode(s), enclosure {entry.Mp3Bytes:N0} bytes");
        return feedPath;
    }

    /// <summary>Loads the host-provided channel metadata (podcast.json) as a flat dictionary.</summary>
    private static Dictionary<string, string> LoadConfig(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(folder, ConfigFileName);
        if (!File.Exists(path)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch { }
        return result;
    }

    private static XmlElement BuildChannel(XmlDocument doc)
    {
        var channel = doc.CreateElement("channel");
        var title = doc.CreateElement("title");
        channel.AppendChild(title);
        var description = doc.CreateElement("description");
        channel.AppendChild(description);
        var link = doc.CreateElement("link");
        channel.AppendChild(link);
        return channel;
    }

    private static void SetChannel(XmlDocument doc, XmlElement channel, string title, string description, string link,
        string lang, string author, string imageUrl, string category, string explicitFlag)
    {
        SetText(channel, "title", title);
        SetText(channel, "description", description);
        SetText(channel, "link", link);
        SetText(channel, "language", lang);
        SetText(channel, "itunes:author", author);
        SetText(channel, "itunes:explicit", explicitFlag);

        if (imageUrl.Length > 0)
        {
            var img = FindOrCreate(channel, "itunes:image");
            img.SetAttribute("href", imageUrl);
        }
        var cat = FindOrCreate(channel, "itunes:category");
        cat.SetAttribute("text", category);
    }

    private static XmlElement BuildItem(XmlDocument doc, PodcastFeedEntry entry, string link)
    {
        var item = doc.CreateElement("item");
        SetText(item, "title", entry.Title);
        SetText(item, "description", entry.Description);
        SetText(item, "pubDate", DateTime.UtcNow.ToString("r"));
        SetText(item, "guid", entry.Mp3FileName);
        var enclosure = doc.CreateElement("enclosure");
        var baseUrl = (link ?? "").TrimEnd('/');
        enclosure.SetAttribute("url", baseUrl.Length > 0 ? baseUrl + "/" + entry.Mp3FileName : entry.Mp3FileName);
        enclosure.SetAttribute("length", entry.Mp3Bytes.ToString());
        enclosure.SetAttribute("type", "audio/mpeg");
        item.AppendChild(enclosure);
        SetText(item, "itunes:duration", entry.DurationSeconds.ToString());
        SetText(item, "itunes:explicit", "false");
        return item;
    }

    private static void SetText(XmlElement parent, string name, string value)
    {
        var el = FindOrCreate(parent, name);
        el.InnerText = value ?? "";
    }

    private static XmlElement FindOrCreate(XmlElement parent, string name)
    {
        // Child iteration instead of XPath: prefixed names ("itunes:author") would need a
        // namespace manager, and this stays prefix-agnostic. Prefixed elements are created
        // with their namespace URI so the writer accepts them.
        foreach (XmlNode child in parent.ChildNodes)
            if (child is XmlElement el && el.Name == name) return el;
        var doc = parent.OwnerDocument!;
        var colon = name.IndexOf(':');
        var prefix = colon > 0 ? name[..colon] : "";
        var ns = prefix switch
        {
            "itunes" => "http://www.itunes.com/dtds/podcast-1.0.dtd",
            "podcast" => "https://podcastnamespace.org/",
            _ => "",
        };
        var created = doc.CreateElement(prefix, colon > 0 ? name[(colon + 1)..] : name, ns);
        parent.AppendChild(created);
        return created;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return "";
    }
}
