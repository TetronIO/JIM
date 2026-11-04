// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Something that will not work as a Connected System's settings stand, raised when they were saved.
/// </summary>
public class ConnectedSystemSettingWarningDto
{
    /// <summary>
    /// The name of the setting the warning is about; null when it is about the settings as a whole.
    /// </summary>
    public string? Setting { get; set; }

    /// <summary>
    /// What will not work, and what to do about it.
    /// </summary>
    public string Message { get; set; } = null!;
}
