// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// A provider-agnostic view of an authenticated SSO identity, carrying only the claim
/// values JIM needs to resolve or provision a MetaverseObject. This decouples the
/// application layer from the web layer's ClaimsPrincipal plumbing: the presentation
/// layer extracts the relevant claims and hands over this DTO.
/// </summary>
public record SsoIdentity
{
    /// <summary>
    /// The immutable unique identifier for the user, taken from the configured SSO unique-id
    /// claim (for example the <c>sub</c> claim). Required; without it the user cannot be matched.
    /// </summary>
    public required string UniqueId { get; init; }

    /// <summary>Optional display name (OIDC <c>name</c> claim).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional first/given name (OIDC <c>given_name</c> claim).</summary>
    public string? FirstName { get; init; }

    /// <summary>Optional last/family name (OIDC <c>family_name</c> claim).</summary>
    public string? LastName { get; init; }

    /// <summary>Optional user principal name (OIDC <c>preferred_username</c> claim).</summary>
    public string? UserPrincipalName { get; init; }
}
