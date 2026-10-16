// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The likely cause of an unresolved reference (#959), judged from the Connected System's current Container Scope
/// with the Connector's own containment rule, so the portal can never describe a scope the import does not search.
/// Uses the LDAP Connector's real rule rather than a stand-in: escaped separators and One Level scope are part of
/// what decides the answer.
/// </summary>
[TestFixture]
public class UnresolvedReferenceClassifierTests
{
    private const string Corp = "DC=corp,DC=example,DC=com";
    private const string Lab = "DC=lab,DC=example,DC=com";
    private const string Emea = "DC=emea,DC=corp,DC=example,DC=com";

    private LdapConnector _connector = null!;
    private ConnectedSystem _connectedSystem = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new LdapConnector();
        _connectedSystem = BuildConnectedSystem();
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
    }

    [Test]
    public void Diagnose_ValueInAContainerNoSelectionCovers_NamesTheContainerToSelect()
    {
        var diagnosis = Classifier().Diagnose("member", $"CN=Ana Silva,OU=Contractors,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerNotInScope));
            Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Contractors,{Corp}"));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.Jim));
            Assert.That(diagnosis.Finding, Does.Contain($"OU=Contractors,{Corp}"), "The finding names the Container, so the portal can set it as a value.");
            Assert.That(diagnosis.Remedy, Is.Not.Empty);
        }
    }

    [Test]
    public void Diagnose_ValueBeneathANestedUnselectedContainer_NamesTheNearestContainer()
    {
        // Selecting the nearest Container is the smallest change that brings the object into scope.
        var diagnosis = Classifier().Diagnose("member", $"CN=Ben Ortiz,OU=Agency,OU=Contractors,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerNotInScope));
            Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Agency,OU=Contractors,{Corp}"));
        }
    }

    [Test]
    public void Diagnose_ValueWithAnEscapedComma_IsPlacedByTheConnectorsOwnRule()
    {
        var diagnosis = Classifier().Diagnose("member", $"CN=Silva\\, Ana,OU=Contractors,{Corp}");

        Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Contractors,{Corp}"));
    }

    [Test]
    public void Diagnose_ValueInAnExcludedContainer_NamesTheExclusion()
    {
        var diagnosis = Classifier().Diagnose("manager", $"CN=Old Hand,OU=Leavers,OU=Staff,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerExcluded));
            Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Leavers,OU=Staff,{Corp}"));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.Jim));
            Assert.That(diagnosis.Finding, Does.Contain($"OU=Leavers,OU=Staff,{Corp}"));
        }
    }

    [Test]
    public void Diagnose_ValueBeneathAOneLevelSelection_IsNotInScope()
    {
        // OU=Groups is selected One Level: it covers what sits directly in it, not OU=Nested beneath it.
        var diagnosis = Classifier().Diagnose("member", $"CN=Team,OU=Nested,OU=Groups,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerNotInScope));
            Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Nested,OU=Groups,{Corp}"));
        }
    }

    [Test]
    public void Diagnose_ValueInScope_SendsTheAdministratorToTheSourceSystem()
    {
        var diagnosis = Classifier().Diagnose("member", $"CN=Lee Grant,OU=Staff,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.NotFoundInScope));
            Assert.That(diagnosis.ContainerExternalId, Is.EqualTo($"OU=Staff,{Corp}"));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.SourceSystem));
            Assert.That(diagnosis.Finding, Does.Contain($"OU=Staff,{Corp}"));
            Assert.That(diagnosis.Remedy, Does.Contain("Full Import"));
        }
    }

    [Test]
    public void Diagnose_ValueInAnUnselectedPartition_NamesThePartition()
    {
        var diagnosis = Classifier().Diagnose("member", $"CN=Test User,OU=Users,{Lab}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.PartitionNotSelected));
            Assert.That(diagnosis.PartitionExternalId, Is.EqualTo(Lab));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.Jim));
            Assert.That(diagnosis.Finding, Does.Contain(Lab));
        }
    }

    [Test]
    public void Diagnose_ValueInAChildDomainPartitionBeneathASelectedOne_NamesTheChildPartition()
    {
        // DC=emea,DC=corp,... sits beneath the selected DC=corp,... but is a partition of its own, and unselected:
        // the most specific partition decides, exactly as the most specific Container does.
        var diagnosis = Classifier().Diagnose("member", $"CN=Dana Kovac,OU=Users,{Emea}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.PartitionNotSelected));
            Assert.That(diagnosis.PartitionExternalId, Is.EqualTo(Emea));
        }
    }

    [Test]
    public void Diagnose_ValueOutsideEveryPartition_SaysJimCannotSeeIt()
    {
        var diagnosis = Classifier().Diagnose("member", "CN=Remote User,OU=Users,DC=partner,DC=org");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.OutsideConnectedSystem));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.SourceSystem));
            Assert.That(diagnosis.Remedy, Does.Contain("Warn"));
        }
    }

    [Test]
    public void Diagnose_ValueOutsideEveryPartitionUnderWarn_SuggestsIgnoreRatherThanTheSettingAlreadyChosen()
    {
        _connectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;

        var diagnosis = Classifier().Diagnose("member", "CN=Remote User,OU=Users,DC=partner,DC=org");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Remedy, Does.Not.Contain("to Warn"), "Warn is already chosen; telling the reader to choose it reads as a fault.");
            Assert.That(diagnosis.Remedy, Does.Contain("Ignore"));
        }
    }

    [Test]
    public void Diagnose_ValueOutsideEveryPartitionUnderIgnore_SuggestsNoChangeToTheSetting()
    {
        _connectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore;

        var diagnosis = Classifier().Diagnose("member", "CN=Remote User,OU=Users,DC=partner,DC=org");

        Assert.That(diagnosis.Remedy, Does.Not.Contain("set Unresolved Reference Handling"),
            "Ignore already stops runs reporting them; there is no further setting to change.");
    }

    [Test]
    public void Diagnose_AmbiguousValue_TakesPrecedenceAndNamesTheCandidateObjectTypes()
    {
        var diagnosis = Classifier().Diagnose("member", "42", ambiguousWith: ["user", "contact"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.AmbiguousObjectType));
            Assert.That(diagnosis.CandidateObjectTypeNames, Is.EqualTo(new[] { "contact", "user" }));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.Jim));
            Assert.That(diagnosis.Finding, Does.Contain("member"));
        }
    }

    [Test]
    public void Diagnose_ConnectorThatCannotSayWhatItsContainersHold_FallsBackToNotFound()
    {
        var diagnosis = new UnresolvedReferenceClassifier(_connectedSystem, containment: null)
            .Diagnose("manager", "EMP-48213");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.NotFound));
            Assert.That(diagnosis.FixLocation, Is.EqualTo(UnresolvedReferenceFixLocation.SourceSystem));
            Assert.That(diagnosis.Remedy, Does.Contain("Full Import"));
        }
    }

    [Test]
    public void Diagnose_ValueOutsideEveryKnownContainer_NamesItsPartition()
    {
        // No Container JIM knows of holds it, so the partition is the place the finding can point at.
        var diagnosis = Classifier().Diagnose("member", $"CN=Pat Lee,OU=Unknown,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerNotInScope));
            Assert.That(diagnosis.ContainerExternalId, Is.Null);
            Assert.That(diagnosis.PartitionExternalId, Is.EqualTo(Corp));
            Assert.That(diagnosis.Finding, Does.Contain(Corp));
        }
    }

    [Test]
    public void Diagnose_CarriesTheAttributeAndValueThrough()
    {
        var diagnosis = Classifier().Diagnose("managedBy", $"CN=Mark Doyle,OU=Contractors,{Corp}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.AttributeName, Is.EqualTo("managedBy"));
            Assert.That(diagnosis.Value, Is.EqualTo($"CN=Mark Doyle,OU=Contractors,{Corp}"));
        }
    }

    private UnresolvedReferenceClassifier Classifier() => new(_connectedSystem, _connector);

    /// <summary>
    /// Corp AD: DC=corp selected (OU=Staff selected with OU=Leavers excluded beneath it, OU=Groups selected One Level
    /// with OU=Nested beneath it, OU=Contractors and OU=Agency unselected); DC=lab and the DC=emea child domain known
    /// but unselected.
    /// </summary>
    private static ConnectedSystem BuildConnectedSystem()
    {
        var corp = new ConnectedSystemPartition { Id = 1, ExternalId = Corp, Name = "corp.example.com", Selected = true, Containers = [] };
        var staff = Container(10, $"OU=Staff,{Corp}", "Staff", corp, selected: true);
        Container(11, $"OU=Leavers,OU=Staff,{Corp}", "Leavers", parent: staff, excluded: true);
        var contractors = Container(12, $"OU=Contractors,{Corp}", "Contractors", corp);
        Container(13, $"OU=Agency,OU=Contractors,{Corp}", "Agency", parent: contractors);
        var groups = Container(14, $"OU=Groups,{Corp}", "Groups", corp, selected: true, scope: ConnectedSystemContainerScope.OneLevel);
        Container(15, $"OU=Nested,OU=Groups,{Corp}", "Nested", parent: groups);

        var lab = new ConnectedSystemPartition { Id = 2, ExternalId = Lab, Name = "lab.example.com", Selected = false, Containers = [] };
        Container(20, $"OU=Users,{Lab}", "Users", lab);

        var emea = new ConnectedSystemPartition { Id = 3, ExternalId = Emea, Name = "emea.corp.example.com", Selected = false, Containers = [] };
        Container(30, $"OU=Users,{Emea}", "Users", emea);

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Corp AD",
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector", SupportsPartitionContainers = true },
            Partitions = [corp, lab, emea]
        };
    }

    private static ConnectedSystemContainer Container(
        int id, string externalId, string name,
        ConnectedSystemPartition? partition = null,
        ConnectedSystemContainer? parent = null,
        bool selected = false,
        bool excluded = false,
        ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree)
    {
        var container = new ConnectedSystemContainer
        {
            Id = id,
            ExternalId = externalId,
            Name = name,
            Selected = selected,
            Excluded = excluded,
            Scope = scope
        };

        if (partition != null)
        {
            container.Partition = partition;
            container.PartitionId = partition.Id;
            partition.Containers!.Add(container);
        }

        if (parent != null)
            parent.AddChildContainer(container);

        return container;
    }
}
