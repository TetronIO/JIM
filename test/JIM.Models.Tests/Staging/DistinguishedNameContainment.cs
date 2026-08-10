// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A Distinguished Name containment rule standing in for a Connector's own, so that tests of scope resolution
/// exercise the resolution rather than any Connector.
/// </summary>
/// <remarks>
/// Deliberately simpler than the LDAP Connector's rule: no escaped separators, no normalisation of the spacing some
/// directories emit after each separator. Those belong to <c>LdapConnectorUtilitiesTests</c>, which tests the real
/// predicate; a second implementation of them here would be a second thing to keep correct with no second
/// beneficiary.
/// </remarks>
internal sealed class DistinguishedNameContainment : IConnectorContainment
{
    internal static DistinguishedNameContainment Instance { get; } = new();

    public bool IsWithinContainer(string? objectIdentifier, ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (string.IsNullOrWhiteSpace(objectIdentifier) || string.IsNullOrWhiteSpace(container.ExternalId))
            return false;

        if (objectIdentifier.Equals(container.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            // A subtree search returns its base entry; a one-level search does not.
            return container.Scope != ConnectedSystemContainerScope.OneLevel;
        }

        // Compare on the ",<base>" boundary so that OU=NotCorp,DC=x is not mistaken for a descendant of OU=Corp,DC=x.
        var suffix = "," + container.ExternalId;
        if (!objectIdentifier.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        // One level: what remains once the base is removed must be a single RDN.
        return container.Scope != ConnectedSystemContainerScope.OneLevel ||
               !objectIdentifier[..^suffix.Length].Contains(',', StringComparison.Ordinal);
    }
}
