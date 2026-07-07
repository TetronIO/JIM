// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Interfaces;
using System.Text.Json.Serialization;
namespace JIM.Models.ExampleData;

public class ExampleDataSet : IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity. Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation. Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    public bool BuiltIn { get; set; }

    /// <summary>
    /// The .NET Culture, i.e. "en-GB" the example data set values are in.
    /// More info: https://www.venea.net/web/culture_code
    /// </summary>
    public string Culture { get; set; } = null!;

    public List<ExampleDataSetValue> Values { get; set; } = new();

    [JsonIgnore]
    public List<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
}