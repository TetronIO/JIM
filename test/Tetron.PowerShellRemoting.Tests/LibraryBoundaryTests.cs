// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Xml.Linq;
using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The remoting library must stand alone, so it can be split into its own repository and package without untangling
/// it from JIM (#2021). These tests fail the build the moment it depends on any JIM project or assembly.
/// </summary>
public class LibraryBoundaryTests
{
    private static readonly Assembly Library = typeof(Krb5Configuration).Assembly;

    [Test]
    public void LibraryAssembly_ReferencesNoJimAssembly()
    {
        var jimReferences = Library.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => name.StartsWith("JIM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(jimReferences, Is.Empty, "Tetron.PowerShellRemoting must not depend on JIM.");
    }

    [Test]
    public void LibraryProject_HasNoProjectReferences()
    {
        var project = XDocument.Load(FindLibraryProject());

        var projectReferences = project.Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")).ToList();

        Assert.That(projectReferences, Is.Empty, "Tetron.PowerShellRemoting must stand alone; it may reference packages only.");
    }

    [Test]
    public void LibraryTypes_AllLiveInTheLibrarysOwnNamespace()
    {
        var strays = Library.GetTypes()
            .Where(t => t.Namespace is not null && !t.Namespace.StartsWith("Tetron.PowerShellRemoting", StringComparison.Ordinal))
            .Where(t => !t.Namespace!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) && !t.Namespace!.StartsWith("System.", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.That(strays, Is.Empty);
    }

    private static string FindLibraryProject()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Tetron.PowerShellRemoting", "Tetron.PowerShellRemoting.csproj");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find src/Tetron.PowerShellRemoting/Tetron.PowerShellRemoting.csproj above the test directory.");
    }
}
