using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DMZAgent.Sdk.Concordia;

/// <summary>
/// Concordia MCP 1.0 client — the governance side of DMZAgent.
///
/// Companion to <see cref="DMZAgentClient"/> for the agent-stream
/// surface. Customer agents speak MCP 1.0 over JSON-RPC against
/// <c>/mcp/v1</c> to enforce covenants, record audit decisions,
/// query installed Canons, and read soul snapshots on subjects.
///
/// <example>
/// <code>
/// using var client = new ConcordiaClient(apiKey: "ck_live_…");
///
/// var v = await client.EnforceCovenantAsync(
///     subjectId:     "user:alice",
///     actionKind:    "payment.issue",
///     actionPayload: new Dictionary&lt;string, object?&gt; { ["amount"] = 9900 });
///
/// if (v.Verdict == Verdict.Block) return SafeFallback(v.Rationale);
///
/// await client.RecordDecisionAsync(
///     subjectId:    "user:alice",
///     decisionKind: "payment_issued",
///     outcome:      "completed");
/// </code>
/// </example>
///
/// Naming follows the spec §8 C# map: PascalCase async methods,
/// <c>…Exception</c> suffixes, <c>ConcordiaClient</c> for the client
/// class. Wire field names stay snake_case (the
/// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>
/// declarations on the records carry that contract).
/// </summary>
public sealed class ConcordiaClient : IDisposable
{
    /// <summary>Production base URL.</summary>
    public const string DefaultBaseUrl = "https://api.dmzagent.com";

    /// <summary>Default per-request timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>User-Agent header on every request.</summary>
    public static readonly string DefaultUserAgent = "dmzagent-concordia-csharp/0.6.0";

    private const string McpPath = "/mcp/v1";

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly IReadOnlyDictionary<int, Func<string, IReadOnlyDictionary<string, object?>?, ConcordiaException>> ErrorFactory =
        new Dictionary<int, Func<string, IReadOnlyDictionary<string, object?>?, ConcordiaException>>
        {
            [-32001] = (m, d) => new ConcordiaAuthException(m, d),
            [-32002] = (m, d) => new ConcordiaQuotaExceededException(m, d),
            [-32003] = (m, d) => new ConcordiaPolicyEngineUnavailableException(m, d),
            [-32004] = (m, d) => new ConcordiaCanonNotInstalledException(m, d),
            [-32005] = (m, d) => new ConcordiaSubjectNotFoundException(m, d),
            [-32006] = (m, d) => new ConcordiaCircuitOpenException(m, d),
            [-32007] = (m, d) => new ConcordiaPermissionDeniedException(m, d),
        };

    private readonly HttpClient _http;
    private readonly bool       _ownsHttpClient;
    private readonly string     _baseUrl;
    private          int        _idSeq;
    private          bool       _disposed;

    /// <summary>
    /// Construct a ConcordiaClient.
    /// </summary>
    /// <param name="apiKey">Workspace-scoped API key. Must start with
    /// <c>ck_</c>; throws <see cref="ConcordiaException"/> otherwise.
    /// If null, reads <c>DMZAGENT_API_KEY</c> from the environment.</param>
    /// <param name="baseUrl">Override the default
    /// <c>https://api.dmzagent.com</c> (e.g. for staging or local dev).</param>
    /// <param name="timeout">Per-request timeout; defaults to 10s.</param>
    /// <param name="userAgent">Override User-Agent header.</param>
    /// <param name="handler">Inject a custom
    /// <see cref="HttpMessageHandler"/> (tests).</param>
    public ConcordiaClient(
        string? apiKey   = null,
        string? baseUrl  = null,
        TimeSpan? timeout = null,
        string? userAgent = null,
        HttpMessageHandler? handler = null)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("DMZAGENT_API_KEY") ?? "";
        if (string.IsNullOrEmpty(key))
        {
            throw new ConcordiaException(
                "apiKey required (pass apiKey or set DMZAGENT_API_KEY)");
        }
        if (!key.StartsWith("ck_", StringComparison.Ordinal))
        {
            throw new ConcordiaException(
                "apiKey must start with 'ck_' — double-check you copied a DMZAgent key, not another service's token");
        }

        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

        if (handler != null)
        {
            _http = new HttpClient(handler) { Timeout = timeout ?? DefaultTimeout };
            _ownsHttpClient = true;
        }
        else
        {
            _http = new HttpClient { Timeout = timeout ?? DefaultTimeout };
            _ownsHttpClient = true;
        }

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? DefaultUserAgent);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
    }

    // ----- JSON-RPC dispatch ----------------------------------------------

    private int NextId() => Interlocked.Increment(ref _idSeq);

    private async Task<JsonElement> RpcAsync(
        string method,
        IReadOnlyDictionary<string, object?>? @params,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = NextId(),
            ["method"]  = method,
            ["params"]  = @params ?? new Dictionary<string, object?>(),
        };

        var json = JsonSerializer.Serialize(body, RequestJson);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(
                new Uri(_baseUrl + McpPath, UriKind.Absolute),
                content,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (TaskCanceledException tce) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConcordiaProtocolException(
                $"Concordia request timed out", innerException: tce);
        }
        catch (HttpRequestException hre)
        {
            throw new ConcordiaProtocolException(
                $"Concordia transport error: {hre.Message}", innerException: hre);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var bodyText = await response.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new ConcordiaProtocolException(
                    $"Concordia returned HTTP {(int)response.StatusCode}",
                    code: (int)response.StatusCode,
                    data: new Dictionary<string, object?>
                    {
                        ["status"] = (int)response.StatusCode,
                        ["body"]   = bodyText.Length > 1000 ? bodyText[..1000] : bodyText,
                    });
            }

            string raw = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException je)
            {
                throw new ConcordiaProtocolException(
                    "Concordia returned non-JSON body", innerException: je);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ConcordiaProtocolException("Envelope is not a JSON object");
                }
                if (!root.TryGetProperty("jsonrpc", out var jr)
                    || jr.GetString() != "2.0")
                {
                    throw new ConcordiaProtocolException(
                        $"Envelope jsonrpc != \"2.0\"");
                }

                if (root.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.Object)
                {
                    int code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                    string msg = err.TryGetProperty("message", out var m)
                        ? (m.GetString() ?? "") : "";
                    IReadOnlyDictionary<string, object?> data = err.TryGetProperty("data", out var d) &&
                        d.ValueKind == JsonValueKind.Object
                            ? ParseObject(d)
                            : new Dictionary<string, object?>();

                    if (ErrorFactory.TryGetValue(code, out var factory))
                    {
                        throw factory(msg, data);
                    }
                    // Unknown app code → base ConcordiaException
                    string? errId = data.TryGetValue("error_id", out var eid)
                        ? eid as string : null;
                    throw new ConcordiaException(msg, code, errId, data);
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new ConcordiaProtocolException(
                        "Envelope missing both `result` and `error`");
                }

                // Clone the element so it survives JsonDocument disposal.
                return result.Clone();
            }
        }
    }

    /// <summary>Recursively convert a JsonElement object into a
    /// Dictionary&lt;string, object?&gt; for the error <c>data</c>
    /// payload. Keeps caller code free of JsonElement.</summary>
    private static IReadOnlyDictionary<string, object?> ParseObject(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = ParseValue(prop.Value);
        }
        return dict;
    }

    private static object? ParseValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Object => ParseObject(el),
        JsonValueKind.Array  => ParseArray(el),
        _                    => el.GetRawText(),
    };

    private static IReadOnlyList<object?> ParseArray(JsonElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray()) list.Add(ParseValue(item));
        return list;
    }

    private async Task<T> CallToolAsync<T>(
        string name,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RpcAsync("tools/call",
            new Dictionary<string, object?>
            {
                ["name"]      = name,
                ["arguments"] = arguments,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new ConcordiaProtocolException(
                $"tools/call {name} returned non-object");
        }

        // Prefer _data; fallback to canonical content[0].text JSON.
        if (result.TryGetProperty("_data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            var decoded = data.Deserialize<T>(ResponseJson)
                ?? throw new ConcordiaProtocolException(
                    $"tools/call {name} _data did not deserialize");
            return decoded;
        }
        if (result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typ)
                    && typ.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    string? s = txt.GetString();
                    if (s != null)
                    {
                        try
                        {
                            var decoded = JsonSerializer.Deserialize<T>(s, ResponseJson)
                                ?? throw new ConcordiaProtocolException(
                                    $"tools/call {name} content did not deserialize");
                            return decoded;
                        }
                        catch (JsonException)
                        {
                            continue;
                        }
                    }
                }
            }
        }
        throw new ConcordiaProtocolException(
            $"tools/call {name} response had no decodable content");
    }

    // ----- Discovery -------------------------------------------------------

    /// <summary>MCP initialize handshake. Returns the raw server result.</summary>
    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        => await RpcAsync("initialize", null, cancellationToken).ConfigureAwait(false);

    /// <summary>Liveness check. Returns <c>true</c> on success.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var r = await RpcAsync("ping", null, cancellationToken).ConfigureAwait(false);
        return r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    }

    // ----- Tools -----------------------------------------------------------

    /// <summary>Score a proposed action against installed policy Canons.
    /// Per CONCORDIA_MCP.md §4.1.</summary>
    public Task<EnforceCovenantResult> EnforceCovenantAsync(
        string subjectId,
        string actionKind,
        IReadOnlyDictionary<string, object?>? actionPayload = null,
        IReadOnlyDictionary<string, object?>? context       = null,
        CancellationToken cancellationToken                  = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["subject_id"]     = subjectId,
            ["action_kind"]    = actionKind,
            ["action_payload"] = actionPayload ?? new Dictionary<string, object?>(),
            ["context"]        = context       ?? new Dictionary<string, object?>(),
        };
        return CallToolAsync<EnforceCovenantResult>("enforce_covenant", args, cancellationToken);
    }

    /// <summary>Append an audit-grade decision record to the workspace's
    /// hash-chained ledger. Per CONCORDIA_MCP.md §4.2.</summary>
    public Task<RecordDecisionResult> RecordDecisionAsync(
        string subjectId,
        string decisionKind,
        IReadOnlyDictionary<string, object?>? payload = null,
        string actor   = "agent",
        string outcome = "completed",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["subject_id"]    = subjectId,
            ["decision_kind"] = decisionKind,
            ["payload"]       = payload ?? new Dictionary<string, object?>(),
            ["actor"]         = actor,
            ["outcome"]       = outcome,
        };
        return CallToolAsync<RecordDecisionResult>("record_decision", args, cancellationToken);
    }

    /// <summary>Search installed Canons for matching policy text,
    /// vocabulary, prompts, or descriptions. Per CONCORDIA_MCP.md §4.3.</summary>
    public Task<QueryCorpusResult> QueryCorpusAsync(
        string query,
        string scope = "installed",
        IReadOnlyList<string>? canonFilter = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["scope"] = scope,
            ["limit"] = limit,
        };
        if (canonFilter is { Count: > 0 })
        {
            args["canon_filter"] = canonFilter;
        }
        return CallToolAsync<QueryCorpusResult>("query_corpus", args, cancellationToken);
    }

    /// <summary>Return the latest soul snapshot for a subject.
    /// Per CONCORDIA_MCP.md §4.4. Throws
    /// <see cref="ConcordiaSubjectNotFoundException"/> when the subject
    /// has no soul in this workspace.</summary>
    public Task<SubjectSoul> GetSubjectSoulAsync(
        string subjectId,
        string depth = "latest",
        IReadOnlyList<string>? tagFilter = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["subject_id"] = subjectId,
            ["depth"]      = depth,
        };
        if (tagFilter is { Count: > 0 })
        {
            args["tag_filter"] = tagFilter;
        }
        return CallToolAsync<SubjectSoul>("get_subject_soul", args, cancellationToken);
    }

    // ----- Resources -------------------------------------------------------

    private async Task<T> ReadResourceAsync<T>(string uri, CancellationToken cancellationToken)
    {
        var result = await RpcAsync("resources/read",
            new Dictionary<string, object?> { ["uri"] = uri },
            cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("contents", out var contents)
            || contents.ValueKind != JsonValueKind.Array)
        {
            throw new ConcordiaProtocolException(
                $"resources/read {uri} returned no contents array");
        }
        foreach (var block in contents.EnumerateArray())
        {
            if (block.TryGetProperty("mimeType", out var mt)
                && mt.GetString() == "application/json"
                && block.TryGetProperty("text", out var txt))
            {
                string? s = txt.GetString();
                if (s != null)
                {
                    try
                    {
                        var decoded = JsonSerializer.Deserialize<T>(s, ResponseJson);
                        if (decoded == null)
                        {
                            throw new ConcordiaProtocolException(
                                $"resources/read {uri} content deserialized as null");
                        }
                        return decoded;
                    }
                    catch (JsonException je)
                    {
                        throw new ConcordiaProtocolException(
                            $"resources/read {uri} returned non-JSON content", innerException: je);
                    }
                }
            }
        }
        throw new ConcordiaProtocolException(
            $"resources/read {uri} returned no JSON content block");
    }

    /// <summary><c>concordia:/workspace/policies</c> — every CB policy
    /// installed in this workspace.</summary>
    public async Task<IReadOnlyList<PolicySummary>> WorkspacePoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        var page = await ReadResourceAsync<PoliciesPage>(
            "concordia:/workspace/policies", cancellationToken).ConfigureAwait(false);
        return page.Policies;
    }

    /// <summary><c>concordia:/workspace/canons</c> — every installed
    /// Canon's metadata.</summary>
    public async Task<IReadOnlyList<InstalledCanon>> WorkspaceCanonsAsync(
        CancellationToken cancellationToken = default)
    {
        var page = await ReadResourceAsync<CanonsPage>(
            "concordia:/workspace/canons", cancellationToken).ConfigureAwait(false);
        return page.Canons;
    }

    /// <summary><c>concordia:/workspace/recent-ledger</c> — paginated
    /// ledger read. To page forward, re-call with
    /// <c>since: page.NextSince</c> until <c>NextSince</c> is null.</summary>
    public Task<LedgerPage> RecentLedgerAsync(
        int since = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var uri = $"concordia:/workspace/recent-ledger?since={since}&limit={limit}";
        return ReadResourceAsync<LedgerPage>(uri, cancellationToken);
    }

    /// <summary>Async-iterate every ledger entry from <c>since</c>
    /// onwards, paginating under the hood. Stops when a page returns
    /// <c>NextSince == null</c>.</summary>
    public async IAsyncEnumerable<LedgerEntry> IterLedgerAsync(
        int since = 0,
        int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int? cursor = since;
        while (cursor.HasValue)
        {
            var page = await RecentLedgerAsync(
                cursor.Value, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries) yield return entry;
            cursor = page.NextSince;
        }
    }

    // Internal pagination wrappers — the resource handlers return
    // {workspace_id, count, policies/canons/entries}; we only surface
    // the inner lists to callers.
    private sealed record PoliciesPage(
        [property: JsonPropertyName("policies")] IReadOnlyList<PolicySummary> Policies);
    private sealed record CanonsPage(
        [property: JsonPropertyName("canons")] IReadOnlyList<InstalledCanon> Canons);
}
