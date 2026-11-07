// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An attribute export execution loaded in part (#2816): a large group's member attribute holding only the members the
/// batch's changes could match, beside how many it holds.
/// </summary>
/// <param name="AttributeId">The attribute loaded in part.</param>
/// <param name="AttributeName">Its name.</param>
/// <param name="AtLeast">How many values the object holds at least: counted up to a cap the load sets above anything the
/// export's changes could remove, so a decision that turns on the count (would the group be left empty?) is answered as the
/// whole value set would answer it.</param>
public sealed record ExportPartiallyLoadedAttribute(int AttributeId, string AttributeName, int AtLeast)
    : PartiallyLoadedAttribute(AttributeId, AttributeName)
{
    /// <inheritdoc />
    public override bool HoldsAnyValue => AtLeast > 0;

    /// <inheritdoc />
    public override int? AtLeastValueCount => AtLeast;

    /// <inheritdoc />
    public override string Describe() => $"for export, with the values its changes name and at least {AtLeast} held";
}
