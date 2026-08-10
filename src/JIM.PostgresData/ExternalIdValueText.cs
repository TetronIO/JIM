// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq.Expressions;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.PostgresData;

/// <summary>
/// The single definition of how an external ID's stored value is rendered as text by a list-page query.
/// <para>
/// An external ID is stored in whichever typed column matches its attribute's declared
/// <see cref="JIM.Models.Core.AttributeDataType"/>: an Active Directory or Samba AD <c>objectGUID</c> anchor
/// lands in <c>GuidValue</c>, a SQL integer key in <c>IntValue</c>, an Oracle <c>NUMBER</c> key in
/// <c>DecimalValue</c>, and only an OpenLDAP-style <c>entryUUID</c> in <c>StringValue</c>. Reading
/// <c>StringValue</c> alone therefore blanks the External Id column for most Connected Systems, which is
/// exactly what issue #1286 was: the Connector Space list projected, searched and sorted on that one column.
/// </para>
/// <para>
/// These are EF Core expressions rather than an in-memory formatter so that one rule serves all three
/// uses. Searching and sorting happen in the database, over the whole result set, and cannot be served by a
/// coalesce applied after materialisation; projecting through the same expression is what guarantees that
/// the value a user sees, the value they can search for, and the key the rows are ordered by are the same
/// string. Npgsql translates each to a single correlated scalar subquery containing one <c>CASE</c>, so the
/// cost matches the <c>StringValue</c>-only projection it replaces. Keep the projection single-column:
/// selecting an anonymous type of the raw columns instead makes EF Core emit a <c>ROW_NUMBER()</c> window
/// over the entire attribute-value table.
/// </para>
/// <para>
/// The column priority mirrors <see cref="ConnectedSystemObjectAttributeValue.ToStringNoName"/>, which is
/// what the Connected System Object detail page renders. The rendered text agrees with it exactly for
/// string, integer, long, decimal, GUID and boolean values (PostgreSQL casts a <c>uuid</c> to the same
/// lowercase hyphenated form as <see cref="Guid.ToString()"/>, a <c>numeric</c> to the same
/// scale-preserving invariant form, and Npgsql renders a boolean as <c>'True'</c>/<c>'False'</c> to match
/// .NET). A <c>DateTime</c> is the one exception: PostgreSQL renders it in the database session's form
/// rather than the host's current culture. No connector declares a date and time anchor, and it is included
/// only so that the column can never silently blank again for a type someone adds later. The byte and
/// reference columns are deliberately absent; neither can identify an object.
/// </para>
/// </summary>
public static class ExternalIdValueText
{
    /// <summary>
    /// Renders a Connected System Object's external ID (primary or secondary) as the text a list page shows.
    /// Yields null when no value column is populated, so a caller can fall back to a snapshot column.
    /// </summary>
    public static readonly Expression<Func<ConnectedSystemObjectAttributeValue, string?>> FromAttributeValue = av =>
        !string.IsNullOrEmpty(av.StringValue) ? av.StringValue
        : av.DateTimeValue != null ? av.DateTimeValue.Value.ToString()
        : av.IntValue != null ? av.IntValue.Value.ToString()
        : av.LongValue != null ? av.LongValue.Value.ToString()
        : av.DecimalValue != null ? av.DecimalValue.Value.ToString()
        : av.GuidValue != null ? av.GuidValue.Value.ToString()
        : av.BoolValue != null ? av.BoolValue.Value.ToString()
        : null;

    /// <summary>
    /// The same rule for a Pending Export's not-yet-exported external ID, which carries the identical set of
    /// typed value columns.
    /// </summary>
    public static readonly Expression<Func<PendingExportAttributeValueChange, string?>> FromPendingExportAttributeValueChange = avc =>
        !string.IsNullOrEmpty(avc.StringValue) ? avc.StringValue
        : avc.DateTimeValue != null ? avc.DateTimeValue.Value.ToString()
        : avc.IntValue != null ? avc.IntValue.Value.ToString()
        : avc.LongValue != null ? avc.LongValue.Value.ToString()
        : avc.DecimalValue != null ? avc.DecimalValue.Value.ToString()
        : avc.GuidValue != null ? avc.GuidValue.Value.ToString()
        : avc.BoolValue != null ? avc.BoolValue.Value.ToString()
        : null;
}
