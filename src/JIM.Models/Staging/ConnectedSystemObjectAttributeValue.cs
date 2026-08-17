// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectAttributeValue
{
    /// <summary>
    /// The MetaverseObjectId of the referenced CSO (the CSO pointed to by ReferenceValueId).
    /// Populated via direct SQL in the repository to avoid the deep EF Include chain
    /// (AttributeValues → ReferenceValue → MetaverseObject) that fails at scale.
    /// </summary>
    [NotMapped]
    public Guid? ResolvedReferenceMetaverseObjectId { get; set; }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// The parent attribute for this attribute value object.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }

    /// <summary>
    /// The parent Connected System Object for this attribute value object.
    /// </summary>
    public ConnectedSystemObject ConnectedSystemObject { get; set; } = null!;

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    /// <summary>
    /// This holds a link to the referenced object immediately after provisioning from the Metaverse, or after unresolved references are resolved at the end of imports.
    /// Termed as a hard reference, as the soft reference will have been resolved to a Connected System Object as part of setting this value.
    /// </summary>
    public ConnectedSystemObject? ReferenceValue { get; set; }
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// This holds the soft (aka raw) reference value from the Connected System before it gets resolved into a hard reference to another Connected System Object as part of an Import operation.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }

    /// <summary>
    /// The string a target system is handed when this value is an anchor being written into a
    /// reference (for example a manager column, or a group member). Null when no anchor-capable slot
    /// holds a value, which callers must treat as "not resolvable yet", never as an empty anchor:
    /// a database-generated anchor has no value until the object's own export is confirmed (#1398).
    /// </summary>
    /// <remarks>
    /// Every slot an external ID can occupy is read (#1386 stores confirmed anchors typed, so a
    /// LongNumber anchor lives in <see cref="LongValue"/> and a high-precision Oracle NUMBER in
    /// <see cref="DecimalValue"/>, #1283). Decimals render canonically so 4200.00 and 4200 write the
    /// same anchor; numbers pin the invariant culture so the rendering does not depend on the
    /// thread that produced it. Case is preserved: this is the value the target system receives,
    /// not a lookup key.
    /// </remarks>
    public string? ToReferenceValueString()
    {
        return StringValue
            ?? GuidValue?.ToString()
            ?? IntValue?.ToString(CultureInfo.InvariantCulture)
            ?? LongValue?.ToString(CultureInfo.InvariantCulture)
            ?? (DecimalValue.HasValue ? ExternalIdValue.ToCanonicalString(DecimalValue.Value) : null);
    }

    public override string ToString()
    {
        if (Attribute != null)
            return $"{Attribute.Name}: {ToStringNoName()}";

        return ToStringNoName() ?? "";
    }

    public string? ToStringNoName()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue != null)
            return DateTimeValue.ToString();

        if (IntValue != null)
            return IntValue.ToString();

        if (LongValue != null)
            return LongValue.ToString();

        if (DecimalValue != null)
            return DecimalValue.Value.ToString(CultureInfo.InvariantCulture);

        if (ByteValue != null)
            return ByteValue.Length.ToString();

        if (GuidValue.HasValue)
            return GuidValue.Value.ToString();

        if (BoolValue.HasValue)
            return BoolValue.Value.ToString();

        if (ReferenceValue != null && !string.IsNullOrEmpty(UnresolvedReferenceValue))
            return $"Resolved: {ReferenceValue.Id}. Unresolved: {UnresolvedReferenceValue}";

        if (ReferenceValue != null)
            return "Resolved: " + ReferenceValue.Id;

        if (string.IsNullOrEmpty(UnresolvedReferenceValue)) 
            return string.Empty;
        
        return "Unresolved: " + UnresolvedReferenceValue;
    }
}