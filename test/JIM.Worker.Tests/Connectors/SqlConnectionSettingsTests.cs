// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The discrete Connectivity settings a provider turns into a connection string. The password rides
/// this type, so the type itself must not be able to leak it into a log line or an exception message.
/// </summary>
[TestFixture]
public class SqlConnectionSettingsTests
{
    [Test]
    public void ToString_PasswordSet_RedactsIt()
    {
        var settings = new SqlConnectionSettings
        {
            Host = "sql.example.local",
            DatabaseName = "HR",
            Username = "jim_reader",
            Password = "s3cret"
        };

        var rendered = settings.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Not.Contain("s3cret"),
                "Anything that reaches a log or an exception message must never carry the credential; a record's generated ToString would.");
            Assert.That(rendered, Does.Contain("sql.example.local"),
                "Redaction must not cost the diagnostic value of knowing which host was addressed.");
        });
    }

    [Test]
    public void ToString_NoPasswordSet_StillRedacts()
    {
        var settings = new SqlConnectionSettings { Host = "sql.example.local" };

        Assert.That(settings.ToString(), Does.Not.Contain("Password=\""),
            "The rendered form must not distinguish a set password from an unset one.");
    }
}
