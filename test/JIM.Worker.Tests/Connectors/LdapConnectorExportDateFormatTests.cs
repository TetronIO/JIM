// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The GeneralizedTime the LDAP Connector writes (#2153, S6): a whole second keeps the <c>.0Z</c> form every directory
/// already accepts, and a fraction is written with up to six digits, trailing zeros trimmed, so a microsecond export to
/// an RFC directory round-trips exactly.
/// </summary>
[TestFixture]
public class LdapConnectorExportDateFormatTests
{
    private static readonly DateTime Instant = new(2026, 3, 15, 10, 30, 45, DateTimeKind.Utc);

    [Test]
    public void ConvertDateTimeToLdapFormat_WholeSecond_KeepsDotZeroZ()
    {
        Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant), Is.EqualTo("20260315103045.0Z"));
    }

    [Test]
    public void ConvertDateTimeToLdapFormat_Fraction_WritesUpToSixDigitsTrimmed()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant.AddTicks(1_234_560)), Is.EqualTo("20260315103045.123456Z"));
            Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant.AddMilliseconds(500)), Is.EqualTo("20260315103045.5Z"));
            Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant.AddTicks(1_234_567)), Is.EqualTo("20260315103045.123456Z"),
                "A value is written as JIM holds it, floored to the microsecond.");
        }
    }

    [Test]
    public void ConvertDateTimeToLdapFormat_LocalValue_IsWrittenAsTheSameUtcInstant()
    {
        Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant.ToLocalTime()), Is.EqualTo("20260315103045.0Z"));
    }

    [Test]
    public void ConvertDateTimeToLdapFormat_NonGregorianCulture_StillWritesTheGregorianYear()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.That(LdapConnectorExport.ConvertDateTimeToLdapFormat(Instant), Is.EqualTo("20260315103045.0Z"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
