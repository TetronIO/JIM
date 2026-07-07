// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The shell dialect a code snippet is written in, used to drive syntax highlighting
/// of JIM-authored example commands (see <see cref="Helpers.HighlightShell"/>).
/// </summary>
public enum ShellLanguage
{
    /// <summary>Bourne-again shell (bash), for Linux/macOS example commands.</summary>
    Bash,
    /// <summary>PowerShell, for Windows and cross-platform example commands.</summary>
    PowerShell
}
