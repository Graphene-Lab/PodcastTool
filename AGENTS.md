# PodcastTool — notes for coding agents

> Read this file first. This repo is one tool plugin of the AIOffice solution: it is compiled on
> its own and loaded by the hosts from `Tools/PodcastTool/` at runtime.

## What this is

A roughly 30-minute narrated podcast episode, produced from web and international news-feed
research: the model writes the narrative script, the plugin mixes the audio.

## Read before you edit

| If you touch… | Read first |
|---|---|
| anything | [PLUGIN-GUARDRAILS](../AIOrchestrator/docs-dev/PLUGIN-GUARDRAILS.md) — the contract shared by the 12 plugins; the file lives in the sibling AIOrchestrator repo (private), reachable from the solution checkout |
| the pipeline, the audio mixing | `docs-dev/` |
| a tool method or its XML docs | [AGENT_TOOLS_GUIDE](../AIOrchestrator/API/AGENT_TOOLS_GUIDE.md) |

## Non-negotiables

1. The class derives from `BaseAgentTool` and keeps `[ToolPurpose(ToolPurpose.Podcast)]`.
2. Public methods and their XML docs **are** the agent's surface: no public method without docs,
   and a public method is never renamed (the model calls it by that name).
3. AnyCPU, no `RuntimeIdentifier`, `GenerateDocumentationFile` stays on.
4. The plugin folder never carries `AIOrchestrator.dll`; no state is written into `Tools/`.
5. **The TTS engine belongs to the host.** The plugin calls the shared `KokoroTts`; the
   `kokoro.onnx` model and the voices are provisioned by the host build. Never ship them in the
   plugin folder.
6. A new metadata goes in a class attribute (or a `virtual` member with a default) — never an
   `abstract` member: the dlls already shipped must keep loading.

## Where things are

- `PodcastTool.cs` — the tool class; its public methods are what the model can call
- `Assets/` — the intro/outro and static audio resources shipped with the plugin
- `docs-dev/` — the pipeline notes
- `PodcastTool.Harness/` — nested harness, excluded from the library compile

## Shipping

Publishes a GitHub release zip **and** a NuGet package; AgentBridge ships it in every release.
AIOffice does not build it into its dev `Tools/`.
