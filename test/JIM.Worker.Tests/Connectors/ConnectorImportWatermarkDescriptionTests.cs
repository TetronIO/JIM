// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Connectors.LDAP;
using JIM.Connectors.SCIM;
using JIM.Connectors.SCIM.ServiceProvider;
using JIM.Connectors.Sql;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers each built-in Connector's reading of its own import watermark, which the Activity page shows in place of
/// the raw persisted data (an LDAP Connector's, for example, is mostly its detected view of the directory).
/// </summary>
[TestFixture]
public class ConnectorImportWatermarkDescriptionTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private static IEnumerable<TestCaseData> WatermarkKeepingConnectors()
    {
        yield return new TestCaseData(new LdapConnector()).SetArgDisplayNames("LDAP");
        yield return new TestCaseData(new ScimServiceProviderConnector()).SetArgDisplayNames("SCIM 2.0 Service Provider");
        yield return new TestCaseData(new ScimConnector()).SetArgDisplayNames("SCIM 2.0 Client");
        yield return new TestCaseData(new SqlConnector()).SetArgDisplayNames("SQL");
    }

    [TestCaseSource(nameof(WatermarkKeepingConnectors))]
    public void DescribeImportWatermark_NoOrUnreadableData_ReturnsNull(IConnector connector)
    {
        var describer = connector as IConnectorImportWatermarkDescription;
        Assert.That(describer, Is.Not.Null, "every built-in Connector that keeps a watermark must describe it");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(describer!.DescribeImportWatermark(null, Logger), Is.Null);
            Assert.That(describer!.DescribeImportWatermark("", Logger), Is.Null);
            Assert.That(describer!.DescribeImportWatermark("not json", Logger), Is.Null);
        }
    }

    [Test]
    public void DescribeImportWatermark_LdapOpenLdap_ReturnsTheAccessLogTimeAndNothingElse()
    {
        // The persisted data is mostly the directory's detected capabilities; only the access log time is a watermark.
        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.OpenLDAP,
            LastAccesslogTimestamp = "20261024202113.992856Z",
            NamingContexts = ["dc=yellowstone,dc=local", "cn=accesslog"],
            SupportedControls = ["2.16.840.1.113730.3.4.5"]
        };

        var description = new LdapConnector().DescribeImportWatermark(JsonSerializer.Serialize(rootDse), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("access log"));
            Assert.That(description!.Points, Has.Count.EqualTo(1));
            Assert.That(description!.Points[0].Scope, Is.Null);
            Assert.That(description!.Points[0].Time, Is.EqualTo(new DateTime(2026, 10, 24, 20, 21, 13, 992, DateTimeKind.Utc).AddTicks(8560)));
            Assert.That(description!.Points[0].Time!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void DescribeImportWatermark_LdapActiveDirectory_ReturnsTheUpdateSequenceNumber()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory, HighestCommittedUsn = 482913 };

        var description = new LdapConnector().DescribeImportWatermark(JsonSerializer.Serialize(rootDse), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("Update Sequence Number"));
            Assert.That(description!.Points.Single().Number, Is.EqualTo(482913));
        }
    }

    [Test]
    public void DescribeImportWatermark_LdapChangelogDirectory_ReturnsTheChangeNumber()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.DirectoryServer389, LastChangeNumber = 1207 };

        var description = new LdapConnector().DescribeImportWatermark(JsonSerializer.Serialize(rootDse), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("change log entry"));
            Assert.That(description!.Points.Single().Number, Is.EqualTo(1207));
        }
    }

    [Test]
    public void DescribeImportWatermark_LdapActiveDirectoryWithoutAUsn_ReturnsNull()
    {
        // The data is there, but this directory's watermark is not: nothing to describe.
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory, LastAccesslogTimestamp = "20261024202113.992856Z" };

        Assert.That(new LdapConnector().DescribeImportWatermark(JsonSerializer.Serialize(rootDse), Logger), Is.Null);
    }

    [Test]
    public void DescribeImportWatermark_ScimServiceProvider_ReturnsTheChangeSequence()
    {
        var description = new ScimServiceProviderConnector().DescribeImportWatermark("{\"changeSequence\":8}", Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("change sequence"));
            Assert.That(description!.Points.Single().Number, Is.EqualTo(8));
        }
    }

    [Test]
    public void DescribeImportWatermark_ScimClient_ReturnsTheWatermarkInstantInUtc()
    {
        var state = new ScimImportState { Watermark = new DateTimeOffset(2026, 10, 24, 22, 15, 0, TimeSpan.FromHours(1)) };

        var description = new ScimConnector().DescribeImportWatermark(state.Serialise(), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("last modified time"));
            Assert.That(description!.Points.Single().Time, Is.EqualTo(new DateTime(2026, 10, 24, 21, 15, 0, DateTimeKind.Utc)));
            Assert.That(description!.Points.Single().Time!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void DescribeImportWatermark_SqlChangeLog_ReturnsAPointPerObjectType()
    {
        var watermark = new SqlConnectorWatermark
        {
            Mode = SqlDeltaImportMode.ChangeLogTable,
            ObjectTypes =
            {
                ["User"] = new SqlDeltaValue("1500", AttributeDataType.LongNumber),
                ["Group"] = new SqlDeltaValue("42", AttributeDataType.Number)
            }
        };

        var description = new SqlConnector().DescribeImportWatermark(watermark.Serialise(), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("change log sequence"));
            Assert.That(description!.Points.Select(p => (p.Scope, p.Number)), Is.EquivalentTo(new (string?, long?)[] { ("User", 1500), ("Group", 42) }));
        }
    }

    [Test]
    public void DescribeImportWatermark_SqlWatermarkColumn_IncludesRelatedTablesAndKeepsDatesAsText()
    {
        // A watermark column's date is only ever compared against that column, never read as a point in time, so it
        // is shown as the Connector holds it.
        var watermark = new SqlConnectorWatermark
        {
            Mode = SqlDeltaImportMode.WatermarkColumn,
            ObjectTypes = { ["User"] = new SqlDeltaValue("2026-10-24T20:00:00.0000000Z", AttributeDataType.DateTime) },
            RelatedTables = { ["User"] = new Dictionary<string, SqlDeltaValue> { ["Roles"] = new SqlDeltaValue("17", AttributeDataType.Number) } }
        };

        var description = new SqlConnector().DescribeImportWatermark(watermark.Serialise(), Logger);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("watermark column"));
            Assert.That(description!.Points, Has.Count.EqualTo(2));
            Assert.That(description!.Points[0].Scope, Is.EqualTo("User"));
            Assert.That(description!.Points[0].Text, Is.EqualTo("2026-10-24T20:00:00.0000000Z"));
            Assert.That(description!.Points[0].Time, Is.Null);
            Assert.That(description!.Points[1].Scope, Is.EqualTo("User: Roles"));
            Assert.That(description!.Points[1].Number, Is.EqualTo(17));
        }
    }
}
