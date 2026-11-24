// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Linq;
using JIM.Models.Exchange;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// A queued Exchange change is titled by the recipient type it makes the account, in the reader's language (#3084);
/// the title used to be the English noun with its first letter raised.
/// </summary>
[TestFixture]
public class ExchangeChangeTitleTests
{
    [Test]
    public void Title_EveryRecipientType_InBritishEnglish_ReadsAsItDidBefore()
    {
        using var _ = new UiCulture("en-GB");

        using (Assert.EnterMultipleScope())
        {
            foreach (var type in ExchangeRecipientTypes.All.Where(t => t != ExchangeRecipientTypes.None))
            {
                var noun = ExchangeRecipientRules.Noun(type);
                Assert.That(ExchangeChangeOutcomeDisplay.Title(type), Is.EqualTo(char.ToUpperInvariant(noun[0]) + noun[1..]), type);
            }
        }
    }

    [Test]
    public void Title_SharedMailbox_InFrench_ReadsTheTranslation()
    {
        using var _ = new UiCulture("fr");

        Assert.That(ExchangeChangeOutcomeDisplay.Title(ExchangeRecipientTypes.SharedMailbox), Is.EqualTo("Boîte aux lettres partagée"));
    }

    [TestCase(null)]
    [TestCase(ExchangeRecipientTypes.None)]
    [TestCase("RemoteMailbox")]
    public void Title_NoneOrUnrecognised_IsTheFallback(string? recipientType)
    {
        using var _ = new UiCulture("en-GB");

        Assert.That(ExchangeChangeOutcomeDisplay.Title(recipientType), Is.EqualTo("Exchange recipient"));
    }

    private sealed class UiCulture : System.IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCulture(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
