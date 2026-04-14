// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Partial update payload for a predefined search. All fields are optional; only the fields that
/// are provided (non-null) will be applied.
/// </summary>
public class UpdatePredefinedSearchRequest
{
    /// <summary>
    /// When set, toggles whether the search is visible to end users via the portal and the
    /// search API. Disabled searches remain visible in the admin UI.
    /// </summary>
    public bool? IsEnabled { get; set; }
}
