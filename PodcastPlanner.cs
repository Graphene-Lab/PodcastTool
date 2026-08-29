using System.Text.Json;

namespace AIOrchestrator.API;

/// <summary>One narrative act of the episode: title, story beats and the target length in
/// characters (the acts sum to the episode target).</summary>
internal sealed record ActPlan(int Act, string Title, string[] Beats, int TargetChars);

/// <summary>The episode plan produced in a single LLM call: whether the episode needs recent
/// facts (news/events — time-sensitive) or is evergreen, the ENGLISH search queries (boolean
/// combinations + variants) and the three-act outline.</summary>
internal sealed record PodcastPlan(bool NeedsRecentFacts, string[] SearchQueries, ActPlan[] Acts);

/// <summary>Internal: plans the episode with one structured LLM call. The news-vs-evergreen
/// judgment is CONCEPTUAL (not keyword-based): the model answers "does this episode depend on
/// recent or upcoming facts?" — a podcast about food events gets recent-fact research, a
/// podcast about recipes does not. The queries are always English (the sources are
/// international) and include variant phrasings because a single query returns few results.</summary>
internal static class PodcastPlanner
{
    /// <summary>Plans the episode: recent-facts judgment + English queries + an
    /// <paramref name="actCount"/>-act narrative outline. The act LENGTHS are NOT part of the
    /// plan — they are deterministic (PodcastLengths) and imposed by the writer; every act
    /// receives the same base <paramref name="actChars"/> (the per-act correction happens at
    /// write time).</summary>
    internal static PodcastPlan? Plan(string topic, string lang, int actCount, int actChars)
    {
        var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
        var langName = PodcastScript.LangName(lang);
        var (response, error) = llm.SendQuery(
            $"""
            Today's date is {DateTime.UtcNow:yyyy-MM-dd} (UTC) — use it as the temporal reference.

            Analyze the podcast topic "{topic}" and return a plan as JSON only (no prose, no fences).

            1. "needsRecentFacts": true when the episode depends on RECENT or UPCOMING facts and
               events (news, politics, markets, product launches, ongoing or upcoming events,
               trends — anything time-sensitive). False for evergreen content (how-to, recipes,
               concepts, history, theory) whose value does not change with time — UNLESS the
               topic concerns events in that domain (e.g. food events are news, food recipes are not).

            2. "searchQueries": 3 to 6 ENGLISH search queries to research the topic on
               international sources. Combine multiple themes with AND; add VARIANT queries with
               synonyms because a single query returns very few results
               (e.g. "traveling AND low cost", "cheap holidays", "budget travel").

            3. "acts": a {actCount}-act narrative outline. For each act: "title" and 4-7 "beats"
               (key story moments). The act lengths are NOT part of the plan — the writer
               receives them deterministically.

            The podcast will be written in {langName}.

            Output exactly a JSON object with these keys: "needsRecentFacts" (boolean),
            "searchQueries" (array of 3-6 English strings) and "acts" (array of {actCount}
            objects, each with "act" (1..{actCount}), "title" (string) and "beats" (array of
            4-7 strings)). No prose, no code fences, no other keys.
            """,
            useHistory: false, role: LLMUtility.SystemRole.None, maxToken: 2500, temperature: 0.3,
            forceJsonResponse: true);

        if (error != null || string.IsNullOrWhiteSpace(response)) return null;

        var json = response;
        Utility.RemoveFencesEncapsulationAndFixTrim(ref json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var needsRecent = root.TryGetProperty("needsRecentFacts", out var nr) && nr.ValueKind == JsonValueKind.True;

            var queries = new List<string>();
            if (root.TryGetProperty("searchQueries", out var qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (var q in qs.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(q.GetString()))
                        queries.Add(q.GetString()!);
            if (queries.Count == 0) queries.Add(topic);
            if (queries.Count > 6) queries = queries.Take(6).ToList();

            var acts = new List<ActPlan>();
            if (root.TryGetProperty("acts", out var actsEl) && actsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in actsEl.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var beats = a.TryGetProperty("beats", out var bs) && bs.ValueKind == JsonValueKind.Array
                        ? bs.EnumerateArray().Select(b => b.GetString() ?? "").Where(b => b.Length > 0).ToArray()
                        : Array.Empty<string>();
                    // The length is DETERMINISTIC — the LLM's "targetChars" (if any) is ignored.
                    acts.Add(new ActPlan(acts.Count + 1, title, beats, actChars));
                }
            }
            if (acts.Count == 0)
            {
                for (int n = 1; n <= actCount; n++)
                    acts.Add(new ActPlan(n, $"Act {n}", Array.Empty<string>(), actChars));
            }

            Log.LogStep($"PodcastPlanner: needsRecentFacts={needsRecent}, {queries.Count} queries, {acts.Count} acts (target {actChars} chars/act)");
            return new PodcastPlan(needsRecent, queries.ToArray(), acts.ToArray());
        }
        catch (Exception ex)
        {
            Log.LogStep($"PodcastPlanner: plan JSON invalid — {ex.Message}");
            return null;
        }
    }
}
