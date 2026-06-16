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
