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

namespace DMZAgent.Sdk;

/// <summary>
/// The DMZAgent client — primary entry point for the .NET SDK.
///
/// Per sdk-spec.md §4 / §8.1 the C# binding renames the canonical
/// <c>DMZAgent</c> class to <c>DMZAgentClient</c> (the language
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
/// using var cx = new DMZAgentClient("ck_live_…");
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
public sealed class DMZAgentClient : IDisposable
{
    /// <summary>Public default per sdk-spec.md §1.1.</summary>
    public const string DefaultBaseUrl = "https://api.dmzagent.com";

    /// <summary>Public default per sdk-spec.md §1.5.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Spec version this build targets. Bumped via Directory.Build.props.</summary>
    // Generated from <DMZAgentSpecVersion> in Directory.Build.props by the
    // GenerateSpecVersion target, so the pin and this constant cannot drift.
    public const string SpecVersion = GeneratedSpecVersion.Value;

    /// <summary>The User-Agent set on every request per sdk-spec.md §1.4.</summary>
    public static readonly string DefaultUserAgent = $"dmzagent-csharp/{SpecVersion}";

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
    private readonly string     _apiKey;
    private readonly string     _userAgent;
    private          bool       _disposed;

    /// <summary>
    /// Construct a client with the default HTTP transport.
    /// </summary>
    /// <param name="apiKey">Workspace API key, must start with <c>ck_</c>.</param>
    /// <param name="baseUrl">Override for staging / self-hosted (default <see cref="DefaultBaseUrl"/>).</param>
    /// <param name="timeout">Per-request timeout (default 10 s).</param>
    /// <param name="userAgent">Override User-Agent (default <c>dmzagent-csharp/&lt;spec-version&gt;</c>).</param>
    /// <exception cref="DMZAgentValidationException">When <paramref name="apiKey"/> is empty or doesn't start with <c>ck_</c>.</exception>
    public DMZAgentClient(
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
    public DMZAgentClient(
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
            throw new DMZAgentValidationException(
                "api_key must start with 'ck_' — get one from your tenant_admin");
        }

        _baseUrl   = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _apiKey    = apiKey;
        _userAgent = userAgent ?? DefaultUserAgent;

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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
    }

    /// <summary>Construct a client that reuses a caller-managed
    /// <see cref="HttpClient"/>. The SDK will NOT dispose it.</summary>
    public DMZAgentClient(
        string      apiKey,
        HttpClient  httpClient,
        string?     baseUrl   = null,
        string?     userAgent = null)
    {
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("ck_", StringComparison.Ordinal))
        {
            throw new DMZAgentValidationException(
                "api_key must start with 'ck_' — get one from your tenant_admin");
        }

        _baseUrl        = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _apiKey         = apiKey;
        _userAgent      = userAgent ?? DefaultUserAgent;
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
    /// <exception cref="DMZAgentValidationException">When <paramref name="kind"/> is not in <see cref="EventKinds.All"/>.</exception>
    public Task<EmitResult> EmitEventAsync(
        string                                       kind,
        string                                       subjectType,
        string                                       agentSubjectId,
        IReadOnlyDictionary<string, object?>?        payload          = null,
        string?                                      interactionId    = null,
        string?                                      interactionKind  = "chat_session",
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        string?                                      speakerSubjectId = null,
        string?                                      speakerRole      = null,
        string?                                      occurredAt       = null,
        IReadOnlyDictionary<string, object?>?        metadata         = null,
        // Caller-generated key making a retry safe (spec §1.8). Supply your
        // own: the SDK never invents one, because a key minted per call
        // deduplicates nothing and one derived from the payload would
        // collapse two genuinely distinct but identical events.
        string?                                      idempotencyKey   = null,
        CancellationToken                            cancellationToken = default)
    {
        if (!EventKinds.All.Contains(kind))
        {
            throw new DMZAgentValidationException(
                $"kind must be one of [{string.Join(", ", EventKinds.All)}], got '{kind}'");
        }
        if (string.IsNullOrEmpty(subjectType) || !EventKinds.ValidSubjectTypes.Contains(subjectType))
        {
            throw new DMZAgentValidationException(
                $"subjectType must be one of [{string.Join(", ", EventKinds.ValidSubjectTypes)}], got '{subjectType}'");
        }
        if (string.IsNullOrEmpty(agentSubjectId))
        {
            throw new DMZAgentValidationException("agent_subject_id is required");
        }

        var body = new Dictionary<string, object?>
        {
            ["kind"]             = kind,
            ["subject_type"]     = subjectType,
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

        return PostEmitAsync("/v1/agent-stream/event", body, idempotencyKey, cancellationToken);
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
        string                                       subjectType,
        string?                                      interactionId    = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects = null,
        IReadOnlyDictionary<string, object?>?        payloadExtra     = null,
        CancellationToken                            cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentSubjectId))
        {
            throw new DMZAgentValidationException(
                "agentSubjectId is required — every event is grounded against an agent identity " +
                "(use DMZAgentClient.Conversation(...) to avoid passing this on every call)");
        }

        var payload = new Dictionary<string, object?> { ["text"] = text };
        if (payloadExtra is not null)
        {
            foreach (var kv in payloadExtra) payload[kv.Key] = kv.Value;
        }

        return EmitEventAsync(
            kind:              EventKinds.SubjectSays,
            subjectType:       subjectType,
            agentSubjectId:    agentSubjectId,
            payload:           payload,
            interactionId:     interactionId,
            subjects:          subjects,
            speakerSubjectId:  subjectId,
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
        string                                       subjectType,
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
            kind:              EventKinds.ToolCall,
            subjectType:       subjectType,
            agentSubjectId:    subjectId,
            payload:           payload,
            interactionId:     interactionId,
            subjects:          subjects,
            speakerSubjectId:  subjectId,
            speakerRole:       "agent",
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
        string                                       subjectType,
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
            kind:              EventKinds.ToolResult,
            subjectType:       subjectType,
            agentSubjectId:    subjectId,
            payload:           payload,
            interactionId:     interactionId,
            subjects:          subjects,
            speakerSubjectId:  subjectId,
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
        string                                       subjectType,
        string?                                      interactionId    = null,
        CancellationToken                            cancellationToken = default)
    {
        return EmitEventAsync(
            kind:              EventKinds.Observation,
            subjectType:       subjectType,
            agentSubjectId:    agentSubjectId,
            payload:           payload,
            interactionId:     interactionId,
            subjects:          subjects,
            cancellationToken: cancellationToken);
    }

    // =================================================================== //
    // Circuit breaker — POST /v1/cb/check                                 //
    // =================================================================== //

    /// <summary>
    /// Synchronous circuit-breaker check (sdk-spec.md §5.6). Pass EXACTLY
    /// ONE of <paramref name="subjectId"/> or <paramref name="interactionId"/>.
    /// </summary>
    /// <exception cref="DMZAgentValidationException">When neither or both arguments are set.</exception>
    public async Task<CheckResult> CheckAsync(
        string?           subjectId      = null,
        string?           interactionId  = null,
        CancellationToken cancellationToken = default)
    {
        var hasSubject     = !string.IsNullOrEmpty(subjectId);
        var hasInteraction = !string.IsNullOrEmpty(interactionId);
        if (hasSubject == hasInteraction)
        {
            throw new DMZAgentValidationException("pass exactly one of subjectId or interactionId");
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
    // Capture — POST /v1/agent-stream/event                               //
    // =================================================================== //

    /// <summary>
    /// Ingest a single behavior event and return the accepted-only ack
    /// (sdk-spec.md §5.9).
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(
        string                                              subjectId,
        string                                              kind,
        string                                              subjectType,
        IReadOnlyDictionary<string, object?>?               payload          = null,
        string?                                             agentSubjectId   = null,
        string?                                             interactionId    = null,
        string?                                             interactionKind  = "chat_session",
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? subjects        = null,
        string?                                             speakerSubjectId = null,
        string?                                             speakerRole      = null,
        string?                                             occurredAt       = null,
        IReadOnlyDictionary<string, object?>?               metadata         = null,
        // Caller-generated key making a retry safe (spec §1.8). Supply your
        // own: the SDK never invents one, because a key minted per call
        // deduplicates nothing and one derived from the payload would
        // collapse two genuinely distinct but identical events.
        string?                                             idempotencyKey   = null,
        CancellationToken                                   cancellationToken = default)
    {
        if (!EventKinds.All.Contains(kind))
        {
            throw new DMZAgentValidationException(
                $"kind must be one of [{string.Join(", ", EventKinds.All)}], got '{kind}'");
        }
        if (string.IsNullOrEmpty(subjectType) || !EventKinds.ValidSubjectTypes.Contains(subjectType))
        {
            throw new DMZAgentValidationException(
                $"subjectType must be one of [{string.Join(", ", EventKinds.ValidSubjectTypes)}], got '{subjectType}'");
        }
        if (string.IsNullOrEmpty(subjectId))
        {
            throw new DMZAgentValidationException("subject_id is required");
        }

        var body = new Dictionary<string, object?>
        {
            ["kind"]         = kind,
            ["subject_id"]   = subjectId,
            ["subject_type"] = subjectType,
            ["payload"]      = payload ?? new Dictionary<string, object?>(),
        };
        if (!string.IsNullOrEmpty(agentSubjectId))  body["agent_subject_id"] = agentSubjectId;
        if (!string.IsNullOrEmpty(interactionId))    body["interaction_id"] = interactionId;
        if (!string.IsNullOrEmpty(interactionKind))  body["interaction_kind"] = interactionKind;
        if (subjects is { Count: > 0 })              body["subjects"] = subjects;
        if (!string.IsNullOrEmpty(speakerSubjectId)) body["speaker_subject_id"] = speakerSubjectId;
        if (!string.IsNullOrEmpty(speakerRole))      body["speaker_role"] = speakerRole;
        if (!string.IsNullOrEmpty(occurredAt))       body["occurred_at"] = occurredAt;
        if (metadata is { Count: > 0 })              body["metadata"] = metadata;

        var raw = await PostJsonAsync("/v1/agent-stream/event", body, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return ParseCaptureResult(raw);
    }

    // =================================================================== //
    // AwaitOutcome — GET /v1/frames/{id}/story                            //
    // =================================================================== //

    /// <summary>
    /// Poll the frame story endpoint until reasoning completes
    /// (sdk-spec.md §5.10).
    /// </summary>
    public async Task<OutcomeResult> AwaitOutcomeAsync(
        string            frameId,
        double            timeoutSeconds = 30.0,
        CancellationToken cancellationToken = default)
    {
        timeoutSeconds = Math.Min(timeoutSeconds, 120.0);
        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var delay = TimeSpan.FromMilliseconds(100);
        var maxDelay = TimeSpan.FromSeconds(2);
        Exception? lastError = null;
        var path = $"/v1/frames/{Uri.EscapeDataString(frameId)}/story";

        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            try
            {
                var raw = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
                return ParseOutcomeResult(raw);
            }
            catch (DMZAgentValidationException) { throw; }
            catch (DMZAgentAuthException) { throw; }
            catch (DMZAgentPermissionException) { throw; }
            catch (Exception ex) { lastError = ex; }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
        }
        throw new DMZAgentServerException($"await_outcome timed out after {timeoutSeconds}s for frame {frameId}",
            statusCode: null, body: lastError?.Message);
    }

    // =================================================================== //
    // Notification prefs — GET/PUT /v1/settings/notifications              //
    // =================================================================== //

    /// <summary>
    /// Fetch the current API key's notification preferences
    /// (sdk-spec.md §5.12).
    /// </summary>
    public async Task<NotificationPrefs> GetNotificationPrefsAsync(CancellationToken cancellationToken = default)
    {
        var raw = await GetJsonAsync("/v1/settings/notifications", cancellationToken).ConfigureAwait(false);
        return ParseNotificationPrefs(raw);
    }

    /// <summary>
    /// Update notification preferences (sdk-spec.md §5.13).
    /// </summary>
    public async Task<NotificationPrefs> UpdateNotificationPrefsAsync(
        IReadOnlyDictionary<string, object?> prefs,
        CancellationToken cancellationToken = default)
    {
        var raw = await PutJsonAsync("/v1/settings/notifications", prefs, cancellationToken).ConfigureAwait(false);
        return ParseNotificationPrefs(raw);
    }

    // =================================================================== //
    // Division config — GET/PUT /v1/divisions/{id}/config                  //
    // =================================================================== //

    /// <summary>
    /// Read a division's JSON configuration blob (sdk-spec.md §5.14).
    /// </summary>
    public async Task<DivisionConfig> GetDivisionConfigAsync(
        string divisionId,
        CancellationToken cancellationToken = default)
    {
        var path = $"/v1/divisions/{Uri.EscapeDataString(divisionId)}/config";
        var raw = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseDivisionConfig(raw);
    }

    /// <summary>
    /// Replace a division's full JSON configuration blob (sdk-spec.md §5.15).
    /// </summary>
    public async Task<DivisionConfig> UpdateDivisionConfigAsync(
        string divisionId,
        IReadOnlyDictionary<string, object?> config,
        CancellationToken cancellationToken = default)
    {
        var path = $"/v1/divisions/{Uri.EscapeDataString(divisionId)}/config";
        var raw = await PutJsonAsync(path, config, cancellationToken).ConfigureAwait(false);
        return ParseDivisionConfig(raw);
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
        string?                         idempotencyKey,
        CancellationToken               cancellationToken)
    {
        var raw = await PostJsonAsync(path, body, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return ParseEmitResult(raw);
    }

    internal async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DMZAgentClient));

        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyRequestAuth(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DMZAgentServerException($"timeout calling {path}: {ex.Message}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DMZAgentServerException($"network error calling {path}: {ex.Message}", innerException: ex);
        }

        using (response)
        {
            return await HandleResponseAsync(response, path, cancellationToken).ConfigureAwait(false);
        }
    }

    internal Task<JsonElement> PostJsonAsync(
        string                          path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken               cancellationToken)
        => PostJsonAsync(path, body, null, cancellationToken);

    /// <param name="idempotencyKey">
    /// Caller-generated key making a retry of this request safe (spec §1.8),
    /// or <c>null</c> for none. The SDK never generates one: a key minted per
    /// call is unique per call and deduplicates nothing, and a key derived
    /// from the payload would collapse two genuinely distinct but identical
    /// events.
    /// </param>
    internal async Task<JsonElement> PostJsonAsync(
        string                          path,
        IReadOnlyDictionary<string, object?> body,
        string?                         idempotencyKey,
        CancellationToken               cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DMZAgentClient));

        var url     = $"{_baseUrl}{path}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, WireJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
        ApplyRequestAuth(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DMZAgentServerException($"timeout calling {path}: {ex.Message}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DMZAgentServerException($"network error calling {path}: {ex.Message}", innerException: ex);
        }

        using (response)
        {
            return await HandleResponseAsync(response, path, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<JsonElement> PutJsonAsync(
        string                          path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken               cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DMZAgentClient));

        var url     = $"{_baseUrl}{path}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, WireJson);

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApplyRequestAuth(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DMZAgentServerException($"timeout calling {path}: {ex.Message}", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DMZAgentServerException($"network error calling {path}: {ex.Message}", innerException: ex);
        }

        using (response)
        {
            return await HandleResponseAsync(response, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyRequestAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.UserAgent.ParseAdd(_userAgent);
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
            // 400 and 422 both mean "fix the request" — malformed vs
            // parsed-but-rejected. The spec taxonomy maps both here;
            // StatusCode tells them apart for callers that care.
            400 or 422 => new DMZAgentValidationException(
                $"server rejected request to {path}: {detail}", status, bodyForException),
            // 409 is the Idempotency-Key in-flight conflict (§1.8). Kept
            // above the >= 500 arm and off it entirely: the duplicate is the
            // caller's own earlier request, so retrying the same key replays
            // its stored response instead of causing a second side effect.
            409 => new DMZAgentConflictException(
                $"a request with this Idempotency-Key is already in flight on {path}",
                status, bodyForException),
            429 => new DMZAgentRateLimitException(
                $"rate limited on {path}", status, bodyForException,
                // .Delta is populated only for the delta-seconds form; an
                // HTTP-date leaves it null, which is the behaviour we want.
                response.Headers.RetryAfter?.Delta is { } d ? (int)d.TotalSeconds : null),
            401 => new DMZAgentAuthException(
                "invalid or revoked API key", status, bodyForException),
            403 => new DMZAgentPermissionException(
                "API key lacks required scope for this operation", status, bodyForException),
            >= 500 => new DMZAgentServerException(
                $"server error from {path} ({status}): {detail}", status, bodyForException),
            _ => new DMZAgentException(
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
        bool?                  accepted       = OptBool(raw, "accepted");
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
            Accepted:       accepted,
            NWorkspaces:    OptInt(raw, "n_workspaces"),
            FrameId:        frameId,
            SubjectId:      subjectId,
            Outcome:        outcome,
            TriageDecision: triageDecision,
            TagsFired:      tagsFired,
            ScoredByCanons: scoredByCanons,
            SoulVersion:    soulVersion,
            LedgerIndex:    ledgerIndex,
            FollowMyData:   followMyData,
            Livemode:       OptBool(raw, "livemode"),
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

    internal static CaptureResult ParseCaptureResult(JsonElement raw)
    {
        string                frameId        = OptString(raw, "frame_id") ?? string.Empty;
        bool                  accepted       = OptBool(raw, "accepted") ?? false;
        int                   nWorkspaces    = OptInt(raw, "n_workspaces") ?? 0;
        string                interactionId  = OptString(raw, "interaction_id") ?? string.Empty;
        IReadOnlyList<string> subjectsList   = OptStringArray(raw, "subjects") ?? Array.Empty<string>();
        string?               followMyData   = OptString(raw, "follow_my_data");

        return new CaptureResult(
            FrameId:        frameId,
            Accepted:       accepted,
            NWorkspaces:    nWorkspaces,
            InteractionId:  interactionId,
            Subjects:       subjectsList,
            FollowMyData:   followMyData,
            Livemode:       OptBool(raw, "livemode"),
            Raw:            raw);
    }

    internal static OutcomeResult ParseOutcomeResult(JsonElement raw)
    {
        string          frameId     = OptString(raw, "frame_id") ?? string.Empty;
        string          outcome     = OptString(raw, "outcome") ?? string.Empty;
        JsonElement?    error       = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("error", out var e) ? e : null;
        JsonElement?    tagsFired   = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("tags_fired", out var tf) ? tf : null;
        JsonElement?    reasoning   = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("reasoning", out var r) ? r : null;
        long?           soulVersion = OptLong(raw, "soul_version");
        string          finishedAt  = OptString(raw, "finished_at") ?? string.Empty;

        return new OutcomeResult(
            FrameId:     frameId,
            Outcome:     outcome,
            Error:       error,
            TagsFired:   tagsFired,
            Reasoning:   reasoning,
            SoulVersion: soulVersion,
            FinishedAt:  finishedAt,
            Raw:         raw);
    }

    internal static NotificationPrefs ParseNotificationPrefs(JsonElement raw)
    {
        string  emailCadence    = OptString(raw, "email_cadence") ?? string.Empty;
        string? emailPausedUntil = OptString(raw, "email_paused_until");
        bool    pushEnabled     = OptBool(raw, "push_enabled") ?? false;
        string? phone           = OptString(raw, "phone");
        bool    smsEnabled      = OptBool(raw, "sms_enabled") ?? false;
        bool    whatsappEnabled = OptBool(raw, "whatsapp_enabled") ?? false;
        string? webhookUrl      = OptString(raw, "webhook_url");

        return new NotificationPrefs(
            EmailCadence:    emailCadence,
            EmailPausedUntil: emailPausedUntil,
            PushEnabled:     pushEnabled,
            Phone:           phone,
            SmsEnabled:      smsEnabled,
            WhatsappEnabled: whatsappEnabled,
            WebhookUrl:      webhookUrl,
            Raw:             raw);
    }

    internal static DivisionConfig ParseDivisionConfig(JsonElement raw)
    {
        var config = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("config", out var c)
            ? c
            : default;

        return new DivisionConfig(
            Config: config,
            Raw:    raw);
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

    private static int? OptInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v))  return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
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
