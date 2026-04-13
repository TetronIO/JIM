// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
namespace JIM.Data.Repositories;

public interface ISearchRepository
{
    public Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync();
    public Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri);
    public Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType);
    public Task SetEnabledAsync(int id, bool isEnabled);
}