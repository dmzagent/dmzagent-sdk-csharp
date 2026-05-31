using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concordex.Sdk;

/// <summary>
/// Result of every event-emit method (<see cref="ConcordexClient.SubjectSaysAsync"/>,
/// <see cref="ConcordexClient.ToolCallAsync"/>, etc.).
///
/// Per sdk-spec.md §7.1 — fields the server omits when "unknown" are
/// exposed as nullable. Reach into <see cref="Raw"/> for response fields
/// the strongly-typed surface doesn't cover yet (the SDK preserves the
/// full server payload).
/// </summary>
public sealed record EmitResult(
    [property: JsonPropertyName("interaction_id")]   string                      InteractionId,
    [property: JsonPropertyName("subjects")]         IReadOnlyList<string>       Subjects,
    [property: JsonPropertyName("queued")]           bool                        Queued,
    [property: JsonPropertyName("frame_id")]         string?                     FrameId,
    [property: JsonPropertyName("subject_id")]       string?                     SubjectId,
    [property: JsonPropertyName("outcome")]          string?                     Outcome,
    [property: JsonPropertyName("triage_decision")]  string?                     TriageDecision,
    [property: JsonPropertyName("tags_fired")]       IReadOnlyList<string>?      TagsFired,
    [property: JsonPropertyName("scored_by_canons")] IReadOnlyList<string>?      ScoredByCanons,
    [property: JsonPropertyName("soul_version")]     long?                       SoulVersion,
    [property: JsonPropertyName("ledger_index")]     long?                       LedgerIndex,
    [property: JsonPropertyName("follow_my_data")]   string?                     FollowMyData,
    [property: JsonIgnore]                           JsonElement                 Raw
);

/// <summary>
/// Result of <see cref="ConcordexClient.CheckAsync"/>. Per sdk-spec.md
/// §7.2 — every field carries server semantics:
/// <list type="bullet">
///   <item><see cref="State"/> ∈ <c>closed</c> | <c>half_open</c> | <c>open</c>.</item>
///   <item><see cref="Allow"/> is <c>false</c> only when state is <c>open</c>.</item>
///   <item><see cref="Warning"/> is <c>true</c> when state is <c>half_open</c>.</item>
/// </list>
/// </summary>
public sealed record CheckResult(
    [property: JsonPropertyName("state")]            string                                                          State,
    [property: JsonPropertyName("allow")]            bool                                                            Allow,
    [property: JsonPropertyName("warning")]          bool                                                            Warning,
    [property: JsonPropertyName("reason")]           string                                                          Reason,
    [property: JsonPropertyName("fired_policies")]   IReadOnlyList<IReadOnlyDictionary<string, object?>>             FiredPolicies,
    [property: JsonPropertyName("anchor")]           IReadOnlyDictionary<string, object?>?                           Anchor,
    [property: JsonPropertyName("checked_at")]       string                                                          CheckedAt,
    [property: JsonPropertyName("latency_ms")]       double                                                          LatencyMs,
    [property: JsonPropertyName("route_latency_ms")] double                                                          RouteLatencyMs,
    [property: JsonIgnore]                           JsonElement                                                     Raw
);
