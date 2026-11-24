// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Linq;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// A Synchronisation Rule's validation findings carry a message the portal shows in the viewer's language (#3030),
/// beside the British English everything else reads.
/// </summary>
[TestFixture]
public class SyncRuleValidationLocalisationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Test]
    public void Validate_EmptyRule_EnglishUnchangedAndEveryFindingRendersInFrench()
    {
        var findings = new SyncRule().Validate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Select(f => f.Message), Is.EqualTo(new[]
            {
                "Name must be set",
                "Connected System must be set",
                "Connected System Object Type must be set",
                "Metaverse Object Type must be set",
                "Direction must be set",
                "No Attribute Flow Rules have been defined. Whilst valid, this means no data will flow between the two systems"
            }));
            Assert.That(findings.Select(f => f.Detail), Has.None.Null);
            Assert.That(findings.Select(f => f.Detail!.ToEnglish()), Is.EqualTo(findings.Select(f => f.Message)));
            Assert.That(MessageRenderer.Render(findings[0].Detail!, French, French), Is.EqualTo("Le nom doit être défini"));
        }
    }
}
