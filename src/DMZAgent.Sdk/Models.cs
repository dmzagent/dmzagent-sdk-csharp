using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DMZAgent.Sdk;

/// <summary>
/// Result of every event-emit method (<see cref="DMZAgentClient.SubjectSaysAsync"/>,
/// <see cref="DMZAgentClient.ToolCallAsync"/>, etc.).
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
    [property: JsonPropertyName("accepted")]         bool?                       Accepted,
    // Workspaces the frame fanned out to (spec §7.1). Was reachable only
    // through Raw before 0.8.0.
    [property: JsonPropertyName("n_workspaces")]     int?                        NWorkspaces,
    [property: JsonPropertyName("frame_id")]         string?                     FrameId,
    [property: JsonPropertyName("subject_id")]       string?                     SubjectId,
    [property: JsonPropertyName("outcome")]          string?                     Outcome,
    [property: JsonPropertyName("triage_decision")]  string?                     TriageDecision,
    [property: JsonPropertyName("tags_fired")]       IReadOnlyList<string>?      TagsFired,
    [property: JsonPropertyName("scored_by_canons")] IReadOnlyList<string>?      ScoredByCanons,
    [property: JsonPropertyName("soul_version")]     long?                       SoulVersion,
    [property: JsonPropertyName("ledger_index")]     long?                       LedgerIndex,
    [property: JsonPropertyName("follow_my_data")]   string?                     FollowMyData,
    // true for a live key, false for a test key (ck_test_…), null when the
    // server omitted it (spec §1.2, §2.1). Null rather than false: false is
    // the positive claim "this is test data", and asserting that about a
    // response that never carried the field is the confusion the signal
    // exists to prevent.
    [property: JsonPropertyName("livemode")]         bool?                       Livemode,
    [property: JsonIgnore]                           JsonElement                 Raw
);

/// <summary>
/// Result of <see cref="DMZAgentClient.CheckAsync"/>. Per sdk-spec.md
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

public sealed record CaptureResult(
    [property: JsonPropertyName("frame_id")]         string                      FrameId,
    [property: JsonPropertyName("accepted")]         bool                        Accepted,
    [property: JsonPropertyName("n_workspaces")]     int                         NWorkspaces,
    [property: JsonPropertyName("interaction_id")]   string                      InteractionId,
    [property: JsonPropertyName("subjects")]         IReadOnlyList<string>       Subjects,
    [property: JsonPropertyName("follow_my_data")]   string?                     FollowMyData,
    // See EmitResult.Livemode (spec §1.2, §2.1).
    [property: JsonPropertyName("livemode")]         bool?                       Livemode,
    [property: JsonIgnore]                           JsonElement                 Raw
);

// Returned by AwaitOutcomeAsync (sdk-spec.md §7.3). Per-workspace
// reasoning results for a captured frame: a frame is division-scoped, so
// it fans out to every workspace in its division and produces one trace
// per workspace, each entry in Reasoning naming the workspace_id that
// produced it.
//
// Outcome is a fold over Reasoning computed server-side, with precedence
// failed > held > applied > no_change > skipped (§2.7). It is null until
// at least one trace exists — never guessed. It was string.Empty when the
// key was absent, which is neither a real outcome nor distinguishable
// from one.
//
// XML doc comments are illegal on record parameters, so this is a plain
// comment block.
public sealed record OutcomeResult(
    [property: JsonPropertyName("frame_id")]         string                      FrameId,
    [property: JsonPropertyName("outcome")]          string?                     Outcome,
    [property: JsonPropertyName("division_id")]      string?                     DivisionId,
    [property: JsonPropertyName("workspace_ids")]    JsonElement?                WorkspaceIds,
    [property: JsonPropertyName("complete")]         bool                        Complete,
    [property: JsonPropertyName("error")]            JsonElement?                Error,
    [property: JsonPropertyName("tags_fired")]       JsonElement?                TagsFired,
    [property: JsonPropertyName("reasoning")]        JsonElement?                Reasoning,
    [property: JsonPropertyName("soul_version")]     long?                       SoulVersion,
    [property: JsonPropertyName("finished_at")]      string                      FinishedAt,
    [property: JsonIgnore]                           JsonElement                 Raw
);

public sealed record NotificationPrefs(
    [property: JsonPropertyName("email_cadence")]       string          EmailCadence,
    [property: JsonPropertyName("email_paused_until")]  string?         EmailPausedUntil,
    [property: JsonPropertyName("push_enabled")]        bool            PushEnabled,
    [property: JsonPropertyName("phone")]               string?         Phone,
    [property: JsonPropertyName("sms_enabled")]         bool            SmsEnabled,
    [property: JsonPropertyName("whatsapp_enabled")]    bool            WhatsappEnabled,
    [property: JsonPropertyName("webhook_url")]         string?         WebhookUrl,
    [property: JsonIgnore]                              JsonElement     Raw
);

public sealed record DivisionConfig(
    [property: JsonPropertyName("config")]           JsonElement         Config,
    [property: JsonIgnore]                           JsonElement         Raw
);

public sealed record ReviewEvent(
    [property: JsonPropertyName("event_id")]         string          EventId,
    [property: JsonPropertyName("type")]             string          Type,
    [property: JsonPropertyName("review_id")]        string          ReviewId,
    [property: JsonPropertyName("subject_id")]       string          SubjectId,
    [property: JsonPropertyName("tag_id")]           string          TagId,
    [property: JsonPropertyName("level")]            string          Level,
    [property: JsonPropertyName("status")]           string          Status,
    [property: JsonPropertyName("tier")]             string          Tier,
    [property: JsonPropertyName("decision")]         string?         Decision,
    [property: JsonPropertyName("workspace_id")]     string          WorkspaceId,
    [property: JsonPropertyName("division_id")]      string?         DivisionId,
    [property: JsonPropertyName("frame_id")]         string?         FrameId,
    [property: JsonPropertyName("occurred_at")]      string          OccurredAt,
    [property: JsonIgnore]                           JsonElement     Raw
);
