// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// How a generated value is checked for availability in one Connected System it is exported to (Unique Value
/// Generation, #242, release 3). See <see cref="GeneratedValueParticipant"/>.
/// </summary>
public enum GeneratedValueParticipantCheck
{
    /// <summary>
    /// JIM checks its own records of the Connected System and also probes it: asks the target directly whether the
    /// value is already in use.
    /// </summary>
    JimRecordsAndProbe = 0,

    /// <summary>
    /// JIM checks its own records of the Connected System only; <see cref="GeneratedValueParticipant.Reason"/> says
    /// why it does not probe.
    /// </summary>
    JimRecordsOnly = 1,

    /// <summary>
    /// The value is not checked against this Connected System at all; <see cref="GeneratedValueParticipant.Reason"/>
    /// says why.
    /// </summary>
    NotChecked = 2
}

/// <summary>
/// Why a generated value is checked less than fully in one Connected System it is exported to (Unique Value
/// Generation, #242, release 3). See <see cref="GeneratedValueParticipant"/>.
/// </summary>
public enum GeneratedValueParticipantReason
{
    /// <summary>Checked fully: JIM's records and a probe.</summary>
    None = 0,

    /// <summary>The Connected System's Connector cannot probe its target.</summary>
    ConnectorCannotProbe = 1,

    /// <summary>
    /// The Connector can probe, but not this attribute: either it identifies the account in this system (the external
    /// ID or the Distinguished Name), or its values only need to be unique within their container (a naming attribute),
    /// so a system-wide search would report false collisions.
    /// </summary>
    AttributeNotProbed = 2,

    /// <summary>Only text values are probed, and this attribute is not text.</summary>
    NotTextValue = 3,

    /// <summary>
    /// The value reaches this Connected System through an export expression, so what is written there differs from the
    /// generated value; JIM checks it neither against its own records nor by probe.
    /// </summary>
    ExportedThroughExpression = 4,

    /// <summary>
    /// The value reaches this Connected System combined with other sources in one Attribute Flow, so what is written
    /// there differs from the generated value; JIM does not check it.
    /// </summary>
    CombinedWithOtherSources = 5,

    /// <summary>
    /// An administrator excluded this Connected System: values already in use there do not stop JIM choosing them.
    /// </summary>
    Excluded = 6
}
