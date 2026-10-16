// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Data.Repositories;

/// <summary>
/// Saved configuration test cases (#2055 Phase 4). Every read is untracked: a case is read beside a Synchronisation
/// Rule or Connected System the caller may hold tracked, and must never join that graph.
/// </summary>
public interface IConfigurationTestRepository
{
    Task<ConfigurationTestCase?> GetTestCaseAsync(int id);

    /// <summary>
    /// The cases with these ids, in id order; an id with no case is left out.
    /// </summary>
    Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesAsync(IReadOnlyCollection<int> ids);

    /// <summary>
    /// Every case, in id order.
    /// </summary>
    Task<IReadOnlyList<ConfigurationTestCase>> GetAllTestCasesAsync();

    /// <summary>
    /// The cases a Synchronisation Rule's configuration covers, in id order: its Scope, its Attribute Flows, and its
    /// own Object Matching Rules (Synchronisation Rule mode).
    /// </summary>
    Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForSyncRuleAsync(int syncRuleId);

    /// <summary>
    /// The cases covering Simple mode Object Matching Rules on a Connected System's Object Types, in id order: the
    /// cases its own configuration history records.
    /// </summary>
    Task<IReadOnlyList<ConfigurationTestCase>> GetSimpleModeMatchingTestCasesAsync(int connectedSystemId);

    /// <summary>
    /// Every case under a Connected System, in id order: those of each of its Synchronisation Rules and those of its
    /// Simple mode Object Matching Rules.
    /// </summary>
    Task<IReadOnlyList<ConfigurationTestCase>> GetTestCasesForConnectedSystemAsync(int connectedSystemId);

    Task CreateTestCaseAsync(ConfigurationTestCase testCase);

    Task UpdateTestCaseAsync(ConfigurationTestCase testCase);

    Task DeleteTestCaseAsync(int id);
}
