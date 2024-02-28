using JIM.Models.Core;
using System.Security.Claims;

namespace JIM.Utilities
{
    public static class IdentityUtilities
    {
        public static Guid GetUserId(ClaimsPrincipal claimsPrincipal)
        {
            return new Guid(claimsPrincipal.Claims.Single(q => q.Type == Constants.BuiltInClaims.MetaverseObjectId).Value);
        }
    }
}