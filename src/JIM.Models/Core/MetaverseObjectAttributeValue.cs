// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(StringValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(DateTimeValue))]
[Index(nameof(IntValue))]
[Index(nameof(LongValue))]
[Index(nameof(ReferenceValue))]
[Index(nameof(UnresolvedReferenceValue))]
[Index(nameof(GuidValue))]
public class MetaverseObjectAttributeValue
{
    public Guid Id { get; set; }
    public MetaverseAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }
    public MetaverseObject MetaverseObject { get; set; } = null!;
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
    public byte[]? ByteValue { get; set; }

    /// <summary>
    /// A reference to another Metaverse Object. Used for attributes like 'Manager' and 'Member'.
    /// </summary>
    public MetaverseObject? ReferenceValue { get; set; }
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// When wanting to set a ReferenceValue, the referenced MVO may not yet exist as it might not have been projected from a CS to the MV yet.
    /// In this situation, the reference should be staged by setting a reference to the projecting CSO here. The sync processor can then run through these at
    /// the end of a sync run when all MVOs have been projected, and convert the UnresolvedReferenceValue to a ReferenceValue.
    /// </summary>
    public ConnectedSystemObject? UnresolvedReferenceValue { get; set; }
    public Guid? UnresolvedReferenceValueId { get; set; }

    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }

    /// <summary>
    /// If this attribute value was contributed to the Metaverse by a Connected System, then this identifies that system.
    /// Null when the attribute value is managed internally (not contributed by any Connected System).
    /// Retained alongside <see cref="ContributedBySyncRuleId"/>: denormalised so it survives deletion of the
    /// contributing Synchronisation Rule, and keeps winner-share analytics a single-column aggregate.
    /// </summary>
    public ConnectedSystem? ContributedBySystem { get; set; }
    public int? ContributedBySystemId { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose mapping won attribute priority resolution and contributed this value.
    /// Together with <see cref="AttributeId"/> this identifies the winning mapping. Null when the value is managed
    /// internally (not contributed by a Synchronisation Rule), or when the contributing rule has since been deleted
    /// (the FK is set null on rule deletion; <see cref="ContributedBySystemId"/> is retained as the denormalised record).
    /// </summary>
    public SyncRule? ContributedBySyncRule { get; set; }
    public int? ContributedBySyncRuleId { get; set; }

    /// <summary>
    /// When true, this row is an asserted null: a connected, in-scope contributor positively asserted "no value"
    /// for this attribute ("Null is a value" set on the winning mapping, or an import expression evaluating to null).
    /// All value columns are null and the row carries provenance. Distinct from the absence of any row, which means
    /// "no contributor". Carries no value: engine-side read queries filter these rows out so the synchronisation hot
    /// path treats them exactly as an absent row, while observability and Metaverse Object views include them so an
    /// admin can see a positively-asserted blank and its contributing rule/system. For a multivalued attribute under
    /// winner-takes-all-values, a single asserted-null marker row represents the asserted empty set.
    /// </summary>
    public bool NullValue { get; set; }

    /// <summary>
    /// True when this row would carry no information once its <see cref="ReferenceValueId"/> is discounted:
    /// every payload column is null, no unresolved reference is staged, and the row is not an asserted-null
    /// marker (<see cref="NullValue"/>). Deleting a Metaverse Object removes such rows from surviving
    /// referencing objects rather than nulling their FK, which would leave an informationless "ghost" row (#1019).
    /// Deliberately does NOT test <see cref="ReferenceValueId"/> itself; callers pair this with their own
    /// "references a deleted object" membership test. Must stay in step with the SQL predicates in
    /// SyncRepository.MvoOperations.DeleteMetaverseObjectsAsync, MetaverseRepository.DeleteMetaverseObjectAsync
    /// and the DeleteGhostMetaverseReferenceAttributeValues migration (parity pinned by
    /// MvoDeletionGhostReferenceRowDatabaseTests).
    /// </summary>
    public bool IsValuelessReferenceRow()
    {
        return StringValue == null &&
               DateTimeValue == null &&
               IntValue == null &&
               LongValue == null &&
               ByteValue == null &&
               GuidValue == null &&
               BoolValue == null &&
               UnresolvedReferenceValueId == null &&
               UnresolvedReferenceValue == null &&
               !NullValue;
    }

    public override string ToString()
    {
        var output = "";
        if (Attribute != null)
            output += $"{Attribute.Name}: ";

        if (!string.IsNullOrEmpty(StringValue))
        {
            output += StringValue;
            return output;
        }

        if (DateTimeValue != null)
        {
            output += DateTimeValue.ToString();
            return output;
        }

        if (IntValue != null)
        {
            output += IntValue.ToString();
            return output;
        }

        if (LongValue != null)
        {
            output += LongValue.ToString();
            return output;
        }

        if (ByteValue != null)
        {
            output += ByteValue.Length.ToString();
            return output;
        }

        if (GuidValue.HasValue)
        {
            output += GuidValue.Value.ToString();
            return output;
        }

        if (BoolValue.HasValue)
        {
            output += BoolValue.Value.ToString();
            return output;
        }

        if (ReferenceValue != null)
        {
            output += $"{ReferenceValue.Id} ({ReferenceValue.DisplayName})";
            return output;
        }

        return string.Empty;
    }
}