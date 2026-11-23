// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Localisation;

/// <summary>
/// A language the portal can be shown in.
/// </summary>
/// <param name="CultureName">
/// The culture the portal switches to: a specific culture where spelling differs by region (<c>en-GB</c>,
/// <c>en-US</c>), otherwise the neutral language (<c>de</c>, <c>fr</c>), so every regional variant of it (de-AT,
/// de-CH, fr-BE, fr-CA) is served the same translation through .NET's parent-culture fallback.
/// </param>
/// <param name="NativeName">The language's name written in that language, as the picker shows it.</param>
/// <param name="Stage">
/// How far the language has got: see <see cref="LanguageStage"/>. Languages are delivered one at a time, so at most a
/// few are in progress while the rest wait as planned.
/// </param>
public sealed record UiLanguage(string CultureName, string NativeName, LanguageStage Stage)
{
    /// <summary>
    /// Whether the language is offered in production. A language stays unreleased while any portal text has no
    /// translation in it, so nobody is shown a page that is half one language and half another. Development builds
    /// offer every language, marked as a preview, so a translation can be checked as it fills in.
    /// </summary>
    public bool IsReleased => Stage == LanguageStage.Released;

    /// <summary>
    /// Whether every string must be translated into this language in the change that adds or edits it. True once a
    /// language is in progress: from then on <c>LocalisationResourceTests</c> fails a change that leaves it behind.
    /// </summary>
    public bool IsRequiredInEveryChange => Stage is LanguageStage.InProgress or LanguageStage.Released;
}
