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
/// Exercises golden envelopes (serialization parity), signature vectors
/// (verifier parity), error mapping (exception parity), and step vectors
/// (how an agent-mode step's answer is read).
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

        var stub   = new StubHttpMessageHandler(HttpStatusCode.OK, ResponseFor(method));
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        await DispatchAsync(cx, method, args);

        stub.LastRequestPath.Should().Be(expPath, $"fixture {name}");

        // A read vector pins its verb and its query string. Asserting only
        // the body would let a GET that sent every filter as nothing at all
        // pass, since a GET has no body to be wrong about.
        var expVerb = fixture.TryGetProperty("expected_method", out var mv)
            ? mv.GetString()! : "POST";
        stub.LastRequest!.Method.Method.Should().Be(expVerb, $"fixture {name}: verb");

        if (fixture.TryGetProperty("expected_query", out var eq)
            && eq.ValueKind == JsonValueKind.Object)
        {
            var got = System.Web.HttpUtility.ParseQueryString(
                stub.LastRequest!.RequestUri!.Query);
            var gotMap = got.AllKeys
                .Where(k => k is not null)
                .ToDictionary(k => k!, k => got[k]!);
            var wantMap = eq.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => prop.Value.GetString()!);
            gotMap.Should().BeEquivalentTo(wantMap, $"fixture {name}: query");
        }

        if (expected.ValueKind == JsonValueKind.Null)
        {
            string.IsNullOrEmpty(stub.LastRequestBody).Should().BeTrue(
                $"fixture {name}: expected no request body");
        }
        else
        {
            var capturedNorm = JsonNormalize.Canonicalize(stub.LastRequestBody!);
            var expectedNorm = JsonNormalize.Canonicalize(expected);
            capturedNorm.Should().Be(expectedNorm, $"fixture {name}: body mismatch");
        }

        // Authorization header sanity (sdk-spec.md §1.2 / §1.3 / §1.4).
        stub.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        stub.LastRequest!.Headers.Authorization!.Parameter.Should().Be(TestApiKey);
        if (stub.LastRequest!.Content is not null)
        {
            stub.LastRequest!.Content!.Headers.ContentType!.MediaType
                .Should().Be("application/json");
        }
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

        var fxHeaders = new Dictionary<string, string>();
        if (fixture.TryGetProperty("headers", out var hEl) && hEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in hEl.EnumerateObject())
            {
                fxHeaders[h.Name] = h.Value.ToString();
            }
        }

        var stub = new StubHttpMessageHandler((HttpStatusCode)status, bodyRaw, fxHeaders);
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

            // Presence check, not a null test: the corpus carries an explicit
            // null case for a 429 sent without a Retry-After header, and a
            // null test would make that indistinguishable from absence.
            if (fixture.TryGetProperty("expected_retry_after", out var raEl))
            {
                var rle = exc.Which.Should().BeOfType<DMZAgentRateLimitException>(
                    $"fixture {name}: expected_retry_after implies RateLimitError").Which;
                int? wantRetryAfter = raEl.ValueKind == JsonValueKind.Null ? null : raEl.GetInt32();
                rle.RetryAfter.Should().Be(wantRetryAfter, $"fixture {name}: retry_after");
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
        DMZAgentRateLimitException   => "RateLimitError",
        DMZAgentConflictException    => "ConflictError",
        DMZAgentAuthException        => "AuthError",
        DMZAgentPermissionException  => "PermissionError",
        DMZAgentServerException      => "ServerError",
        CircuitBreakerOpenException   => "CBOpenError",
        _                              => "DMZAgentError",
    };

    // ===================================================================== //
    // Step vectors — how a step's answer is read (sdk-spec.md §1.9, §7.16)   //
    // ===================================================================== //

    public static IEnumerable<object[]> StepVectorFixtures() => StepVectors("fixtures");
    public static IEnumerable<object[]> StepVectorFailures() => StepVectors("failures");

    private static IEnumerable<object[]> StepVectors(string section)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPaths.StepVectors));
        foreach (var fixture in doc.RootElement.GetProperty(section).EnumerateArray())
        {
            yield return new object[] { fixture.GetProperty("name").GetString()!, fixture.GetRawText() };
        }
    }

    /// <summary>Serves the fixture's <c>responses</c> in order, repeating the last.</summary>
    private static StubHttpMessageHandler ServeResponses(JsonElement fixture)
    {
        var responses = fixture.GetProperty("responses").EnumerateArray()
            .Select(r => ((HttpStatusCode)r.GetProperty("status").GetInt32(),
                          r.GetProperty("body").GetRawText()))
            .ToList();
        var served = 0;
        return new StubHttpMessageHandler((_, _) =>
        {
            var (status, body) = responses[Math.Min(served++, responses.Count - 1)];
            return StubHttpMessageHandler.MakeResponse(status, body);
        });
    }

    [Theory]
    [MemberData(nameof(StepVectorFixtures))]
    public async Task StepVector_reads_the_answer(string name, string fixtureRaw)
    {
        using var doc     = JsonDocument.Parse(fixtureRaw);
        var       fixture = doc.RootElement;
        fixture.GetProperty("method").GetString().Should().Be("agent_step", $"fixture {name}");

        var stub = ServeResponses(fixture);
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        var result = await AgentStepFromArgsAsync(cx, fixture.GetProperty("args"));

        var expReq = fixture.GetProperty("expected_request");
        stub.LastRequest!.Method.Method.Should().Be(
            expReq.GetProperty("method").GetString(), $"fixture {name}: verb");
        stub.LastRequestPath.Should().Be(
            expReq.GetProperty("path").GetString(), $"fixture {name}: path");

        var expected = fixture.GetProperty("expected_result");
        // runner-spec.md: runs MUST be asserted on every vector. A vector
        // that left it out would let the one field a harness branches on
        // go unchecked, so its absence is a failure here, not a skip.
        expected.TryGetProperty("runs", out _).Should().BeTrue(
            $"fixture {name}: every step vector must pin runs");

        foreach (var prop in expected.EnumerateObject())
        {
            if (prop.Name == "behaviors")
            {
                var want = prop.Value.EnumerateArray().ToList();
                result.Behaviors.Should().HaveCountGreaterThanOrEqualTo(want.Count, $"fixture {name}: behaviors");
                for (var i = 0; i < want.Count; i++)
                {
                    foreach (var bp in want[i].EnumerateObject())
                    {
                        AssertField(BehaviorField(result.Behaviors[i], bp.Name), bp.Value,
                            $"fixture {name}: behaviors[{i}].{bp.Name}");
                    }
                }
                continue;
            }
            AssertField(StepField(result, prop.Name), prop.Value, $"fixture {name}: {prop.Name}");
        }

        _output.WriteLine($"✓ step_vectors/{name}");
    }

    [Theory]
    [MemberData(nameof(StepVectorFailures))]
    public async Task StepVector_failure_raises_and_returns_nothing(string name, string fixtureRaw)
    {
        using var doc     = JsonDocument.Parse(fixtureRaw);
        var       fixture = doc.RootElement;
        var       expected = fixture.GetProperty("expected_exception").GetString()!;

        var stub = ServeResponses(fixture);
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        StepResult? returned = null;
        var exc = await ((Func<Task>)(async () =>
            returned = await AgentStepFromArgsAsync(cx, fixture.GetProperty("args"))))
            .Should().ThrowAsync<DMZAgentException>();
        MapCanonical(exc.Which).Should().Be(expected, $"fixture {name}");
        returned.Should().BeNull($"fixture {name}: an unanswered step must not return a result");

        _output.WriteLine($"✓ step_vectors/{name}");
    }

    /// <summary>A <see cref="StepResult"/> field by its canonical wire name (§8.4).</summary>
    private static object? StepField(StepResult r, string canonical) => canonical switch
    {
        "frame_id"       => r.FrameId,
        "interaction_id" => r.InteractionId,
        "directive"      => r.Directive,
        "scope"          => r.Scope,
        "reason"         => r.Reason,
        "approval_id"    => r.ApprovalId,
        "settled"        => r.Settled,
        "livemode"       => r.Livemode,
        "runs"           => r.Runs,
        _ => throw new InvalidOperationException($"runner: StepResult has no field '{canonical}'"),
    };

    /// <summary>A <see cref="Behavior"/> field by its canonical wire name (§8.4).</summary>
    private static object? BehaviorField(Behavior b, string canonical) => canonical switch
    {
        "tag"            => b.Tag,
        "polarity"       => b.Polarity,
        "strength"       => b.Strength,
        "source"         => b.Source,
        "evidence"       => b.Evidence,
        "calls"          => b.Calls,
        "behavior_id"    => b.BehaviorId,
        "subject_id"     => b.SubjectId,
        "interaction_id" => b.InteractionId,
        "observed_at"    => b.ObservedAt,
        _ => throw new InvalidOperationException($"runner: Behavior has no field '{canonical}'"),
    };

    private static void AssertField(object? actual, JsonElement want, string because)
    {
        switch (want.ValueKind)
        {
            case JsonValueKind.Null:
                actual.Should().BeNull(because);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                actual.Should().Be(want.GetBoolean(), because);
                break;
            case JsonValueKind.Number:
                Convert.ToDouble(actual).Should().Be(want.GetDouble(), because);
                break;
            case JsonValueKind.String:
                actual.Should().Be(want.GetString(), because);
                break;
            case JsonValueKind.Array:
                ((IEnumerable<string>)actual!).Should().Equal(
                    want.EnumerateArray().Select(e => e.GetString()!), because);
                break;
            default:
                throw new InvalidOperationException($"runner: cannot compare {want.ValueKind} ({because})");
        }
    }

    /// <summary>
    /// The body the golden stub answers with. A step whose answer carries no
    /// directive cannot be read and throws (§1.9), so the golden run serves a
    /// readable one; every other method parses <c>{}</c>.
    /// </summary>
    private static string ResponseFor(string method) => method switch
    {
        "agent_step" => """{"frame_id":"fr_g","interaction_id":"sess_1","directive":"proceed","settled":true,"behaviors":[]}""",
        _            => "{}",
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

            // 0.10.0 — the white-label approval control and the readable ledger.
            case "list_approvals":
                await cx.ListApprovalsAsync(
                    status:    OptString(args, "status"),
                    subjectId: OptString(args, "subject_id"),
                    limit:     OptInt(args, "limit"),
                    cursor:    OptString(args, "cursor"));
                return;

            case "decide_approval":
                await cx.DecideApprovalAsync(
                    approvalId: args.GetProperty("approval_id").GetString()!,
                    decision:   OptString(args, "decision")!,
                    actorId:    OptString(args, "actor_id")!,
                    actorLabel: OptString(args, "actor_label"),
                    reason:     OptString(args, "reason"));
                return;

            case "get_incidents":
                await cx.GetIncidentsAsync(
                    status:    OptString(args, "status"),
                    subjectId: OptString(args, "subject_id"),
                    since:     OptString(args, "since"),
                    until:     OptString(args, "until"),
                    limit:     OptInt(args, "limit"),
                    cursor:    OptString(args, "cursor"));
                return;

            // 0.11.0 — agent mode.
            case "agent_step":
                await AgentStepFromArgsAsync(cx, args);
                return;

            case "list_behaviors":
                await cx.ListBehaviorsAsync(
                    subjectId:     OptString(args, "subject_id")!,
                    polarity:      OptString(args, "polarity"),
                    interactionId: OptString(args, "interaction_id"),
                    since:         OptString(args, "since"),
                    until:         OptString(args, "until"),
                    limit:         OptInt(args, "limit"),
                    cursor:        OptString(args, "cursor"));
                return;

            case "get_approval":
                await cx.GetApprovalAsync(OptString(args, "approval_id")!);
                return;

            default:
                throw new InvalidOperationException($"runner: unsupported method '{method}'");
        }
    }

    /// <summary>
    /// <c>agent_step(**args)</c>: every request field of §2.11, passed only
    /// when the fixture names it, so a missing required field reaches the
    /// SDK as missing and the local validation is what answers it.
    /// </summary>
    private static Task<StepResult> AgentStepFromArgsAsync(DMZAgentClient cx, JsonElement args)
        => cx.AgentStepAsync(
            agentSubjectId: OptString(args, "agent_subject_id")!,
            interactionId:  OptString(args, "interaction_id")!,
            phase:          OptString(args, "phase")!,
            callId:         OptString(args, "call_id"),
            tool:           OptString(args, "tool"),
            args:           OptDict(args, "args"),
            status:         OptString(args, "status"),
            result:         OptObject(args, "result"),
            refusedBy:      OptString(args, "refused_by"),
            reason:         OptString(args, "reason"),
            attemptOf:      OptString(args, "attempt_of"),
            intent:         OptDict(args, "intent"),
            occurredAt:     OptString(args, "occurred_at"),
            metadata:       OptDict(args, "metadata"),
            idempotencyKey: OptString(args, "idempotency_key"));

    private static string? OptString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? OptInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

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
