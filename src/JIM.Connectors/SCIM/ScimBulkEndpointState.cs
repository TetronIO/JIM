// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// What an export run has learned about a service provider's bulk endpoint, shared by every batch in
/// that run.
/// <para>
/// A provider advertising bulk support and then serving no such endpoint is a discovery document that
/// lies, and once one batch has proved it there is nothing to gain from every later batch proving it
/// again. The connector exports batches in parallel, so the flag is written once and never cleared:
/// reading a stale <c>false</c> costs one wasted request, whereas anything resettable would need
/// synchronising for no benefit.
/// </para>
/// </summary>
internal sealed class ScimBulkEndpointState
{
    private volatile bool _unavailable;

    public bool IsUnavailable => _unavailable;

    public void MarkUnavailable() => _unavailable = true;
}
