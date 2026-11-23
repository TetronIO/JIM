// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Models.Localisation;
using JIM.Models.Tests.Resources;
using NUnit.Framework;

namespace JIM.Models.Tests.Localisation;

/// <summary>
/// A message JIM shows to a person is a key plus arguments (#3030), rendered in the reader's language by the portal and
/// in British English for the REST API, logs and anything stored, so a person's language never reaches a machine contract.
/// </summary>
[TestFixture]
public class MessageRendererTests
{
    private const string Catalogue = "JIM.Models.Tests:TestMessages";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Test]
    public void ToEnglish_FillsThePlaceholders()
    {
        var message = JimMessage.Create(Catalogue, nameof(TestMessages.NotFound), "HR");

        Assert.That(message.ToEnglish(), Is.EqualTo("The Connected System \"HR\" no longer exists."));
    }

    [Test]
    public void Render_French_UsesTheFrenchText()
    {
        var message = JimMessage.Create(Catalogue, nameof(TestMessages.NotFound), "HR");

        Assert.That(MessageRenderer.Render(message, French, French), Is.EqualTo("Le système connecté « HR » n’existe plus."));
    }

    [TestCase("en-GB", 0, "0 objects deleted.")]
    [TestCase("en-GB", 1, "1 object deleted.")]
    [TestCase("en-GB", 2, "2 objects deleted.")]
    [TestCase("fr", 0, "0 objet supprimé.")]
    [TestCase("fr", 1, "1 objet supprimé.")]
    [TestCase("fr", 1234, "1 234 objets supprimés.")]
    public void Render_CountedMessage_PicksTheFormByTheReadersLanguage(string culture, long count, string expected)
    {
        var language = CultureInfo.GetCultureInfo(culture);
        var message = JimMessage.Counted(Catalogue, nameof(TestMessages.ObjectsDeletedOne), count, count);

        // French groups thousands with a narrow no-break space; compare on ordinary spaces.
        Assert.That(MessageRenderer.Render(message, language, language).Replace(' ', ' ').Replace(' ', ' '), Is.EqualTo(expected));
    }

    [Test]
    public void Render_NestedMessage_IsRenderedInTheSameLanguage()
    {
        var reason = JimMessage.Create(Catalogue, nameof(TestMessages.NotFound), "HR");
        var message = JimMessage.Create(Catalogue, nameof(TestMessages.CouldNotSave), reason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo("Could not save: The Connected System \"HR\" no longer exists."));
            Assert.That(MessageRenderer.Render(message, French, French),
                Is.EqualTo("Enregistrement impossible : Le système connecté « HR » n’existe plus."));
        }
    }

    [Test]
    public void Render_KeyWithNoTranslation_FallsBackToEnglish()
    {
        var message = JimMessage.Create(Catalogue, nameof(TestMessages.EnglishOnly));

        Assert.That(MessageRenderer.Render(message, French, French), Is.EqualTo("Only in English."));
    }

    [Test]
    public void Render_UnknownCatalogue_ShowsTheKeyRatherThanThrowing()
    {
        var message = JimMessage.Create("No.Such.Assembly:Nothing", "SomeKey");

        Assert.That(message.ToEnglish(), Is.EqualTo("SomeKey"));
    }

    [Test]
    public void Message_RoundTripsThroughJson_AndRendersTheSame()
    {
        var reason = JimMessage.Create(Catalogue, nameof(TestMessages.NotFound), "HR");
        var original = JimMessage.Counted(Catalogue, nameof(TestMessages.ObjectsDeletedOne), 3, 3, reason);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<JimMessage>(json)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.ToEnglish(), Is.EqualTo(original.ToEnglish()));
            Assert.That(MessageRenderer.Render(restored, French, French), Is.EqualTo(MessageRenderer.Render(original, French, French)));
        }
    }

    [Test]
    [SetUICulture("fr")]
    [SetCulture("fr")]
    public void JimMessageException_MessageIsEnglishWhateverTheCurrentLanguage()
    {
        var exception = new JimMessageException(JimMessage.Create(Catalogue, nameof(TestMessages.NotFound), "HR"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo("The Connected System \"HR\" no longer exists."));
            Assert.That(exception.Detail.ToCurrentLanguage(), Is.EqualTo("Le système connecté « HR » n’existe plus."));
        }
    }
}
