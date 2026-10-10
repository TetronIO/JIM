// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// One option of a <see cref="SegmentedToggle{TValue}"/>: the value it chooses, the label shown, an optional test id
/// for the option, and an optional link. An option with an <paramref name="Href"/> is a link to that page rather than a
/// button raising <c>ValueChanged</c>, for a control that chooses between sibling pages (Connected Systems and
/// Connectors), so each can be opened in a new tab like any other link.
/// </summary>
public sealed record SegmentedToggleOption<TValue>(TValue Value, string Label, string? TestId = null, string? Href = null);
