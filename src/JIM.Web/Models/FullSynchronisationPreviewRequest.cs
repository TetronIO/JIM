// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A request, from the drift notice or a Run Profile, to preview a Full Synchronisation of the Connected System on show
/// (#1530). Handed to the preview's host on the Details tab, which starts it once and says so, so the page can clear it
/// and a later visit to the tab does not start another.
/// </summary>
/// <param name="Id">Identifies this request, so one is told apart from the next.</param>
/// <param name="RunProfileId">The Full Synchronisation Run Profile it was asked from, if any: the one the panel then runs.</param>
public sealed record FullSynchronisationPreviewRequest(Guid Id, int? RunProfileId = null);
