// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;

namespace Tetron.PowerShellRemoting.Tests;

public class RemoteSessionOptionsTests
{
    [Test]
    public void Password_CannotBeReadFromOutsideTheLibrary()
    {
        // Structured loggers and serialisers read public properties, so logging the options object must not log the
        // password with it.
        var getter = typeof(RemoteSessionOptions).GetProperty(nameof(RemoteSessionOptions.Password))!.GetGetMethod(nonPublic: false);

        Assert.That(getter, Is.Null);
    }

    [Test]
    public void ToString_NamesTheEndpointAndAccountButNotThePassword()
    {
        var options = new RemoteSessionOptions
        {
            ConnectionUri = new Uri("http://ex1.panoply.local/PowerShell/"),
            UserPrincipalName = "svc-jim@PANOPLY.LOCAL",
            Password = "Sup3r-Secret!"
        };

        var text = options.ToString();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("http://ex1.panoply.local/PowerShell/"));
            Assert.That(text, Does.Contain("svc-jim@PANOPLY.LOCAL"));
            Assert.That(text, Does.Not.Contain("Sup3r-Secret!"));
        }
    }
}
