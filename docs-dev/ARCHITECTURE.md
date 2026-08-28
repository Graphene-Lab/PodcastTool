# PodcastTool — Architecture & Operating Logic

This document explains, in detail, how PodcastTool produces a complete podcast episode:
the research logic, the context budget, the narrative writing strategy and the audio
pipeline. It is the developer companion to `README.md` (usage) — it answers *why* the
pipeline is shaped the way it is and how to reason about changes.

## 1. Overview

PodcastTool is a single-method agent tool:

```
generate_podcast(topic, language?, podcastName?)
    → MP3 at /podcast/{podcastName-or-topic}/{topic}-{date}.mp3
    → script .md next to it
```

One call produces: a ~25,000-character narrative script (≈ 30 minutes of speech) and the
mixed audio (welcome intro → jingle → episode body, with a soft ducked background bed)
encoded as MP3.

The pipeline has six phases:

```
1. PLAN      LLM: news-vs-evergreen judgment + English search queries + three-act outline
2. RESEARCH  deterministic: multi-query web search + 15 international RSS feeds + Google News
             per-query, deduplicated and relevance-ranked, bounded by the 3X context budget
3. BRIEF     LLM: compress the raw research into a research brief ≤ X (facts/dates/numbers)
4. WRITE     LLM: the body act by act (in the episode language); bounded per-act enrichment
5. INTRO     LLM: short welcome (podcast name) announcing the themes the body actually covers
6. MIX       deterministic: TTS narration → mix (intro clean → jingle → body on a ducked bed)
             → WAV → MP3 (GroovyMp3) → /podcast/...
```

The design goal is **result quality with token economy**: the raw research never enters the
writing prompt (a brief does), the script length is planned up front instead of fixed by
post-hoc padding, and every LLM call has a bounded, explicit purpose.

## 2. Phase 1 — Plan (one LLM call, one JSON)

`PodcastPlanner.Plan` asks the model for a single JSON object:

```json
{
  "needsRecentFacts": true,
  "searchQueries": ["technology", "AI news", "artificial intelligence trends"],
  "acts": [
    {"act": 1, "title": "The promise", "beats": ["...", "..."], "targetChars": 8000},
    ...
  ]
}
```

### 2.1 The news-vs-evergreen judgment (concept, not keywords)

The episode is classified by the answer to a *conceptual* question: **"does this episode
depend on recent or upcoming facts and events?"** The model decides; there is no keyword
dictionary.

- A podcast about food *events* → `needsRecentFacts: true` (ongoing/upcoming events matter).
- A podcast about food *recipes* → `false` (evergreen; a recipe does not expire).

This is deliberately NOT keyword-based: keyword lists create brittle bugs (the same domain
can be news or evergreen depending on the angle) while the conceptual judgment generalizes.

### 2.2 English search queries

The queries are always **English** even when the topic is in another language — the research
sources (international feeds, Google News, DuckDuckGo) are English-centric, and a query in a
different language returns nothing useful. The model is instructed to combine multiple themes
with `AND` and to add **variant phrasings** ("traveling AND low cost", "cheap holidays",
"budget travel") because a single web query returns very few results (~10), far fewer than
what 30 minutes of narrative needs.

### 2.3 The three-act outline

The acts carry `title`, `beats` (4-7 key story moments) and `targetChars`. This is the
"map of reference points" of the narrative style spec: writing is anchored to a planned
structure instead of improvised, and the per-act targets make the final length predictable
(the planner normalizes the targets so they always sum to the episode target X).

## 3. Phase 2 — Research (deterministic, budget-bounded)

`PodcastContext.Gather` accumulates raw material (search result titles/descriptions, feed
headlines, full article text) until the **3X budget** — the empirical rule that producing a
text of X needs up to 3X of context. Everything is capped and every source fails
independently.

### 3.1 The time filter depends on the judgment

| `needsRecentFacts` | Search 1 | Search 2 | Searches 3+ |
|---|---|---|---|
| `true` (news/events) | `Today` | `Week` | **no filter** |
| `false` (evergreen) | no filter | no filter | no filter |

The first query targets today, the second this week; the remaining queries are unrestricted
so the episode also gets general background for its reflective sections (the brief keeps the
recent-vs-background distinction). Evergreen topics never use a time filter.

### 3.2 Sources

- **Web search**: one DuckDuckGo search per English query (the shared `WebSearchEngine`,
  which throttles calls to one every 10 s — a 3-6 query plan costs 30-60 s).
- **RSS**: 15 verified international feeds (BBC, Guardian, DW, France24, Al Jazeera, NHK,
  UN News, NDTV, Intercept, Drop Site, ProPublica, Project Censored, Global Voices,
  Zero Hedge, CounterPunch) — headlines only, cheap. Reuters/AP no longer publish public
  RSS; their content is reached through the Google News per-query feed.
- **Google News per-query**: `news.google.com/rss/search?q=<query>` — the *topical* bridge:
  it returns recent items for a query, which is what the "10 results" web search alone
  cannot cover.
- **Full pages**: only the top 2-3 pages by relevance score are read in full (the ranking
  extracts URLs from the accumulated lines and scores them by keyword overlap); each page is
  capped at 8,000 characters. A non-news topic (e.g. recipes) simply scores ~0 on the news
  feeds and the web carries the research — the pipeline does not special-case it.

### 3.3 Deduplication and budget

URLs are normalized (lowercased, query strings dropped) and de-duplicated in a set; the raw
material builder stops as soon as the 3X character budget is reached. Duplicate *stories*
across sources are merged later by the brief.

## 4. Phase 3 — The research brief (one LLM call)

The raw material (up to 3X) is compressed by a single LLM call into a **brief ≤ X** that
preserves every concrete fact, date, number, name, quote and statistic, merges duplicates
and keeps the recent-vs-background distinction. **The script writer sees only the brief.**

*Why:* feeding 3X of raw context to the writer wastes tokens on noise and redundant
coverage of the same stories. A compressed brief is smaller, cleaner and keeps the facts.

If the brief call fails, the fallback is the deduplicated raw material capped at X.

## 5. Phase 4 — Writing the body (act by act)

`PodcastScript.Generate` writes each act in a separate LLM call:

- Every act prompt carries the full narrative style spec (hook, short sentences, explicit
  irony markers, `[pausa musicale]`/`[breve silenzio]` cues every 150-200 characters, no
  bullets/emoji), the act title + beats + target length, and — except for act 1 — the last
  300 characters of the previous act as a **seam** so the transition reads as one story.
- The act markers (`[PRIMO ATTO]`, `[SECONDO ATTO]`, `[TERZO ATTO]`) are inserted
  deterministically by the code, not requested from the model.
- After all acts, the length is measured per act (markers excluded). Acts below 80% of
  their target are **expanded inline** ("add narrative considerations that deepen the
  existing beats") — a bounded fallback, at most two rounds, per-act and anchored to the
  act's content, so it never degrades into generic padding.
- Emoji are removed deterministically at the end (`PodcastScript.FilterEmoji` — keeps the
  ASCII `[markers]` and punctuation).

The body is written in the episode language (from the `language` parameter, or detected
from the topic via `Utility.DetectLanguage`).

## 6. Phase 5 — The welcome intro (after the body)

The intro is generated **after** the body, in a separate short LLM call (3-5 sentences,
≤ 700 characters): it welcomes the listeners of the podcast (by name when `podcastName` is
given) and announces the themes that the body **actually** covers (the prompt receives the
body's opening). This ordering guarantees the announcement cannot diverge from the content.

The saved `.md` script is: `intro` + `[SIGLA]` + `body`.

## 7. Phase 6 — The audio pipeline (deterministic mix + MP3)

### 7.1 TTS

The narration is synthesized sentence by sentence (`VoiceConversation.SplitSentences`) by
the plugin-local `PodcastTts` engine (Kokoro, 24 kHz mono int16) — see the migration note
below. The body's `[markers]` become silences (0.4-1.0 s) instead of speech; the cue
markers the writer placed every 150-200 characters thus become real breathing room.

### 7.2 Music assets

Two CC0 tracks (Open Lo-Fi, CC0-1.0) travel with the plugin under `assets/audio/` and are
resolved at runtime from the host `assets/` folder: `jingle.mp3` (the sigla, used for its
first 12 seconds with a fade-out) and `background.mp3` (the bed loop).

### 7.3 Decoding: OwnAudioSharp.Basic

The music is decoded/resampled to 48 kHz stereo with `OwnAudioSharp.Basic` `FileSource`
(the full `OwnAudioSharp` package drags Avalonia/UI; Basic is the audio-only variant).
The Rust native `ownaudio_ffi` must sit in the **host app base directory** — verified
empirically: the byte-loaded plugin shim cannot resolve the native from the plugin folder —
so `PodcastMixer.EnsureNativeAvailable` copies the matching per-RID native from the plugin
payload on first use (idempotent, cross-platform).

*Why a custom mix:* OwnAudioSharp's Mixer in `ClockMode.Offline` renders in **real time**
(measured: 30 s of audio took 120 s wall-clock) — a 30-minute episode would take 30 minutes
to render. The plugin therefore uses OwnAudioSharp for what it is good at (decode,
resample, read) and performs the mix sample-by-sample in a fast deterministic loop
(~seconds for 36 minutes of audio).

### 7.4 The mix

Layout and levels:

```
[intro voice]   (no bed)
[0.6 s breath]
[jingle]        (no bed, full level, 0.6 s fade-out)
[body voice]    on a soft bed: 0.05 under speech, 0.12 in the pauses (smoothed ducking)
```

- The bed starts only where the body starts (the intro and the jingle are clean).
- Ducking is deterministic at sample level: when the narration sample is speaking
  (|voice| > 0.008) the bed gain eases to 0.05, otherwise to 0.12, with a per-frame
  smoothing constant (attack/release) so it does not pump.
- The output is 48 kHz stereo 16-bit WAV, written in one-second blocks.

### 7.5 MP3 encoding: GroovyMp3

The final deliverable is MP3, encoded by **GroovyMp3** (a pure-managed C# port of LAME).
It was chosen over the alternatives:

| Option | Why not |
|---|---|
| `EggEncoder` | natives are **win-x64 only** (contentFiles) — the plugin is cross-platform |
| `Sdcb.FFmpeg` | per-RID ffmpeg natives (~30-60 MB per platform) bloat the plugin zip |
| `GroovyMp3` | **pure managed**, no natives, LAME-derived quality — adequate for speech |

The WAV PCM is fed to `Mp3Encoder` in chunks (`EncodeBuffer` + `EncodeFinish`) and the
bulky WAV intermediate is deleted after encoding.

## 8. Output layout

```
/podcast/{podcastName-or-topic}/
    {topic}-{date}.md      # intro + [SIGLA] + body (the script)
    {topic}-{date}.mp3     # the audio deliverable (the agent attaches it to the reply)
```

Both files are versioned with `GitSupport.Snapshot`; the tool returns the sandbox-relative
paths. The method result tells the agent to attach the MP3 to the user reply.

## 9. Failure handling

- Every network/LLM source fails independently (a broken feed never aborts the episode).
- The tool returns actionable `Error: ...` messages with the cause (empty topic, plan
  failure, no context gathered, empty script, audio skipped with the TTS/assets reason).
- The audio is best-effort: if the host lacks the TTS assets or the music, the script is
  still produced and saved with an "Audio skipped: ..." note.
- All loops are bounded: enrichment ≤ 2 rounds, research ≤ 3X budget, feeds ≤ 40 s.

## 10. Known limitations & migration paths

- **Plugin-local TTS**: `PodcastTts` mirrors the shared `AIOrchestrator.KokoroTts` (which
  landed on AIOrchestrator master after the same-day package cut). Once
  `Graphene.AIOrchestrator ≥ 1.26.08.29` is published, `PodcastMixer` should switch back to
  the shared engine and `PodcastTts.cs` can be deleted.
- **MP3 quality**: GroovyMp3 (LAME port, 128 kbps) is adequate for speech; if music-heavy
  episodes need more headroom, the upgrade path is `Sdcb.FFmpeg` (per-RID natives — the
  plugin zip already carries `runtimes/` natives).
- **Long-form**: a 60-minute episode doubles X, the 3X budget and the TTS time linearly;
  the act targets and enrichment scale automatically.
- **The `puntata` naming**: the episode file uses `{topic}-{date}` — deterministic and
  self-describing; a numeric episode counter could replace the date if the host tracks a series.

## 11. Diagnostics

The harness enables `Log.IsEnabled` and the tool logs each phase (plan summary, per-query
result counts, raw/brief sizes, per-act lengths, narration/jingle timing, MP3 size) to
`logs/<pid>.txt` next to the executable — tail it to monitor a run:
`Get-Content <harness-bin>/logs/<pid>.txt -Tail 20`.
