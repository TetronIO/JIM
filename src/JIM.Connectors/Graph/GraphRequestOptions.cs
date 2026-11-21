// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>How one Microsoft Graph request is sent, beyond its method, address and body.</summary>
internal sealed record GraphRequestOptions
{
    public static readonly GraphRequestOptions Default = new();

    /// <summary>
    /// Sends <c>ConsistencyLevel: eventual</c>, which Graph requires for advanced queries such as <c>$count</c> and
    /// filters on properties like <c>onPremisesSyncBehavior</c>.
    /// </summary>
    public bool EventualConsistency { get; init; }

    /// <summary>
    /// How many writes the request makes against the tenant's write quota, for pacing. Null counts one write for any
    /// method other than GET and none for GET; a <c>$batch</c> request states how many of its requests are writes.
    /// </summary>
    public int? Writes { get; init; }
}
