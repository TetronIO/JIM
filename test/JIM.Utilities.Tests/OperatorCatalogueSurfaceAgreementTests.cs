// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Text.RegularExpressions;
using JIM.Models.Search;

namespace JIM.Utilities.Tests;

/// <summary>
/// PowerShell cannot read the operator catalogue at runtime: a <c>ValidateSet</c> is a literal in the cmdlet's source.
/// These tests hold each literal to <see cref="SearchComparisonOperators.OperatorsFor"/> for the cmdlet's context
/// (#2053), so an operator added to the catalogue, or offered in a new context, fails here until PowerShell offers it too.
/// </summary>
public class OperatorCatalogueSurfaceAgreementTests
{
    private static readonly Regex ComparisonTypeValidateSet = new(
        @"\[ValidateSet\((?<values>[^\]]*?)\)\]\s*\[string\]\$ComparisonType", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex QuotedName = new(@"'(?<name>[A-Za-z]+)'", RegexOptions.Compiled);

    [TestCase("ScopingCriteria/New-JIMScopingCriterion.ps1", CriterionContext.Scoping)]
    [TestCase("ScopingCriteria/Set-JIMScopingCriterion.ps1", CriterionContext.Scoping)]
    [TestCase("Search/New-JIMPredefinedSearchCriterion.ps1", CriterionContext.PredefinedSearch)]
    [TestCase("Search/Set-JIMPredefinedSearchCriterion.ps1", CriterionContext.PredefinedSearch)]
    public void ComparisonTypeValidateSet_OffersExactlyTheCataloguesOperatorsForTheContext(string cmdlet, CriterionContext context)
    {
        var source = File.ReadAllText(Path.Join(PowerShellRoot(), "Public", cmdlet));
        var match = ComparisonTypeValidateSet.Match(source);
        Assert.That(match.Success, Is.True, $"{cmdlet} has no ValidateSet on its ComparisonType parameter");

        var offered = QuotedName.Matches(match.Groups["values"].Value).Select(m => m.Groups["name"].Value);

        Assert.That(offered, Is.EquivalentTo(SearchComparisonOperators.OperatorsFor(context).Select(op => op.ToString())));
    }

    [Test]
    public void ExampleDataDependencyNames_AreExactlyTheCataloguesOperatorsForExampleData()
    {
        var source = File.ReadAllText(Path.Join(PowerShellRoot(), "Private", "ConvertTo-JIMExampleDataTemplateObjectTypes.ps1"));
        var match = Regex.Match(source, @"\$validComparisonTypes = @\((?<values>[^)]*)\)", RegexOptions.Singleline);
        Assert.That(match.Success, Is.True, "ConvertTo-JIMExampleDataTemplateObjectTypes.ps1 no longer declares $validComparisonTypes");

        var offered = QuotedName.Matches(match.Groups["values"].Value).Select(m => m.Groups["name"].Value);

        Assert.That(offered, Is.EquivalentTo(SearchComparisonOperators.OperatorsFor(CriterionContext.ExampleData).Select(op => op.ToString())));
    }

    private static string PowerShellRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root from the test assembly's location.");
        return Path.Join(directory!.FullName, "src", "JIM.PowerShell");
    }
}
