// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JIM.Connectors;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST API returns each message's key beside its English (#3039), and a client may render the message from that key
/// in its own language. Renaming or removing a key would silently break that client, so every key the API can return is
/// listed in <c>PublishedMessageKeys.txt</c> and must stay. New keys are welcome.
/// </summary>
/// <remarks>
/// To add new keys to the list, run this fixture with <c>JIM_UPDATE_PUBLISHED_MESSAGE_KEYS=1</c> and commit the file.
/// Removing a key is a breaking change: deprecate it first and remove it only in a major version.
/// </remarks>
[TestFixture]
public class PublishedMessageKeyTests
{
    private const string LockFileName = "PublishedMessageKeys.txt";

    private static readonly string[] CatalogueFolders =
    [
        "src/JIM.Application/Resources",
        "src/JIM.Models/Resources",
        "src/JIM.Connectors/Resources"
    ];

    [Test]
    public void EveryPublishedKey_IsStillInItsCatalogue()
    {
        var root = RepositoryRoot();
        var lockFile = Path.Combine(root, "test", "JIM.Web.Api.Tests", LockFileName);
        var current = CurrentKeys(root);

        if (Environment.GetEnvironmentVariable("JIM_UPDATE_PUBLISHED_MESSAGE_KEYS") == "1")
        {
            var keep = File.Exists(lockFile) ? ReadLockFile(lockFile) : [];
            File.WriteAllLines(lockFile, keep.Union(current).OrderBy(k => k, StringComparer.Ordinal));
            Assert.Pass($"{LockFileName} rewritten.");
        }

        var published = ReadLockFile(lockFile);
        var missing = published.Except(current).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.That(missing, Is.Empty,
            "These message keys are published by the REST API and no longer exist. Restore them: a rename or removal breaks clients until the next major version.");
    }

    [Test]
    public void EveryBuiltInConnectorCatalogue_IsAFileThisGuardReads()
    {
        // Otherwise a Connector's keys would be published without ever being pinned here.
        var folder = Path.Combine(RepositoryRoot(), "src", "JIM.Connectors", "Resources");
        var missing = BuiltInConnectorCatalogues.ResourceFiles.Values
            .Where(file => !File.Exists(Path.Combine(folder, file + ".resx")))
            .ToList();

        Assert.That(missing, Is.Empty);
    }

    [Test]
    public void EveryCurrentKey_IsPublished()
    {
        // Keeps the list complete, so a key added and then renamed before anyone writes it down is still caught.
        if (Environment.GetEnvironmentVariable("JIM_UPDATE_PUBLISHED_MESSAGE_KEYS") == "1")
            Assert.Pass("The list is being rewritten by the other test.");

        var root = RepositoryRoot();
        var published = ReadLockFile(Path.Combine(root, "test", "JIM.Web.Api.Tests", LockFileName));

        var unlisted = CurrentKeys(root).Except(published).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.That(unlisted, Is.Empty,
            $"New message keys are not in {LockFileName}. Run the tests with JIM_UPDATE_PUBLISHED_MESSAGE_KEYS=1 and commit the file.");
    }

    /// <summary>
    /// Every key as the API publishes it: <c>Catalogue.Key</c>, with a counted message's <c>XOne</c>/<c>XOther</c> pair
    /// published once, as its stem <c>X</c>, the way <see cref="JIM.Models.Localisation.JimMessage.Counted"/> stores it.
    /// </summary>
    private static HashSet<string> CurrentKeys(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in CatalogueFolders)
        {
            foreach (var resx in Directory.GetFiles(Path.Combine(root, folder), "*.resx").Where(IsNeutral))
            {
                var catalogue = PublishedCatalogueName(Path.GetFileNameWithoutExtension(resx));
                var names = XDocument.Load(resx).Root!.Elements("data")
                    .Select(d => (string)d.Attribute("name")!)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var name in names)
                {
                    var stem = CountedStem(name, names);
                    keys.Add(catalogue + "." + (stem ?? name));
                }
            }
        }

        return keys;
    }

    // A built-in Connector's catalogue is published under its whole name (connector:jim.ldap/Messages), the way
    // ApiMessage.KeyOf writes it; JIM's own are published under the file's name.
    private static string PublishedCatalogueName(string fileName) =>
        BuiltInConnectorCatalogues.ResourceFiles.SingleOrDefault(entry => entry.Value == fileName).Key ?? fileName;

    private static string? CountedStem(string name, HashSet<string> names)
    {
        if (name.EndsWith("One", StringComparison.Ordinal) && names.Contains(name[..^"One".Length] + "Other"))
            return name[..^"One".Length];
        if (name.EndsWith("Other", StringComparison.Ordinal) && names.Contains(name[..^"Other".Length] + "One"))
            return name[..^"Other".Length];
        return null;
    }

    // A neutral catalogue has no culture in its name: ActivityMessages.resx, not ActivityMessages.fr.resx.
    private static bool IsNeutral(string path) => !Path.GetFileNameWithoutExtension(path).Contains('.');

    private static HashSet<string> ReadLockFile(string path) =>
        File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JIM.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("JIM.sln not found above the test directory.");
    }
}
