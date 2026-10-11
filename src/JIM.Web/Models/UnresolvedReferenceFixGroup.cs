// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// The next steps for an object's unresolved references that are taken in one place (#959): JIM's configuration, or
/// the Connected System.
/// </summary>
/// <param name="FixLocation">Where the steps are taken.</param>
/// <param name="Fixes">The steps, most values first.</param>
public sealed record UnresolvedReferenceFixGroup(UnresolvedReferenceFixLocation FixLocation, IReadOnlyList<UnresolvedReferenceFix> Fixes);
