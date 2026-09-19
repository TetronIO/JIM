// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the object chip convention documented in <c>src/JIM.Web/CLAUDE.md</c> > "Object chips": a
/// reference to a Connected System Object, a Metaverse Object, a Connected System, a Synchronisation Rule, a
/// Pending Export, a Deletion Record or a Run Profile is an <c>&lt;ObjectChip&gt;</c>, never a hand-rolled
/// <c>MudAvatar</c>/<c>MudChip</c> pair.
/// <para>
/// This is a source-shape test rather than a bUnit render test, for the same reason as
/// <see cref="SearchFieldConventionTests"/>: every individual chip renders correctly in isolation, and the
/// defect this catches is a *new* page hand-rolling another one. Only a sweep over the whole of
/// <c>src/JIM.Web</c> can see that. The detection targets exactly the shape this convention replaced across
/// the site: a <c>MudAvatar</c> whose two- or three-letter text is one of the object glyph
/// abbreviations ObjectChip renders (CS/MV/CSO/MVO/SR/PE/RP/DR). A generic MudAvatar demo (a colour swatch, an
/// unrelated initialism) does not match unless it happens to spell one of those abbreviations, which is the
/// same trade-off <see cref="VocabularyAbbreviationScanTests"/> makes for CSO/MVO in prose.
/// </para>
/// </summary>
[TestFixture]
public class ObjectChipConventionTests
{
    /// <summary>
    /// A hand-rolled avatar that is deliberately not an object chip (a generic design-system swatch, or an
    /// avatar naming something that only coincidentally spells one of the glyph abbreviations) opts out with
    /// this marker on the line, or the two lines, immediately above it. Deliberately a visible comment rather
    /// than an allowlist in this file, so the justification travels with the markup.
    /// </summary>
    private const string ExemptionMarker = "object-chip: exempt";

    /// <summary>The component that owns the convention. It is the one place these glyphs are expected.</summary>
    private const string ComponentFileName = "ObjectChip.razor";

    /// <summary>How many lines above the tag are searched for <see cref="ExemptionMarker"/>.</summary>
    private const int ExemptionMarkerLookBehindLines = 2;

    /// <summary>
    /// The object glyph abbreviations ObjectChip renders (<c>ObjectChip.razor</c>'s <c>GlyphText</c>): the
    /// three-letter pair for the two object kinds, and the two-letter glyph for everything else.
    /// </summary>
    private static readonly HashSet<string> ObjectGlyphTexts = new(StringComparer.Ordinal)
    {
        "CS", "MV", "CSO", "MVO", "SR", "PE", "RP", "DR"
    };

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void ObjectAvatars_AcrossJimWeb_UseTheSharedObjectChipComponent()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ComponentFileName, StringComparison.Ordinal))
            .SelectMany(FindUnmigratedObjectAvatars)
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} hand-rolled object avatar(s) found. Use <ObjectChip /> instead:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}  {offender.Description}");

        message
            .AppendLine()
            .AppendLine("A MudAvatar carrying CS/MV/CSO/MVO/SR/PE/RP/DR names the same object kinds ObjectChip")
            .AppendLine("renders with its own glyph, prefix and tooltip; see src/JIM.Web/CLAUDE.md > \"Object chips\".")
            .AppendLine()
            .AppendLine($"If the avatar genuinely names something else, add a Razor comment containing")
            .AppendLine($"\"{ExemptionMarker}\" directly above it, saying why.");

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// Finds every <c>MudAvatar</c> in a Razor file whose text is one of <see cref="ObjectGlyphTexts"/> and has
    /// neither been migrated nor exempted.
    /// </summary>
    private static IEnumerable<Offender> FindUnmigratedObjectAvatars(string path)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("<MudAvatar", StringComparison.Ordinal))
                continue;

            var text = ReadAvatarText(lines, i);
            if (text == null || !ObjectGlyphTexts.Contains(text))
                continue;

            if (HasExemptionMarker(lines, i))
                continue;

            yield return new Offender(relativePath, i + 1, $"carries the text \"{text}\"");
        }
    }

    /// <summary>
    /// Reads a <c>MudAvatar</c> element's inner text, which may span the same line as its opening tag or the
    /// next line, stopping at the closing tag. Returns null when no closing tag is found within a few lines
    /// (a malformed or genuinely multi-line body this simple scanner cannot read reliably).
    /// </summary>
    private static string? ReadAvatarText(IReadOnlyList<string> lines, int startIndex)
    {
        var builder = new StringBuilder();
        var sawOpenTagClose = false;

        for (var i = startIndex; i < lines.Count && i < startIndex + 4; i++)
        {
            var line = lines[i];
            var searchFrom = i == startIndex ? line.IndexOf("<MudAvatar", StringComparison.Ordinal) : 0;
            var openTagEnd = sawOpenTagClose ? searchFrom : line.IndexOf('>', searchFrom);

            if (!sawOpenTagClose)
            {
                if (openTagEnd < 0)
                    continue;

                sawOpenTagClose = true;
                searchFrom = openTagEnd + 1;
            }

            var closeIndex = line.IndexOf("</MudAvatar>", searchFrom, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                builder.Append(line.AsSpan(searchFrom)).Append(' ');
                continue;
            }

            builder.Append(line.AsSpan(searchFrom, closeIndex - searchFrom));
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        return null;
    }

    private static bool HasExemptionMarker(IReadOnlyList<string> lines, int tagStartIndex)
    {
        var firstLine = Math.Max(0, tagStartIndex - ExemptionMarkerLookBehindLines);

        for (var i = firstLine; i < tagStartIndex; i++)
        {
            if (lines[i].Contains(ExemptionMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads
    /// source files, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }

    private sealed record Offender(string RelativePath, int LineNumber, string Description);
}
