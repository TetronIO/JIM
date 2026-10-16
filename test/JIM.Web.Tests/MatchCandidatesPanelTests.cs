// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The candidates an ambiguous match lists on its Activity item (#2052). Before this, the item carried the colliding
/// Metaverse Objects only as ids inside its error message; the panel's job is to make each one something to open.
/// </summary>
[TestFixture]
public class MatchCandidatesPanelTests : JimComponentTestContext
{
    private static readonly Guid LiveId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeletedId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid JoinedCsoId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static MatchCandidateDetail Live(int total = 2) => new()
    {
        Candidate = Candidate(LiveId, "Ann Bloggs", ordinal: 0, total),
        Exists = true,
        MetaverseObjectTypePluralName = "People",
        JoinedObjects =
        [
            new MatchCandidateJoinedObject
            {
                ConnectedSystemObjectId = JoinedCsoId, ConnectedSystemId = 7, ConnectedSystemName = "Corporate Directory",
                TypeName = "user", ExternalId = "abloggs"
            }
        ]
    };

    private static MatchCandidateDetail Deleted(int total = 2) => new()
    {
        Candidate = Candidate(DeletedId, "Zoe Bloggs", ordinal: 1, total),
        Exists = false
    };

    private static ActivityRunProfileExecutionItemMatchCandidate Candidate(Guid id, string name, int ordinal, int total) => new()
    {
        CandidateKind = MatchCandidateKind.MetaverseObject,
        CandidateId = id,
        DisplayNameSnapshot = name,
        TypeNameSnapshot = "Person",
        MatchedValue = "48213",
        ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeNumber",
        Ordinal = ordinal,
        CandidateTotal = total
    };

    private IRenderedComponent<MatchCandidatesPanel> RenderPanel(params MatchCandidateDetail[] candidates) =>
        Render<MatchCandidatesPanel>(p => p.Add(c => c.Candidates, candidates));

    [Test]
    public void Render_CandidateThatStillExists_LinksToItsMetaverseObject()
    {
        var cut = RenderPanel(Live(), Deleted());

        var chip = cut.FindComponents<ObjectChip>().Single(c => c.Instance.Kind == ObjectChipKind.MetaverseObject && c.Instance.Name == "Ann Bloggs");
        Assert.That(chip.Instance.Href, Is.EqualTo($"/t/people/v/{LiveId}"));
    }

    [Test]
    public void Render_CandidateSinceDeleted_IsNamedButNotLinkedAndSaysSo()
    {
        var cut = RenderPanel(Live(), Deleted());

        var chip = cut.FindComponents<ObjectChip>().Single(c => c.Instance.Kind == ObjectChipKind.MetaverseObject && c.Instance.Name == "Zoe Bloggs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Instance.Href, Is.Null, "a link to a deleted object leads nowhere");
            Assert.That(cut.FindAll($"[data-testid='jim-match-candidate-deleted-{DeletedId}']"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"[data-testid='jim-match-candidate-deleted-{LiveId}']"), Is.Empty);
        }
    }

    [Test]
    public void Render_CandidateWithAJoinedObject_LinksTheJoinedObject()
    {
        var cut = RenderPanel(Live(), Deleted());

        var joined = cut.FindComponents<ObjectChip>().Single(c => c.Instance.Kind == ObjectChipKind.ConnectedSystemObject);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(joined.Instance.Href, Is.EqualTo($"/admin/connected-systems/7/connector-space/{JoinedCsoId}"));
            Assert.That(joined.Instance.Name, Is.EqualTo("abloggs"), "named by its external id when it has no display name");
            Assert.That(joined.Instance.Tooltip, Does.Contain("Corporate Directory"));
        }
    }

    [Test]
    public void Render_MoreFoundThanListed_CountsThemAll()
    {
        var cut = RenderPanel(Live(total: 14), Deleted(total: 14));

        var count = cut.FindComponent<TableObjectCount>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(count.Instance.Count, Is.EqualTo(2));
            Assert.That(count.Instance.Total, Is.EqualTo(14));
        }
    }

    [Test]
    public void Render_NoCandidates_RendersNothing()
    {
        var cut = RenderPanel();

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
