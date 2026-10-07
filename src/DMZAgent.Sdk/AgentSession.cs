using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DMZAgent.Sdk;

/// <summary>
/// A handle bound to one agent session (sdk-spec.md §5.23, on the terms of
/// §6). Each method sends one step through
/// <see cref="DMZAgentClient.AgentStepAsync"/> and returns its
/// <see cref="StepResult"/>.
///
/// <para>
/// Constructed via <see cref="DMZAgentClient.AgentSession"/>. Direct
/// construction is not part of the public API.
/// </para>
///
/// <para>
/// The handle holds nothing but its two ids. It does not remember
/// refusals, does not infer <c>attemptOf</c>, and does not track which
/// calls are pending: a harness that knows a call retries an earlier one
/// says so, and the server does not depend on it (§1.9).
/// </para>
///
/// <example>
/// <code>
/// var s = cx.AgentSession("subject:dv:agent-a", "sess_4b1e");
/// await s.IntentAsync("Add a trace id to every request.", paths: new[] { "src/obs/" });
///
/// var step = await s.CallAsync("call_7", "Bash",
///     new Dictionary&lt;string, object?&gt; { ["command"] = "git push origin HEAD" });
/// if (!step.Runs)
/// {
///     // Every call that did not run is reported, whoever refused it.
///     await s.RefusedAsync("call_7", "Bash", refusedBy: "governor", reason: step.Reason);
///     return;
/// }
/// var output = RunTool();
/// await s.ResultAsync("call_7", "Bash", "ok", result: output);
/// </code>
/// </example>
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly DMZAgentClient _client;

    internal AgentSession(DMZAgentClient client, string agentSubjectId, string interactionId)
    {
        _client        = client ?? throw new ArgumentNullException(nameof(client));
        AgentSubjectId = agentSubjectId;
        InteractionId  = interactionId;
    }

    /// <summary>The agent acting in this session.</summary>
    public string AgentSubjectId { get; }

    /// <summary>The session. Caller-assigned and stable for its life.</summary>
    public string InteractionId { get; }

    /// <summary>
    /// What the agent says it will do and touch (<c>phase: intent</c>).
    /// </summary>
    public Task<StepResult> IntentAsync(
        string                 text,
        IReadOnlyList<string>? paths             = null,
        IReadOnlyList<string>? tools             = null,
        string?                idempotencyKey    = null,
        CancellationToken      cancellationToken = default)
    {
        var intent = new Dictionary<string, object?> { ["text"] = text };
        if (paths is not null) intent["paths"] = paths;
        if (tools is not null) intent["tools"] = tools;

        return _client.AgentStepAsync(
            AgentSubjectId, InteractionId, StepPhases.Intent,
            intent:            intent,
            idempotencyKey:    idempotencyKey,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A call the agent is about to make (<c>phase: call</c>). Send it
    /// <em>before</em> the tool runs, and run the tool only when the
    /// result's <see cref="StepResult.Runs"/> is <c>true</c>.
    /// </summary>
    public Task<StepResult> CallAsync(
        string                                callId,
        string                                tool,
        IReadOnlyDictionary<string, object?>? args              = null,
        string?                               attemptOf         = null,
        string?                               idempotencyKey    = null,
        CancellationToken                     cancellationToken = default)
        => _client.AgentStepAsync(
            AgentSubjectId, InteractionId, StepPhases.Call,
            callId:            callId,
            tool:              tool,
            args:              args,
            attemptOf:         attemptOf,
            idempotencyKey:    idempotencyKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// What a call that ran came to (<c>phase: result</c>,
    /// <paramref name="status"/> <c>ok</c> or <c>error</c>). A call that did
    /// not run is reported with <see cref="RefusedAsync"/>.
    /// </summary>
    /// <exception cref="DMZAgentValidationException">
    /// When <paramref name="status"/> is <c>refused</c>: a refusal has to
    /// name who refused it, which this method has no parameter for.
    /// </exception>
    public Task<StepResult> ResultAsync(
        string            callId,
        string            tool,
        string            status,
        object?           result            = null,
        string?           reason            = null,
        string?           idempotencyKey    = null,
        CancellationToken cancellationToken = default)
    {
        if (status == "refused")
        {
            throw new DMZAgentValidationException(
                "status refused is reported with RefusedAsync, which takes refusedBy");
        }
        return _client.AgentStepAsync(
            AgentSubjectId, InteractionId, StepPhases.Result,
            callId:            callId,
            tool:              tool,
            status:            status,
            result:            result,
            reason:            reason,
            idempotencyKey:    idempotencyKey,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A call that did not run (<c>phase: result</c>, <c>status:
    /// refused</c>), naming who refused it: <c>governor</c>,
    /// <c>harness</c> or <c>host</c>.
    /// </summary>
    /// <remarks>
    /// Send one for every call that did not run, whoever refused it. The
    /// refusal is the evidence a later attempt is judged against.
    /// </remarks>
    public Task<StepResult> RefusedAsync(
        string            callId,
        string            tool,
        string            refusedBy,
        string?           reason            = null,
        string?           attemptOf         = null,
        string?           idempotencyKey    = null,
        CancellationToken cancellationToken = default)
        => _client.AgentStepAsync(
            AgentSubjectId, InteractionId, StepPhases.Result,
            callId:            callId,
            tool:              tool,
            status:            "refused",
            refusedBy:         refusedBy,
            reason:            reason,
            attemptOf:         attemptOf,
            idempotencyKey:    idempotencyKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// No-op. The handle owns no resource and keeps no state to release; it
    /// implements the client's resource idiom so it can sit in the same
    /// <c>using</c> as a <see cref="Conversation"/> (§6.3).
    /// </summary>
    public void Dispose()
    {
    }
}
