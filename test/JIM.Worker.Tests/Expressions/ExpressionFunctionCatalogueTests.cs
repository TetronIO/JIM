// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Worker.Tests.Docs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The function catalogue is the one description of every expression function (#2053, PRD Phase 2 requirement 6):
/// it names exactly the functions the evaluator registers, and the documentation's function reference states exactly
/// what it states, so neither can drift from the functions an expression can actually call.
/// </summary>
[TestFixture]
public partial class ExpressionFunctionCatalogueTests
{
    private const string ExpressionsPage = "docs/concepts/expressions.md";

    /// <summary>The reference section each category is documented under.</summary>
    private static readonly Dictionary<ExpressionFunctionCategory, string> SectionHeadings = new()
    {
        [ExpressionFunctionCategory.Text] = "String Functions",
        [ExpressionFunctionCategory.Pattern] = "Pattern Functions",
        [ExpressionFunctionCategory.Conditional] = "Conditional Functions",
        [ExpressionFunctionCategory.Conversion] = "Conversion Functions",
        [ExpressionFunctionCategory.Date] = "Date Functions",
        [ExpressionFunctionCategory.DistinguishedName] = "Distinguished Name (DN) Functions",
        [ExpressionFunctionCategory.Password] = "Password Generation",
        [ExpressionFunctionCategory.MultiValued] = "Multi-Valued Functions",
        [ExpressionFunctionCategory.AccountControl] = "Account Control Functions"
    };

    [Test]
    public void Catalogue_NamesExactlyTheFunctionsTheEvaluatorRegisters()
    {
        var registered = DynamicExpressoEvaluator.RegisteredFunctionNames().Order(StringComparer.Ordinal);
        var catalogued = ExpressionFunctionCatalogue.All.Select(f => f.Name).Order(StringComparer.Ordinal);

        Assert.That(catalogued, Is.EqualTo(registered));
    }

    [Test]
    public void Catalogue_NamesEachFunctionOnce()
    {
        Assert.That(ExpressionFunctionCatalogue.All.Select(f => f.Name), Is.Unique);
    }

    [Test]
    public void Catalogue_PutsOptionalParametersLast()
    {
        var misordered = ExpressionFunctionCatalogue.All
            .Where(f => f.Parameters.SkipWhile(p => !p.IsOptional).Any(p => !p.IsOptional))
            .Select(f => f.Name);

        Assert.That(misordered, Is.Empty);
    }

    [Test]
    public void Catalogue_EveryFunctionHasADescriptionExampleAndDescribedParameters()
    {
        var incomplete = ExpressionFunctionCatalogue.All
            .Where(f => string.IsNullOrWhiteSpace(f.Description) || string.IsNullOrWhiteSpace(f.Returns) ||
                        !f.Example.Contains(f.Name + "(", StringComparison.Ordinal) ||
                        f.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Description)))
            .Select(f => f.Name);

        Assert.That(incomplete, Is.Empty);
    }

    [Test]
    public void Catalogue_EveryExampleIsAValidExpression()
    {
        var evaluator = new DynamicExpressoEvaluator();

        var invalid = ExpressionFunctionCatalogue.All
            .Select(f => (f.Name, Result: evaluator.Validate(f.Example)))
            .Where(r => !r.Result.IsValid)
            .Select(r => $"{r.Name}: {r.Result.ErrorMessage}");

        Assert.That(invalid, Is.Empty);
    }

    [Test]
    public void FunctionReference_StatesEveryFunctionExactlyAsTheCatalogueDoes()
    {
        var documented = DocumentedFunctions();
        var catalogued = ExpressionFunctionCatalogue.All
            .Select(f => new DocumentedFunction(SectionHeadings[f.Category], f.Signature, f.Description, f.Example))
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(documented.Except(catalogued), Is.Empty,
                $"rows in {ExpressionsPage} that the catalogue does not state (wrong section, signature, description or example, or no such function)");
            Assert.That(catalogued.Except(documented), Is.Empty,
                $"catalogue functions {ExpressionsPage} does not state exactly");
        }
    }

    [Test]
    public void NonRepeatableFunctionDetector_FindsEveryFunctionTheCatalogueMarksNonRepeatable()
    {
        var nonRepeatable = ExpressionFunctionCatalogue.All.Where(f => !f.IsRepeatable).ToList();

        Assert.That(nonRepeatable, Is.Not.Empty);
        foreach (var function in nonRepeatable)
            Assert.That(NonRepeatableFunctionDetector.Find(function.Example), Is.EqualTo(new[] { $"{function.Name}()" }), function.Name);
    }

    [Test]
    public void NonRepeatableFunctionDetector_FindsNoRepeatableFunction()
    {
        var reported = ExpressionFunctionCatalogue.All.Where(f => f.IsRepeatable)
            .SelectMany(f => NonRepeatableFunctionDetector.Find(f.Example));

        Assert.That(reported, Is.Empty);
    }

    /// <summary>
    /// Every function row in the reference: a table row under a function section whose first cell is a call.
    /// </summary>
    private static List<DocumentedFunction> DocumentedFunctions()
    {
        var rows = new List<DocumentedFunction>();
        string? section = null;
        foreach (var line in File.ReadLines(DocsCoverageSource.FullPath(ExpressionsPage)))
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                var heading = line.TrimStart('#').Trim();
                section = SectionHeadings.ContainsValue(heading) ? heading : null;
                continue;
            }

            if (section == null)
                continue;

            var match = FunctionRow().Match(line);
            if (match.Success)
            {
                rows.Add(new DocumentedFunction(section, Unescape(match.Groups["signature"].Value),
                    Unescape(match.Groups["description"].Value.Trim()), Unescape(match.Groups["example"].Value)));
            }
        }

        return rows;
    }

    /// <summary>A Markdown table escapes a pipe inside a cell; the catalogue holds it plain.</summary>
    private static string Unescape(string cell) => cell.Replace("\\|", "|", StringComparison.Ordinal);

    private sealed record DocumentedFunction(string Section, string Signature, string Description, string Example);

    [GeneratedRegex(@"^\|\s*`(?<signature>\w+\(.*?\))`\s*\|(?<description>.*)\|\s*`(?<example>.*)`\s*\|\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionRow();
}
