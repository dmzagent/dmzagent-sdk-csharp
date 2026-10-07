using System.Collections.Generic;

namespace DMZAgent.Sdk;

/// <summary>
/// Canonical event-kind constants per sdk-spec.md §8.6.
///
/// The on-wire <c>kind</c> string is ALWAYS the snake_case form
/// (<c>subject_says</c>, <c>tool_call</c>, …) regardless of how the SDK
/// exposes the enum in .NET. C# consumers prefer named constants over
/// magic strings; use <see cref="All"/> when you need to iterate or
/// validate.
/// </summary>
public static class EventKinds
{
    public const string SubjectSays = "subject_says";
    public const string ToolCall    = "tool_call";
    public const string ToolResult  = "tool_result";
    public const string Observation = "observation";

    /// <summary>
    /// Every accepted <c>kind</c> value, in spec order. The wire enum is
    /// closed; the server rejects anything outside this list with HTTP
    /// 400, and so does <see cref="DMZAgentClient.EmitEventAsync"/>
    /// before the request leaves the process.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        SubjectSays,
        ToolCall,
        ToolResult,
        Observation,
    };

    /// <summary>
    /// Valid subject_type values per sdk-spec.md §5.1.
    /// </summary>
    public static readonly HashSet<string> ValidSubjectTypes = new(StringComparer.Ordinal)
    {
        "chat", "sensor", "lead", "ticket", "journey",
    };
}

/// <summary>
/// The three phases of an agent-mode step (sdk-spec.md §1.9, §8.6).
///
/// <para>Not event kinds: a step goes to its own endpoint
/// (<c>POST /v1/agent-stream/step</c>), and <see cref="EventKinds.All"/>
/// is unchanged by agent mode.</para>
/// </summary>
public static class StepPhases
{
    public const string Intent = "intent";
    public const string Call   = "call";
    public const string Result = "result";

    /// <summary>
    /// Every phase, in spec order. <see cref="DMZAgentClient.AgentStepAsync"/>
    /// refuses anything else before the request leaves the process.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[] { Intent, Call, Result };
}

/// <summary>
/// The five directives a step can be answered with (sdk-spec.md §1.9, §8.6).
///
/// <para>The list is what this build knows, not what a server may send:
/// a server can add a directive (Appendix B), and
/// <see cref="StepResult.Directive"/> then carries the raw word while
/// <see cref="StepResult.Runs"/> reads it as <see cref="Block"/>.</para>
/// </summary>
public static class Directives
{
    public const string Proceed  = "proceed";
    public const string Warn     = "warn";
    public const string Hold     = "hold";
    public const string Block    = "block";
    public const string Shutdown = "shutdown";

    /// <summary>Every directive, in spec order.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Proceed, Warn, Hold, Block, Shutdown };

    /// <summary>
    /// Whether a call answered with <paramref name="directive"/> may run:
    /// <c>true</c> exactly for <see cref="Proceed"/> and <see cref="Warn"/>.
    /// </summary>
    /// <remarks>
    /// Written as an allow-list on purpose. A deny-list of the three that
    /// stop a call would let a word this build has never seen through, and
    /// an unknown word from the governor is not a yes (§1.9).
    /// </remarks>
    public static bool Runs(string? directive)
        => directive is Proceed or Warn;
}
