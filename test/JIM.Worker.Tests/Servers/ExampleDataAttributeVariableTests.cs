// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An Example Data pattern's <c>{Attribute}</c> variable names a Metaverse Attribute, and configuration names ignore
/// case (#2051): <c>{first name}</c> reads First Name.
/// </summary>
[TestFixture]
public class ExampleDataAttributeVariableTests
{
    private static MetaverseObject Person() => new()
    {
        AttributeValues =
        [
            new MetaverseObjectAttributeValue { Attribute = new MetaverseAttribute { Name = "First Name" }, StringValue = "Ada" },
            new MetaverseObjectAttributeValue { Attribute = new MetaverseAttribute { Name = "Last Name" }, StringValue = "Lovelace" }
        ]
    };

    [Test]
    public void ReplaceAttributeVariables_ExactName_ReplacesIt()
    {
        Assert.That(ExampleDataServer.ReplaceAttributeVariables(Person(), "{First Name}.{Last Name}"), Is.EqualTo("Ada.Lovelace"));
    }

    [Test]
    public void ReplaceAttributeVariables_NameInDifferentCase_ReplacesIt()
    {
        Assert.That(ExampleDataServer.ReplaceAttributeVariables(Person(), "{first name}@example.io"), Is.EqualTo("Ada@example.io"));
    }

    [Test]
    public void ReplaceAttributeVariables_UnknownName_Throws()
    {
        Assert.That(() => ExampleDataServer.ReplaceAttributeVariables(Person(), "{Middle Name}"), Throws.TypeOf<InvalidDataException>());
    }
}
