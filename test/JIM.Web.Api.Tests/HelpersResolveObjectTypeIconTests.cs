using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersResolveObjectTypeIconTests
{
    [Test]
    public void ResolveObjectTypeIcon_KnownName_ReturnsCorrectIcon()
    {
        var result = Helpers.ResolveObjectTypeIcon("Person");

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Person));
    }

    [Test]
    public void ResolveObjectTypeIcon_GroupsName_ReturnsCorrectIcon()
    {
        var result = Helpers.ResolveObjectTypeIcon("Groups");

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Groups));
    }

    [Test]
    public void ResolveObjectTypeIcon_NullName_ReturnsFallbackIcon()
    {
        var result = Helpers.ResolveObjectTypeIcon(null);

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Category));
    }

    [Test]
    public void ResolveObjectTypeIcon_EmptyName_ReturnsFallbackIcon()
    {
        var result = Helpers.ResolveObjectTypeIcon(string.Empty);

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Category));
    }

    [Test]
    public void ResolveObjectTypeIcon_UnknownName_ReturnsFallbackIcon()
    {
        var result = Helpers.ResolveObjectTypeIcon("NonExistentIcon");

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Category));
    }

    [Test]
    public void ResolveObjectTypeIcon_CaseSensitive_UnknownLowercase_ReturnsFallback()
    {
        // Icon names are PascalCase; lowercase should not match
        var result = Helpers.ResolveObjectTypeIcon("person");

        Assert.That(result, Is.EqualTo(Icons.Material.Filled.Category));
    }
}
