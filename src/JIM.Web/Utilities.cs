namespace JIM.Web
{
    public static class Utilities
    {
        /// <summary>
        /// Converts a string to a format that can be used as a Url parameter.
        /// </summary>
        public static string ConvertToUrlParam(string textToConvert)
        {
            return textToConvert.Replace(" ", "-");
        }

        /// <summary>
        /// Converts a string taken from a Url parameter back to the plain text version.
        /// </summary>
        public static string ConvertFromUrlParam(string urlParam)
        {
            return urlParam.Replace("-", " ");
        }
    }
}
