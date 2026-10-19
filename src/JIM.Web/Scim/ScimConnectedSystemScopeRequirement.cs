// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authorization;

namespace JIM.Web.Scim;

/// <summary>
/// The caller is an API key scoped to the SCIM 2.0 Service Provider of the Connected System named in the route (D3).
/// </summary>
public sealed class ScimConnectedSystemScopeRequirement : IAuthorizationRequirement;
