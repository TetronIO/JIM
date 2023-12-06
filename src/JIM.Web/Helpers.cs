using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Utilities;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace JIM.Web
{
    public static class Helpers
    {
        /// <summary>
        /// Converts a string to a format that can be used as a Url parameter.
        /// </summary>
        public static string ConvertToUrlParam(string textToConvert)
        {
            return textToConvert.Replace(" ", "-").ToLower();
        }

        /// <summary>
        /// Converts a string taken from a Url parameter back to the plain text version.
        /// Note: This does not change case from lower-case URL param to whatever it was originally.
        /// </summary>
        public static string ConvertFromUrlParam(string urlParam)
        {
            return urlParam.Replace("-", " ");
        }

        public static string GetMetaverseObjectUrl(MetaverseObject metaverseObject)
        {
            if (metaverseObject == null)
                return string.Empty;

            return $"/t/{ConvertToUrlParam(metaverseObject.Type.Name)}/v/{metaverseObject.Id}";
        }

        /// <summary>
        /// Returns the MetaverseObject for the currently signed-in JIM.Web user.
        /// </summary>
        public static async Task<MetaverseObject> GetUserAsync(JimApplication jimApplication, Task<AuthenticationState>? authenticationStateTask)
        {
            if (authenticationStateTask == null)
                throw new Exception("Authentication state not available");

            var userId = IdentityUtilities.GetUserId((await authenticationStateTask).User);
            var user = await jimApplication.Metaverse.GetMetaverseObjectAsync(userId);
            return user ?? throw new Exception($"User not found for user id: {userId}");
        }

        #region mudblazor related
        public static Color GetActivityMudBlazorColorForStatus(ActivityStatus status)
        {
            return status switch
            {
                ActivityStatus.Complete => Color.Success,
                ActivityStatus.InProgress => Color.Primary,
                ActivityStatus.CompleteWithError => Color.Warning,
                ActivityStatus.FailedWithError => Color.Error,
                _ => Color.Default,
            };
        }
        #endregion
    }
}
