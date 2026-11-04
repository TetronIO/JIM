// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A Connector whose objects are placed by Distinguished Name suffix, standing in for a Connector's own rule so that
/// tests of the placement policy exercise the policy rather than any Connector. Counts how often it is prepared, so a
/// test can show the policy prepares once per run rather than once per object.
/// </summary>
/// <remarks>
/// Deliberately simpler than the LDAP Connector's rule (no escaping, no case or spacing normalisation beyond an
/// ordinal case-insensitive comparison); <c>LdapConnectorPartitionPlacementTests</c> tests the real one.
/// </remarks>
internal sealed class DistinguishedNameSuffixPlacement : IConnector, IConnectorPartitionPlacement
{
    public string Name => "Suffix Placement Connector";
    public string? Description => null;
    public string? Url => null;

    /// <summary>How many times JIM prepared a placer.</summary>
    public int PreparedCount { get; private set; }

    /// <summary>How many objects JIM asked the placers to place, over all of them.</summary>
    public int PlacedCount { get; private set; }

    public IConnectorPartitionPlacer PreparePartitionPlacement(IReadOnlyCollection<ConnectedSystemPartition> partitions)
    {
        PreparedCount++;
        return new Placer(this, partitions.OrderByDescending(partition => partition.ExternalId.Length).ToList());
    }

    private sealed class Placer(DistinguishedNameSuffixPlacement owner, List<ConnectedSystemPartition> longestFirst) : IConnectorPartitionPlacer
    {
        public ConnectedSystemPartition? GetPartition(string? objectIdentifier)
        {
            owner.PlacedCount++;
            if (string.IsNullOrEmpty(objectIdentifier))
                return null;

            return longestFirst.FirstOrDefault(partition =>
                objectIdentifier.Equals(partition.ExternalId, StringComparison.OrdinalIgnoreCase) ||
                objectIdentifier.EndsWith("," + partition.ExternalId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
