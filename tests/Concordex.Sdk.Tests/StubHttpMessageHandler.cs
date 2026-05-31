using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Concordex.Sdk.Tests;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> per contract-tests runner spec
/// step 2 (sdk-spec runner-spec.md). Captures the outgoing request
/// (method/path/headers/body) and returns a caller-controlled response.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest      { get; private set; }
    public string?             LastRequestBody  { get; private set; }
    public string?             LastRequestPath  { get; private set; }

    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(HttpStatusCode status, string responseBody, string contentType = "application/json")
        : this((_, _) => MakeResponse(status, responseBody, contentType))
    { }

    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest     = request;
        LastRequestPath = request.RequestUri?.AbsolutePath;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        return _responder(request, LastRequestBody ?? string.Empty);
    }

    public static HttpResponseMessage MakeResponse(HttpStatusCode status, string body, string contentType = "application/json")
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
    }
}
