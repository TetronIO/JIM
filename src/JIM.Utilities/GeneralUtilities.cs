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

        
        public static bool AreByteArraysTheSame(ReadOnlySpan<byte> array1, ReadOnlySpan<byte> array2)
        {
            // byte[] is implicitly convertible to ReadOnlySpan<byte>
            return array1.SequenceEqual(array2);
        }
    }
}