// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Services;

/// <summary>
/// The portal's entry point for starting a configuration change preview (#827). Surfaces inject this rather than
/// calling the application layer directly, so the informed-choice prompt for large previews is applied once rather
/// than remembered per surface.
/// </summary>
public interface IConfigurationChangePreviewStarter
{
    /// <summary>
    /// Starts a preview, asking first where the estimated size warrants it.
    /// </summary>
    /// <returns>
    /// The preview's Activity id, to hand to <c>ConfigurationChangePreviewPanel</c>; or null when the
    /// administrator was shown the cost of a large preview and declined it, in which case nothing was started.
    /// </returns>
    Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request);
}
