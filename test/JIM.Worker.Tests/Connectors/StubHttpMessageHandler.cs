// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A test double for <see cref="HttpMessageHandler"/> that records requests and returns scripted
/// responses, so SCIM HTTP behaviour is covered without touching a network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _responder;
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private int _callCount;

    /// <param name="responder">
    /// Builds the response for a request. The second argument is the 1-based call number, letting a
    /// test return a different response per attempt (for example throttle first, then succeed).
    /// </param>
    public StubHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    /// <summary>
    /// Every request seen, in order. Snapshotted so callers can enumerate without racing the handler.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    public int CallCount
    {
        get
        {
            lock (_gate)
                return _callCount;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The body must be read before the responder runs, because returning a response can dispose
        // the request content on some paths.
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        int callNumber;
        lock (_gate)
        {
            callNumber = ++_callCount;
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, request.Headers));
        }

        return await _responder(request, callNumber);
    }

    /// <summary>
    /// An immutable snapshot of a request, safe to assert against after the handler has moved on.
    /// </summary>
    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? Body,
        System.Net.Http.Headers.HttpRequestHeaders Headers);
}
