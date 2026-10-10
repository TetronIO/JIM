// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// Pairs of list pages that share a breadcrumb and are chosen between with a <see cref="SegmentedToggle{TValue}"/> of
/// links, held once so both pages of a pair offer the same choice.
/// </summary>
public static class SiblingPages
{
    /// <summary>Connected Systems and the Connectors they are built from.</summary>
    public static readonly IReadOnlyList<SegmentedToggleOption<string>> ConnectedSystems =
    [
        new("systems", "Connected Systems", Href: "/admin/connected-systems"),
        new("connectors", "Connectors", Href: "/admin/connected-systems/connectors")
    ];

    /// <summary>Example Data templates and the Data Sets they draw values from.</summary>
    public static readonly IReadOnlyList<SegmentedToggleOption<string>> ExampleData =
    [
        new("templates", "Templates", Href: "/admin/example-data"),
        new("datasets", "Data Sets", Href: "/admin/example-data/datasets")
    ];
}
