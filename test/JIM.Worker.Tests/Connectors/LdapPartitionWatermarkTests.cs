// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The value JIM holds for one LDAP Partition's Delta Import watermark (#2715): what each change source keeps, written
/// out and read back unchanged, and nothing taken for a watermark that is not one.
/// </summary>
[TestFixture]
public class LdapPartitionWatermarkTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    /// <summary>A watermark of each kind, every field it carries set; named by kind because the type is internal.</summary>
    private static LdapPartitionWatermark OneOf(string kindName) => Enum.Parse<LdapDeltaSourceKind>(kindName) switch
    {
        LdapDeltaSourceKind.Usn => new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Usn,
            Usn = 9_876_543_210,
            InvocationId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            DnsHostName = "dc1.corp.local"
        },
        LdapDeltaSourceKind.Changelog => new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Changelog,
            ChangeNumber = 4_200_000_000,
            ChangeTime = "20261007190130Z"
        },
        _ => new LdapPartitionWatermark
        {
            Kind = LdapDeltaSourceKind.Accesslog,
            AccesslogTimestamp = "20261024202113.992856Z",
            AccesslogTimestampIsLogged = true
        }
    };

    [TestCase(nameof(LdapDeltaSourceKind.Usn))]
    [TestCase(nameof(LdapDeltaSourceKind.Changelog))]
    [TestCase(nameof(LdapDeltaSourceKind.Accesslog))]
    public void Serialise_ThenParse_ReadsBackEveryField(string kindName)
    {
        var watermark = OneOf(kindName);

        var readBack = LdapPartitionWatermark.Parse(watermark.Serialise(), Logger);

        Assert.That(readBack, Is.EqualTo(watermark));
    }

    [Test]
    public void Serialise_Always_NamesTheKindRatherThanNumberingIt()
    {
        // The value is shown raw beside the Activity's description, so it says what it is.
        var serialised = new LdapPartitionWatermark { Kind = LdapDeltaSourceKind.Changelog, ChangeNumber = 7 }.Serialise();

        Assert.That(serialised, Does.Contain("\"Changelog\""));
    }

    [TestCase(null, Description = "no watermark recorded")]
    [TestCase("", Description = "an empty value")]
    [TestCase("not json", Description = "a value that is not JSON")]
    [TestCase("null", Description = "JSON that holds nothing")]
    [TestCase("{\"Usn\":42}", Description = "no kind, so nothing says how to read the position")]
    [TestCase("{\"Kind\":\"Paper\",\"Usn\":42}", Description = "a kind no change source reads")]
    [TestCase("[1,2]", Description = "JSON of another shape")]
    public void Parse_ValueHoldingNoWatermark_ReturnsNullWithoutThrowing(string? value)
    {
        LdapPartitionWatermark? parsed = null;

        Assert.That(() => parsed = LdapPartitionWatermark.Parse(value, Logger), Throws.Nothing);
        Assert.That(parsed, Is.Null);
    }

    #region DescribePartitionWatermark

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    public void DescribePartitionWatermark_NoOrUnreadableValue_ReturnsNull(string? value)
    {
        Assert.That(Describe(value), Is.Null);
    }

    [Test]
    public void DescribePartitionWatermark_Accesslog_ReturnsTheAccessLogTimeInUtc()
    {
        var value = OneOf(nameof(LdapDeltaSourceKind.Accesslog)).Serialise();

        var description = Describe(value);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("access log"));
            Assert.That(description!.Points, Has.Count.EqualTo(1));
            Assert.That(description!.Points[0].Scope, Is.Null, "JIM scopes each point by the partition's name");
            Assert.That(description!.Points[0].Time, Is.EqualTo(new DateTime(2026, 10, 24, 20, 21, 13, 992, DateTimeKind.Utc).AddTicks(8560)));
            Assert.That(description!.Points[0].Time!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void DescribePartitionWatermark_Usn_ReturnsTheUpdateSequenceNumberAndNotTheDomainController()
    {
        var value = OneOf(nameof(LdapDeltaSourceKind.Usn)).Serialise();

        var description = Describe(value);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("Update Sequence Number"));
            Assert.That(description!.Points.Single().Number, Is.EqualTo(9_876_543_210));
            Assert.That(description!.Points.Single().Scope, Is.Null);
        }
    }

    [Test]
    public void DescribePartitionWatermark_Changelog_ReturnsTheChangeNumber()
    {
        var value = OneOf(nameof(LdapDeltaSourceKind.Changelog)).Serialise();

        var description = Describe(value);

        Assert.That(description, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("change log entry"));
            Assert.That(description!.Points.Single().Number, Is.EqualTo(4_200_000_000));
        }
    }

    [TestCase("{\"Kind\":\"Usn\",\"InvocationId\":\"11111111-2222-3333-4444-555555555555\"}", Description = "a USN watermark with no USN")]
    [TestCase("{\"Kind\":\"Usn\",\"AccesslogTimestamp\":\"20261024202113.992856Z\"}", Description = "another kind's position, which a USN watermark does not hold")]
    [TestCase("{\"Kind\":\"Accesslog\",\"AccesslogTimestamp\":\"yesterday\"}", Description = "an access log time that does not read as one")]
    public void DescribePartitionWatermark_KindWithoutItsPosition_ReturnsNull(string value)
    {
        Assert.That(Describe(value), Is.Null);
    }

    [Test]
    public void LdapConnector_DescribesItsWatermarksPerPartitionOnly()
    {
        // The Connected System-level record holds no position (#2715): describing it as well would show an import's
        // read range twice, and a Connector Space clear must keep it, the pinned domain controller included (#2724).
        Assert.That(typeof(IConnectorImportWatermark).IsAssignableFrom(typeof(LdapConnector)), Is.False);
    }

    private static ImportWatermarkDescription? Describe(string? value)
    {
        using var connector = new LdapConnector();
        return connector.DescribePartitionWatermark(value, Logger);
    }

    #endregion
}
