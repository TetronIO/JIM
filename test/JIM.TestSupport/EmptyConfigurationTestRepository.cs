// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Logic;

namespace JIM.TestSupport;

/// <summary>
/// A configuration test case repository holding no cases (#2055 Phase 4), for a fixture that mocks IRepository. A
/// save of a Synchronisation Rule, an Attribute Flow or an Object Matching Rule reads the cases covering it for the
/// save gate, and change capture reads them into its snapshot; a bare mock would hand both null.
/// </summary>
public class EmptyConfigurationTestRepository : IConfigurationTestRepository
{
    public Task<ConfigurationTestCase?> GetTestCaseAsync(int id) => Task.FromResult<ConfigurationTestCase?>(null);

    public Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesAsync(IReadOnlyCollection<int> ids) => None();

    public Task<IReadOnlyList<ConfigurationTestCase>> GetAllTestCasesAsync() => None();

    public Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForSyncRuleAsync(int syncRuleId) => None();

    public Task<IReadOnlyList<ConfigurationTestCase>> GetSimpleModeMatchingTestCasesAsync(int connectedSystemId) => None();

    public Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForConnectedSystemAsync(int connectedSystemId) => None();

    public Task CreateTestCaseAsync(ConfigurationTestCase testCase) => throw new NotSupportedException("This repository holds no test cases.");

    public Task UpdateTestCaseAsync(ConfigurationTestCase testCase) => throw new NotSupportedException("This repository holds no test cases.");

    public Task DeleteTestCaseAsync(int id) => throw new NotSupportedException("This repository holds no test cases.");

    private static Task<IReadOnlyList<ConfigurationTestCase>> None() => Task.FromResult<IReadOnlyList<ConfigurationTestCase>>([]);
}
