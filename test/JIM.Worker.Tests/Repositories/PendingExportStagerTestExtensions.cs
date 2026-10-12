// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Arranges a Pending Export staged by the given runs, oldest first, the only way the model lets them be recorded.
/// </summary>
internal static class PendingExportStagerTestExtensions
{
    public static PendingExport StagedBy(this PendingExport export, params Guid[] stagers)
    {
        foreach (var stager in stagers)
            export.RecordStager(stager);
        return export;
    }
}
