// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST API emits every date attribute value in its canonical form (#2153): UTC, to the microsecond, written with
/// <c>Z</c>. A value read from PostgreSQL arrives as Kind Local, which serialised with the host's offset; the same
/// instant must read the same through every endpoint, whatever the server's time zone.
/// </summary>
[TestFixture]
public class DateTimeValueDtoTests
{
    /// <summary>A stored date as the database hands it back: Kind Local. The source held it to the 100ns.</summary>
    private static readonly DateTime SourceDate = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    private static readonly DateTime StoredDate = SourceDate.ToLocalTime();

    private const string ExpectedJson = "\"2026-03-01T09:00:00.123456Z\"";

    [Test]
    public void MetaverseObjectAttributeValueDto_FromEntity_EmitsTheDateInUtcWithZ()
    {
        var dto = MetaverseObjectAttributeValueDto.FromEntity(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Id = 1, Name = "Start Date", Type = AttributeDataType.DateTime },
            AttributeId = 1,
            DateTimeValue = StoredDate
        });

        Assert.That(JsonSerializer.Serialize(dto.DateTimeValue), Is.EqualTo(ExpectedJson));
    }

    [Test]
    public void ConnectedSystemObjectAttributeValueDto_FromEntity_EmitsTheDateInUtcWithZ()
    {
        var dto = ConnectedSystemObjectAttributeValueDto.FromEntity(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "startDate", Type = AttributeDataType.DateTime },
            AttributeId = 1,
            DateTimeValue = StoredDate
        });

        Assert.That(JsonSerializer.Serialize(dto.DateTimeValue), Is.EqualTo(ExpectedJson));
    }

    [Test]
    public void MetaverseObjectHeaderDto_FromHeader_EmitsTheDateAttributeInUtcWithZ()
    {
        var header = new MetaverseObjectHeader { Id = Guid.NewGuid(), TypeName = "Person", TypePluralName = "People", CachedDisplayName = "Jo Bloggs" };
        header.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Id = 1, Name = "Start Date", Type = AttributeDataType.DateTime },
            AttributeId = 1,
            DateTimeValue = StoredDate
        });

        var dto = MetaverseObjectHeaderDto.FromHeader(header);

        Assert.That(JsonSerializer.Serialize(dto.Attributes["Start Date"]), Is.EqualTo(ExpectedJson));
    }

    [Test]
    public void PendingExportAttributeValueChangeDto_FromEntity_EmitsTheDateInUtcWithZ()
    {
        var dto = PendingExportAttributeValueChangeDto.FromEntity(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "startDate", Type = AttributeDataType.DateTime },
            AttributeId = 1,
            ChangeType = PendingExportAttributeChangeType.Update,
            DateTimeValue = StoredDate
        });

        Assert.That(JsonSerializer.Serialize(dto.DateTimeValue), Is.EqualTo(ExpectedJson));
    }
}
