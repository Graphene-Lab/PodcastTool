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

var tool = new PodcastTool();

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

    Console.WriteLine("── SELFTEST: audio pipeline (TTS + jingle + background + mix) ──");
    var shortScript = """
        Welcome to our test podcast. Today we talk about artificial intelligence.
        [SIGLA]
        It is everywhere, from your phone to your car. [pausa musicale]
        The story begins with a simple question. [breve silenzio]
        [PRIMO ATTO]
        And that question changes everything we know.
        """;
    var wav = Path.Combine(Path.GetTempPath(), "podcasttool-selftest.wav");
    var ok = PodcastMixer.Mix(shortScript, "en", wav);
    if (ok && File.Exists(wav))
    {
        var info = new FileInfo(wav);
        // 48 kHz stereo int16 → seconds = bytes / (48000*2*2)
        var seconds = info.Length / (48000L * 2 * 2);
        Console.WriteLine($"Mix OK: {wav} — {info.Length:N0} bytes, ~{seconds}s");
        Console.WriteLine($"  narration spoken: {seconds > 5}");
        Console.WriteLine($"  jingle gap present: {seconds > 12}");   // jingle (~12s) + speech
    }
    else
    {
        Console.WriteLine($"Mix FAILED: {PodcastMixer.LastError}");
    }
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
