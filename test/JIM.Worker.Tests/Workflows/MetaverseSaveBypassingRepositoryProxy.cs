// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Wraps the workflow harness's <see cref="IRepository"/> so that <see cref="IMetaverseRepository.UpdateMetaverseObjectAsync"/>
/// completes without touching EF, and forwards everything else unchanged. The harness keeps synchronised Metaverse
/// Objects in the in-memory sync repository by reference, so a direct edit applied to the instance is already "saved"
/// there; EF's in-memory store has never held those objects and would refuse the update. Lets a workflow test drive
/// <c>MetaverseServer.UpdateMetaverseObjectAsync</c> (a direct edit, #1750 Phase 4) end to end against synchronised
/// objects. Built on <see cref="DispatchProxy"/> for the reason <see cref="UniqueValues.CountingSyncRepositoryProxy"/> gives.
/// </summary>
public class MetaverseSaveBypassingRepositoryProxy : DispatchProxy
{
    private IRepository _inner = null!;
    private IMetaverseRepository _metaverse = null!;

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> whose <see cref="IRepository.Metaverse"/> skips the single-object save.
    /// </summary>
    public static IRepository Create(IRepository inner)
    {
        var proxy = Create<IRepository, MetaverseSaveBypassingRepositoryProxy>();
        var bypassing = (MetaverseSaveBypassingRepositoryProxy)(object)proxy;
        bypassing._inner = inner;
        bypassing._metaverse = MetaverseProxy.Create(inner.Metaverse);
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        if (targetMethod.Name == $"get_{nameof(IRepository.Metaverse)}")
            return _metaverse;

        return targetMethod.Invoke(_inner, args);
    }

    /// <summary>
    /// The Metaverse repository half: <see cref="IMetaverseRepository.UpdateMetaverseObjectAsync"/> is a completed no-op.
    /// </summary>
    public class MetaverseProxy : DispatchProxy
    {
        private IMetaverseRepository _inner = null!;

        public static IMetaverseRepository Create(IMetaverseRepository inner)
        {
            var proxy = Create<IMetaverseRepository, MetaverseProxy>();
            ((MetaverseProxy)(object)proxy)._inner = inner;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("DispatchProxy invoked with no target method.");

            if (targetMethod.Name == nameof(IMetaverseRepository.UpdateMetaverseObjectAsync))
                return Task.CompletedTask;

            return targetMethod.Invoke(_inner, args);
        }
    }
}
