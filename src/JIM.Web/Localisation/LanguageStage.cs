// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Localisation;

/// <summary>
/// How far a portal language has got. Languages are delivered one at a time (engineering/LOCALISATION.md).
/// </summary>
public enum LanguageStage
{
    /// <summary>
    /// Intended, not being delivered yet: translations may exist for some strings, and new strings need none.
    /// </summary>
    Planned,

    /// <summary>
    /// Being delivered: every string must carry a translation in the change that adds it, so the language is complete
    /// the day its last hard-coded text is converted. Offered in development builds only.
    /// </summary>
    InProgress,

    /// <summary>
    /// Complete and offered to everyone.
    /// </summary>
    Released
}
