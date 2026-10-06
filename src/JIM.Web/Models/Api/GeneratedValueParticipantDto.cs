// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Connected System attribute a generated value is exported to, and how JIM checks the value's availability there
/// (Unique Value Generation, #242, release 3).
/// </summary>
public class GeneratedValueParticipantDto
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

    /// <summary>Whether this Connected System is excluded from the value's availability checks.</summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// Whether this Connected System can be excluded: only where the value is exported to it unchanged from a generated
    /// value on an import Synchronisation Rule.
    /// </summary>
    public bool CanBeExcluded { get; set; }

    /// <summary>
    /// How the value's availability is checked here: <c>JimRecordsAndProbe</c> (JIM's records of the system, and a
    /// probe asking the system directly), <c>JimRecordsOnly</c>, or <c>NotChecked</c>.
    /// </summary>
    public GeneratedValueParticipantCheck Check { get; set; }

    /// <summary>
    /// Why the check is less than <c>JimRecordsAndProbe</c>: <c>ConnectorCannotProbe</c>, <c>AttributeNotProbed</c>,
    /// <c>NotTextValue</c>, <c>ExportedThroughExpression</c>, <c>CombinedWithOtherSources</c> or <c>Excluded</c>;
    /// <c>None</c> when it is not.
    /// </summary>
    public GeneratedValueParticipantReason Reason { get; set; }

    public static GeneratedValueParticipantDto FromModel(GeneratedValueParticipant participant) => new()
    {
        ConnectedSystemId = participant.ConnectedSystemId,
        ConnectedSystemName = participant.ConnectedSystemName,
        ConnectorName = participant.ConnectorName,
        ConnectedSystemObjectTypeAttributeId = participant.ConnectedSystemObjectTypeAttributeId,
        AttributeName = participant.AttributeName,
        IsExcluded = participant.IsExcluded,
        CanBeExcluded = participant.CanBeExcluded,
        Check = participant.Check,
        Reason = participant.Reason
    };
}
