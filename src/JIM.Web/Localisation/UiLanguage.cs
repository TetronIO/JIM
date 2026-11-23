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
/// <param name="IsReleased">
/// Whether the language is offered in production. A language stays unreleased while any portal text has no
/// translation in it, so nobody is shown a page that is half one language and half another. Development builds
/// offer every language, marked as a preview, so a translation can be checked as it fills in.
/// <c>LocalisationResourceTests</c> fails the build for a released language missing any translation.
/// </param>
public sealed record UiLanguage(string CultureName, string NativeName, bool IsReleased);
