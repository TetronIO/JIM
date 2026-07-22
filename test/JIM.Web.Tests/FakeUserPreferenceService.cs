// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Web.Services;

namespace JIM.Web.Tests;

/// <summary>
/// In-memory <see cref="IUserPreferenceService"/> stand-in for bUnit component tests. Records the
/// causality preference writes so tests can assert persistence without JS interop.
/// </summary>
public sealed class FakeUserPreferenceService : IUserPreferenceService
{
    /// <summary>
    /// The causality view value returned by <see cref="GetCausalityViewAsync"/>.
    /// </summary>
    public string? StoredCausalityView { get; set; }

    /// <summary>
    /// The technical-names value returned by <see cref="GetCausalityTechNamesAsync"/>.
    /// </summary>
    public bool? StoredCausalityTechNames { get; set; }

    /// <summary>
    /// Every value passed to <see cref="SetCausalityViewAsync"/>, in call order.
    /// </summary>
    public List<string> CausalityViewWrites { get; } = [];

    /// <summary>
    /// Every value passed to <see cref="SetCausalityTechNamesAsync"/>, in call order.
    /// </summary>
    public List<bool> CausalityTechNamesWrites { get; } = [];

    public Task<int> GetRowsPerPageAsync() => Task.FromResult(10);

    public Task SetRowsPerPageAsync(int rowsPerPage) => Task.CompletedTask;

    public Task<bool> GetNavGroupExpandedAsync(string groupName) => Task.FromResult(false);

    public Task SetNavGroupExpandedAsync(string groupName, bool expanded) => Task.CompletedTask;

    public Task<bool?> GetDarkModeAsync() => Task.FromResult<bool?>(null);

    public Task SetDarkModeAsync(bool isDarkMode) => Task.CompletedTask;

    public Task<bool?> GetDrawerPinnedAsync() => Task.FromResult<bool?>(null);

    public Task SetDrawerPinnedAsync(bool isPinned) => Task.CompletedTask;

    public Task<string?> GetMvaViewModeAsync(string attributeName) => Task.FromResult<string?>(null);

    public Task SetMvaViewModeAsync(string attributeName, string viewMode) => Task.CompletedTask;

    public Task<string?> GetMvoDetailViewModeAsync() => Task.FromResult<string?>(null);

    public Task SetMvoDetailViewModeAsync(string viewMode) => Task.CompletedTask;

    public Task<bool?> GetTableDenseAsync() => Task.FromResult<bool?>(null);

    public Task SetTableDenseAsync(bool isDense) => Task.CompletedTask;

    public Task<bool?> GetCategoryExpandedAsync(int objectTypeId, string categoryName) => Task.FromResult<bool?>(null);

    public Task SetCategoryExpandedAsync(int objectTypeId, string categoryName, bool expanded) => Task.CompletedTask;

    public Task<string?> GetCausalityViewAsync() => Task.FromResult(StoredCausalityView);

    public Task SetCausalityViewAsync(string view)
    {
        CausalityViewWrites.Add(view);
        StoredCausalityView = view;
        return Task.CompletedTask;
    }

    public Task<bool?> GetCausalityTechNamesAsync() => Task.FromResult(StoredCausalityTechNames);

    public Task SetCausalityTechNamesAsync(bool enabled)
    {
        CausalityTechNamesWrites.Add(enabled);
        StoredCausalityTechNames = enabled;
        return Task.CompletedTask;
    }
}
