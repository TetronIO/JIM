// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Localisation;

/// <summary>
/// How the portal words why an unresolved reference did not resolve and what to do about it (#3030): in the viewer's
/// language, falling back to the English the diagnosis carries.
/// </summary>
public static class UnresolvedReferenceTextDisplay
{
    /// <summary>What is wrong with one value, in the viewer's language.</summary>
    public static string ShowFinding(this ConnectedSystemObjectUnresolvedReference diagnosis) =>
        MessageDisplay.Show(diagnosis.FindingDetail, diagnosis.Finding) ?? string.Empty;

    /// <summary>What to do about one value, in the viewer's language.</summary>
    public static string ShowRemedy(this ConnectedSystemObjectUnresolvedReference diagnosis) =>
        MessageDisplay.Show(diagnosis.RemedyDetail, diagnosis.Remedy) ?? string.Empty;

    /// <summary>What is wrong, for a step that addresses several values, in the viewer's language.</summary>
    public static string ShowFinding(this UnresolvedReferenceFix fix) =>
        MessageDisplay.Show(fix.FindingDetail, fix.Finding) ?? string.Empty;

    /// <summary>What to do about it, for a step that addresses several values, in the viewer's language.</summary>
    public static string ShowRemedy(this UnresolvedReferenceFix fix) =>
        MessageDisplay.Show(fix.RemedyDetail, fix.Remedy) ?? string.Empty;
}
