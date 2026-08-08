// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Naming the population a Configuration Change Preview summary row covers (#1275). A row covers many objects, so
/// the type is written in the plural; the interesting part is which source of the plural is used, because the
/// general rule turns "Person" into "Persons" and only the administrator's own plural knows it is "People".
/// </summary>
[TestFixture]
public class PreviewPopulationNameTests
{
    [Test]
    public void Pluralised_MetaverseObjectTypeWithAnAuthoredPlural_UsesIt()
    {
        var plurals = new Dictionary<int, string> { [1] = "People" };

        Assert.That(PreviewPopulationName.Pluralised("Person", 1, plurals), Is.EqualTo("People"),
            "the administrator authored the plural on the type; deriving one instead would contradict it");
    }

    [Test]
    public void Pluralised_ConnectedSystemObjectType_DerivesOne()
    {
        // No id, so this is a class the Connector discovered rather than a type JIM named. There is no authored
        // plural to read, and there never will be.
        Assert.That(PreviewPopulationName.Pluralised("inetOrgPerson", null, new Dictionary<int, string>()),
            Is.EqualTo("inetOrgPersons"));
    }

    [Test]
    public void Pluralised_MetaverseObjectTypeDeletedSinceThePreviewRan_FallsBackToTheSnapshottedName()
    {
        // The type is gone, so its authored plural is gone with it, but the preview snapshotted the name and the row
        // still has to say something. Deriving is better than rendering the singular or nothing at all.
        Assert.That(PreviewPopulationName.Pluralised("User", 99, new Dictionary<int, string>()),
            Is.EqualTo("Users"));
    }

    [Test]
    public void Pluralised_TypeWithAnEmptyAuthoredPlural_DerivesOne()
    {
        var plurals = new Dictionary<int, string> { [1] = string.Empty };

        Assert.That(PreviewPopulationName.Pluralised("User", 1, plurals), Is.EqualTo("Users"));
    }

    [Test]
    public void Pluralised_NoTypeName_IsEmpty()
    {
        Assert.That(PreviewPopulationName.Pluralised(null, 1, new Dictionary<int, string>()), Is.Empty);
    }
}
