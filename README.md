# PodcastTool

Podcast generation agent tool for AIOrchestrator: creates a complete narrated podcast
episode about a topic — a continuous narrative script of the configured duration (default
~30 minutes of speech) and the mixed audio (voice + jingle + quiet background loop) as an
MP3 file.

## Pipeline

1. **Research**: the shared web-search summaries (news of the last 24 hours + of the week),
   plus 16 verified international RSS feeds (BBC, Guardian, DW, France24, Al Jazeera, NHK,
   UN News, NDTV, The Intercept, Drop Site, ProPublica, Project Censored, Global Voices,
   Zero Hedge, CounterPunch) and a Google News per-topic feed — each item is scored against
   the topic keywords (auto-translated to English when the topic is not English), the best
   articles are read in full.
2. **Script**: the LLM writes the episode following the narrative style spec (dramatic arc,
   acts, hooks, `[pausa musicale]`/`[breve silenzio]` cues every 10–12 seconds, short
   sentences for the TTS); the per-act lengths are **deterministic** (see below); emoji are
   removed deterministically.
3. **Audio**: the narration is synthesized sentence by sentence with the shared Kokoro TTS
   engine (AIOrchestrator `KokoroTts`), the CC0 jingle and background loop are decoded and
   resampled with [OwnAudioSharp.Basic](https://github.com/ModernMube/OwnAudioSharp), and the
   three streams are mixed into a 48 kHz stereo 16-bit WAV.

## Episode duration & the deterministic length mechanism

The episode duration is the ONLY user knob. It lives in the tool config **`podcast.json`**
(next to the executable, under `assets/` — the same host-level assets convention as the
other tools):

```json
{
  "durationMinutes": 30,
  "_comment": "Episode duration in minutes (default 30 = 2 min intro+closing stinger + 28 min of acts). The per-act lengths and the research budget are derived deterministically from this value — changing it needs no code changes."
}
```

The file is created automatically with the default on first use and is **never overwritten**,
neither by the plugin updates (the updater skips `.json` files) nor by the tool itself, so
the user's setting always survives. Editing it is enough to change the episode length.

Everything else is **derived, never left to the LLM**:

- `actsMinutes = durationMinutes − 2` (2 minutes = welcome intro + closing musical stinger).
- `actCount = round(actsMinutes / 9.333)` — the standard act length for the default episode
  is 9.333 minutes (28/3), so the default yields **3 acts**; the count is clamped to [3, 6]
  (the writer and the mixer support any count).
- `actChars = actsMinutes × 833 / actCount` (833 ≈ characters per minute of speech): all acts
  get **equal** size — when the division would not be an integer the per-act length is
  adjusted so the acts stay equal and no act is left over as a shorter/longer tail.
- `budgetChars = totalChars × 3`: the research budget scales with the configured duration.

**Per-act targets at write time (cumulative correction):** the writer tells the LLM the act's
target length, but the LLM only approximates it (a loose model ± a few thousand characters).
After each act the cumulative difference vs the cumulative prediction is applied to the NEXT
act's target:

```
act 1 target = actChars                     (e.g. 7,777)
act 1 writes 8,777  →  difference = +1,000
act 2 target = 7,777 − 1,000 = 6,777
act 2 writes 6,777  →  difference = 0
act 3 target = 7,777
```

The total converges to `actChars × actCount` regardless of the model's tolerance (the
correction is bounded to 50–150% of the base so a single large overrun cannot force an
unusable act). Episodes are never cut: the correction happens at write time, the narrative
stays whole, and the final duration lands within a few minutes of the configured value.

## Methods

| Method | Purpose |
|---|---|
| `generate_podcast` | Generates the episode: script (`.md`) + audio (`.mp3`) under `/podcast/<topic>/`. |

### generate_podcast

```
generate_podcast(topic, language?, podcastName?, minimizeAcronyms?)
```

- `topic` (required): the episode subject. Recent news about it is gathered automatically.
- `language` (optional): two-letter ISO code for the script and voice; detected from the
  topic when omitted.
- `podcastName` (optional): the introduction welcomes the listeners of that podcast by name.
- `minimizeAcronyms` (optional, default true): the writer expands every acronym/abbreviation
  ("Bitcoin" not "BTC") — TTS engines often mispronounce them.

The script starts with a short introduction announcing the covered themes, then the `[SIGLA]`
marker plays the jingle, then the episode proper, then the host farewell and the closing
stinger. The audio is saved as an **MP3** together with an **RSS 2.0 feed**
(iTunes-compatible, one `<item>` per episode) under `/podcast/<podcast-or-topic>/`. Failures
return `Error: ...` with the cause; the audio part is skipped (with a note) when the host
lacks the TTS assets.

## Architecture

The full design — the news-vs-evergreen judgment, the 3X context budget and research brief,
the outline-first writing with per-act enrichment, the deterministic ducked mix and the MP3
encoding — is documented in [docs-dev/ARCHITECTURE.md](docs-dev/ARCHITECTURE.md).

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
