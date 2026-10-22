// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// What one SCIM client's requests did to one Connected System's store within one 15-minute window, as counted by the
/// web tier and flushed to that window's summary Activity (D17).
/// </summary>
public sealed record ScimRequestTally
{
    /// <summary>
    /// The Connected System the requests were for.
    /// </summary>
    public required int ConnectedSystemId { get; init; }

    /// <summary>
    /// The key the requests were made with.
    /// </summary>
    public required Guid ApiKeyId { get; init; }

    /// <summary>
    /// The key's name when the requests were made.
    /// </summary>
    public required string ApiKeyName { get; init; }

    /// <summary>
    /// The start of the 15-minute UTC window the requests fell in.
    /// </summary>
    public required DateTime WindowStart { get; init; }

    /// <summary>
    /// When the first counted request arrived.
    /// </summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>
    /// When the last counted request arrived.
    /// </summary>
    public required DateTime LastSeen { get; init; }

    /// <summary>
    /// Resources created.
    /// </summary>
    public int Created { get; init; }

    /// <summary>
    /// Resources replaced or patched.
    /// </summary>
    public int Updated { get; init; }

    /// <summary>
    /// Resources deleted.
    /// </summary>
    public int Deleted { get; init; }

    /// <summary>
    /// Requests refused with a 4xx answer other than an authentication failure.
    /// </summary>
    public int Rejected { get; init; }

    /// <summary>
    /// The latest rejection in the window, as its status, SCIM error type, method and endpoint (never the error's
    /// detail, which can quote attribute values); null when nothing was rejected.
    /// </summary>
    public string? LatestRejection { get; init; }
}
