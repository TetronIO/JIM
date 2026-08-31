// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Unit tests for <see cref="ReleasedMigrationManifest"/>: the parsing, hashing and verification logic behind
/// <see cref="ReleasedMigrationImmutabilityTests"/>, exercised here over fabricated inputs so every violation
/// class is proven catchable without planting a real violation in the repository.
/// </summary>
public class ReleasedMigrationManifestTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Test]
    public void Parse_CommentsAndBlankLines_AreIgnored()
    {
        var entries = ReleasedMigrationManifest.Parse(
        [
            "# Released migrations manifest.",
            "",
            $"20260101000000_First {HashA} {HashB} 1.0.0"
        ]);

        Assert.That(entries, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].Id, Is.EqualTo("20260101000000_First"));
            Assert.That(entries[0].MigrationHash, Is.EqualTo(HashA));
            Assert.That(entries[0].DesignerHash, Is.EqualTo(HashB));
            Assert.That(entries[0].Version, Is.EqualTo("1.0.0"));
        }
    }

    [Test]
    public void Parse_MalformedLine_Throws()
    {
        Assert.That(() => ReleasedMigrationManifest.Parse(["20260101000000_First onlyonehash"]),
            Throws.TypeOf<FormatException>().With.Message.Contains("20260101000000_First"));
    }

    [Test]
    public void Verify_EmptyManifest_ReportsNothing()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [],
            [new ReleasedMigrationManifest.ActualMigration("20260101000000_First", HashA, HashB)]);

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void Verify_ListedMigrationUnchanged_ReportsNothing()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0")],
            [new ReleasedMigrationManifest.ActualMigration("20260101000000_First", HashA, HashB)]);

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void Verify_ListedMigrationMissing_ReportsRenameOrDelete()
    {
        // The regeneration that is routine pre-release: the id vanishes because the migration was re-added
        // under a fresh timestamp.
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0")],
            [new ReleasedMigrationManifest.ActualMigration("20270101000000_First", HashA, HashB)]);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("20260101000000_First").And.Contain("1.0.0"));
    }

    [Test]
    public void Verify_ListedMigrationEdited_ReportsWhichFile()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0")],
            [new ReleasedMigrationManifest.ActualMigration("20260101000000_First", HashC, HashB)]);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("20260101000000_First").And.Contain(".cs"));
    }

    [Test]
    public void Verify_ListedDesignerEdited_ReportsWhichFile()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0")],
            [new ReleasedMigrationManifest.ActualMigration("20260101000000_First", HashA, HashC)]);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("Designer"));
    }

    [Test]
    public void Verify_UnlistedMigrationSequencedBeforeNewestRelease_IsReported()
    {
        // A feature branch merged post-release without regenerating its migration: the id sorts before the
        // newest released one, so upgraded customer databases would apply it out of model order.
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260601000000_Released", HashA, HashB, "1.0.0")],
            [
                new ReleasedMigrationManifest.ActualMigration("20260601000000_Released", HashA, HashB),
                new ReleasedMigrationManifest.ActualMigration("20260301000000_Straggler", HashC, HashC)
            ]);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("20260301000000_Straggler").And.Contain("20260601000000_Released"));
    }

    [Test]
    public void Verify_UnlistedMigrationSequencedAfterNewestRelease_ReportsNothing()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [new ReleasedMigrationManifest.Entry("20260601000000_Released", HashA, HashB, "1.0.0")],
            [
                new ReleasedMigrationManifest.ActualMigration("20260601000000_Released", HashA, HashB),
                new ReleasedMigrationManifest.ActualMigration("20260701000000_Unreleased", HashC, HashC)
            ]);

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void Verify_DuplicateManifestIds_AreReported()
    {
        var failures = ReleasedMigrationManifest.Verify(
            [
                new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0"),
                new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.1.0")
            ],
            [new ReleasedMigrationManifest.ActualMigration("20260101000000_First", HashA, HashB)]);

        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("listed twice"));
    }

    [Test]
    public void NewestReleasedId_EmptyManifest_ReturnsNull()
    {
        Assert.That(ReleasedMigrationManifest.NewestReleasedId([]), Is.Null);
    }

    [Test]
    public void NewestReleasedId_MultipleEntries_ReturnsTheOrdinalMaximum()
    {
        var newest = ReleasedMigrationManifest.NewestReleasedId(
        [
            new ReleasedMigrationManifest.Entry("20260601000000_Second", HashA, HashB, "1.1.0"),
            new ReleasedMigrationManifest.Entry("20260101000000_First", HashA, HashB, "1.0.0")
        ]);

        Assert.That(newest, Is.EqualTo("20260601000000_Second"));
    }

    [Test]
    public void HashContent_CrlfAndLf_HashIdentically()
    {
        // A Windows checkout must not read as an edit; hashing normalises line endings first.
        var lf = ReleasedMigrationManifest.HashContent("line one\nline two\n");
        var crlf = ReleasedMigrationManifest.HashContent("line one\r\nline two\r\n");

        Assert.That(lf, Is.EqualTo(crlf));
        Assert.That(lf, Has.Length.EqualTo(64));
    }

    [Test]
    public void HashContent_DifferentContent_HashesDifferently()
    {
        Assert.That(
            ReleasedMigrationManifest.HashContent("line one"),
            Is.Not.EqualTo(ReleasedMigrationManifest.HashContent("line two")));
    }
}
