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
    /// <summary>Plans the episode: recent-facts judgment, English search queries (boolean +
    /// variants) and the three-act outline with per-act target lengths. Null on failure.</summary>
    internal static PodcastPlan? Plan(string topic, string lang, int targetChars)
    {
        var llm = new LLMUtility(Setup.ProviderConfig.ProviderName);
        var langName = PodcastScript.LangName(lang);
        var (response, error) = llm.SendQuery(
            $"""
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

            3. "acts": a three-act narrative outline. For each act: "title", 4-7 "beats" (key story
               moments) and "targetChars" (target length in characters — the three acts must sum
               to about {targetChars}).

            The podcast will be written in {langName}.

            Output exactly a JSON object with these keys: "needsRecentFacts" (boolean),
            "searchQueries" (array of 3-6 English strings) and "acts" (array of 3 objects,
            each with "act" (1, 2, 3), "title" (string), "beats" (array of 4-7 strings) and
            "targetChars" (integer)). No prose, no code fences, no other keys.
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
                    var chars = a.TryGetProperty("targetChars", out var tc) && tc.ValueKind == JsonValueKind.Number
                        ? tc.GetInt32() : targetChars / 3;
                    acts.Add(new ActPlan(acts.Count + 1, title, beats, chars));
                }
            }
            if (acts.Count == 0)
            {
                var third = targetChars / 3;
                acts.Add(new ActPlan(1, "Act one", Array.Empty<string>(), third));
                acts.Add(new ActPlan(2, "Act two", Array.Empty<string>(), third));
                acts.Add(new ActPlan(3, "Act three", Array.Empty<string>(), targetChars - 2 * third));
            }

            // Normalize the per-act targets so they always sum to the episode target.
            var total = acts.Sum(a => a.TargetChars);
            if (total != targetChars)
            {
                var scale = (double)targetChars / total;
                var scaled = acts.Select(a => (int)(a.TargetChars * scale)).ToArray();
                scaled[^1] += targetChars - scaled.Sum();
                acts = acts.Select((a, i) => a with { TargetChars = scaled[i] }).ToList();
            }

            Log.LogStep($"PodcastPlanner: needsRecentFacts={needsRecent}, {queries.Count} queries, {acts.Count} acts");
            return new PodcastPlan(needsRecent, queries.ToArray(), acts.ToArray());
        }
        catch (Exception ex)
        {
            Log.LogStep($"PodcastPlanner: plan JSON invalid — {ex.Message}");
            return null;
        }
    }
}
