using System.Text.RegularExpressions;

namespace JIM.Utilities
{
    public static class Utilities
    {
        public static string SplitOnCapitalLetters(this string inputString)
        {
            var words = Regex.Matches(inputString, @"([A-Z][a-z]+)").Cast<Match>().Select(m => m.Value);
            var withSpaces = string.Join(" ", words);
            return withSpaces;
        }
    }
}