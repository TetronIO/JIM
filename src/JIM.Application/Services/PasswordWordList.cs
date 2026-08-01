// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;

namespace JIM.Application.Services;

/// <summary>
/// The words JIM builds a passphrase from.
/// <para>
/// This is deliberately not the <c>Words.en.txt</c> that ships beside it for example data generation. That list
/// is a general English noun list that has never been screened for what belongs in a credential handed to a new
/// employee (it contains, among others, "Abortion", "Beheading", "Cannibal", "Genocide" and "Terrorism"), and it
/// carries hyphenated and multi-word entries that would collide with the separator. Reusing it would have been
/// convenient and wrong.
/// </para>
/// <para>
/// This list is instead an allowlist: every entry was included by a decision rather than surviving the absence
/// of one. That asymmetry is the whole point. Screening a general list by removing what looks unsuitable only
/// removes what somebody thought to look for, and fails towards keeping something that should have gone;
/// selecting into an empty list fails towards leaving out a perfectly good word, which costs a fraction of a
/// bit and nothing else.
/// </para>
/// <para>
/// Entries are drawn from the example data list (so every one is a real, ordinarily-spelled English word),
/// restricted to four to six lowercase letters, and screened for suitability, for confusable homophones where
/// both members would otherwise appear, and for words that are simply hard to spell on hearing.
/// </para>
/// </summary>
internal static class PasswordWordList
{
    private const string ResourceName = "JIM.Application.Resources.PasswordWords.en.txt";

    private static readonly Lazy<IReadOnlyList<string>> LazyWords = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<int> LazyShortestWordLength =
        new(() => Words.Min(w => w.Length), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The words, in the order they ship. Loaded once; the file is embedded in the assembly rather than copied
    /// beside it, so it cannot go missing from a container image.
    /// </summary>
    internal static IReadOnlyList<string> Words => LazyWords.Value;

    /// <summary>
    /// The length of the shortest word in the list, which is what a guaranteed minimum passphrase length has to
    /// be calculated from.
    /// </summary>
    internal static int ShortestWordLength => LazyShortestWordLength.Value;

    private static IReadOnlyList<string> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The password word list '{ResourceName}' is missing from the assembly, so JIM cannot generate a passphrase. " +
                $"The resources present are: {string.Join(", ", assembly.GetManifestResourceNames())}.");

        using var reader = new StreamReader(stream);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim();
            if (word.Length > 0)
                words.Add(word);
        }

        if (words.Count == 0)
            throw new InvalidOperationException($"The password word list '{ResourceName}' is empty. JIM cannot generate a passphrase without it.");

        return words;
    }
}
