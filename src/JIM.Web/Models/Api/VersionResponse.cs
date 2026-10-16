// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// The product and version a JIM instance runs, as <c>GET /api/v1/health/version</c> returns it without
/// authentication.
/// </summary>
public class VersionResponse
{
    /// <summary>
    /// The product's name: always JIM.
    /// </summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>
    /// The version the instance runs, for example 0.15.0.
    /// </summary>
    public string Version { get; set; } = string.Empty;
}
