// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestGraphService;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The simulated tenant the integration stack serves as a stand-in for Microsoft Graph (<see cref="MockGraphTenant"/>),
/// driven in process through a handler that records every request for the test to assert on.
/// </summary>
internal sealed class FakeGraphTenant : MockGraphTenant
{
    public FakeGraphTenant()
    {
        Handler = new StubHttpMessageHandler(Respond);
    }

    public StubHttpMessageHandler Handler { get; }

    public IEnumerable<StubHttpMessageHandler.RecordedRequest> GraphRequests =>
        Handler.Requests.Where(r => r.RequestUri!.Host == "graph.microsoft.com");
}
