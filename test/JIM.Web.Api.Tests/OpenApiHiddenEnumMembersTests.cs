// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using JIM.Models.Transactional;
using JIM.Web.Extensions.Api;
using Microsoft.OpenApi;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The enum members the published OpenAPI document leaves out: those no API caller can meet or use. A Pending Export
/// still being written (Incomplete, #2919) is in no response, and a status filter naming it is refused.
/// </summary>
[TestFixture]
public class OpenApiHiddenEnumMembersTests
{
    private static OpenApiSchema SchemaOf<TEnum>() where TEnum : struct, Enum => new()
    {
        Enum = Enum.GetNames<TEnum>().Select(name => (JsonNode)JsonValue.Create(name)!).ToList()
    };

    private static List<string> Members(OpenApiSchema schema) => schema.Enum!.Select(node => node!.GetValue<string>()).ToList();

    [Test]
    public void ApplyToSchema_PendingExportStatus_LeavesOutIncompleteAndKeepsEveryOtherStatus()
    {
        var schema = SchemaOf<PendingExportStatus>();

        OpenApiHiddenEnumMembers.ApplyToSchema(schema, typeof(PendingExportStatus));

        Assert.That(Members(schema), Is.EquivalentTo(Enum.GetNames<PendingExportStatus>().Where(name => name != nameof(PendingExportStatus.Incomplete))));
    }

    [Test]
    public void ApplyToSchema_NullablePendingExportStatus_LeavesOutIncomplete()
    {
        var schema = SchemaOf<PendingExportStatus>();

        OpenApiHiddenEnumMembers.ApplyToSchema(schema, typeof(PendingExportStatus?));

        Assert.That(Members(schema), Does.Not.Contain(nameof(PendingExportStatus.Incomplete)));
    }

    [Test]
    public void ApplyToSchema_AnotherEnum_IsLeftWhole()
    {
        var schema = SchemaOf<PendingExportChangeType>();

        OpenApiHiddenEnumMembers.ApplyToSchema(schema, typeof(PendingExportChangeType));

        Assert.That(Members(schema), Is.EquivalentTo(Enum.GetNames<PendingExportChangeType>()));
    }
}
