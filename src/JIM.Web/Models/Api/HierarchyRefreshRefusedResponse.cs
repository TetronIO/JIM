// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// The 409 Conflict body of a hierarchy refresh asked to cancel, rather than disable the Run Profiles, when it would
/// remove a partition that Run Profiles target: nothing was changed. Send the request again with
/// <c>runProfileAction</c> set to <c>Disable</c> to apply the refresh and disable them, or point them at another
/// partition first.
/// </summary>
public class HierarchyRefreshRefusedResponse
{
    /// <summary>
    /// Why nothing was changed, naming each Run Profile and the partition it targets.
    /// </summary>
    [Required]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Each Run Profile that targets a partition the refresh would remove.
    /// </summary>
    [Required]
    public List<HierarchyRefreshRunProfileDto> AffectedRunProfiles { get; set; } = [];

    /// <summary>
    /// Creates the response from a refresh that was not applied.
    /// </summary>
    public static HierarchyRefreshRefusedResponse FromModel(HierarchyRefreshResult result) => new()
    {
        Message = RunProfileEnablement.DescribeCancelledRefresh(result.AffectedRunProfiles),
        AffectedRunProfiles = result.AffectedRunProfiles.Select(HierarchyRefreshRunProfileDto.FromModel).ToList()
    };
}
