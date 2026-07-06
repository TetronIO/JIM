// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating an Example Data Set.
/// </summary>
public class CreateExampleDataSetRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The .NET Culture, i.e. "en-GB", the values are in.
    /// </summary>
    [Required]
    [StringLength(10)]
    public string Culture { get; set; } = null!;

    public List<string>? Values { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against this Example Data Set's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for updating an Example Data Set.
/// </summary>
public class UpdateExampleDataSetRequest
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(10)]
    public string? Culture { get; set; }

    /// <summary>
    /// When supplied, replaces the entire set of values.
    /// </summary>
    public List<string>? Values { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against this Example Data Set's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}
