using JIM.Models.Core;
using System.Text.RegularExpressions;

namespace JIM.Web
{
    public static class Utilities
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
                return String.Empty;

            return $"/t/{ConvertToUrlParam(metaverseObject.Type.Name)}/v/{metaverseObject.Id}";
        }

        public static string SplitOnCapitalLetters(this string inputString)
        {
            var words = Regex.Matches(inputString, @"([A-Z][a-z]+)").Cast<Match>().Select(m => m.Value);
            var withSpaces = string.Join(" ", words);
            return withSpaces;
        }
    }
}
