using System;
using System.Collections.Generic;

namespace Concordex.Sdk.Concordia;

/// <summary>
/// Base class for every error raised by the Concordia MCP client.
/// Per sdk-spec.md §8 the C# binding uses the <c>…Exception</c>
/// convention; the canonical wire <c>error_id</c> is exposed on
/// <see cref="ErrorId"/>.
///
/// Every exception exposes <see cref="Code"/> (the JSON-RPC error
/// code, or null for transport-level failures), <see cref="ErrorId"/>
/// (the Concordex slug, e.g. <c>auth_expired</c>), and
/// <see cref="ErrorData"/> (the raw <c>data</c> object from the envelope).
/// </summary>
public class ConcordiaException : Exception
{
    /// <summary>The JSON-RPC error code from the envelope's
    /// <c>error.code</c> field, or <c>null</c> for transport-level
    /// failures.</summary>
    public int? Code { get; }

    /// <summary>The Concordex slug from <c>error.data.error_id</c>
    /// (e.g. <c>auth_expired</c>, <c>tenant_quota_exceeded</c>).
    /// <c>null</c> when the server didn't supply one.</summary>
    public string? ErrorId { get; }

    /// <summary>The raw <c>data</c> object from the JSON-RPC error
    /// envelope, or an empty dictionary when absent. Named
    /// <c>ErrorData</c> rather than <c>Data</c> because the base
    /// <c>Exception.Data</c> property is reserved for arbitrary
    /// key/value tags.</summary>
    public IReadOnlyDictionary<string, object?> ErrorData { get; }

    public ConcordiaException(
        string message,
        int? code = null,
        string? errorId = null,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code      = code;
        ErrorId   = errorId;
        ErrorData = data ?? new Dictionary<string, object?>();
    }
}

/// <summary>Transport / envelope failure — non-200 HTTP, timeout,
/// JSON parse error. Caller may safely retry transient cases with
/// backoff.</summary>
public class ConcordiaProtocolException : ConcordiaException
{
    public ConcordiaProtocolException(
        string message,
        int? code = null,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code, errorId: null, data, innerException) { }
}

/// <summary>API key rejected — <c>auth_expired</c> (-32001).</summary>
public class ConcordiaAuthException : ConcordiaException
{
    public ConcordiaAuthException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32001, errorId: "auth_expired", data, innerException) { }
}

/// <summary>Workspace hit a billing cap —
/// <c>tenant_quota_exceeded</c> (-32002).</summary>
public class ConcordiaQuotaExceededException : ConcordiaException
{
    public ConcordiaQuotaExceededException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32002, errorId: "tenant_quota_exceeded", data, innerException) { }
}

/// <summary>CB substrate degraded —
/// <c>policy_engine_unavailable</c> (-32003).
/// Per spec §8 the customer agent should default to <c>review</c>
/// and defer the action when this fires.</summary>
public class ConcordiaPolicyEngineUnavailableException : ConcordiaException
{
    public ConcordiaPolicyEngineUnavailableException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32003, errorId: "policy_engine_unavailable", data, innerException) { }
}

/// <summary>canon_filter referenced a Canon not in the workspace's
/// corpus — <c>canon_not_installed</c> (-32004).</summary>
public class ConcordiaCanonNotInstalledException : ConcordiaException
{
    public ConcordiaCanonNotInstalledException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32004, errorId: "canon_not_installed", data, innerException) { }
}

/// <summary>GetSubjectSoul against an unknown subject —
/// <c>subject_not_found</c> (-32005). Agent should treat as a new
/// subject with no accumulated tags.</summary>
public class ConcordiaSubjectNotFoundException : ConcordiaException
{
    public ConcordiaSubjectNotFoundException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32005, errorId: "subject_not_found", data, innerException) { }
}

/// <summary>Per-tool failure rate triggered a brake —
/// <c>circuit_open</c> (-32006). Retry with exponential backoff.</summary>
public class ConcordiaCircuitOpenException : ConcordiaException
{
    public ConcordiaCircuitOpenException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32006, errorId: "circuit_open", data, innerException) { }
}

/// <summary>Role gate refusal — <c>permission_denied</c>
/// (-32007).</summary>
public class ConcordiaPermissionDeniedException : ConcordiaException
{
    public ConcordiaPermissionDeniedException(
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        Exception? innerException = null)
        : base(message, code: -32007, errorId: "permission_denied", data, innerException) { }
}
