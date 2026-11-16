// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// One choice in a <see cref="VirtualisedFilterHeader{TValue}"/>'s menu: the value the page filters by, and the
/// words the reader picks it by.
/// </summary>
public sealed record VirtualisedFilterOption<TValue>(TValue Value, string Label);
