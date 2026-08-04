// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Text.RegularExpressions;
using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Sweeps the worker's processors for the call sites that enter each declared step.
/// </summary>
/// <remarks>
/// <para>
/// A step declared in the catalogue but never entered by any processor is worse than no step at
/// all: the run records it, the rail draws it, and it is reported skipped on every single run, so
/// the Activity quietly asserts that work was not needed when in fact nobody wired it up. The
/// catalogue's own tests guard the opposite direction (a key that no run type declares), and
/// neither can be checked by rendering a phase; this is the missing half.
/// </para>
/// <para>
/// A source sweep rather than a behavioural test because the alternative is an end-to-end run per
/// run type, and the failure this catches is the absence of a line of code, which is exactly what
/// no single run can demonstrate. It fails with the offending key named, so the fix is obvious.
/// </para>
/// </remarks>
[TestFixture]
public class RunPhaseCallSiteTests
{
    private static string ProcessorSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var processors = Path.Join(repositoryRoot, "src", "JIM.Worker", "Processors");
        Assert.That(Directory.Exists(processors), Is.True, $"Could not find the worker's processors at '{processors}'.");

        return string.Join("\n", Directory.EnumerateFiles(processors, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root from the test assembly's location.");
        return directory!.FullName;
    }

    [Test]
    public void EveryDeclaredPhase_IsEnteredBySomeProcessor()
    {
        var source = ProcessorSource();

        var declared = Enum.GetValues<ConnectedSystemRunType>()
            .Where(t => t != ConnectedSystemRunType.NotSet)
            .SelectMany(RunProfilePhaseCatalogue.GetPhases)
            .Select(p => p.Key)
            .Distinct()
            .ToList();

        // The constant's name, not its value: call sites enter phases by constant, as the catalogue
        // declares them, and the values are persisted keys that deliberately never change.
        var namesByKey = typeof(RunPhaseKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name, StringComparer.Ordinal);

        var neverEntered = declared
            .Where(key => !Regex.IsMatch(source, $@"EnterAsync\(\s*RunPhaseKeys\.{Regex.Escape(namesByKey[key])}\b"))
            .Select(key => namesByKey[key])
            .ToList();

        Assert.That(neverEntered, Is.Empty,
            $"Declared but never entered, so every run reports them skipped: {string.Join(", ", neverEntered)}");
    }
}
