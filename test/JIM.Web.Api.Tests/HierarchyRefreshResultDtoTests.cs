// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Staging.DTOs;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers how a hierarchy refresh's moved Containers reach the REST API, and so PowerShell, which returns the
/// response as it is.
/// </summary>
[TestFixture]
public class HierarchyRefreshResultDtoTests
{
    [Test]
    public void FromModel_ContainerMovedToAnotherPartition_CarriesBothPartitions()
    {
        var model = new HierarchyRefreshResult
        {
            Success = true,
            MovedContainers =
            [
                new HierarchyMoveItem
                {
                    ExternalId = "OU=Corp,DC=globex,DC=local", Name = "Corp",
                    OldPartitionExternalId = "DC=acme,DC=local", NewPartitionExternalId = "DC=globex,DC=local"
                }
            ]
        };

        var moved = HierarchyRefreshResultDto.FromModel(model).MovedContainers.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(moved.OldPartitionExternalId, Is.EqualTo("DC=acme,DC=local"));
            Assert.That(moved.NewPartitionExternalId, Is.EqualTo("DC=globex,DC=local"));
        }
    }

    [Test]
    public void FromModel_ContainerMovedWithinItsPartition_CarriesNoPartition()
    {
        var model = new HierarchyRefreshResult
        {
            Success = true,
            MovedContainers =
            [
                new HierarchyMoveItem
                {
                    ExternalId = "OU=Sales,DC=acme,DC=local", Name = "Sales", OldParentExternalId = "OU=Corp,DC=acme,DC=local"
                }
            ]
        };

        var moved = HierarchyRefreshResultDto.FromModel(model).MovedContainers.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(moved.OldParentExternalId, Is.EqualTo("OU=Corp,DC=acme,DC=local"));
            Assert.That(moved.OldPartitionExternalId, Is.Null);
            Assert.That(moved.NewPartitionExternalId, Is.Null);
        }
    }
}
