// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the Run Profile Safeguards (#1618) validation rules: a limit only makes sense on an Export Run
/// Profile, and a negative limit is never valid. Mirrors the existing Verification Mode rule.
/// </summary>
[TestFixture]
public class RunProfileSafeguardsValidatorTests
{
    [Test]
    public void Validate_NoLimitsSet_ReturnsNull()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.DeltaImport };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile), Is.Null);
    }

    [Test]
    public void Validate_LimitsSetOnExportRunProfile_ReturnsNull()
    {
        var runProfile = new ConnectedSystemRunProfile
        {
            RunType = ConnectedSystemRunType.Export,
            MaxCreates = 5,
            MaxUpdates = 0,
            MaxDeletes = 100
        };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile), Is.Null);
    }

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public void Validate_MaxCreatesOnNonExportRunProfile_NamesTheField(ConnectedSystemRunType runType)
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = runType, MaxCreates = 10 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxCreates can only be set on an Export Run Profile."));
    }

    [Test]
    public void Validate_MaxUpdatesOnNonExportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.DeltaImport, MaxUpdates = 10 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxUpdates can only be set on an Export Run Profile."));
    }

    [Test]
    public void Validate_MaxDeletesOnNonExportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.DeltaImport, MaxDeletes = 10 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDeletes can only be set on an Export Run Profile."));
    }

    [Test]
    public void Validate_NegativeMaxCreatesOnExportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.Export, MaxCreates = -1 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxCreates cannot be negative."));
    }

    [Test]
    public void Validate_NegativeMaxUpdatesOnExportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.Export, MaxUpdates = -1 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxUpdates cannot be negative."));
    }

    [Test]
    public void Validate_NegativeMaxDeletesOnExportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.Export, MaxDeletes = -1 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDeletes cannot be negative."));
    }

    [Test]
    public void Validate_FirstOffendingFieldReported_WhenMultipleAreInvalid()
    {
        // MaxCreates is checked first; a wrong-run-type Run Profile with several limits set should
        // name MaxCreates rather than MaxUpdates or MaxDeletes.
        var runProfile = new ConnectedSystemRunProfile
        {
            RunType = ConnectedSystemRunType.DeltaImport,
            MaxCreates = 5,
            MaxUpdates = 5,
            MaxDeletes = 5
        };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxCreates can only be set on an Export Run Profile."));
    }

    [Test]
    public void Validate_NullRunProfile_ThrowsArgumentNullException()
    {
        Assert.That(() => RunProfileSafeguardsValidator.Validate(null!), Throws.ArgumentNullException);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Run Profile Safeguards (#1618, Layer 2): MaxDetectedDeletions / MaxDetectedDeletionsPercent
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void Validate_DeletionDetectionLimitsSetOnFullImportRunProfile_ReturnsNull()
    {
        var runProfile = new ConnectedSystemRunProfile
        {
            RunType = ConnectedSystemRunType.FullImport,
            MaxDetectedDeletions = 500,
            MaxDetectedDeletionsPercent = 10
        };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile), Is.Null);
    }

    [Test]
    public void Validate_MaxDetectedDeletionsPercentZero_ReturnsNull()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport, MaxDetectedDeletionsPercent = 0 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile), Is.Null);
    }

    [Test]
    public void Validate_MaxDetectedDeletionsPercentOneHundred_ReturnsNull()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport, MaxDetectedDeletionsPercent = 100 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile), Is.Null);
    }

    [TestCase(ConnectedSystemRunType.Export)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public void Validate_MaxDetectedDeletionsOnNonFullImportRunProfile_NamesTheField(ConnectedSystemRunType runType)
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = runType, MaxDetectedDeletions = 500 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDetectedDeletions can only be set on a Full Import Run Profile."));
    }

    [Test]
    public void Validate_MaxDetectedDeletionsPercentOnNonFullImportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.Export, MaxDetectedDeletionsPercent = 10 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDetectedDeletionsPercent can only be set on a Full Import Run Profile."));
    }

    [Test]
    public void Validate_NegativeMaxDetectedDeletionsOnFullImportRunProfile_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport, MaxDetectedDeletions = -1 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDetectedDeletions cannot be negative."));
    }

    [Test]
    public void Validate_MaxDetectedDeletionsPercentBelowZero_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport, MaxDetectedDeletionsPercent = -1 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDetectedDeletionsPercent must be between 0 and 100."));
    }

    [Test]
    public void Validate_MaxDetectedDeletionsPercentAboveOneHundred_NamesTheField()
    {
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport, MaxDetectedDeletionsPercent = 101 };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxDetectedDeletionsPercent must be between 0 and 100."));
    }

    [Test]
    public void Validate_ExportLimitsCheckedBeforeDeletionDetectionLimits_ReportsExportFieldFirst()
    {
        // MaxCreates/MaxUpdates/MaxDeletes are validated ahead of the two deletion-detection fields, so
        // a Run Profile with both kinds set wrongly still reports the first offending export field.
        var runProfile = new ConnectedSystemRunProfile
        {
            RunType = ConnectedSystemRunType.DeltaImport,
            MaxCreates = 5,
            MaxDetectedDeletions = 5
        };

        Assert.That(RunProfileSafeguardsValidator.Validate(runProfile),
            Is.EqualTo("MaxCreates can only be set on an Export Run Profile."));
    }
}
