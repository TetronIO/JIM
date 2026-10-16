// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// One attribute's share of a probe of one Connected System (Generated Value Forms, #2023): the attribute to search,
/// and the values to look for in it, one to ten of them.
/// </summary>
/// <param name="ConnectedSystemObjectTypeAttributeId">The attribute to search for the candidates.</param>
/// <param name="Candidates">The values to look for, in the order their outcomes are answered.</param>
public sealed record UniquenessProbeBatch(int ConnectedSystemObjectTypeAttributeId, IReadOnlyList<string> Candidates);
