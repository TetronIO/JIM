// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;

namespace JIM.Web.Shared;

/// <summary>
/// Pairs of list pages that share a breadcrumb and are chosen between with a <see cref="SegmentedToggle{TValue}"/> of
/// links, held once so both pages of a pair offer the same choice.
/// </summary>
public static class SiblingPages
{
    /// <summary>Connected Systems and the Connectors they are built from.</summary>
    public static IReadOnlyList<SegmentedToggleOption<string>> ConnectedSystems =>
    [
        new("systems", DisplayText.SiblingConnectedSystems, Href: "/admin/connected-systems"),
        new("connectors", DisplayText.SiblingConnectors, Href: "/admin/connected-systems/connectors")
    ];

    /// <summary>Example Data templates and the Data Sets they draw values from.</summary>
    public static IReadOnlyList<SegmentedToggleOption<string>> ExampleData =>
    [
        new("templates", DisplayText.SiblingTemplates, Href: "/admin/example-data"),
        new("datasets", DisplayText.SiblingDataSets, Href: "/admin/example-data/datasets")
    ];
}
