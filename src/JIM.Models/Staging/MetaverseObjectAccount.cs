// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One account a Metaverse Object is joined to, as an administrator choosing where to set a password sees it
/// (issue #1172).
/// <para>
/// A flat projection rather than a Connected System Object, because the choice is made over a handful of rows
/// showing which system an account is in and what it is called there. Carrying the whole graph would load
/// every attribute value of every account to render a checkbox.
/// </para>
/// </summary>
public class MetaverseObjectAccount
{
    public required Guid ConnectedSystemObjectId { get; init; }

    public required int ConnectedSystemId { get; init; }

    public required string ConnectedSystemName { get; init; }

    /// <summary>
    /// What the account is called in the Connected System: its Distinguished Name, or whatever else identifies
    /// it there. This is what an administrator matches against the person in front of them.
    /// </summary>
    public required string AccountIdentifier { get; init; }

    /// <summary>
    /// Whether the Connected System's Connector can set a password at all. False is reported rather than
    /// filtered out: an administrator looking for a system that is not in the list needs to know it was
    /// considered and cannot do this, not be left wondering whether JIM knows about the account.
    /// </summary>
    public required bool ConnectorCanSetPasswords { get; init; }

    /// <summary>
    /// What JIM last discovered about the Connected System's password rules, or null where nothing was read.
    /// Fed to reconciliation so a password generated for several accounts satisfies all of their systems.
    /// </summary>
    public ConnectedSystemPasswordPolicy? DiscoveredPolicy { get; init; }

    /// <summary>
    /// Whether this Connected System's password rules can be read by JIM at all.
    /// <para>
    /// The difference between "JIM has not read the rules yet" and "there are no rules to read" decides whether
    /// an administrator has anything to do about it. Only some systems publish a password policy a client can
    /// read; where none is published, an absent policy is expected rather than a gap to close.
    /// </para>
    /// <para>
    /// Derived from two facts: the Connector's own capability, and the outcome recorded on the discovered
    /// policy row (<see cref="ConnectedSystemPasswordPolicy.DiscoveryOutcome"/>). A Connector that can read
    /// policies still cannot read one from a directory that publishes none, so a row whose outcome is
    /// <see cref="PasswordPolicyDiscoveryOutcome.NotPublished"/> makes this false; no row at all leaves it to the
    /// Connector's capability, because the schema has simply not been read yet.
    /// </para>
    /// </summary>
    public bool ConnectorCanDiscoverPasswordPolicy { get; init; }

    /// <summary>
    /// The expiry behaviours this Connected System's Connector can apply. Empty when it cannot set passwords.
    /// Callers offering a choice across several accounts must offer only what all of them can honour.
    /// </summary>
    public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours { get; init; } = [];
}
