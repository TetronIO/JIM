// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The one switch from a directory's kind of change tracking to the source that reads it.
/// </summary>
[TestFixture]
public class LdapDeltaSourcesTests
{
    private static ILdapDeltaSource Create(LdapDeltaSourceKind kind) =>
        LdapDeltaSources.Create(kind, new Mock<ILdapOperationExecutor>().Object, Log.Logger);

    [Test]
    public void Create_Usn_ReturnsTheUsnSource() =>
        Assert.That(Create(LdapDeltaSourceKind.Usn), Is.TypeOf<LdapUsnDeltaSource>());

    [Test]
    public void Create_Accesslog_ReturnsTheAccesslogSource() =>
        Assert.That(Create(LdapDeltaSourceKind.Accesslog), Is.TypeOf<LdapAccesslogDeltaSource>());

    [Test]
    public void Create_Changelog_ReturnsTheChangelogSource() =>
        Assert.That(Create(LdapDeltaSourceKind.Changelog), Is.TypeOf<LdapChangelogDeltaSource>());

    [Test]
    public void Create_EveryDirectoryType_HasASource()
    {
        foreach (var directoryType in Enum.GetValues<LdapDirectoryType>())
        {
            var kind = new LdapConnectorRootDse { DirectoryType = directoryType }.DeltaSourceKind;
            Assert.That(() => Create(kind), Throws.Nothing, $"{directoryType} has no change source");
        }
    }
}
