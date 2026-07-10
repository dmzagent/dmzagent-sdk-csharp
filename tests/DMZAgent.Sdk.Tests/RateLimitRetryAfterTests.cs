using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// Unit tests for the 429 → <see cref="DMZAgentRateLimitException"/>
/// mapping and its <c>Retry-After</c> parsing (sdk-spec.md §3, spec
/// 0.7.0). Delta-seconds only; absent or unparseable → <c>null</c>.
/// The SDK must never sleep or auto-retry — it surfaces the value.
/// </summary>
public class RateLimitRetryAfterTests
{
    private const string TestApiKey = "ck_test_xxxxxxxxxxxxxxxxxxxxx";

    private static async Task<DMZAgentRateLimitException> Provoke429Async(IReadOnlyDictionary<string, string>? headers)
    {
        var stub = new StubHttpMessageHandler(
            (HttpStatusCode)429,
            "{\"detail\":\"rate cap reached\",\"error\":\"rate_limited\"}",
            headers: headers);
        using var cx = new DMZAgentClient(TestApiKey, handler: stub);

        var exc = await ((Func<Task>)(() => cx.SubjectSaysAsync(
                subjectId:      "user:ws_test:cust",
                text:           "hi",
                agentSubjectId: "user:ws_test:bot",
                subjectType:    "sensor")))
            .Should().ThrowAsync<DMZAgentRateLimitException>();
        return exc.Which;
    }

    [Fact]
    public async Task RetryAfter_present_delta_seconds_is_parsed()
    {
        var exc = await Provoke429Async(new Dictionary<string, string> { ["Retry-After"] = "30" });
        exc.RetryAfter.Should().Be(30);
        exc.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task RetryAfter_zero_is_parsed_as_zero()
    {
        var exc = await Provoke429Async(new Dictionary<string, string> { ["Retry-After"] = "0" });
        exc.RetryAfter.Should().Be(0);
    }

    [Fact]
    public async Task RetryAfter_absent_is_null()
    {
        var exc = await Provoke429Async(headers: null);
        exc.RetryAfter.Should().BeNull();
    }

    [Theory]
    [InlineData("soon")]                            // non-numeric garbage
    [InlineData("Wed, 21 Oct 2026 07:28:00 GMT")]   // HTTP-date form — not delta-seconds
    [InlineData("-5")]                              // negative — invalid delta-seconds
    [InlineData("1.5")]                             // decimals — invalid delta-seconds
    [InlineData("30 seconds")]                      // trailing junk
    [InlineData("99999999999999999999")]            // overflow
    [InlineData("")]                                // empty value
    public async Task RetryAfter_unparseable_is_null(string headerValue)
    {
        var exc = await Provoke429Async(new Dictionary<string, string> { ["Retry-After"] = headerValue });
        exc.RetryAfter.Should().BeNull($"Retry-After '{headerValue}' is not valid delta-seconds");
    }
}
