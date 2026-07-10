using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DMZAgent.Sdk.Webhook;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// Contract-conformance runner per spec contract-tests/runner-spec.md.
/// Exercises all three corpora — golden envelopes (serialization
/// parity), signature vectors (verifier parity), and error mapping
/// (exception parity).
/// </summary>
public class ContractTests
{
    private const string TestApiKey = "ck_test_xxxxxxxxxxxxxxxxxxxxx";
    private readonly ITestOutputHelper _output;

    public ContractTests(ITestOutputHelper output) { _output = output; }

    // ===================================================================== //
    // Spec version pinning (sdk-spec.md §11.1)                              //
    // ===================================================================== //

    [Fact]
    public void Spec_version_matches_pinned_value()
    {
        File.Exists(SpecPaths.VersionFile).Should().BeTrue(
            $"the spec repo must be checked out at {SpecPaths.SpecRoot}");
        var pinned = File.ReadAllText(SpecPaths.VersionFile).Trim();
        pinned.Should().Be(DMZAgentClient.SpecVersion,
            "Directory.Build.props DMZAgentSpecVersion must match the spec repo's VERSION file");
    }

    // ===================================================================== //
    // Golden envelopes — serialization parity                                //
    // ===================================================================== //

    public static IEnumerable<object[]> GoldenEnvelopeFixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPaths.GoldenEnvelopes));
        foreach (var fixture in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            yield return new object[] { fixture.GetProperty("name").GetString()!, fixture.GetRawText() };
        }
    }

    [Theory]
    [MemberData(nameof(GoldenEnvelopeFixtures))]
    public async Task GoldenEnvelope_serializes_to_canonical_body(string name, string fixtureRaw)
    {
        using var doc      = JsonDocument.Parse(fixtureRaw);
        var       fixture  = doc.RootElement;
        var       method   = fixture.GetProperty("method").GetString()!;
        var       args     = fixture.GetProperty("args");
        var       expected = fixture.GetProperty("expected_body");
        var       expPath  = fixture.GetProperty("expected_path").GetString()!;

        var stub   = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        await DispatchAsync(cx, method, args);

        stub.LastRequestPath.Should().Be(expPath, $"fixture {name}");

        var capturedNorm = JsonNormalize.Canonicalize(stub.LastRequestBody!);
        var expectedNorm = JsonNormalize.Canonicalize(expected);
        capturedNorm.Should().Be(expectedNorm, $"fixture {name}: body mismatch");

        // Authorization header sanity (sdk-spec.md §1.2 / §1.3 / §1.4).
        stub.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        stub.LastRequest!.Headers.Authorization!.Parameter.Should().Be(TestApiKey);
        stub.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        stub.LastRequest!.Headers.UserAgent.ToString().Should().Contain("dmzagent-csharp/");

        _output.WriteLine($"✓ golden_envelopes/{name}");
    }

    public static IEnumerable<object[]> ValidationFailureFixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPaths.GoldenEnvelopes));
        foreach (var fixture in doc.RootElement.GetProperty("validation_failures").EnumerateArray())
        {
            yield return new object[] { fixture.GetProperty("name").GetString()!, fixture.GetRawText() };
        }
    }

    [Theory]
    [MemberData(nameof(ValidationFailureFixtures))]
    public async Task ValidationFailure_raises_expected_exception(string name, string fixtureRaw)
    {
        using var doc     = JsonDocument.Parse(fixtureRaw);
        var       fixture = doc.RootElement;
        var       method  = fixture.GetProperty("method").GetString()!;
        var       args    = fixture.GetProperty("args");
        var       msgPart = fixture.GetProperty("expected_message_contains").GetString()!;

        Func<Task> act;
        if (method == "construct")
        {
            var apiKey = args.GetProperty("api_key").GetString() ?? string.Empty;
            act = () => Task.FromResult(new DMZAgentClient(apiKey));
        }
        else
        {
            var stub   = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
            var client = new DMZAgentClient(TestApiKey, handler: stub);
            act = () => DispatchAsync(client, method, args);
        }

        // The spec accepts ValidationError or an argument-level exception
        // (canonical type "ValidationError_or_ArgumentError"). Our binding
        // raises DMZAgentValidationException for every validation case
        // — that's a subtype of DMZAgentException, satisfying either
        // arm of the union.
        var exc = await act.Should().ThrowAsync<DMZAgentException>();
        exc.Which.Message.Should().Contain(msgPart, $"fixture {name}");

        _output.WriteLine($"✓ validation_failures/{name}");
    }

    // ===================================================================== //
    // Signature vectors — verifier parity                                   //
    // ===================================================================== //

    public static IEnumerable<object[]> SignatureFixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPaths.SignatureVectors));
        foreach (var fixture in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            yield return new object[] { fixture.GetProperty("name").GetString()!, fixture.GetRawText() };
        }
    }

    [Theory]
    [MemberData(nameof(SignatureFixtures))]
    public void SignatureVector_verifies_as_expected(string name, string fixtureRaw)
    {
        using var doc     = JsonDocument.Parse(fixtureRaw);
        var       fixture = doc.RootElement;

        var payload          = fixture.GetProperty("payload").GetString() ?? string.Empty;
        var secret           = fixture.GetProperty("secret").GetString()!;
        var header           = fixture.GetProperty("header").GetString()!;
        var toleranceSeconds = fixture.GetProperty("tolerance_seconds").GetInt32();
        var nowUnix          = fixture.GetProperty("now_unix").GetInt64();
        var expectedValid    = fixture.GetProperty("valid").GetBoolean();

        // <COMPUTE> placeholders get filled in by the runner at test
        // time, per runner-spec.md.
        if (header.Contains("<COMPUTE_WITH_OTHER>"))
        {
            var otherSecret = fixture.GetProperty("header_signed_with").GetString()!;
            header = SubstituteCompute(header, "<COMPUTE_WITH_OTHER>", otherSecret, payload);
        }
        else if (header.Contains("<COMPUTE>"))
        {
            header = SubstituteCompute(header, "<COMPUTE>", secret, payload);
        }

        var actual = WebhookSignature.Verify(payload, header, secret, toleranceSeconds, nowUnix);
        actual.Should().Be(expectedValid, $"fixture {name}");

        _output.WriteLine($"✓ signature_vectors/{name}");
    }

    private static string SubstituteCompute(string header, string placeholder, string secret, string payload)
    {
        // Pull t out of header so we can compute hmac_sha256(secret, "t.payload").
        var tField = header.Split(',').FirstOrDefault(p => p.TrimStart().StartsWith("t=", StringComparison.Ordinal));
        if (tField is null) return header;
        var t = tField.Trim().Substring(2);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{payload}"));
        var hex = Convert.ToHexString(sig).ToLowerInvariant();
        return header.Replace(placeholder, hex);
    }

    // ===================================================================== //
    // Error mapping — exception parity                                      //
    // ===================================================================== //

    public static IEnumerable<object[]> ErrorMappingFixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPaths.ErrorMapping));
        foreach (var fixture in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            yield return new object[] { fixture.GetProperty("name").GetString()!, fixture.GetRawText() };
        }
    }

    [Theory]
    [MemberData(nameof(ErrorMappingFixtures))]
    public async Task ErrorMapping_raises_canonical_exception(string name, string fixtureRaw)
    {
        using var doc     = JsonDocument.Parse(fixtureRaw);
        var       fixture = doc.RootElement;

        var status     = fixture.GetProperty("status").GetInt32();
        var bodyRaw    = fixture.GetProperty("body").GetRawText();
        var method     = fixture.GetProperty("method").GetString()!;
        var args       = fixture.GetProperty("args");

        // Optional response headers (e.g. Retry-After for the 429
        // fixtures added in spec 0.7.0).
        Dictionary<string, string>? headers = null;
        if (fixture.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            headers = new Dictionary<string, string>();
            foreach (var h in headersEl.EnumerateObject())
            {
                headers[h.Name] = h.Value.GetString()!;
            }
        }

        var stub = new StubHttpMessageHandler((HttpStatusCode)status, bodyRaw, headers: headers);
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        if (method == "guard_with_raise_on_open")
        {
            var subjectId   = args.GetProperty("subject_id").GetString()!;
            var raiseOnOpen = args.GetProperty("raise_on_open").GetBoolean();

            if (fixture.TryGetProperty("expected_exception", out var expExc) && expExc.ValueKind == JsonValueKind.String)
            {
                var expected = expExc.GetString()!;
                var exc = await ((Func<Task>)(async () =>
                {
                    using var g = await cx.GuardAsync(subjectId: subjectId, raiseOnOpen: raiseOnOpen);
                })).Should().ThrowAsync<DMZAgentException>();
                MapCanonical(exc.Which).Should().Be(expected, $"fixture {name}");

                if (fixture.TryGetProperty("expected_fields", out var fields) && exc.Which is CircuitBreakerOpenException cbe)
                {
                    if (fields.TryGetProperty("reason", out var rEl))
                        cbe.Reason.Should().Be(rEl.GetString(), $"fixture {name}: reason");
                    if (fields.TryGetProperty("scope_ref", out var sEl))
                        cbe.ScopeRef.Should().Be(sEl.GetString(), $"fixture {name}: scope_ref");
                }
            }
            else
            {
                // No expected exception: the guard MUST return a result
                // whose `expected_result_fields` match. raiseOnOpen=false.
                using var g = await cx.GuardAsync(subjectId: subjectId, raiseOnOpen: raiseOnOpen);
                if (fixture.TryGetProperty("expected_result_fields", out var rFields))
                {
                    if (rFields.TryGetProperty("allow", out var aEl))
                        g.Result.Allow.Should().Be(aEl.GetBoolean(), $"fixture {name}: allow");
                    if (rFields.TryGetProperty("state", out var stEl))
                        g.Result.State.Should().Be(stEl.GetString(), $"fixture {name}: state");
                }
            }
        }
        else
        {
            var expected           = fixture.GetProperty("expected_exception").GetString()!;
            var expectedStatusCode = fixture.GetProperty("expected_status_code").GetInt32();

            var exc = await ((Func<Task>)(() => DispatchAsync(cx, method, args)))
                .Should().ThrowAsync<DMZAgentException>();
            MapCanonical(exc.Which).Should().Be(expected, $"fixture {name}");
            exc.Which.StatusCode.Should().Be(expectedStatusCode, $"fixture {name}: status_code");

            // Spec 0.7.0: RateLimitError fixtures pin retry_after
            // (JSON null → the property must be null).
            if (fixture.TryGetProperty("expected_retry_after", out var retryEl))
            {
                var rle = exc.Which.Should().BeOfType<DMZAgentRateLimitException>(
                    $"fixture {name}: expected_retry_after implies RateLimitError").Which;
                int? expectedRetryAfter = retryEl.ValueKind == JsonValueKind.Number
                    ? retryEl.GetInt32()
                    : null;
                rle.RetryAfter.Should().Be(expectedRetryAfter, $"fixture {name}: retry_after");
            }
        }

        _output.WriteLine($"✓ error_mapping/{name}");
    }

    /// <summary>
    /// Map a thrown SDK exception back to the canonical name listed in
    /// sdk-spec.md §8.5 (the runner's <c>canonical_type</c> step).
    /// </summary>
    private static string MapCanonical(DMZAgentException exc) => exc switch
    {
        DMZAgentValidationException  => "ValidationError",
        DMZAgentAuthException        => "AuthError",
        DMZAgentPermissionException  => "PermissionError",
        DMZAgentRateLimitException   => "RateLimitError",
        DMZAgentServerException      => "ServerError",
        CircuitBreakerOpenException   => "CBOpenError",
        _                              => "DMZAgentError",
    };

    // ===================================================================== //
    // Dispatch — turn a canonical method name + snake_case args into a     //
    // C# method call.                                                       //
    // ===================================================================== //

    private static async Task DispatchAsync(DMZAgentClient cx, string method, JsonElement args)
    {
        switch (method)
        {
            case "subject_says":
                await cx.SubjectSaysAsync(
                    subjectId:      args.GetProperty("subject_id").GetString()!,
                    text:           args.GetProperty("text").GetString()!,
                    agentSubjectId: args.GetProperty("agent_subject_id").GetString()!,
                    subjectType:    OptString(args, "subject_type") ?? "chat",
                    interactionId:  OptString(args, "interaction_id"),
                    subjects:       OptSubjectList(args, "subjects"),
                    payloadExtra:   OptDict(args, "payload_extra"));
                return;

            case "tool_call":
                await cx.ToolCallAsync(
                    subjectId:      args.GetProperty("subject_id").GetString()!,
                    tool:           args.GetProperty("tool").GetString()!,
                    subjectType:    OptString(args, "subject_type") ?? "chat",
                    args:           OptDict(args, "args"),
                    interactionId:  OptString(args, "interaction_id"),
                    subjects:       OptSubjectList(args, "subjects"));
                return;

            case "tool_result":
                await cx.ToolResultAsync(
                    subjectId:      args.GetProperty("subject_id").GetString()!,
                    tool:           args.GetProperty("tool").GetString()!,
                    subjectType:    OptString(args, "subject_type") ?? "chat",
                    result:         OptObject(args, "result"),
                    interactionId:  OptString(args, "interaction_id"),
                    subjects:       OptSubjectList(args, "subjects"));
                return;

            case "observation":
                await cx.ObservationAsync(
                    agentSubjectId: args.GetProperty("agent_subject_id").GetString()!,
                    subjects:       OptSubjectList(args, "subjects") ?? Array.Empty<IReadOnlyDictionary<string, object?>>(),
                    payload:        OptDict(args, "payload") ?? new Dictionary<string, object?>(),
                    subjectType:    OptString(args, "subject_type") ?? "chat",
                    interactionId:  OptString(args, "interaction_id"));
                return;

            case "check":
                await cx.CheckAsync(
                    subjectId:     OptString(args, "subject_id"),
                    interactionId: OptString(args, "interaction_id"));
                return;

            case "emit_event":
                await cx.EmitEventAsync(
                    kind:           args.GetProperty("kind").GetString()!,
                    subjectType:    OptString(args, "subject_type") ?? "chat",
                    agentSubjectId: args.GetProperty("agent_subject_id").GetString()!,
                    payload:        OptDict(args, "payload"));
                return;

            default:
                throw new InvalidOperationException($"runner: unsupported method '{method}'");
        }
    }

    private static string? OptString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyDictionary<string, object?>? OptDict(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return ToDict(v);
    }

    private static object? OptObject(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? ToObject(v) : null;

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>>? OptSubjectList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in v.EnumerateArray())
        {
            list.Add(ToDict(item));
        }
        return list;
    }

    private static IReadOnlyDictionary<string, object?> ToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = ToObject(prop.Value);
        }
        return dict;
    }

    private static object? ToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDouble(),
        JsonValueKind.Object => ToDict(v),
        JsonValueKind.Array  => v.EnumerateArray().Select(ToObject).ToList(),
        _                    => null,
    };
}
