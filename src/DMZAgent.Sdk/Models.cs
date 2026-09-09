using System;
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
    [property: JsonIgnore]                           JsonElement                                                     Raw,
    // How the caller got this result (spec §4.4). No counterpart on the
    // wire: with the state cache off — the default — these are always
    // false, Zero, false.
    [property: JsonIgnore]                           bool                                                            Cached = false,
    [property: JsonIgnore]                           TimeSpan                                                        CacheAge = default,
    [property: JsonIgnore]                           bool                                                            Stale = false,
    // The approval this denial is waiting on, or null (spec §2.2).
    // Non-null only alongside Allow == false. It is a field rather than a
    // fourth breaker state so that code reading Allow alone still refuses:
    // a client that has never heard of approvals must not start allowing
    // what it used to deny.
    [property: JsonPropertyName("pending_approval_id")] string?                                                      PendingApprovalId = null
)
{
    /// <summary>
    /// This is an ask, not a refusal — a human can still clear it.
    ///
    /// <para>The difference <see cref="PendingApprovalId"/> exists to
    /// express: branch on it to show your approval UI instead of telling
    /// the user no.</para>
    /// </summary>
    public bool AwaitingApproval => PendingApprovalId is not null;

    /// <summary>
    /// This result, marked as served from the cache at <paramref name="age"/> old.
    /// </summary>
    /// <remarks>
    /// <see cref="LatencyMs"/>, <see cref="RouteLatencyMs"/>,
    /// <see cref="CheckedAt"/> and <see cref="Raw"/> are left alone: they
    /// describe the check that actually happened, and rewriting them to
    /// describe the cache hit would erase the only record of when the
    /// server was last asked.
    /// </remarks>
    public CheckResult AsCached(TimeSpan age, bool stale = false) =>
        this with
        {
            Cached   = true,
            CacheAge = age < TimeSpan.Zero ? TimeSpan.Zero : age,
            Stale    = stale,
        };
}

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

// ---------------------------------------------------------------------------
// Human-in-the-loop approvals and the incident ledger (spec §2.8–§2.10, 0.10.0)
// ---------------------------------------------------------------------------

/// <summary>
/// The human half of an <see cref="Approval"/> — who decided, and why.
///
/// <para><see cref="ActorId"/> is the <em>caller's</em> identifier for a
/// person, not ours. We resolve it against no directory and store it as
/// given, which is what lets a customer's own users decide without ever
/// holding an account here.</para>
/// </summary>
public sealed record ApprovalDecision(
    [property: JsonPropertyName("decision")]    string  Decision,
    [property: JsonPropertyName("actor_id")]    string  ActorId,
    [property: JsonPropertyName("actor_label")] string? ActorLabel,
    [property: JsonPropertyName("reason")]      string? Reason,
    [property: JsonPropertyName("decided_at")]  string  DecidedAt
);

/// <summary>
/// An action held pending a human decision (spec §7.12).
///
/// <para>Every field here is something <em>you</em> render. There is no
/// message written for your end user, no copy of ours, and no display
/// string: <see cref="Reason"/> and each <see cref="FiredPolicies"/>
/// entry's name are the words your operator typed when they wrote the
/// policy, and <see cref="Action"/> is the call your agent was about to
/// make. Building display text out of them is your job precisely because
/// a sentence we wrote would read the same in every customer's
/// product.</para>
///
/// <para><see cref="ExpiresAt"/> stays the server's ISO-8601 string rather
/// than a parsed countdown. Seconds-remaining computed at parse time is
/// wrong by however long you held the object, and the caller rendering an
/// approval deadline is exactly the caller who holds it.</para>
/// </summary>
public sealed record Approval(
    [property: JsonPropertyName("approval_id")]    string                                              ApprovalId,
    [property: JsonPropertyName("status")]         string                                              Status,
    [property: JsonPropertyName("subject_id")]     string                                              SubjectId,
    [property: JsonPropertyName("interaction_id")] string?                                             InteractionId,
    [property: JsonPropertyName("frame_id")]       string?                                             FrameId,
    // The held call, verbatim: {tool, args}.
    [property: JsonPropertyName("action")]         IReadOnlyDictionary<string, object?>                Action,
    [property: JsonPropertyName("reason")]         string                                              Reason,
    [property: JsonPropertyName("fired_policies")] IReadOnlyList<IReadOnlyDictionary<string, object?>> FiredPolicies,
    [property: JsonPropertyName("requested_at")]   string                                              RequestedAt,
    [property: JsonPropertyName("expires_at")]     string                                              ExpiresAt,
    // Always "decline". An approval that becomes an allow because nobody
    // looked at it is a delay with extra steps, not a control (spec §2.9).
    [property: JsonPropertyName("on_expiry")]      string                                              OnExpiry,
    [property: JsonPropertyName("anchor")]         IReadOnlyDictionary<string, object?>?               Anchor,
    [property: JsonPropertyName("decision")]       ApprovalDecision?                                   Decision,
    [property: JsonIgnore]                         JsonElement                                         Raw
)
{
    /// <summary><c>true</c> while nobody has decided.</summary>
    public bool IsPending => Status == "pending";

    /// <summary>The tool this approval is holding, or <c>""</c> when absent.</summary>
    public string Tool => Action.TryGetValue("tool", out var t) && t is string s ? s : string.Empty;
}

/// <summary>
/// One page of <c>DMZAgentClient.ListApprovalsAsync</c> (spec §7.11).
///
/// <para><see cref="NextCursor"/> is <c>null</c> on the last page. Nothing
/// here follows it for you — see
/// <c>DMZAgentClient.IterApprovalsAsync</c>.</para>
/// </summary>
public sealed record ApprovalPage(
    [property: JsonPropertyName("approvals")]   IReadOnlyList<Approval> Approvals,
    [property: JsonPropertyName("next_cursor")] string?                 NextCursor,
    [property: JsonIgnore]                      JsonElement             Raw
)
{
    public int Count => Approvals.Count;
}

/// <summary>One thing that was done about an <see cref="Incident"/> (spec §7.14).</summary>
public sealed record Remediation(
    [property: JsonPropertyName("remediation_id")] string                                RemediationId,
    [property: JsonPropertyName("kind")]           string                                Kind,
    [property: JsonPropertyName("outcome")]        string                                Outcome,
    [property: JsonPropertyName("approval_id")]    string?                               ApprovalId,
    [property: JsonPropertyName("actor_id")]       string?                               ActorId,
    [property: JsonPropertyName("reason")]         string?                               Reason,
    [property: JsonPropertyName("occurred_at")]    string                                OccurredAt,
    [property: JsonPropertyName("anchor")]         IReadOnlyDictionary<string, object?>? Anchor
);

/// <summary>
/// One entry of the append-only incident ledger (spec §7.14).
///
/// <para><see cref="Anchor"/> is the ledger entry that opened this incident,
/// in the same <c>{ledger_index, hash}</c> shape
/// <see cref="CheckResult.Anchor"/> carries. A caller who recorded an anchor
/// at check time can find that entry here and compare hashes; a mismatch is
/// the one alarm the ledger exists to make possible.</para>
///
/// <para>An incident with no remediations and status <c>open</c> is the
/// normal shape of something nobody has answered yet — not an error, and not
/// something to collapse to null.</para>
/// </summary>
public sealed record Incident(
    [property: JsonPropertyName("incident_id")]    string                                              IncidentId,
    [property: JsonPropertyName("status")]         string                                              Status,
    [property: JsonPropertyName("kind")]           string                                              Kind,
    [property: JsonPropertyName("subject_id")]     string                                              SubjectId,
    [property: JsonPropertyName("frame_id")]       string?                                             FrameId,
    [property: JsonPropertyName("opened_at")]      string                                              OpenedAt,
    [property: JsonPropertyName("closed_at")]      string?                                             ClosedAt,
    [property: JsonPropertyName("reason")]         string                                              Reason,
    [property: JsonPropertyName("fired_policies")] IReadOnlyList<IReadOnlyDictionary<string, object?>> FiredPolicies,
    // Oldest first. MAY be empty.
    [property: JsonPropertyName("remediations")]   IReadOnlyList<Remediation>                          Remediations,
    [property: JsonPropertyName("anchor")]         IReadOnlyDictionary<string, object?>?               Anchor,
    [property: JsonIgnore]                         JsonElement                                         Raw
)
{
    /// <summary><c>true</c> while nobody has answered this.</summary>
    public bool IsOpen => Status == "open";
}

/// <summary>
/// One page of <c>DMZAgentClient.GetIncidentsAsync</c> (spec §7.13).
///
/// <para>Newest <c>ledger_index</c> first, as the server ordered it. The SDK
/// does not re-sort: ordering by a timestamp cannot separate two entries
/// written in the same second, and the ledger's own order is the one that
/// means something.</para>
/// </summary>
public sealed record IncidentPage(
    [property: JsonPropertyName("incidents")]   IReadOnlyList<Incident> Incidents,
    [property: JsonPropertyName("next_cursor")] string?                 NextCursor,
    [property: JsonIgnore]                      JsonElement             Raw
)
{
    public int Count => Incidents.Count;
}
