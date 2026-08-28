using AIOrchestrator;
using AIOrchestrator.API;
using System.Diagnostics;

// ═══════════════════════════════════════════════════════════════════════
//  PodcastTool.Harness — end-to-end test against a live LLM provider.
//
//  Usage:
//    dotnet run --project PodcastTool.Harness -- "topic"                 # full episode
//    dotnet run --project PodcastTool.Harness -- --selftest              # fast offline-ish
//    dotnet run --project PodcastTool.Harness -- "topic" --name "My Show"
//
//  Default provider: DeepSeekBridge (the local codex-deepseek-bridge at
//  127.0.0.1:8787). Override with --provider NAME.
//  The workspace lives in %TEMP%\podcasttool-workspace (never in the repo).
// ═══════════════════════════════════════════════════════════════════════

var selftest = args.Contains("--selftest");
var sampleOut = Arg(args, "--sample");
var provider = Arg(args, "--provider") ?? "DeepSeekBridge";
var podcastName = Arg(args, "--name");
var topicArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        if (args[i] is "--provider" or "--name") i++;   // skip the flag's value
        continue;
    }
    topicArgs.Add(args[i]);
}
var topic = string.Join(" ", topicArgs);

Setup.DocumentsPath = Path.Combine(Path.GetTempPath(), "podcasttool-workspace");
Directory.CreateDirectory(Setup.DocumentsPath);
InitGitWorkspace(Setup.DocumentsPath);

// Diagnostics: the AIOrchestrator Log is off by default — enable it and write the tool's
// step trace to logs/<pid>.txt next to the harness for the post-run analysis.
Log.IsEnabled = true;

var config = new ProviderConfig
{
    ProviderName = provider,
    Protocol = ProviderProtocol.OpenAI,
    CacheType = ProviderCacheType.PrefixCache,
    ModelName = "deepseek-web/deepseek-chat",
    BaseAddress = new Uri("http://127.0.0.1:8787/"),
    EndPoint = "v1/chat/completions",
    Timeout = TimeSpan.FromMinutes(5),
    PauseBetweenRequests = TimeSpan.FromSeconds(5),
    ContextWindow = 1_000_000,
    ForceTextToolDefinitions = true,
};
ProviderConfigs.Upsert(config, persist: false);
Setup.ProviderConfig = config;

Console.WriteLine($"═══ PodcastTool.Harness (provider={provider}, workspace={Setup.DocumentsPath}) ═══");
EnsureKokoroModel();
Console.WriteLine($"Diagnostic log: {Log.CurrentLogFile}");

var tool = new PodcastTool();

if (sampleOut != null)
{
    // Generates a short English demo clip (intro → jingle → body on the ducked bed) — the
    // sample used in the AgentBridge README media gallery.
    Console.WriteLine($"── SAMPLE → {sampleOut} ──");
    var intro = "Welcome to the AgentBridge podcast. Today we explore how artificial intelligence is reshaping the way we work.";
    var body = """
        [PRIMO ATTO]
        Imagine walking into your office and finding a colleague who never sleeps, never forgets, and never complains. [pausa musicale]
        That is the promise of autonomous agents. [breve silenzio]
        [SECONDO ATTO]
        They read your documents, answer your calls, and draft your reports. [pausa musicale]
        All inside a sandbox that cannot be breached.
        [TERZO ATTO]
        Because the future is already here. [pausa musicale]
        Running on your own hardware, under your own control.
        """;
    var wav = Path.ChangeExtension(sampleOut, ".wav");
    var ok = PodcastMixer.Mix(intro, body, "en", wav, sampleOut);
    Console.WriteLine(ok ? $"Sample OK: {sampleOut}" : $"Sample FAILED: {PodcastMixer.LastError}");
    return;
}

if (selftest)
{
    RunSelftest(tool);
    return;
}

if (string.IsNullOrWhiteSpace(topic))
{
    Console.WriteLine("Usage: dotnet run --project PodcastTool.Harness -- \"topic\" [--name \"Show\"] [--provider NAME] [--selftest]");
    return;
}

Console.WriteLine($"Topic: {topic} | Podcast: {podcastName ?? "(none)"}");
var sw = Stopwatch.StartNew();
var result = tool.GeneratePodcast(topic, podcastName: podcastName);
sw.Stop();
Console.WriteLine();
Console.WriteLine("────── RESULT ──────");
Console.WriteLine(result);
Console.WriteLine($"Total wall time: {sw.Elapsed.TotalMinutes:F1} minutes.");

// Verify the produced files.
foreach (var line in result.Split('\n'))
{
    var idx = line.IndexOf(" at /");
    if (idx < 0) continue;
    var agentPath = line[(idx + 4)..].Trim().Split(' ')[0];
    if (SandboxPath.TryResolve(agentPath, out var host) && File.Exists(host))
    {
        var info = new FileInfo(host);
        Console.WriteLine($"OK: {agentPath} — {info.Length:N0} bytes ({info.Length / 1024d / 1024d:F1} MB)");
    }
}

static void RunSelftest(PodcastTool tool)
{
    Console.WriteLine("── SELFTEST: deterministic filters ──");
    var emoji = "Ciao! 👋😀 Benvenuti a \"Il Mio Podcast\" [SIGLA] — 100% bello.";
    var filtered = PodcastScript.FilterEmoji(emoji);
    Console.WriteLine($"FilterEmoji: '{filtered}'");
    Console.WriteLine($"  emoji gone: {!filtered.Contains("👋") && !filtered.Contains("😀")}");
    Console.WriteLine($"  markers kept: {filtered.Contains("[SIGLA]")}");
    Console.WriteLine($"  quotes kept: {filtered.Contains('\"')}");

    Console.WriteLine("── SELFTEST: audio pipeline (intro→jingle→body + ducked bed + MP3) ──");
    var shortIntro = "Benvenuti al Podcast Tool. Oggi parliamo di intelligenza artificiale.";
    var shortBody = """
        [PRIMO ATTO]
        It is everywhere, from your phone to your car. [pausa musicale]
        The story begins with a simple question. [breve silenzio]
        [SECONDO ATTO]
        And that question changes everything we know.
        [TERZO ATTO]
        Because the future is already here.
        """;
    var wav = Path.Combine(Path.GetTempPath(), "podcasttool-selftest.wav");
    var mp3 = Path.Combine(Path.GetTempPath(), "podcasttool-selftest.mp3");
    if (File.Exists(mp3)) File.Delete(mp3);
    var ok = PodcastMixer.Mix(shortIntro, shortBody, "en", wav, mp3);
    if (ok && File.Exists(mp3))
    {
        var info = new FileInfo(mp3);
        Console.WriteLine($"Mix OK: {mp3} — {info.Length:N0} bytes");
        Console.WriteLine($"  mp3 produced: {info.Length > 5000}");
        Console.WriteLine($"  wav intermediate removed: {!File.Exists(wav)}");
        // MP3 bitrate sanity: bytes / duration ≈ bitrate/8. Duration unknown here, so just
        // check the file is non-trivial and starts with the MP3 sync frame (0xFF 0xFB/E3).
        var head = File.ReadAllBytes(mp3);
        Console.WriteLine($"  mp3 sync frame: {head[0]:X2} {head[1]:X2} (expect FF E3/FB)");
    }
    else
    {
        Console.WriteLine($"Mix FAILED: {PodcastMixer.LastError}");
    }

    Console.WriteLine("── SELFTEST: RSS feed builder ──");
    var feedDir = Path.Combine(Path.GetTempPath(), "podcasttool-feedtest");
    if (Directory.Exists(feedDir)) Directory.Delete(feedDir, true);   // clean between runs
    Directory.CreateDirectory(feedDir);
    var feedPath = PodcastFeed.Update(feedDir,
        new PodcastFeedEntry("Episodio 1: Test", "Benvenuti a questo episodio di prova.",
            "test-20260828.mp3", 123456, 1800),
        "Il Podcast del Mattino", "it");
    var feedXml = File.ReadAllText(feedPath);
    Console.WriteLine($"feed created: {File.Exists(feedPath)}");
    Console.WriteLine($"  channel title: {feedXml.Contains("<title>Il Podcast del Mattino</title>")}");
    Console.WriteLine($"  item present: {feedXml.Contains("<item>") && feedXml.Contains("test-20260828.mp3")}");
    Console.WriteLine($"  enclosure: {feedXml.Contains("length=\"123456\"") && feedXml.Contains("type=\"audio/mpeg\"")}");
    Console.WriteLine($"  itunes:duration: {feedXml.Contains("<itunes:duration>1800</itunes:duration>")}");
    // Second episode appends; same guid replaces (no duplicates).
    var feedPath2 = PodcastFeed.Update(feedDir,
        new PodcastFeedEntry("Episodio 1: Test", "Stesso episodio.",
            "test-20260828.mp3", 999, 1900),
        "Il Podcast del Mattino", "it");
    var feedXml2 = File.ReadAllText(feedPath2);
    Console.WriteLine($"  replace same guid: {feedXml2.Contains("length=\"999\"") && !feedXml2.Contains("length=\"123456\"")}");
    var feedPath3 = PodcastFeed.Update(feedDir,
        new PodcastFeedEntry("Episodio 2: Test", "Secondo episodio.",
            "test2-20260828.mp3", 222, 2000),
        "Il Podcast del Mattino", "it");
    var feedXml3 = File.ReadAllText(feedPath3);
    Console.WriteLine($"  append episode 2: {(feedXml3.Split("<item>").Length - 1) == 2}");
    Console.WriteLine("SELFTEST DONE");
}

static string? Arg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

/// <summary>git-init the temp workspace so GitSupport.Snapshot works.</summary>
static void InitGitWorkspace(string dir)
{
    try
    {
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            using var p = Process.Start(new ProcessStartInfo("git", "init -q") { WorkingDirectory = dir, UseShellExecute = false });
            p?.WaitForExit();
        }
    }
    catch { }
}

/// <summary>Provides kokoro.onnx next to the harness (the tool's KokoroTts resolves it from
/// the app base). Preferred: the sibling VoiceAgent build output; fallback: download once.</summary>
static void EnsureKokoroModel()
{
    var baseDir = AppContext.BaseDirectory;
    var target = Path.Combine(baseDir, "kokoro.onnx");
    if (File.Exists(target)) return;

    var candidates = new[]
    {
        Path.Combine(baseDir, "..", "..", "..", "..", "..", "AIOffice.VoiceAgent", "bin", "Debug", "net10.0", "kokoro.onnx"),
        Path.Combine(baseDir, "..", "..", "..", "..", "..", "AIOffice.VoiceAgent", "bin", "Release", "net10.0", "kokoro.onnx"),
        Path.Combine(baseDir, "..", "..", "..", "..", "AIOffice.VoiceAgent.Win", "bin", "Debug", "net10.0-windows10.0.19041.0", "kokoro.onnx"),
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (File.Exists(full))
        {
            File.Copy(full, target);
            Console.WriteLine($"Kokoro model reused from: {full}");
            return;
        }
    }

    Console.WriteLine("Kokoro model not found next to the harness — downloading (~325 MB, one time)...");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    using var src = http.GetStreamAsync("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx").GetAwaiter().GetResult();
    using var dst = File.Create(target);
    src.CopyTo(dst);
    Console.WriteLine("Kokoro model downloaded.");
}
