// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A change to how the Connected System classifies an Object Type (its class kind, or which selected Object Type it
/// inherits from) can change which Object Type existing entries resolve to at the next import (#2049). It must
/// count as a change, so a refresh carrying only that is never reported as "up to date" and discarded; and it must
/// stay out of the destructive flag, because cancelling it keeps things exactly as they are (matching reads the
/// classification JIM has stored), so it warrants neither the three-way decision nor a discard confirmation.
/// </summary>
[TestFixture]
public class SchemaRefreshResultTests
{
    [Test]
    public void HasChanges_WithOnlyClassificationChanges_ReturnsTrue()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.AddChangedClassification("posixAccount", ClassKindChange("structural", "auxiliary"));

        Assert.That(result.HasChanges, Is.True,
            "A classification change alone is a change; reporting the schema as up to date would discard it.");
    }

    [Test]
    public void HasRemovalsOrDefinitionChanges_WithOnlyClassificationChanges_ReturnsFalse()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.AddChangedClassification("posixAccount", ClassKindChange("structural", "auxiliary"));

        Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False,
            "Cancelling a classification change keeps things as they are, so it must not drive the destructive decision.");
    }

    [Test]
    public void HasObjectTypeClassificationChanges_WithNoClassificationChanges_ReturnsFalse()
    {
        var result = new SchemaRefreshResult
        {
            Success = true,
            AddedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mobile"] }
        };

        Assert.That(result.HasObjectTypeClassificationChanges, Is.False);
    }

    [Test]
    public void AddChangedClassification_ForTheSameObjectTypeTwice_GroupsBothUnderIt()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.AddChangedClassification("inetOrgPerson", ClassKindChange("auxiliary", "structural"));
        result.AddChangedClassification("inetOrgPerson", new SchemaObjectTypeClassificationChange
        {
            Aspect = SchemaObjectTypeClassificationAspect.Inheritance,
            OldValues = ["top"],
            NewValues = ["person", "top"]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasObjectTypeClassificationChanges, Is.True);
            Assert.That(result.ChangedObjectTypeClassifications.Keys, Is.EquivalentTo(new[] { "inetOrgPerson" }));
            Assert.That(result.ChangedObjectTypeClassifications["inetOrgPerson"].Select(c => c.Aspect),
                Is.EquivalentTo(new[] { SchemaObjectTypeClassificationAspect.ClassKind, SchemaObjectTypeClassificationAspect.Inheritance }));
        }
    }

    private static SchemaObjectTypeClassificationChange ClassKindChange(string oldValue, string newValue) => new()
    {
        Aspect = SchemaObjectTypeClassificationAspect.ClassKind,
        OldValues = [oldValue],
        NewValues = [newValue]
    };
}
