// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Which resources a SCIM list request selects, in the store's own terms: the predicate model the SCIM server builds
/// from a request's filter and the repository renders as parameterised SQL, so JIM.Data never depends on the SCIM
/// protocol layer. Every value in it is passed as a parameter; nothing a client sent is ever written into SQL text.
/// </summary>
public abstract record ScimStoreFilter;
