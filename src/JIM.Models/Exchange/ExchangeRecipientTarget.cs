// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// The directory account a queued Exchange change is converged on (#2021): who it is, by the identifiers the
/// directory gives it, and where it was written.
/// </summary>
public sealed record ExchangeRecipientTarget
{
    /// <summary>
    /// The Connected System Object the change is for.
    /// </summary>
    public required Guid ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The account's immutable anchor (Active Directory's <c>objectGUID</c>), or null where the object has none yet.
    /// Preferred over the name, because a rename between the export and the delivery leaves it unchanged.
    /// </summary>
    public Guid? ObjectGuid { get; init; }

    /// <summary>
    /// The account's distinguished name, or null where it is not known.
    /// </summary>
    public string? DistinguishedName { get; init; }

    /// <summary>
    /// The account's Object Type (user, group, contact), which decides which Exchange cmdlets apply.
    /// </summary>
    public required string ObjectType { get; init; }

    /// <summary>
    /// The domain controller the export wrote the account to, or null where the export did not say. Exchange is told
    /// to use it, so it finds an account the export has only just written.
    /// </summary>
    public string? DomainController { get; init; }

    public override string ToString() => DistinguishedName ?? ObjectGuid?.ToString() ?? ConnectedSystemObjectId.ToString();
}
