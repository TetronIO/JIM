// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// One Connected System attribute a generated value is exported to, and how JIM checks the value's availability
/// there (Unique Value Generation, #242, release 3): the read model behind the "Checked for availability in" panel on
/// the generated Attribute Flow form, the REST mapping DTO's <c>participants</c> and <c>Get-JIMSyncRuleMapping</c>.
/// One per (Connected System, attribute), ordered by Connected System name then attribute name.
/// </summary>
public class GeneratedValueParticipant
{
    /// <summary>The Connected System the value is exported to.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>The Connected System's name.</summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>The name of the Connected System's Connector.</summary>
    public string ConnectorName { get; set; } = null!;

    /// <summary>The Connected System attribute the value is exported as.</summary>
    public int ConnectedSystemObjectTypeAttributeId { get; set; }

    /// <summary>The Connected System attribute's name.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>Whether the generated mapping excludes this Connected System from its availability checks.</summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// Whether the Connected System can be excluded: only where the value is exported unchanged (a single-source,
    /// direct Attribute Flow) to it from an import-mode generated value. An export-mode generated value is checked only
    /// in its own Connected System, and a value exported through an expression is not checked at all, so neither has
    /// anything to exclude.
    /// </summary>
    public bool CanBeExcluded { get; set; }

    /// <summary>How the value's availability is checked here.</summary>
    public GeneratedValueParticipantCheck Check { get; set; }

    /// <summary>Why the check is less than <see cref="GeneratedValueParticipantCheck.JimRecordsAndProbe"/>; None when it is not.</summary>
    public GeneratedValueParticipantReason Reason { get; set; }

    /// <summary>
    /// Whether the Connected System's Connector can report that a value it was sent is already in use (release 4): only
    /// then can Collision Remediation act on a rejection there. Elsewhere, a collision is an ordinary export error.
    /// </summary>
    public bool ReportsCollisions { get; set; }
}
