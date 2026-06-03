using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Concordex.Sdk.Concordia;

/// <summary>Verdict returned by <c>enforce_covenant</c>. <c>Escalate</c>
/// is reserved for a future state — MCP 1.0 returns allow / review / block.</summary>
public static class Verdict
{
    public const string Allow    = "allow";
    public const string Review   = "review";
    public const string Block    = "block";
    public const string Escalate = "escalate";
}

/// <summary>Circuit-breaker state at the moment of an enforce_covenant call.</summary>
public sealed record CbStateChange(
    [property: JsonPropertyName("state")]        string State,
    [property: JsonPropertyName("warning")]      bool   Warning,
    [property: JsonPropertyName("soul_version")] int?   SoulVersion
);

/// <summary>Result of <see cref="ConcordiaClient.EnforceCovenantAsync"/>.</summary>
public sealed record EnforceCovenantResult(
    [property: JsonPropertyName("verdict")]          string                    Verdict,
    [property: JsonPropertyName("policy_ids")]       IReadOnlyList<string>     PolicyIds,
    [property: JsonPropertyName("rationale")]        string                    Rationale,
    [property: JsonPropertyName("cb_state_change")]  CbStateChange             CbStateChange,
    [property: JsonPropertyName("ledger_entry_id")]  string                    LedgerEntryId
)
{
    /// <summary>True iff <see cref="Verdict"/> is <c>"allow"</c>.</summary>
    [JsonIgnore]
    public bool Allow   => Verdict == Concordia.Verdict.Allow;

    /// <summary>True iff <see cref="Verdict"/> is <c>"block"</c>.</summary>
    [JsonIgnore]
    public bool Blocked => Verdict == Concordia.Verdict.Block;
}

/// <summary>Result of <see cref="ConcordiaClient.RecordDecisionAsync"/>.</summary>
public sealed record RecordDecisionResult(
    [property: JsonPropertyName("ledger_entry_id")] string LedgerEntryId,
    [property: JsonPropertyName("chain_head_hash")] string ChainHeadHash,
    [property: JsonPropertyName("index")]           int?   Index
);

/// <summary>One match from <c>query_corpus</c>.</summary>
public sealed record CorpusMatch(
    [property: JsonPropertyName("canon_id")]      string CanonId,
    [property: JsonPropertyName("canon_version")] string CanonVersion,
    [property: JsonPropertyName("section")]       string Section,
    [property: JsonPropertyName("excerpt")]       string Excerpt,
    [property: JsonPropertyName("relevance")]     double Relevance
);

/// <summary>Result of <see cref="ConcordiaClient.QueryCorpusAsync"/>.</summary>
public sealed record QueryCorpusResult(
    [property: JsonPropertyName("matches")]     IReadOnlyList<CorpusMatch> Matches,
    [property: JsonPropertyName("scope")]       string                     Scope,
    [property: JsonPropertyName("canon_count")] int                        CanonCount
);

/// <summary>One tag on a subject's soul snapshot.</summary>
public sealed record SoulTag(
    [property: JsonPropertyName("tag_id")]              string  TagId,
    [property: JsonPropertyName("score")]               double  Score,
    [property: JsonPropertyName("raw_strength")]        double? RawStrength,
    [property: JsonPropertyName("evidence_count")]      int     EvidenceCount,
    [property: JsonPropertyName("first_observed_at")]   string  FirstObservedAt,
    [property: JsonPropertyName("last_reinforced_at")]  string  LastReinforcedAt
);

/// <summary>One recent reasoning trace on a subject.</summary>
public sealed record SoulTrace(
    [property: JsonPropertyName("trace_id")]    string  TraceId,
    [property: JsonPropertyName("frame_id")]    string? FrameId,
    [property: JsonPropertyName("verdict")]     string? Verdict,
    [property: JsonPropertyName("outcome")]     string  Outcome,
    [property: JsonPropertyName("started_at")]  string  StartedAt,
    [property: JsonPropertyName("finished_at")] string? FinishedAt
);

/// <summary>Result of <see cref="ConcordiaClient.GetSubjectSoulAsync"/>.</summary>
public sealed record SubjectSoul(
    [property: JsonPropertyName("subject_id")]    string                 SubjectId,
    [property: JsonPropertyName("snapshot_at")]   string                 SnapshotAt,
    [property: JsonPropertyName("soul_version")]  int                    SoulVersion,
    [property: JsonPropertyName("tags")]          IReadOnlyList<SoulTag> Tags,
    [property: JsonPropertyName("recent_traces")] IReadOnlyList<SoulTrace> RecentTraces,
    [property: JsonPropertyName("retired_count")] int                    RetiredCount
);

/// <summary>One CB policy from <c>concordia:/workspace/policies</c>.</summary>
public sealed record PolicySummary(
    [property: JsonPropertyName("cb_policy_id")] string  CbPolicyId,
    [property: JsonPropertyName("name")]         string  Name,
    [property: JsonPropertyName("description")]  string  Description,
    [property: JsonPropertyName("scope")]        string  Scope,
    [property: JsonPropertyName("action")]       string  Action,
    [property: JsonPropertyName("enabled")]      bool    Enabled,
    [property: JsonPropertyName("rules")]        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rules,
    [property: JsonPropertyName("updated_at")]   string? UpdatedAt
);

/// <summary>One installed Canon from <c>concordia:/workspace/canons</c>.</summary>
public sealed record InstalledCanon(
    [property: JsonPropertyName("canon_id")]          string  CanonId,
    [property: JsonPropertyName("name")]              string  Name,
    [property: JsonPropertyName("category")]          string? Category,
    [property: JsonPropertyName("author_name")]       string? AuthorName,
    [property: JsonPropertyName("latest_version")]    string? LatestVersion,
    [property: JsonPropertyName("short_description")] string  ShortDescription,
    [property: JsonPropertyName("icon_url")]          string? IconUrl,
    [property: JsonPropertyName("enabled")]           bool    Enabled,
    [property: JsonPropertyName("installed_at")]      string? InstalledAt
);

/// <summary>One ledger row from <c>concordia:/workspace/recent-ledger</c>.</summary>
public sealed record LedgerEntry(
    [property: JsonPropertyName("event_id")]     string                          EventId,
    [property: JsonPropertyName("index")]        int                             Index,
    [property: JsonPropertyName("prev_hash")]    string                          PrevHash,
    [property: JsonPropertyName("payload_hash")] string                          PayloadHash,
    [property: JsonPropertyName("hash")]         string                          Hash,
    [property: JsonPropertyName("payload")]      IReadOnlyDictionary<string, object?> Payload,
    [property: JsonPropertyName("recorded_at")]  string                          RecordedAt
);

/// <summary>Paginated result from <see cref="ConcordiaClient.RecentLedgerAsync"/>.
/// When <see cref="NextSince"/> is non-null, the caller should re-call with
/// <c>since: nextSince</c> to fetch the next page.</summary>
public sealed record LedgerPage(
    [property: JsonPropertyName("entries")]    IReadOnlyList<LedgerEntry> Entries,
    [property: JsonPropertyName("since")]      int                        Since,
    [property: JsonPropertyName("limit")]      int                        Limit,
    [property: JsonPropertyName("count")]      int                        Count,
    [property: JsonPropertyName("next_since")] int?                       NextSince
);
