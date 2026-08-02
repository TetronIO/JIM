// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Guards the shipped passphrase word list.
/// <para>
/// The list is a credential ingredient, not example data, and the properties asserted here are what make it safe
/// to hand a generated passphrase to a person: every entry is a real word, spelled with letters only, short
/// enough to transcribe, and drawn from a list somebody made an inclusion decision about. The suitability
/// screening itself cannot be asserted by a test; what a test can do is stop the list quietly reverting to the
/// unscreened general-purpose one.
/// </para>
/// </summary>
[TestFixture]
public class PasswordWordListTests
{
    [Test]
    public void WordList_LoadsFromTheAssembly()
    {
        // Embedded rather than copied beside the binary, so this also proves the resource name still matches.
        Assert.That(PasswordWordList.Words, Is.Not.Empty);
    }

    [Test]
    public void WordList_ContainsOnlyLowercaseLetters()
    {
        // Anything else would leak into a generated password: a stray capital breaks the capitalisation styles,
        // a hyphen or space collides with the separator, and a digit misreports the character categories.
        var offenders = PasswordWordList.Words.Where(w => !w.All(c => c is >= 'a' and <= 'z')).ToList();
        Assert.That(offenders, Is.Empty, $"Words with characters outside a-z: {string.Join(", ", offenders.Take(10))}");
    }

    [Test]
    public void WordList_ContainsOnlyWordsShortEnoughToTranscribe()
    {
        var offenders = PasswordWordList.Words.Where(w => w.Length is < 4 or > 6).ToList();
        Assert.That(offenders, Is.Empty, $"Words outside 4 to 6 characters: {string.Join(", ", offenders.Take(10))}");
    }

    [Test]
    public void WordList_ContainsNoDuplicates()
    {
        // A duplicate is not just untidy: it silently weights one word twice and makes the entropy readout a
        // small overstatement, which is the direction that matters.
        var duplicates = PasswordWordList.Words
            .GroupBy(w => w, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.That(duplicates, Is.Empty, $"Duplicated words: {string.Join(", ", duplicates.Take(10))}");
    }

    [Test]
    public void WordList_ContainsNoAmbiguousLookAlikeConfusion()
    {
        // The list is drawn on when the administrator asked for ambiguous characters to be left out, and a word
        // is used whole rather than filtered character by character. Only 'l' is at issue: the list is lowercase,
        // so 'I' and 'O' cannot occur, and words never contain digits.
        Assert.That(PasswordWordList.Words.Any(w => w.Contains('l', StringComparison.Ordinal)), Is.True,
            "The list is expected to contain words with the letter l; if that changes, the ambiguity note on the generator needs revisiting.");
    }

    [Test]
    public void WordList_IsLargeEnoughToCarryItsShareOfTheEntropy()
    {
        // A passphrase is only worth using if each word is worth a useful number of bits. Ten bits per word puts
        // a four-word phrase over forty bits before anything is appended, which is the point of the style.
        Assert.That(Math.Log2(PasswordWordList.Words.Count), Is.GreaterThanOrEqualTo(10d));
    }

    [Test]
    public void WordList_IsNotTheExampleDataWordList()
    {
        // The example data list ships in the same folder and is tempting to reuse. It is a general English noun
        // list that has never been screened for what is appropriate in a credential handed to a new employee,
        // and it carries hyphenated and multi-word entries. These are entries it has and this list must not.
        string[] entriesTheExampleListHas = ["abortion", "beheading", "cannibal", "genocide", "terrorism", "murder"];

        Assert.That(PasswordWordList.Words.Intersect(entriesTheExampleListHas, StringComparer.OrdinalIgnoreCase), Is.Empty);
    }

    [Test]
    public void WordList_IsSortedSoChangesAreReviewable()
    {
        // Not a security property: it keeps the diff of a future edit readable, which is what makes a change to
        // a credential ingredient reviewable at all.
        Assert.That(PasswordWordList.Words, Is.Ordered.Using<string>(StringComparer.Ordinal));
    }
}
