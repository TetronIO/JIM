// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Wraps an <see cref="IRepository"/> so that one named <see cref="IMetaverseRepository"/> method throws instead of
/// running, and forwards everything else unchanged: a failure injected at an exact point in a write sequence, such as
/// between deleting a Metaverse Object and writing its deletion record. Built on <see cref="DispatchProxy"/> for the
/// reason <see cref="UniqueValues.CountingSyncRepositoryProxy"/> gives.
/// </summary>
public class ThrowingMetaverseRepositoryProxy : DispatchProxy
{
    private IRepository _inner = null!;
    private IMetaverseRepository _metaverse = null!;

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> whose <see cref="IRepository.Metaverse"/> throws
    /// <paramref name="failure"/> from <paramref name="methodName"/>.
    /// </summary>
    public static IRepository Create(IRepository inner, string methodName, Exception failure)
    {
        var proxy = Create<IRepository, ThrowingMetaverseRepositoryProxy>();
        var throwing = (ThrowingMetaverseRepositoryProxy)(object)proxy;
        throwing._inner = inner;
        throwing._metaverse = MetaverseProxy.Create(inner.Metaverse, methodName, failure);
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
    /// The Metaverse repository half: the named method throws, every other one is forwarded.
    /// </summary>
    public class MetaverseProxy : DispatchProxy
    {
        private IMetaverseRepository _inner = null!;
        private string _methodName = null!;
        private Exception _failure = null!;

        public static IMetaverseRepository Create(IMetaverseRepository inner, string methodName, Exception failure)
        {
            var proxy = Create<IMetaverseRepository, MetaverseProxy>();
            var throwing = (MetaverseProxy)(object)proxy;
            throwing._inner = inner;
            throwing._methodName = methodName;
            throwing._failure = failure;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("DispatchProxy invoked with no target method.");

            if (targetMethod.Name == _methodName)
                throw _failure;

            return targetMethod.Invoke(_inner, args);
        }
    }
}
