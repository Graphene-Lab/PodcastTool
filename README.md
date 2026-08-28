# PodcastTool

Podcast generation agent tool for AIOrchestrator: creates a complete narrated podcast
episode about a topic — a continuous narrative script (~25,000 characters, ~30 minutes of
speech) and the mixed audio (voice + jingle + quiet background loop) as a WAV file.

## Pipeline

1. **Research**: the shared web-search summaries (news of the last 24 hours + of the week),
   plus 16 verified international RSS feeds (BBC, Guardian, DW, France24, Al Jazeera, NHK,
   UN News, NDTV, The Intercept, Drop Site, ProPublica, Project Censored, Global Voices,
   Zero Hedge, CounterPunch) and a Google News per-topic feed — each item is scored against
   the topic keywords (auto-translated to English when the topic is not English), the best
   articles are read in full.
2. **Script**: the LLM writes the episode following the narrative style spec (dramatic arc,
   three acts, hooks, `[pausa musicale]`/`[breve silenzio]` cues every 10–12 seconds, short
   sentences for the TTS), in chunks until the target length; emoji are removed
   deterministically.
3. **Audio**: the narration is synthesized sentence by sentence with the shared Kokoro TTS
   engine (AIOrchestrator `KokoroTts`), the CC0 jingle and background loop are decoded and
   resampled with [OwnAudioSharp.Basic](https://github.com/ModernMube/OwnAudioSharp), and the
   three streams are mixed into a 48 kHz stereo 16-bit WAV.

## Methods

| Method | Purpose |
|---|---|
| `generate_podcast` | Generates the episode: script (`.md`) + audio (`.wav`) under `/podcasts/<topic>/`. |

### generate_podcast

```
generate_podcast(topic, language?, podcastName?)
```

- `topic` (required): the episode subject. Recent news about it is gathered automatically.
- `language` (optional): two-letter ISO code for the script and voice; detected from the
  topic when omitted.
- `podcastName` (optional): the introduction welcomes the listeners of that podcast by name.

The script starts with a short introduction announcing the covered themes, then the `[SIGLA]`
marker plays the jingle, then the episode proper. Failures return `Error: ...` with the cause;
the audio part is skipped (with a note) when the host lacks the TTS assets.

## Music assets (CC0)

`Assets/audio/` ships the jingle and the background loop from
[Open Lo-Fi](https://github.com/btahir/open-lofi) (CC0-1.0 — no attribution required) — see
`Assets/audio/CREDITS.md`. They travel with the plugin (`lib/<tfm>/assets/audio/`) and are
resolved from the host `assets/` folder at runtime.

## Usage

Loads as a plugin (see [AGENT_TOOLS_GUIDE.md](https://github.com/Graphene-Lab/AgentHarness/blob/master/API/AGENT_TOOLS_GUIDE.md)):
drop the `dll` + `xml` into the host's `Tools/` folder, or let the host build it via its
`BuildToolPlugins` target. The tool is auto-updatable from NuGet (`Graphene.PodcastTool`).

## Testing / Harness

The `PodcastTool.Harness` console project drives the tool end-to-end against a live LLM
provider (default `DeepSeekBridge` — the local `codex-deepseek-bridge` at `127.0.0.1:8787`):

```
dotnet run --project PodcastTool.Harness -- "artificial intelligence"          # full episode
dotnet run --project PodcastTool.Harness -- "quantum computing" --max-chars 6000   # short smoke
```

The harness provisions the Kokoro model (`kokoro.onnx`, ~325 MB, one-time) from the sibling
`AIOffice.VoiceAgent` build output or by download, and runs the tool in a `%TEMP%` sandbox.

## Packaging

Date-based auto versioning (`1.yy.MM.dd`); push a tag `v*` to publish:
`plugin-release.yml` builds the self-contained plugin zip (GitHub Releases — the host
deployment channel) and `publish.yml` packs/pushes the NuGet package (`Graphene.PodcastTool`).
