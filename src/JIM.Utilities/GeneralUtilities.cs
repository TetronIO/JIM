using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
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

        public static string GetMetaverseObjectHref(MetaverseObject metaverseObject)
        {
            return $"/t/{metaverseObject.Type.Name.ToLower()}/v/{metaverseObject.Id}";
        }
        public static string? GetMetaverseObjectHrefText(MetaverseObject metaverseObject)
        {
            return metaverseObject.GetAttributeValue(Constants.BuiltInAttributes.DisplayName)?.StringValue;
        }

        public static string GetMetaverseObjectHref(MetaverseObjectHeader metaverseObjectHeader)
        {
            return $"/t/{metaverseObjectHeader.TypeName.ToLower()}/v/{metaverseObjectHeader.Id}";
        }

        public static string? GetMetaverseObjectHrefText(MetaverseObjectHeader metaverseObjectHeader)
        {
            return metaverseObjectHeader.GetAttributeValue(Constants.BuiltInAttributes.DisplayName)?.StringValue;
        }

        public static string GetConnectedSystemHref(ConnectedSystem connectedSystem)
        {
            return $"/admin/connected-systems/{connectedSystem.Id}";
        }

        public static string GetConnectedSystemHref(ConnectedSystemHeader connectedSystemHeader)
        {
            return $"/admin/connected-systems/{connectedSystemHeader.Id}";
        }
    }
}