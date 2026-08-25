// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for an attribute's writability. Administrators script JIM as much as they click it,
/// so every writability state a Connected System can report has to reach an API client intact.
/// </summary>
[TestFixture]
public class ConnectedSystemAttributeDtoTests
{
    [TestCase(AttributeWritability.Writable, "Writable")]
    [TestCase(AttributeWritability.ReadOnly, "ReadOnly")]
    [TestCase(AttributeWritability.WritableOnCreate, "WritableOnCreate")]
    public void FromEntity_CarriesWritabilityAsItsEnumName(AttributeWritability writability, string expected)
    {
        var entity = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "employee_number",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Writability = writability
        };

        var dto = ConnectedSystemAttributeDto.FromEntity(entity);

        Assert.That(dto.Writability, Is.EqualTo(expected));
    }

    [Test]
    public void FromEntity_CoversEveryWritabilityState()
    {
        // Guards against a future state being stringified to something a client cannot switch on.
        using (Assert.EnterMultipleScope())
        {
            foreach (var writability in Enum.GetValues<AttributeWritability>())
            {
                var dto = ConnectedSystemAttributeDto.FromEntity(new ConnectedSystemObjectTypeAttribute
                {
                    Id = 1, Name = "anAttribute", Type = AttributeDataType.Text, Writability = writability
                });

                Assert.That(Enum.TryParse<AttributeWritability>(dto.Writability, out var roundTripped) && roundTripped == writability,
                    Is.True, $"{writability} must survive the DTO as a parseable enum name");
            }
        }
    }

    [Test]
    public void FromEntity_ADeclaredReferenceTarget_CarriesItsIdAndNameForApiClients()
    {
        // The declared target decides which Object Type a reference resolves within (#1285);
        // an API client inspecting a schema needs to see it, read-only.
        var targetObjectType = new ConnectedSystemObjectType { Id = 9, Name = "Department" };
        var entity = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "DEPARTMENT_ID",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            ReferencedObjectTypeId = 9,
            ReferencedObjectType = targetObjectType
        };

        var dto = ConnectedSystemAttributeDto.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ReferencedObjectTypeId, Is.EqualTo(9));
            Assert.That(dto.ReferencedObjectTypeName, Is.EqualTo("Department"));
        }
    }

    [Test]
    public void FromEntity_AnUndeclaredReferenceTarget_CarriesNulls()
    {
        var entity = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "MANAGER",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued
        };

        var dto = ConnectedSystemAttributeDto.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ReferencedObjectTypeId, Is.Null);
            Assert.That(dto.ReferencedObjectTypeName, Is.Null);
        }
    }
}
