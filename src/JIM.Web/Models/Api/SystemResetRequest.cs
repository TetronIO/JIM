// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Options for a factory reset (POST /api/v1/system/reset).
/// </summary>
public class SystemResetRequest
{
    /// <summary>
    /// When false (the default), Metaverse Objects holding the built-in Administrator role are preserved
    /// so the operator is not locked out of the portal. When true, those administrator identities are
    /// removed as well, leaving a true brand-new install.
    /// </summary>
    public bool IncludeAdministrators { get; set; }

    /// <summary>
    /// Acknowledges the portal lockout risk. An administrator-inclusive wipe is refused when no initial
    /// administrator is configured (JIM_SSO_INITIAL_ADMIN), because the portal would then be inaccessible
    /// afterwards. Set to true to proceed anyway (for example when access is retained via the
    /// infrastructure API key). Ignored when IncludeAdministrators is false.
    /// </summary>
    public bool AcknowledgeAdministratorLockout { get; set; }
}
