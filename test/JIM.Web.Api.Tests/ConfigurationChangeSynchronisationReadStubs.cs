// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Data.Repositories;
using Moq;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Stubs the repository reads behind what a Synchronisation Rule, an Activity or a Metaverse Object Type says about changes
/// awaiting synchronisation and Metaverse Objects awaiting export review (#2615). A mocked repository returns null for an
/// unstubbed method that returns a list or a dictionary, and the servers behind the endpoints read these on every
/// response, so a fixture that exercises such an endpoint without caring about them stubs them as empty here.
/// </summary>
internal static class ConfigurationChangeSynchronisationReadStubs
{
    public static Mock<IConnectedSystemRepository> StubConfigurationChangeSynchronisationReads(this Mock<IConnectedSystemRepository> mock)
    {
        mock.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>())).ReturnsAsync([]);
        mock.Setup(r => r.GetConfigurationChangeSynchronisationSummariesAwaitingAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync([]);
        mock.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync([]);
        return mock;
    }

    public static Mock<IMetaverseRepository> StubExportReviewReads(this Mock<IMetaverseRepository> mock)
    {
        mock.Setup(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync([]);
        return mock;
    }
}
