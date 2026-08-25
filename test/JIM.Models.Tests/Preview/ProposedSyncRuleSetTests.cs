// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Logic;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// How a preview says which Synchronisation Rules the engine should evaluate (#1462).
///
/// The synchronisation preview engine previously took a single proposed rule and substituted it by id, never
/// adding. That expresses "this rule would be different" and nothing else, which covers a changed scope or a
/// changed mapping but cannot express a rule joining or leaving the evaluated set at all. Enabling a disabled rule
/// therefore previewed as no change (the rule is not in the loaded set, so the substitution found nothing), and
/// disabling an enabled one previewed as no change too (the disabled stand-in stayed in the list, and nothing
/// downstream re-checks Enabled). Both are confident wrong answers about the highest-blast-radius toggle a
/// Synchronisation Rule has.
/// </summary>
[TestFixture]
public class ProposedSyncRuleSetTests
{
    private static List<SyncRule> Loaded() =>
    [
        new() { Id = 1, Name = "HR Import" },
        new() { Id = 2, Name = "AD Export" }
    ];

    [Test]
    public void Substituting_ReplacesByIdAndNeverAdds()
    {
        // The original semantics, kept exactly: every adapter that previews a changed rule relies on them, and a
        // rule that is absent because it is disabled must stay absent.
        var rules = Loaded();

        ProposedSyncRuleSet.Substituting(new SyncRule { Id = 2, Name = "AD Export (proposed)" }).Apply(rules);
        ProposedSyncRuleSet.Substituting(new SyncRule { Id = 99, Name = "Not loaded" }).Apply(rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules, Has.Count.EqualTo(2));
            Assert.That(rules.Single(rule => rule.Id == 2).Name, Is.EqualTo("AD Export (proposed)"));
            Assert.That(rules.Any(rule => rule.Id == 99), Is.False);
        }
    }

    [Test]
    public void Adding_PutsARuleIntoTheSetThatWasNotThere()
    {
        // What enabling a disabled rule means: it starts being evaluated.
        var rules = Loaded();

        ProposedSyncRuleSet.Adding(new SyncRule { Id = 7, Name = "Newly enabled" }).Apply(rules);

        Assert.That(rules.Any(rule => rule.Id == 7), Is.True);
    }

    [Test]
    public void Adding_ARuleAlreadyInTheSet_ReplacesItRatherThanDuplicatingIt()
    {
        // A rule evaluated twice would double every contribution it makes, so the set is a set.
        var rules = Loaded();

        ProposedSyncRuleSet.Adding(new SyncRule { Id = 1, Name = "HR Import (proposed)" }).Apply(rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules, Has.Count.EqualTo(2));
            Assert.That(rules.Single(rule => rule.Id == 1).Name, Is.EqualTo("HR Import (proposed)"));
        }
    }

    [Test]
    public void Removing_TakesARuleOutOfTheSet()
    {
        // What disabling a rule means: it stops being evaluated. Substituting a disabled stand-in does not achieve
        // this, because nothing downstream of the load re-checks Enabled.
        var rules = Loaded();

        ProposedSyncRuleSet.Removing(1).Apply(rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules, Has.Count.EqualTo(1));
            Assert.That(rules.Single().Id, Is.EqualTo(2));
        }
    }

    [Test]
    public void Removing_ARuleThatIsNotThere_ChangesNothing()
    {
        var rules = Loaded();

        ProposedSyncRuleSet.Removing(99).Apply(rules);

        Assert.That(rules, Has.Count.EqualTo(2));
    }

    [Test]
    public void Apply_Null_IsRefused()
    {
        Assert.That(() => ProposedSyncRuleSet.Removing(1).Apply(null!), Throws.ArgumentNullException);
    }
}
