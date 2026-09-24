// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// The visibility tier of a feature flag (#1781).
/// </summary>
public enum FeatureFlagTier
{
    /// <summary>
    /// Never surfaced to administrators. On only in development and in the integration harness (enabled through
    /// PowerShell in scenario setup). Never shown in Production, including in the Service Settings portal page.
    /// </summary>
    InDevelopment = 0,

    /// <summary>
    /// Visible to administrators in the Service Settings page's "Preview features" card, documented on the Preview
    /// features page, and labelled with a Preview chip wherever the feature surfaces.
    /// </summary>
    Preview = 1
}
