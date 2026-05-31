using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Concordex.Sdk;

/// <summary>
/// The Concordex client — primary entry point for the .NET SDK.
///
/// Per sdk-spec.md §4 / §8.1 the C# binding renames the canonical
/// <c>Concordex</c> class to <c>ConcordexClient</c> (the language
/// reserves bare type names for value-bearing entities; service classes
/// take a <c>Client</c> suffix).
///
/// <para>
/// Thread-safe and reusable. Built on a single
/// <see cref="System.Net.Http.HttpClient"/>; share one instance across
/// the application lifetime, dispose at shutdown.
/// </para>
///
/// <example>
/// <code>
/// using var cx = new ConcordexClient("ck_live_…");
///
/// await cx.SubjectSaysAsync(
///     agentSubjectId: "user:ws:bot",
///     subjectId:      "user:ws:cust",
///     text:           "I want a refund.");
///
/// var g = await cx.CheckAsync(subjectId: "user:ws:bot");
/// if (!g.Allow) return Refuse(g.Reason);
/// </code>
/// </example>
/// </summary>
public sealed class ConcordexClient : IDisposable
{
    /// <summary>Public default per sdk-spec.md §1.1.</summary>
    public const string DefaultBaseUrl = "https://api.concordex.dev";

    /// <summary>Public default per sdk-spec.md §1.5.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Spec version this build targets. Bumped via Directory.Build.props.</summary>
    public const string SpecVersion = "0.5.0";

    /// <summary>The User-Agent set on every request per sdk-spec.md §1.4.</summary>
    public static readonly string DefaultUserAgent = $"concordex-csharp/{SpecVersion}";

    // JSON options used for every wire body. Spec-conformance lives or
    // dies here: the golden-envelopes corpus compares POST bodies after
    // JSON normalization, but we want to ship clean output to humans
    // reading logs too. Default writer indents off, no extra whitespace,
    // no trailing newline. Property naming follows the explicit
    // [JsonPropertyName] attributes on the wire-body record types below
    // (no global naming policy — every field is spelled out).
    private static readonly JsonSerializerOptions WireJson = new()
    {
        WriteIndented              = false,
        DefaultIgnoreCondition     = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder                    = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // JSON options for parsing server responses. Tolerant of unknown
    // fields (forward-compat per spec §B), accepts snake_case.
    internal static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly bool       _ownsHttpClient;
    private readonly string     _baseUrl;
    private          bool       _disposed;

    /// <summary>
    /// Construct a client with the default HTTP transport.
    /// </summary>
    /// <param name="apiKey">Workspace API key, must start with <c>ck_</c>.</param>
    /// <param name="baseUrl">Override for staging / self-hosted (default <see cref="DefaultBaseUrl"/>).</param>
    /// <param name="timeout">Per-request timeout (default 10 s).</param>
    /// <param name="userAgent">Override User-Agent (default <c>concordex-csharp/&lt;spec-version&gt;</c>).</param>
    /// <exception cref="ConcordexValidationException">When <paramref name="apiKey"/> is empty or doesn't start with <c>ck_</c>.</exception>
    public ConcordexClient(
        string   apiKey,
        string?  baseUrl   = null,
        TimeSpan? timeout  = null,
        string?  userAgent = null)
        : this(apiKey, handler: null, baseUrl: baseUrl, timeout: timeout, userAgent: userAgent)
    {
    }

    /// <summary>
    /// Construct a client with a caller-supplied
    /// <see cref="HttpMessageHandler"/> — the seam the contract-test
    /// harness uses to drive a stub transport (sdk-spec.md §10 / runner
    /// step 2).
    /// </summary>
    public ConcordexClient(
        string               apiKey,
        HttpMessageHandler?  handler,
        string?              baseUrl   = null,
        TimeSpan?            timeout   = null,
        string?              userAgent = null)
    {
        // Validation per sdk-spec.md §1.2 — empty key and bad-prefix key
        // are both bounced at construction, before any request goes out.
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("ck_", StringComparison.Ordinal))
        {
            throw new ConcordexValidationException(
                "api_key must start with 'ck_' — get one from your tenant_admin");
        }

        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

        if (handler is null)
        {
            _http = new HttpClient();
            _ownsHttpClient = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
            _ownsHttpClient = true;
        }

        _http.Timeout = timeout ?? DefaultTimeout;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? DefaultUserAgent);
    }

    /// <summary>Construct a client that reuses a caller-managed
    /// <see cref="HttpClient"/>. The SDK will NOT dispose it.</summary>
    public ConcordexClient(
        string      apiKey,
        HttpClient  httpClient,
        string?     baseUrl   = null,
        string?     userAgent = null)
    {
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("ck_", StringComparison.Ordinal))
        {
            throw new ConcordexValidationException(
                "api_key must start with 'ck_' — get one from your tenant_admin");
        }

        _baseUrl        = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http           = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = false;

        // We don't mutate caller-supplied defaults beyond ensuring the
        // auth + UA headers are present for our requests. To keep the
        // shared client undisturbed, we attach them per-request below.
    }

    // =================================================================== //
    // Event emission — POST /v1/agent-stream/event                        //
    // =================================================================== //

    /// <summary>
    /// Low-level event emitter (sdk-spec.md §5.1). Every higher-level
    /// helper lands here.
    /// </summary>
    /// <exception cref="ConcordexValidationException">When <paramref name="kind"/> is not in <see cref="EventKinds.All"/>.</exception>
    public Task<EmitResult> EmitEventAsync(
        string                                       kind,
        string                                       agentSubjectId,
        IReadOnlyDictionary<string, object?>?        payload          = null,
        string?                                      interactionId    = null,
        string?                                      interactionKind  = "chat_session",
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        string?                                      speakerSubjectId = null,
        string?                                      speakerRole      = null,
        string?                                      occurredAt       = null,
        IReadOnlyDictionary<string, object?>?        metadata         = null,
        CancellationToken                            cancellationToken = default)
    {
        if (!EventKinds.All.Contains(kind))
        {
            throw new ConcordexValidationException(
                $"kind must be one of [{string.Join(", ", EventKinds.All)}], got '{kind}'");
        }
        if (string.IsNullOrEmpty(agentSubjectId))
        {
            throw new ConcordexValidationException("agent_subject_id is required");
        }

        var body = new Dictionary<string, object?>
        {
            ["kind"]             = kind,
            ["agent_subject_id"] = agentSubjectId,
            ["payload"]          = payload ?? new Dictionary<string, object?>(),
        };
        if (!string.IsNullOrEmpty(interactionId))    body["interaction_id"]   = interactionId;
        if (!string.IsNullOrEmpty(interactionKind))  body["interaction_kind"] = interactionKind;
        if (subjects is { Count: > 0 })              body["subjects"]         = subjects;
        if (!string.IsNullOrEmpty(speakerSubjectId)) body["speaker_subject_id"] = speakerSubjectId;
        if (!string.IsNullOrEmpty(speakerRole))      body["speaker_role"]     = speakerRole;
        if (!string.IsNullOrEmpty(occurredAt))       body["occurred_at"]      = occurredAt;
        if (metadata is { Count: > 0 })              body["metadata"]         = metadata;

        return PostEmitAsync("/v1/agent-stream/event", body, cancellationToken);
    }

    /// <summary>
    /// Convenience wrapper for <c>kind = "subject_says"</c> (sdk-spec.md
    /// §5.2). The SDK puts the speaker on the wire as
    /// <c>speaker_subject_id</c>.
    /// </summary>
    public Task<EmitResult> SubjectSaysAsync(
        string                                       subjectId,
        string                                       text,
        string                                       agentSubjectId,
        string?                                      interactionId    = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        IReadOnlyDictionary<string, object?>?        payloadExtra     = null,
        CancellationToken                            cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentSubjectId))
        {
            throw new ConcordexValidationException(
                "agentSubjectId is required — every event is grounded against an agent identity " +
                "(use ConcordexClient.Conversation(...) to avoid passing this on every call)");
        }

        var payload = new Dictionary<string, object?> { ["text"] = text };
        if (payloadExtra is not null)
        {
            foreach (var kv in payloadExtra) payload[kv.Key] = kv.Value;
        }

        return EmitEventAsync(
            kind:             EventKinds.SubjectSays,
            agentSubjectId:   agentSubjectId,
            payload:          payload,
            interactionId:    interactionId,
            subjects:         subjects,
            speakerSubjectId: subjectId,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Convenience wrapper for <c>kind = "tool_call"</c> (sdk-spec.md
    /// §5.3). Use BEFORE the tool runs — this emits intent. The result
    /// lands separately via <see cref="ToolResultAsync"/>.
    /// </summary>
    public Task<EmitResult> ToolCallAsync(
        string                                       subjectId,
        string                                       tool,
        IReadOnlyDictionary<string, object?>?        args             = null,
        string?                                      interactionId    = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        CancellationToken                            cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tool"] = tool,
            ["args"] = args ?? new Dictionary<string, object?>(),
        };
        return EmitEventAsync(
            kind:             EventKinds.ToolCall,
            agentSubjectId:   subjectId,
            payload:          payload,
            interactionId:    interactionId,
            subjects:         subjects,
            speakerSubjectId: subjectId,
            speakerRole:      "agent",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Convenience wrapper for <c>kind = "tool_result"</c> (sdk-spec.md
    /// §5.4). Pair with the prior <see cref="ToolCallAsync"/>.
    /// </summary>
    /// <param name="result">Free-form tool return value; JSON-encoded by the SDK.</param>
    public Task<EmitResult> ToolResultAsync(
        string                                       subjectId,
        string                                       tool,
        object?                                      result,
        string?                                      interactionId    = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        CancellationToken                            cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tool"]   = tool,
            ["result"] = result,
        };
        return EmitEventAsync(
            kind:             EventKinds.ToolResult,
            agentSubjectId:   subjectId,
            payload:          payload,
            interactionId:    interactionId,
            subjects:         subjects,
            speakerSubjectId: subjectId,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Convenience wrapper for <c>kind = "observation"</c> (sdk-spec.md
    /// §5.5). Use for structured events that don't fit a speech-bubble
    /// shape — video keyframes, IoT, sensor readings.
    /// </summary>
    public Task<EmitResult> ObservationAsync(
        string                                       agentSubjectId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> subjects,
        IReadOnlyDictionary<string, object?>         payload,
        string?                                      interactionId    = null,
        CancellationToken                            cancellationToken = default)
    {
        return EmitEventAsync(
            kind:           EventKinds.Observation,
            agentSubjectId: agentSubjectId,
            payload:        payload,
            interactionId:  interactionId,
            subjects:       subjects,
            cancellationToken: cancellationToken);
    }

    // =================================================================== //
    // Circuit breaker — POST /v1/cb/check                                 //
    // =================================================================== //

    /// <summary>
    /// Synchronous circuit-breaker check (sdk-spec.md §5.6). Pass EXACTLY
    /// ONE of <paramref name="subjectId"/> or <paramref name="interactionId"/>.
    /// </summary>
    /// <exception cref="ConcordexValidationException">When neither or both arguments are set.</exception>
    public async Task<CheckResult> CheckAsync(
        string?           subjectId      = null,
        string?           interactionId  = null,
        CancellationToken cancellationToken = default)
    {
        var hasSubject     = !string.IsNullOrEmpty(subjectId);
        var hasInteraction = !string.IsNullOrEmpty(interactionId);
        if (hasSubject == hasInteraction)
        {
            throw new ConcordexValidationException("pass exactly one of subjectId or interactionId");
        }

        var scope    = hasSubject ? "subject" : "interaction";
        var scopeRef = (hasSubject ? subjectId : interactionId)!;

        var body = new Dictionary<string, object?>
        {
            ["scope"]     = scope,
            ["scope_ref"] = scopeRef,
        };

        var raw = await PostJsonAsync("/v1/cb/check", body, cancellationToken).ConfigureAwait(false);
        return ParseCheckResult(raw);
    }

    /// <summary>
    /// Resource-scoped wrapper around <see cref="CheckAsync"/>
    /// (sdk-spec.md §5.7). Usable via <c>using var g = await client.GuardAsync(...)</c>;
    /// the disposable handle exposes <see cref="GuardScope.Result"/>.
    ///
    /// When <paramref name="raiseOnOpen"/> is <c>true</c> and the check
    /// returned <c>allow == false</c>, this raises
    /// <see cref="CircuitBreakerOpenException"/> instead of returning.
    /// </summary>
    public async Task<GuardScope> GuardAsync(
        string?           subjectId      = null,
        string?           interactionId  = null,
        bool              raiseOnOpen    = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CheckAsync(subjectId, interactionId, cancellationToken).ConfigureAwait(false);
        if (raiseOnOpen && !result.Allow)
        {
            throw new CircuitBreakerOpenException(
                message:       $"circuit breaker open: {result.Reason}",
                reason:        result.Reason,
                firedPolicies: result.FiredPolicies,
                anchor:        result.Anchor,
                scopeRef:      subjectId ?? interactionId ?? string.Empty);
        }
        return new GuardScope(result);
    }

    // =================================================================== //
    // Conversation factory                                                //
    // =================================================================== //

    /// <summary>
    /// Open a Conversation handle bound to this client (sdk-spec.md §5.8 / §6).
    ///
    /// The C# naming map (§8.2) routes the canonical <c>conversation</c>
    /// method to the PascalCase <c>Conversation</c> name; the result
    /// type is also <see cref="Conversation"/>. Resolving the overload
    /// against the constructor is unambiguous because the type is
    /// non-constructible from user code — <see cref="Conversation"/> has
    /// no public constructor.
    /// </summary>
    public Conversation Conversation(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> participants,
        string?                                             agentSubjectId = null,
        string                                              kind           = "chat_session",
        IReadOnlyDictionary<string, object?>?               metadata       = null)
    {
        return new Conversation(this, participants, agentSubjectId, kind, metadata);
    }

    // =================================================================== //
    // Lifecycle                                                            //
    // =================================================================== //

    /// <summary>Idempotent close. Calling more than once is a no-op.</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
    }

    // =================================================================== //
    // Internal — HTTP plumbing                                            //
    // =================================================================== //

    internal async Task<EmitResult> PostEmitAsync(
        string                          path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken               cancellationToken)
    {
        var raw = await PostJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
        return ParseEmitResult(raw);
    }

    internal async Task<JsonElement> PostJsonAsync(
        string                          path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken               cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConcordexClient));

        var url     = $"{_baseUrl}{path}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, WireJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // When constructed with a caller-supplied HttpClient we don't
        // own the default headers; ensure our auth + UA still attach
        // per-request (DefaultRequestHeaders on a shared client would
        // mutate it for unrelated callers).
        if (!_ownsHttpClient)
        {
            if (_http.DefaultRequestHeaders.Authorization is null)
            {
                // Caller didn't pre-wire it; in this overload they're
                // sharing a generic HttpClient — but we already set our
                // own auth header via the constructor path that takes a
                // handler, not this one. The shared-HttpClient path is
                // documented as caller-responsibility for auth.
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConcordexServerException($"timeout calling {path}: {ex.Message}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ConcordexServerException($"network error calling {path}: {ex.Message}", innerException: ex);
        }

        using (response)
        {
            return await HandleResponseAsync(response, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> HandleResponseAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var raw    = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Parse body once. Server-error bodies may be JSON or plain
        // text; preserve whichever shape arrived for the exception's
        // .Body property.
        JsonElement? jsonBody = null;
        object?      bodyForException;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(raw) ? "null" : raw);
            jsonBody         = doc.RootElement.Clone();
            bodyForException = jsonBody;
        }
        catch (JsonException)
        {
            bodyForException = raw;
        }

        if (status is >= 200 and < 300)
        {
            return jsonBody ?? default;
        }

        var detail = ExtractDetail(jsonBody, raw);

        throw status switch
        {
            400 => new ConcordexValidationException(
                $"server rejected request to {path}: {detail}", status, bodyForException),
            401 => new ConcordexAuthException(
                "invalid or revoked API key", status, bodyForException),
            403 => new ConcordexPermissionException(
                "API key lacks required scope for this operation", status, bodyForException),
            >= 500 => new ConcordexServerException(
                $"server error from {path} ({status}): {detail}", status, bodyForException),
            _ => new ConcordexException(
                $"unexpected status {status} from {path}", status, bodyForException),
        };
    }

    private static string ExtractDetail(JsonElement? jsonBody, string raw)
    {
        if (jsonBody is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("detail", out var d)
            && d.ValueKind == JsonValueKind.String)
        {
            return d.GetString() ?? raw;
        }
        return raw;
    }

    // =================================================================== //
    // Result parsing                                                       //
    // =================================================================== //

    internal static EmitResult ParseEmitResult(JsonElement raw)
    {
        string?                interactionId  = OptString(raw, "interaction_id");
        IReadOnlyList<string>? subjectsList   = OptStringArray(raw, "subjects");
        bool                   queued         = OptBool(raw, "queued") ?? false;
        string?                frameId        = OptString(raw, "frame_id");
        string?                subjectId      = OptString(raw, "subject_id");
        string?                outcome        = OptString(raw, "outcome");
        string?                triageDecision = OptString(raw, "triage_decision");
        IReadOnlyList<string>? tagsFired      = OptStringArray(raw, "tags_fired");
        IReadOnlyList<string>? scoredByCanons = OptStringArray(raw, "scored_by_canons");
        long?                  soulVersion    = OptLong(raw, "soul_version");
        long?                  ledgerIndex    = OptLong(raw, "ledger_index");
        string?                followMyData   = OptString(raw, "follow_my_data");

        return new EmitResult(
            InteractionId:  interactionId ?? string.Empty,
            Subjects:       subjectsList ?? Array.Empty<string>(),
            Queued:         queued,
            FrameId:        frameId,
            SubjectId:      subjectId,
            Outcome:        outcome,
            TriageDecision: triageDecision,
            TagsFired:      tagsFired,
            ScoredByCanons: scoredByCanons,
            SoulVersion:    soulVersion,
            LedgerIndex:    ledgerIndex,
            FollowMyData:   followMyData,
            Raw:            raw);
    }

    internal static CheckResult ParseCheckResult(JsonElement raw)
    {
        string state          = OptString(raw, "state") ?? "closed";
        bool   allow          = OptBool(raw, "allow") ?? true;
        bool   warning        = OptBool(raw, "warning") ?? false;
        string reason         = OptString(raw, "reason") ?? string.Empty;
        string checkedAt      = OptString(raw, "checked_at") ?? string.Empty;
        double latencyMs      = OptDouble(raw, "latency_ms") ?? 0.0;
        double routeLatencyMs = OptDouble(raw, "route_latency_ms") ?? 0.0;

        var firedPolicies = new List<IReadOnlyDictionary<string, object?>>();
        if (raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("fired_policies", out var fpElement)
            && fpElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in fpElement.EnumerateArray())
            {
                firedPolicies.Add(JsonObjectToDict(entry));
            }
        }

        IReadOnlyDictionary<string, object?>? anchor = null;
        if (raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("anchor", out var anchorElement)
            && anchorElement.ValueKind == JsonValueKind.Object)
        {
            anchor = JsonObjectToDict(anchorElement);
        }

        return new CheckResult(
            State:          state,
            Allow:          allow,
            Warning:        warning,
            Reason:         reason,
            FiredPolicies:  firedPolicies,
            Anchor:         anchor,
            CheckedAt:      checkedAt,
            LatencyMs:      latencyMs,
            RouteLatencyMs: routeLatencyMs,
            Raw:            raw);
    }

    // ---- JsonElement option helpers ---- //

    private static string? OptString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static bool? OptBool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                    => null,
        };
    }

    private static long? OptLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    }

    private static double? OptDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static IReadOnlyList<string>? OptStringArray(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        if (v.ValueKind != JsonValueKind.Array)  return null;
        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? string.Empty);
        }
        return list;
    }

    internal static IReadOnlyDictionary<string, object?> JsonObjectToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>();
        if (obj.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = JsonValueToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonValueToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDouble(),
        JsonValueKind.Object => JsonObjectToDict(v),
        JsonValueKind.Array  => JsonArrayToList(v),
        _                    => null,
    };

    private static List<object?> JsonArrayToList(JsonElement arr)
    {
        var list = new List<object?>();
        foreach (var item in arr.EnumerateArray()) list.Add(JsonValueToObject(item));
        return list;
    }
}

// GuardScope lives in Guard.cs.
