// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Request to update a certificate's editable properties.
/// </summary>
public class UpdateCertificateRequest
{
    /// <summary>
    /// New name for the certificate (optional, null to keep current).
    /// </summary>
    [StringLength(255, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 255 characters.")]
    public string? Name { get; set; }

    /// <summary>
    /// Updated notes (optional, null to keep current).
    /// </summary>
    [StringLength(2000, ErrorMessage = "Notes must not exceed 2000 characters.")]
    public string? Notes { get; set; }

    /// <summary>
    /// Enable or disable the certificate (optional, null to keep current).
    /// </summary>
    public bool? IsEnabled { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Change reason must not exceed 1000 characters.")]
    public string? ChangeReason { get; set; }
}
