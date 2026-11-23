// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Localisation;

/// <summary>
/// The message catalogues JIM.Application renders text from (#3030): resource files under <c>Resources/</c>, named
/// <c>"&lt;assembly&gt;:&lt;resource file&gt;"</c> as <see cref="JIM.Models.Localisation.JimMessage"/> expects. Each is declared
/// once here so no call site writes the string.
/// </summary>
public static class MessageCatalogues
{
    /// <summary>What a Configuration Change Preview tells an administrator: findings, estimates, warnings and progress.</summary>
    public const string Preview = "JIM.Application:PreviewMessages";

    /// <summary>How a scoping explanation is worded: criteria, requirements, hints and the reasons an object is not connected.</summary>
    public const string Scoping = "JIM.Application:ScopingMessages";
}
