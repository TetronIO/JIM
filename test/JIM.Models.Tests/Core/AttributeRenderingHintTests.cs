using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

[TestFixture]
public class AttributeRenderingHintTests
{
    [Test]
    public void AttributeRenderingHint_Default_HasExpectedOrdinalAsync()
    {
        Assert.That((int)AttributeRenderingHint.Default, Is.EqualTo(0));
    }

    [Test]
    public void AttributeRenderingHint_Table_HasExpectedOrdinalAsync()
    {
        Assert.That((int)AttributeRenderingHint.Table, Is.EqualTo(1));
    }

    [Test]
    public void AttributeRenderingHint_ChipSet_HasExpectedOrdinalAsync()
    {
        Assert.That((int)AttributeRenderingHint.ChipSet, Is.EqualTo(2));
    }

    [Test]
    public void AttributeRenderingHint_List_HasExpectedOrdinalAsync()
    {
        Assert.That((int)AttributeRenderingHint.List, Is.EqualTo(3));
    }

    [Test]
    public void MetaverseAttribute_RenderingHint_DefaultsToDefaultAsync()
    {
        var attribute = new MetaverseAttribute();
        Assert.That(attribute.RenderingHint, Is.EqualTo(AttributeRenderingHint.Default));
    }

    [Test]
    public void MetaverseAttribute_RenderingHint_CanBeSetAsync()
    {
        var attribute = new MetaverseAttribute
        {
            RenderingHint = AttributeRenderingHint.Table
        };
        Assert.That(attribute.RenderingHint, Is.EqualTo(AttributeRenderingHint.Table));
    }
}
