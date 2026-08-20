// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins the sentence a finished import leaves on its Activity (#170): the counts, their grouping and the
/// throughput suffix, so it reads like the synchronisation and export processors' messages.
/// </summary>
[TestFixture]
public class ImportOutcomeMessageTests
{
    [Test]
    public void ForImport_LargeCountsWithErrors_GroupsTheDigitsAndNamesTheErrors()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 1_000_000, created: 999_880, updated: 0, unchanged: 0, errors: 120, throughput: " in 18 min 13 sec (avg 915 obj/s)");

        Assert.That(message, Is.EqualTo("Import complete: 1,000,000 objects (999,880 created, 0 updated, 120 errors) in 18 min 13 sec (avg 915 obj/s)"));
    }

    [Test]
    public void ForImport_NothingUnchangedAndNoErrors_LeavesThoseOut()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 2, created: 2, updated: 0, unchanged: 0, errors: 0, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 2 objects (2 created, 0 updated)"));
    }

    [Test]
    public void ForImport_UnchangedObjects_AreCounted()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 50, created: 0, updated: 3, unchanged: 47, errors: 1, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 50 objects (0 created, 3 updated, 47 unchanged, 1 error)"));
    }
}
