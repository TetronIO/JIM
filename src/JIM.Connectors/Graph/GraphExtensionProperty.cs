// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Graph;

/// <summary>
/// A directory extension registered in the tenant, as <c>getAvailableExtensionProperties</c> describes it: a custom
/// property an application has added to users or groups, such as one synchronised from an on-premises attribute.
/// </summary>
internal sealed class GraphExtensionProperty
{
    /// <summary>The property's name on the object, i.e. <c>extension_0c1d2e3f4a5b6c7d8e9f_employeeNumber</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Binary, Boolean, DateTime, Integer, LargeInteger or String.</summary>
    [JsonPropertyName("dataType")]
    public string? DataType { get; set; }

    [JsonPropertyName("isMultiValued")]
    public bool IsMultiValued { get; set; }

    [JsonPropertyName("isSyncedFromOnPremises")]
    public bool? IsSyncedFromOnPremises { get; set; }

    /// <summary>The object types the property is on: User, Group, and others JIM does not synchronise.</summary>
    [JsonPropertyName("targetObjects")]
    public List<string> TargetObjects { get; set; } = [];

    [JsonPropertyName("appDisplayName")]
    public string? AppDisplayName { get; set; }

    [JsonPropertyName("deletedDateTime")]
    public DateTimeOffset? DeletedDateTime { get; set; }
}
