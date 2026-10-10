// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// One option of a <see cref="SegmentedToggle{TValue}"/>: the value it chooses, the label shown, an optional test id
/// for the option, an optional link and an optional tooltip. An option with an <paramref name="Href"/> is a link to
/// that page rather than a button raising <c>ValueChanged</c>, for a control that chooses between sibling pages
/// (Connected Systems and Connectors), so each can be opened in a new tab like any other link. An option with a
/// <paramref name="Tooltip"/> explains itself on hover, for a label too terse to say what it chooses.
/// </summary>
public sealed record SegmentedToggleOption<TValue>(TValue Value, string Label, string? TestId = null, string? Href = null,
    string? Tooltip = null);
