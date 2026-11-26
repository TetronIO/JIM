// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.UniqueValues;

/// <summary>
/// One value a caller has built itself and wants issued to a Metaverse Object, if it is free
/// (<see cref="UniqueValueGenerationServer.ClaimValuesAsync"/>, #1789). Example data is the caller: it builds values
/// from its own patterns and Example Data Sets, and asks the service only whether each one is taken.
/// </summary>
public sealed record UniqueValueClaim
{
    /// <summary>
    /// The Metaverse attribute the value is for.
    /// </summary>
    public required int MetaverseAttributeId { get; init; }

    /// <summary>
    /// The attribute's data type. <see cref="AttributeDataType.Number"/> and <see cref="AttributeDataType.LongNumber"/>
    /// are checked as numbers and need <see cref="NumericValue"/>; every other type is checked as text.
    /// </summary>
    public required AttributeDataType TargetType { get; init; }

    /// <summary>
    /// The attribute's display name, for diagnostics.
    /// </summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// The value as text. Compared case-insensitively, as every uniqueness gate is.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// The value as a number, for a Number or Long Number attribute. Null otherwise.
    /// </summary>
    public long? NumericValue { get; init; }
}
