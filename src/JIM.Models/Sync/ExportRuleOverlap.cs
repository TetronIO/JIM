// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;
using JIM.Models.Resources;

namespace JIM.Models.Sync;

/// <summary>
/// Records a Metaverse Object that two or more enabled export Synchronisation Rules cover in the same Connected System and
/// Connected System Object Type while writing the same target attribute (#2826). Neither rule stages the contested
/// attributes for this object; everything else they write is staged into the object's one Pending Export, and its other
/// Connected Systems are unaffected. Surfaced to the administrator as an <c>ExportRuleOverlap</c> Run Profile Execution Item
/// error.
/// </summary>
/// <remarks>
/// A Metaverse Object holds one Connected System Object per Connected System, so every rule covering it there writes to the
/// same object. Rules that write different attributes share it (#2826); which of several rules wins one attribute is
/// export precedence, #2827. An overlap with no <see cref="AttributeNames"/> is one #2820 recorded, when any two rules
/// covering an object were an overlap; it reads as it did then.
/// </remarks>
public class ExportRuleOverlap
{
    /// <summary>
    /// The Metaverse Object the rules cover.
    /// </summary>
    public required Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System the rules export to.
    /// </summary>
    public required int ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of the Connected System the rules export to.
    /// </summary>
    public required string ConnectedSystemName { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type the rules target.
    /// </summary>
    public required string ObjectTypeName { get; set; }

    /// <summary>
    /// The names of the export Synchronisation Rules that write a contested attribute, in the order the run stages them.
    /// </summary>
    public required List<string> SyncRuleNames { get; set; }

    /// <summary>
    /// The target attributes more than one of the rules writes, which none of them exports for this object.
    /// </summary>
    public List<string> AttributeNames { get; set; } = [];

    /// <summary>
    /// The Connected System Object the Metaverse Object already holds in that Connected System, or null when it has none
    /// yet (the rules would both have provisioned one).
    /// </summary>
    public Guid? ExistingConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Says what happened and how to fix it, for the administrator: the error message of the run's execution item and of
    /// the Synchronisation Preview's error alike, so the two read the same.
    /// </summary>
    public string DescribeForAdministrator() => Describe().ToEnglish();

    /// <summary><see cref="DescribeForAdministrator"/> as a message.</summary>
    public JimMessage Describe() => AttributeNames.Count > 0
        ? JimMessage.Create(MessageCatalogues.Sync, nameof(SyncMessages.ExportRuleAttributeClash), SyncRuleNames.Count, ObjectTypeName,
            ConnectedSystemName, string.Join(", ", AttributeNames.Select(name => $"'{name}'")),
            string.Join(", ", SyncRuleNames.Select(name => $"'{name}'")))
        : JimMessage.Create(MessageCatalogues.Sync, nameof(SyncMessages.ExportRuleOverlap), SyncRuleNames.Count, ObjectTypeName, ConnectedSystemName,
            string.Join(", ", SyncRuleNames.Select(name => $"'{name}'")));
}
