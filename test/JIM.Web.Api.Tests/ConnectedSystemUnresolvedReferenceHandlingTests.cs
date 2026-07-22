// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the UnresolvedReferenceHandling Connected System property across the API DTO layer, mirroring the
/// MaxExportParallelism precedent in ConnectedSystemParallelExportTests. Controller-level PUT mapping coverage
/// (value set updates the entity; value omitted leaves it unchanged) lives in
/// SynchronisationControllerUpdateUnresolvedReferenceHandlingTests, since that behaviour is a conditional-mapping
/// line in the controller rather than something the DTOs alone can prove.
/// </summary>
[TestFixture]
public class ConnectedSystemUnresolvedReferenceHandlingTests
{
    #region ConnectedSystemDetailDto.FromEntity mapping tests

    [Test]
    public void FromEntity_UnresolvedReferenceHandlingDefault_MapsAsError()
    {
        // Arrange: a Connected System that has never had the property set explicitly.
        var entity = CreateConnectedSystemEntity();

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Error));
    }

    [Test]
    public void FromEntity_UnresolvedReferenceHandlingWarn_MapsCorrectly()
    {
        // Arrange
        var entity = CreateConnectedSystemEntity();
        entity.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn;

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Warn));
    }

    [Test]
    public void FromEntity_UnresolvedReferenceHandlingIgnore_MapsCorrectly()
    {
        // Arrange
        var entity = CreateConnectedSystemEntity();
        entity.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore;

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Ignore));
    }

    #endregion

    #region ConnectedSystem UnresolvedReferenceHandling property tests

    [Test]
    public void ConnectedSystem_UnresolvedReferenceHandling_DefaultsToError()
    {
        var cs = new ConnectedSystem { Name = "Test" };
        Assert.That(cs.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Error));
    }

    #endregion

    #region UpdateConnectedSystemRequest tests

    [Test]
    public void UpdateConnectedSystemRequest_UnresolvedReferenceHandling_AcceptsValidValue()
    {
        var request = new UpdateConnectedSystemRequest
        {
            UnresolvedReferenceHandling = UnresolvedReferenceHandling.Warn
        };

        Assert.That(request.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Warn));
    }

    [Test]
    public void UpdateConnectedSystemRequest_UnresolvedReferenceHandlingNull_IsValid()
    {
        var request = new UpdateConnectedSystemRequest();

        Assert.That(request.UnresolvedReferenceHandling, Is.Null);
    }

    [Test]
    public void UpdateConnectedSystemRequest_UnresolvedReferenceHandlingUndefinedValue_FailsValidation()
    {
        // The API's JSON converter accepts integer enum values, so a client can send a number outside the defined
        // enum range (e.g. {"unresolvedReferenceHandling": 99}). DataAnnotations validation must reject it so the
        // controller returns 400 rather than persisting an undefined value.
        var request = new UpdateConnectedSystemRequest
        {
            UnresolvedReferenceHandling = (UnresolvedReferenceHandling)99
        };

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.That(isValid, Is.False, "Expected validation to fail for an undefined enum value.");
        Assert.That(results.Any(r => r.MemberNames.Contains(nameof(UpdateConnectedSystemRequest.UnresolvedReferenceHandling))),
            "Expected the validation failure to be attributed to UnresolvedReferenceHandling.");
    }

    [Test]
    public void UpdateConnectedSystemRequest_UnresolvedReferenceHandlingDefinedValue_PassesValidation()
    {
        var request = new UpdateConnectedSystemRequest
        {
            UnresolvedReferenceHandling = UnresolvedReferenceHandling.Ignore
        };

        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.That(isValid, Is.True, "Expected validation to pass for a defined enum value.");
    }

    #endregion

    #region Helper methods

    private static ConnectedSystem CreateConnectedSystemEntity()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Description = "Test Description",
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1,
                Name = "Test Connector"
            },
            ObjectTypes = new List<ConnectedSystemObjectType>(),
            Objects = new List<ConnectedSystemObject>(),
            PendingExports = new List<PendingExport>(),
            SettingValues = new List<ConnectedSystemSettingValue>()
        };
    }

    #endregion
}
