// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A sample input to a configuration tester (#2055): the values an object holds for one of its attributes, such as
/// one a Synchronisation Rule's scoping criteria compare or an Object Matching Rule reads. No values means the object
/// holds none.
/// </summary>
/// <param name="AttributeName">The attribute's name, as the object type's schema names it.</param>
/// <param name="Values">The values, typed or as text to be read as the attribute's type.</param>
public sealed record AttributeSample(string AttributeName, IReadOnlyList<object> Values);
