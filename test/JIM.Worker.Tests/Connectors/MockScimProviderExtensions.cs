// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestScimServiceProvider;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Drives the shared <see cref="MockScimProvider"/> in process, without a socket.
/// <para>
/// The provider itself lives in JIM.TestScimServiceProvider, where the integration stack's containerised
/// host also serves it over HTTPS. Keeping the transport out of the provider is what lets the unit suite
/// and the integration scenario exercise one implementation rather than two that drift apart.
/// </para>
/// </summary>
internal static class MockScimProviderExtensions
{
    internal static StubHttpMessageHandler CreateHandler(this MockScimProvider provider)
    {
        return new StubHttpMessageHandler(provider.Respond);
    }
}
