// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.ExampleData;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A repository reads through the context it was given. <see cref="ExampleDataRepository"/> was the
/// second of two places that opened a <see cref="JimDbContext"/> of its own instead, which takes an
/// extra pooled connection and reads its configuration from the environment rather than from
/// whatever the caller was working against.
/// </summary>
/// <remarks>
/// Driven through a mocked context with no database behind it, so a read that reaches for a
/// connection of its own cannot pass.
/// </remarks>
[TestFixture]
public class ExampleDataRepositoryContextTests
{
    [Test]
    public async Task GetTemplateHeaderAsync_ReadsFromTheContextItWasGivenAsync()
    {
        // Moq's proxy runs JimDbContext's parameterless constructor, which builds a connection
        // string; the values point at a host called "dummy", so nothing that opens its own context
        // can get an answer here.
        TestUtilities.SetEnvironmentVariables();

        var templates = new List<ExampleDataTemplate>
        {
            new() { Id = 3, Name = "Contoso Users", BuiltIn = true }
        };

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.ExampleDataTemplates).Returns(templates.BuildMockDbSet().Object);

        using var jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object));

        var header = await jim.ExampleData.GetTemplateHeaderAsync(3);

        Assert.That(header, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(header!.Name, Is.EqualTo("Contoso Users"));
            Assert.That(header.BuiltIn, Is.True);
        });
    }
}
