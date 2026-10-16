// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Where the portal sends an administrator to act on an unresolved reference's likely cause (#959): the Connected System
/// tab the fix is made on.
/// </summary>
/// <param name="Text">The link's label.</param>
/// <param name="Href">The tab's address.</param>
public sealed record UnresolvedReferenceAction(string Text, string Href);
