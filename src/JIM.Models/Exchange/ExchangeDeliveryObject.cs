// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Exchange;

/// <summary>
/// What the Exchange delivery lane reads about one account before converging its queued change (#2021): the
/// identifiers Exchange is told to act on, and what the account's declared recipient state is now, which is the
/// state the last import read with the connector-defined changes still awaiting Exchange laid over it.
/// <para>
/// The lane withdraws a queued change that no longer matches that state. Export evaluation withdraws a change
/// awaiting Exchange when the declaration goes back to what Exchange last stamped, and an import can move the account
/// on, so a change queued earlier can describe a state nobody declares any more; delivered, it would make a recipient
/// nobody asked for.
/// </para>
/// </summary>
public sealed record ExchangeDeliveryObject
{
    public required Guid ConnectedSystemObjectId { get; init; }

    /// <summary>The account's Object Type (user, group, contact).</summary>
    public required string ObjectType { get; init; }

    /// <summary>The account's anchor value, where it is a GUID (Active Directory's <c>objectGUID</c>).</summary>
    public Guid? ObjectGuid { get; init; }

    /// <summary>The account's distinguished name, where known.</summary>
    public string? DistinguishedName { get; init; }

    /// <summary>
    /// The five connector-defined Exchange attributes as the last import read them, by name; an attribute the import
    /// read no value for is absent.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ImportedValues { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// The connector-defined changes the account's Pending Export still holds for Exchange
    /// (<see cref="PendingExportAttributeValueChange.AwaitsExchange"/>).
    /// </summary>
    public IReadOnlyList<ExchangeHeldChange> HeldChanges { get; init; } = [];
}
