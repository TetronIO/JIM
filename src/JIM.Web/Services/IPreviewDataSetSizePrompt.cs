// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Services;

/// <summary>
/// Asks an administrator how much of a large configuration change preview to keep (#827, PRD scenario 5). A seam
/// rather than a direct dialog call, so the decision about *when* to ask (see
/// <see cref="ConfigurationChangePreviewStarter"/>) can be tested without a rendered dialog, and so a surface that
/// is not a Blazor page could answer it another way.
/// </summary>
public interface IPreviewDataSetSizePrompt
{
    /// <summary>
    /// Presents the estimated cost and the choice.
    /// </summary>
    /// <param name="estimatedDeltaRows">
    /// The estimated object-level rows the preview would produce. Stated to the administrator, because a choice
    /// offered without a size is not an informed one.
    /// </param>
    /// <returns>
    /// The chosen persistence, or null when the administrator backed out. Null means *do not run the preview*: they
    /// were shown a cost and declined it, which is not the same as accepting the recommendation.
    /// </returns>
    Task<ConfigurationChangePreviewDeltaPersistence?> AskAsync(long estimatedDeltaRows);
}
