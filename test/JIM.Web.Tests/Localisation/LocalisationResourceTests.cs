// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Keeps the portal's resource files consistent with each other (#3030): every translation answers to an English
/// string that exists, carries the same placeholders, uses the agreed word for each JIM term, and a language is only
/// released once nothing in it is missing. These are the checks a translator cannot be expected to remember, and a
/// missing one shows up as a crash (a dropped placeholder) or as half a page in English.
/// </summary>
[TestFixture]
public partial class LocalisationResourceTests
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);
    private static readonly Lazy<IReadOnlyList<ResourceFile>> ResourceFiles = new(LoadResourceFiles);

    /// <summary>
    /// British spellings that differ in American English. A neutral string containing one needs an en-US entry
    /// that does not; otherwise American users read British spelling, which in a product UI reads as a typo.
    /// </summary>
    [GeneratedRegex(
        @"\b\w*(?:synchronis|organis|authoris|recognis|initialis|customis|minimis|maximis|optimis|prioritis|summaris|normalis|visualis|categoris|serialis|finalis|utilis|emphasis(?:e|ed|es|ing)\b|analys(?:e|ed|es|ing)\b|catalogue|colour|behaviour|favour|honour|neighbour|centre|licence|cancell|enrol(?:s|led|ment)?\b|\bgrey|programme|travell|labell|modelling|defence|artefact|judgement|fulfil(?:s|ment)?\b|dialogue)\w*",
        RegexOptions.IgnoreCase)]
    private static partial Regex BritishSpelling();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [Test]
    public void ResourceFolder_HasNeutralFilesToCheck()
    {
        Assert.That(ResourceFiles.Value.Count(file => file.Culture == null), Is.GreaterThan(0));
    }

    [Test]
    public void EveryCultureFile_HasANeutralFile_AndAnOfferedLanguage()
    {
        var neutralNames = ResourceFiles.Value.Where(file => file.Culture == null).Select(file => file.BaseName).ToHashSet();
        var languages = UiLanguages.All.Select(language => language.CultureName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var problems = ResourceFiles.Value
            .Where(file => file.Culture != null)
            .SelectMany(file => new[]
            {
                neutralNames.Contains(file.BaseName) ? null : $"{file.FileName}: no {file.BaseName}.resx to translate",
                languages.Contains(file.Culture!) ? null : $"{file.FileName}: '{file.Culture}' is not a language in UiLanguages.All"
            })
            .OfType<string>()
            .ToList();

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryTranslation_AnswersToAnEnglishString()
    {
        var problems = ForEachTranslation((file, key, _, neutral) =>
            neutral == null ? $"{file.FileName}: '{key}' has no English string in {file.BaseName}.resx (renamed or removed?)" : null);

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryValue_IsNotEmpty()
    {
        var problems = ResourceFiles.Value
            .SelectMany(file => file.Values.Where(entry => string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => $"{file.FileName}: '{entry.Key}' is empty"))
            .ToList();

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryTranslation_KeepsTheEnglishStringsPlaceholders()
    {
        var problems = ForEachTranslation((file, key, value, neutral) =>
        {
            if (neutral == null)
                return null;

            var expected = Placeholders(neutral);
            var actual = Placeholders(value);
            return expected.SetEquals(actual)
                ? null
                : $"{file.FileName}: '{key}' uses {{{string.Join("}, {", actual.Order())}}} where the English uses {{{string.Join("}, {", expected.Order())}}}";
        });

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void LanguagesInProgressOrReleased_TranslateEveryString()
    {
        // A language being delivered gets its translation in the change that adds each string, so it is complete the
        // day the last hard-coded text is converted. English variants hold only the strings whose spelling differs, so
        // completeness is a different question for them; AmericanEnglish_OverridesEveryBritishSpelling asks it.
        var released = UiLanguages.All
            .Where(language => language.IsRequiredInEveryChange && !language.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        var problems = new List<string>();
        foreach (var language in released)
        {
            foreach (var neutral in ResourceFiles.Value.Where(file => file.Culture == null))
            {
                var translation = ResourceFiles.Value.FirstOrDefault(file =>
                    file.BaseName == neutral.BaseName && string.Equals(file.Culture, language.CultureName, StringComparison.OrdinalIgnoreCase));

                problems.AddRange(neutral.Values.Keys
                    .Where(key => translation == null || !translation.Values.ContainsKey(key))
                    .Select(key => $"{language.CultureName}: {neutral.BaseName}.{key} has no translation"));
            }
        }

        Assert.That(problems, Is.Empty,
            "A language in progress or released must have every string translated, in the change that adds the string " +
            "(engineering/LOCALISATION.md). Translate these." + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AmericanEnglish_OverridesEveryBritishSpelling()
    {
        var problems = new List<string>();
        foreach (var neutral in ResourceFiles.Value.Where(file => file.Culture == null && file.BaseName != "MudBlazorText"))
        {
            var american = ResourceFiles.Value.FirstOrDefault(file => file.BaseName == neutral.BaseName && file.Culture == "en-US");
            foreach (var (key, value) in neutral.Values.Where(entry => BritishSpelling().IsMatch(entry.Value)))
            {
                if (american == null || !american.Values.TryGetValue(key, out var americanValue))
                    problems.Add($"{neutral.BaseName}.{key}: \"{value}\" needs an entry in {neutral.BaseName}.en-US.resx");
                else if (BritishSpelling().Match(americanValue) is { Success: true } match)
                    problems.Add($"{neutral.BaseName}.{key}: the en-US entry still says \"{match.Value}\"");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryLanguage_UsesTheGlossaryTermForEachJimTerm()
    {
        var glossaryPath = Path.Join(RepositoryRoot.Value, "engineering", "localisation", "glossary.json");
        using var glossary = JsonDocument.Parse(File.ReadAllText(glossaryPath));
        var terms = glossary.RootElement.GetProperty("terms").EnumerateArray().ToList();
        Assert.That(terms, Is.Not.Empty);

        var problems = new List<string>();
        foreach (var neutral in ResourceFiles.Value.Where(file => file.Culture == null))
        {
            var translations = ResourceFiles.Value.Where(file => file.BaseName == neutral.BaseName && file.Culture != null).ToList();
            foreach (var (key, english) in neutral.Values)
            {
                foreach (var term in terms)
                {
                    if (!Regex.IsMatch(english, term.GetProperty("en-GB").GetString()!))
                        continue;

                    foreach (var language in UiLanguages.All.Where(language => language != UiLanguages.Default))
                    {
                        if (!term.TryGetProperty(language.CultureName, out var patternElement))
                            continue;

                        var translation = translations.FirstOrDefault(file => file.Culture == language.CultureName);
                        var isEnglish = language.CultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase);
                        string? value = null;
                        if (translation != null && translation.Values.TryGetValue(key, out var translated))
                            value = translated;
                        else if (isEnglish)
                            value = english;

                        if (value == null)
                            continue;

                        var options = isEnglish ? RegexOptions.None : RegexOptions.IgnoreCase;
                        if (!Regex.IsMatch(value, patternElement.GetString()!, options))
                        {
                            problems.Add(
                                $"{language.CultureName}: {neutral.BaseName}.{key} \"{value}\" does not use the glossary term for " +
                                $"\"{term.GetProperty("term").GetString()}\" (expected /{patternElement.GetString()}/)");
                        }
                    }
                }
            }
        }

        Assert.That(problems, Is.Empty,
            "Translations must use the agreed word for each JIM term (engineering/localisation/glossary.json)." +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void MudBlazorText_CarriesEveryKeyMudBlazorAsksFor()
    {
        var mudBlazor = typeof(MudBlazor.MudButton).Assembly;
        var resourceName = mudBlazor.GetManifestResourceNames().Single(name => name.EndsWith("LanguageResource.resources", StringComparison.Ordinal));
        var manager = new ResourceManager(resourceName[..^".resources".Length], mudBlazor);
        var mudKeys = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet();

        var ours = ResourceFiles.Value.Single(file => file.BaseName == "MudBlazorText" && file.Culture == null).Values.Keys.ToHashSet();
        var missing = mudKeys.Except(ours).Order().ToList();

        Assert.That(missing, Is.Empty,
            "MudBlazor has strings JIM does not translate, probably after an upgrade. Add each to MudBlazorText.resx " +
            "and every language's MudBlazorText file: " + string.Join(", ", missing));
    }

    private static List<string> ForEachTranslation(Func<ResourceFile, string, string, string?, string?> check)
    {
        var neutrals = ResourceFiles.Value.Where(file => file.Culture == null).ToDictionary(file => file.BaseName);
        var problems = new List<string>();
        foreach (var file in ResourceFiles.Value.Where(file => file.Culture != null))
        {
            neutrals.TryGetValue(file.BaseName, out var neutral);
            foreach (var (key, value) in file.Values)
            {
                string? english = null;
                neutral?.Values.TryGetValue(key, out english);
                if (check(file, key, value, english) is { } problem)
                    problems.Add(problem);
            }
        }

        return problems;
    }

    private static HashSet<string> Placeholders(string value) =>
        Placeholder().Matches(value).Select(match => match.Groups[1].Value).ToHashSet();

    private static IReadOnlyList<ResourceFile> LoadResourceFiles()
    {
        var folder = Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "Resources");
        return Directory.EnumerateFiles(folder, "*.resx")
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var parts = Path.GetFileNameWithoutExtension(path).Split('.', 2);
                var values = XDocument.Load(path).Root!.Elements("data")
                    .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);
                return new ResourceFile(fileName, parts[0], parts.Length > 1 ? parts[1] : null, values);
            })
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (JIM.sln).");
    }

    private sealed record ResourceFile(string FileName, string BaseName, string? Culture, IReadOnlyDictionary<string, string> Values);
}
