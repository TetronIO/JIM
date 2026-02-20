using JIM.Models.Core;
using System.Security.Claims;
namespace JIM.Utilities;

public static class IdentityUtilities
{
    /// <summary>
    /// Gets the JIM Metaverse Object ID from the claims principal.
    /// This claim is added by JIM.Web when the user logs in via OIDC.
    /// </summary>
    /// <param name="claimsPrincipal">The claims principal.</param>
    /// <returns>The Metaverse Object ID.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the claim is not found.</exception>
    public static Guid GetUserId(ClaimsPrincipal claimsPrincipal)
    {
        return new Guid(claimsPrincipal.Claims.Single(q => q.Type == Constants.BuiltInClaims.MetaverseObjectId).Value);
    }

    /// <summary>
    /// Tries to get the JIM Metaverse Object ID from the claims principal.
    /// </summary>
    /// <param name="claimsPrincipal">The claims principal.</param>
    /// <param name="userId">The Metaverse Object ID if found.</param>
    /// <returns>True if the claim was found, false otherwise.</returns>
    public static bool TryGetUserId(ClaimsPrincipal claimsPrincipal, out Guid userId)
    {
        userId = Guid.Empty;
        var claim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == Constants.BuiltInClaims.MetaverseObjectId);
        if (claim == null)
            return false;

        return Guid.TryParse(claim.Value, out userId);
    }

    /// <summary>
    /// Gets the SSO unique identifier claim value from the claims principal.
    /// This is typically the object ID from the identity provider (e.g., Azure AD).
    /// </summary>
    /// <param name="claimsPrincipal">The claims principal.</param>
    /// <param name="claimType">The claim type to look for (from SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE).</param>
    /// <returns>The claim value, or null if not found.</returns>
    public static string? GetSsoUniqueIdentifier(ClaimsPrincipal claimsPrincipal, string claimType)
    {
        return claimsPrincipal.Claims.FirstOrDefault(c => c.Type == claimType)?.Value;
    }
}