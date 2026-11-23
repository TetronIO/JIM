// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Enum values shown to people come from <c>EnumText.resx</c> (#3030). Every key must name a real enum value, and an
/// enum with any value translated must have them all, or one status in a list would read in English.
/// </summary>
[TestFixture]
public class EnumTextTests
{
    private static readonly Type[] EnumTypes = typeof(Activity).Assembly.GetTypes()
        .Concat(typeof(EnumDisplay).Assembly.GetTypes())
        .Where(type => type.IsEnum)
        .ToArray();

    [Test]
    public void EveryKey_NamesARealEnumValue()
    {
        var keys = NeutralKeys();
        var problems = keys
            .Where(key => !Resolves(key))
            .Select(key => $"EnumText.{key}: no enum value of that name (renamed or removed?)")
            .ToList();

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryEnumWithAnyValueTranslated_HasEveryValueTranslated()
    {
        var keys = NeutralKeys().ToHashSet();
        var problems = new List<string>();
        foreach (var enumName in keys.Select(key => key[..key.IndexOf('_')]).Distinct())
        {
            var type = EnumTypes.Where(t => t.Name == enumName).ToList();
            if (type.Count != 1)
                continue;

            problems.AddRange(Enum.GetNames(type[0])
                .Where(value => !keys.Contains($"{enumName}_{value}"))
                .Select(value => $"EnumText: {enumName}_{value} is missing"));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    [SetUICulture("fr")]
    public void ForDisplay_French_ReadsTheTranslation()
    {
        Assert.That(ActivityStatus.CompleteWithWarning.ForDisplay(), Is.EqualTo("Terminée avec avertissement"));
    }

    [Test]
    public void ForDisplay_English_ReadsAsBefore()
    {
        Assert.That(ActivityStatus.CompleteWithWarning.ForDisplay(), Is.EqualTo("Complete With Warning"));
    }

    private static IEnumerable<string> NeutralKeys() =>
        EnumText.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key);

    private static bool Resolves(string key)
    {
        var separator = key.IndexOf('_');
        if (separator <= 0)
            return false;

        var enumName = key[..separator];
        var value = key[(separator + 1)..];
        var types = EnumTypes.Where(type => type.Name == enumName).ToList();
        return types.Count == 1 && Enum.GetNames(types[0]).Contains(value);
    }
}
