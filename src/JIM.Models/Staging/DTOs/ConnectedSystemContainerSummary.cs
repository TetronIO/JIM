// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Just enough of a Container to name it: its own name, and the identifier that tells two Containers of the same
/// name apart.
/// </summary>
/// <remarks>
/// For surfaces holding a Container id and needing to render it, where loading the Container (which pulls its
/// partition, its Connected System and its children) would be many times the weight of the answer. The Activity's
/// exclusion discard counts are the first such surface (#1255): they are keyed by id so that they survive the
/// Container being renamed, and a name has to be resolved when one is displayed.
/// </remarks>
/// <param name="Id">The Container's id.</param>
/// <param name="Name">The Container's own name, which is what leads a row wherever Containers are listed.</param>
/// <param name="ExternalId">
/// The Container's identifier in the Connected System's own terms, such as a Distinguished Name. What
/// distinguishes two Containers sharing a name in different branches.
/// </param>
public sealed record ConnectedSystemContainerSummary(int Id, string Name, string ExternalId);
