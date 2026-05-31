using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Concordex.Sdk;

/// <summary>
/// Stateful handle for a single interaction (sdk-spec.md §6).
///
/// Caches the server-assigned <c>interaction_id</c> from the first
/// emit response and re-sends it on every subsequent call so the
/// server stitches events together. The participant roster is the
/// <c>subjects</c> array sent on every event.
///
/// <para>
/// Constructed via <see cref="ConcordexClient.Conversation"/>. Direct
/// construction is not part of the public API.
/// </para>
///
/// <example>
/// <code>
/// using var conv = cx.Conversation(participants: new[]
/// {
///     new Dictionary&lt;string, object?&gt; {["subject_id"] = "user:ws:bot",  ["role"] = "agent",    ["kind"] = "agent"},
///     new Dictionary&lt;string, object?&gt; {["subject_id"] = "user:ws:cust", ["role"] = "customer", ["kind"] = "human"},
/// });
/// await conv.SaysAsync("user:ws:cust", "I want a refund.");
/// </code>
/// </example>
/// </summary>
public sealed class Conversation : IDisposable
{
    // Roles the SDK treats as "this participant is an agent for the
    // wire protocol's agent_subject_id requirement." Mirrors the
    // Python reference impl — see sdks/python/concordex/conversation.py.
    private static readonly HashSet<string> AgentRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "service", "system",
    };

    private readonly ConcordexClient                                       _client;
    private readonly string                                                _agentSubjectId;
    private readonly string                                                _interactionKind;
    private readonly IReadOnlyDictionary<string, object?>?                 _metadata;
    private readonly List<Dictionary<string, object?>>                     _subjects;

    private string? _interactionId;
    private bool    _disposed;

    internal Conversation(
        ConcordexClient                                     client,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> participants,
        string?                                             agentSubjectId,
        string                                              interactionKind,
        IReadOnlyDictionary<string, object?>?               metadata)
    {
        if (participants is null || participants.Count == 0)
        {
            throw new ConcordexValidationException("participants must be a non-empty list");
        }

        var roster = new List<Dictionary<string, object?>>(participants.Count);
        for (int i = 0; i < participants.Count; i++)
        {
            var p = participants[i];
            if (p is null || !p.TryGetValue("subject_id", out var sidObj) || sidObj is not string sid || string.IsNullOrWhiteSpace(sid))
            {
                throw new ConcordexValidationException($"participants[{i}] missing subject_id");
            }
            var entry = new Dictionary<string, object?>
            {
                ["subject_id"] = sid.Trim(),
                ["role"]       = p.TryGetValue("role", out var r) && r is string rs && !string.IsNullOrEmpty(rs) ? rs : "other",
                ["kind"]       = p.TryGetValue("kind", out var k) && k is string ks && !string.IsNullOrEmpty(ks) ? ks : "other",
            };
            if (p.TryGetValue("metadata", out var md)) entry["metadata"] = md;
            roster.Add(entry);
        }

        if (string.IsNullOrEmpty(agentSubjectId))
        {
            agentSubjectId = roster
                .FirstOrDefault(p => p.TryGetValue("role", out var rr) && rr is string rrs && AgentRoles.Contains(rrs))
                ?["subject_id"] as string
                ?? (string)roster[0]["subject_id"]!;
        }

        _client          = client;
        _agentSubjectId  = agentSubjectId!;
        _interactionKind = interactionKind;
        _metadata        = metadata;
        _subjects        = roster;
    }

    // =================================================================== //
    // Properties                                                          //
    // =================================================================== //

    /// <summary>Server-assigned id, available after the first event.</summary>
    public string? InteractionId => _interactionId;

    /// <summary>Snapshot of the current roster.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Subjects =>
        _subjects.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(s)).ToList();

    /// <summary>The wire-level <c>agent_subject_id</c> used on every event.</summary>
    public string AgentSubjectId => _agentSubjectId;

    // =================================================================== //
    // Roster                                                              //
    // =================================================================== //

    /// <summary>
    /// Add (or refresh) a participant. Idempotent on <paramref name="subjectId"/> —
    /// re-adding the same id refreshes role/kind/metadata.
    /// </summary>
    public void AddSubject(
        string                                subjectId,
        string?                               role     = "other",
        string?                               kind     = "other",
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new ConcordexValidationException("subjectId is required");
        }

        var newEntry = new Dictionary<string, object?>
        {
            ["subject_id"] = subjectId,
            ["role"]       = role ?? "other",
            ["kind"]       = kind ?? "other",
        };
        if (metadata is not null) newEntry["metadata"] = metadata;

        for (int i = 0; i < _subjects.Count; i++)
        {
            if ((string)_subjects[i]["subject_id"]! == subjectId)
            {
                _subjects[i] = newEntry;
                return;
            }
        }
        _subjects.Add(newEntry);
    }

    // =================================================================== //
    // Event emission                                                      //
    // =================================================================== //

    /// <summary>A subject in the conversation spoke.</summary>
    public async Task<EmitResult> SaysAsync(
        string                                subjectId,
        string                                text,
        IReadOnlyDictionary<string, object?>? payloadExtra = null,
        CancellationToken                     cancellationToken = default)
    {
        var r = await _client.SubjectSaysAsync(
            subjectId:        subjectId,
            text:              text,
            agentSubjectId:   _agentSubjectId,
            interactionId:    _interactionId,
            subjects:         RosterAsReadonly(),
            payloadExtra:     payloadExtra,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CaptureInteractionId(r);
        return r;
    }

    /// <summary>A subject (typically the agent) invoked a tool.</summary>
    public async Task<EmitResult> ToolCallAsync(
        string                                subjectId,
        string                                tool,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken                     cancellationToken = default)
    {
        var r = await _client.ToolCallAsync(
            subjectId:         subjectId,
            tool:              tool,
            args:              args,
            interactionId:     _interactionId,
            subjects:          RosterAsReadonly(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CaptureInteractionId(r);
        return r;
    }

    /// <summary>A tool returned a result. Pair with the prior <see cref="ToolCallAsync"/>.</summary>
    public async Task<EmitResult> ToolResultAsync(
        string            subjectId,
        string            tool,
        object?           result,
        CancellationToken cancellationToken = default)
    {
        var r = await _client.ToolResultAsync(
            subjectId:         subjectId,
            tool:              tool,
            result:            result,
            interactionId:     _interactionId,
            subjects:          RosterAsReadonly(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CaptureInteractionId(r);
        return r;
    }

    /// <summary>Structured observation — video keyframe, IoT event, anything not utterance-shaped.</summary>
    public async Task<EmitResult> ObservationAsync(
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken                    cancellationToken = default)
    {
        var r = await _client.ObservationAsync(
            agentSubjectId:    _agentSubjectId,
            subjects:          RosterAsReadonly(),
            payload:           payload,
            interactionId:     _interactionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CaptureInteractionId(r);
        return r;
    }

    // =================================================================== //
    // Circuit breaker                                                      //
    // =================================================================== //

    /// <summary>CB check scoped to a specific subject in the conversation.</summary>
    public Task<CheckResult> CheckAsync(string subjectId, CancellationToken cancellationToken = default)
        => _client.CheckAsync(subjectId: subjectId, cancellationToken: cancellationToken);

    /// <summary>Resource-scoped CB check for a subject in the conversation.</summary>
    public Task<GuardScope> GuardAsync(
        string            subjectId,
        bool              raiseOnOpen = false,
        CancellationToken cancellationToken = default)
        => _client.GuardAsync(subjectId: subjectId, raiseOnOpen: raiseOnOpen, cancellationToken: cancellationToken);

    // =================================================================== //
    // Lifecycle                                                            //
    // =================================================================== //

    /// <summary>Idempotent close. Currently a no-op (sdk-spec.md §6.3);
    /// v1.0 will emit an <c>end_interaction</c> event.</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // future: emit an end_interaction event here.
    }

    // =================================================================== //
    // Internal                                                            //
    // =================================================================== //

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> RosterAsReadonly()
    {
        // Snapshot the roster so concurrent AddSubject() doesn't mutate
        // an in-flight payload.
        return _subjects
            .Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(s))
            .ToList();
    }

    private void CaptureInteractionId(EmitResult r)
    {
        if (string.IsNullOrEmpty(_interactionId) && !string.IsNullOrEmpty(r.InteractionId))
        {
            _interactionId = r.InteractionId;
        }
    }
}
