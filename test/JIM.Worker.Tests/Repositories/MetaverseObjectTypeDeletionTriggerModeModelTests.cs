// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Pins the migration side of the #119 trigger-mode default split: the relational model must declare a
/// store-level default of 0 (<see cref="AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect"/>) for
/// the MetaverseObjectTypes.DeletionTriggerMode column, so rows that existed before the column was added
/// keep the pre-existing any-source-triggers behaviour with no backfill. The property initialiser side
/// (new entities default to AllSourcesDisconnect) is pinned in JIM.Models.Tests. Removing the
/// HasDefaultValue configuration would silently flip every pre-existing configuration to the new
/// semantics, so the relational annotation is asserted here against the built model.
/// </summary>
[TestFixture]
public class MetaverseObjectTypeDeletionTriggerModeModelTests
{
    [Test]
    public void DeletionTriggerMode_RelationalModel_DeclaresSpecificSourcesDisconnectColumnDefault()
    {
        // Building the model needs the Npgsql provider for relational metadata but never connects;
        // the connection string is a syntactically valid placeholder.
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only;Password=model-only")
            .Options;
        using var context = new JimDbContext(options);

        var entityType = context.Model.FindEntityType(typeof(MetaverseObjectType));
        Assert.That(entityType, Is.Not.Null, "MetaverseObjectType not found in the EF model.");

        var property = entityType!.FindProperty(nameof(MetaverseObjectType.DeletionTriggerMode));
        Assert.That(property, Is.Not.Null, "DeletionTriggerMode not mapped on MetaverseObjectType.");

        // GetDefaultValue() falls back to the CLR default when no store default is configured, so it cannot
        // distinguish "explicitly defaulted to 0" from "not configured at all"; the relational annotation is
        // the store-level default the migration renders, and is what must be pinned.
        // "Relational:DefaultValue" is the annotation HasDefaultValue writes (RelationalAnnotationNames is
        // pubternal, so the stable string literal is used instead).
        var storeDefault = property!.FindAnnotation("Relational:DefaultValue");
        Assert.That(storeDefault, Is.Not.Null,
            "DeletionTriggerMode must declare a store-level column default (HasDefaultValue) so rows that " +
            "existed before the column was added read a deliberate value rather than relying on provider behaviour.");
        Assert.That(storeDefault!.Value, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect),
            "The DeletionTriggerMode column default must be SpecificSourcesDisconnect (0) so existing rows " +
            "keep the pre-#119 any-source-triggers behaviour with no backfill.");
    }
}
