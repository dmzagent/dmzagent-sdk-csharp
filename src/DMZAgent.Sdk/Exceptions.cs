using System;
using System.Collections.Generic;

namespace DMZAgent.Sdk;

/// <summary>
/// Base class for every error raised by the DMZAgent SDK. Per
/// sdk-spec.md §3 / §8.5 the C# binding uses the <c>…Exception</c>
/// convention; the canonical wire name remains <c>DMZAgentError</c>.
///
/// Every exception exposes <see cref="StatusCode"/> and <see cref="Body"/>
/// — the HTTP status the server returned and the parsed response body
/// (as a <see cref="System.Text.Json.JsonElement"/> when JSON, otherwise
/// the raw string). Both may be <c>null</c> for network / timeout cases.
/// </summary>
public class DMZAgentException : Exception
{
    /// <summary>HTTP status code returned by the server, or <c>null</c>
    /// when no response was received (timeout, DNS failure, etc.).</summary>
    public int? StatusCode { get; }

    /// <summary>Parsed response body. <c>JsonElement</c> when the server
    /// returned JSON, <c>string</c> when it returned plain text,
    /// <c>null</c> for network errors.</summary>
    public object? Body { get; }

    public DMZAgentException(string message, int? statusCode = null, object? body = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Body       = body;
    }
}

/// <summary>
/// HTTP 401 — API key missing, invalid, or revoked.
/// Maps to canonical <c>AuthError</c>.
/// </summary>
public class DMZAgentAuthException : DMZAgentException
{
    public DMZAgentAuthException(string message, int? statusCode = null, object? body = null, Exception? innerException = null)
        : base(message, statusCode, body, innerException) { }
}

/// <summary>
/// HTTP 403 — API key valid but lacks the scope required for this operation.
/// Maps to canonical <c>PermissionError</c>.
/// </summary>
public class DMZAgentPermissionException : DMZAgentException
{
    public DMZAgentPermissionException(string message, int? statusCode = null, object? body = null, Exception? innerException = null)
        : base(message, statusCode, body, innerException) { }
}

/// <summary>
/// HTTP 400 or 422 — server rejected the payload. 400 means malformed;
/// 422 means it parsed but failed evaluation. The spec's error taxonomy
/// maps both here, because the caller's remedy is the same: fix the
/// request, do not retry it unchanged. <see cref="DMZAgentException.StatusCode"/>
/// distinguishes them when that matters.
///
/// Also thrown for client-side argument validation (e.g. unknown event
/// kind, both / neither subject_id+interaction_id passed to Check).
/// Maps to canonical <c>ValidationError</c>.
/// </summary>
public class DMZAgentValidationException : DMZAgentException
{
    public DMZAgentValidationException(string message, int? statusCode = null, object? body = null, Exception? innerException = null)
        : base(message, statusCode, body, innerException) { }
}

/// <summary>
/// HTTP 429 — the caller is being rate limited. Maps to canonical
/// <c>RateLimitError</c>.
///
/// <see cref="RetryAfter"/> is seconds taken from the <c>Retry-After</c>
/// response header, or <c>null</c> when the server did not send one.
/// Callers should handle <c>null</c> rather than assume a default: the
/// contract corpus carries a vector for each case precisely because both
/// occur. Only the delta-seconds form is honoured — RFC 9110 also permits
/// an HTTP-date, and a caller handed a wrong number is worse off than one
/// handed <c>null</c>.
/// </summary>
public class DMZAgentRateLimitException : DMZAgentException
{
    /// <summary>Seconds to wait before retrying, or <c>null</c> when the
    /// server sent no usable <c>Retry-After</c> header.</summary>
    public int? RetryAfter { get; }

    public DMZAgentRateLimitException(string message, int? statusCode = null, object? body = null, int? retryAfter = null, Exception? innerException = null)
        : base(message, statusCode, body, innerException)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// HTTP 5xx, timeout, or any underlying transport failure. Safe to
/// retry with backoff. Original cause is preserved via
/// <see cref="Exception.InnerException"/> per sdk-spec.md §3.1.
/// Maps to canonical <c>ServerError</c>.
/// </summary>
public class DMZAgentServerException : DMZAgentException
{
    public DMZAgentServerException(string message, int? statusCode = null, object? body = null, Exception? innerException = null)
        : base(message, statusCode, body, innerException) { }
}

/// <summary>
/// Raised by <see cref="DMZAgentClient.GuardAsync"/> when
/// <c>raiseOnOpen</c> is true and the circuit-breaker check returned
/// <c>allow == false</c>. NOT raised from the HTTP layer — a 200
/// response carrying <c>{"state":"open","allow":false}</c> is what
/// trips this.
///
/// Maps to canonical <c>CBOpenError</c>. Exposes the policy-decision
/// fields documented in sdk-spec.md §3.
/// </summary>
public class CircuitBreakerOpenException : DMZAgentException
{
    /// <summary>Human-readable rationale from the policy decision.</summary>
    public string Reason { get; }

    /// <summary>Policies that fired, each <c>{cb_policy_id, name, action}</c>.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> FiredPolicies { get; }

    /// <summary>Ledger anchor for the transition, or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Anchor { get; }

    /// <summary>The <c>subject_id</c> or <c>interaction_id</c> that was checked.</summary>
    public string ScopeRef { get; }

    public CircuitBreakerOpenException(
        string message,
        string reason,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> firedPolicies,
        IReadOnlyDictionary<string, object?>? anchor,
        string scopeRef)
        : base(message)
    {
        Reason        = reason;
        FiredPolicies = firedPolicies;
        Anchor        = anchor;
        ScopeRef      = scopeRef;
    }
}
