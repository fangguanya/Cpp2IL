using System;
using System.Collections.Generic;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 按应用上下文保存异常抛出辅助函数的解析结果，并保证递归解析与并发读取具有确定性。
/// </summary>
internal sealed class ThrowHelperNameCache
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, string?> _resolvedNames = new();
    private readonly HashSet<ulong> _addressesBeingResolved = new();

    /// <summary>
    /// 查询已经闭合的解析结果，不会触发新的解析。
    /// </summary>
    internal bool TryGetValue(ulong address, out string? resolvedName)
    {
        lock (_gate)
            return _resolvedNames.TryGetValue(address, out resolvedName);
    }

    /// <summary>
    /// 返回缓存值或在互斥区内执行一次解析。递归环返回空值，解析异常不会污染缓存。
    /// </summary>
    internal string? Resolve(ulong address, Func<string?> valueFactory)
    {
        if (valueFactory == null)
            throw new ArgumentNullException(nameof(valueFactory));

        lock (_gate)
        {
            if (_resolvedNames.TryGetValue(address, out var resolvedName))
                return resolvedName;

            if (!_addressesBeingResolved.Add(address))
                return null;

            try
            {
                resolvedName = valueFactory();
                _resolvedNames.Add(address, resolvedName);
                return resolvedName;
            }
            finally
            {
                _addressesBeingResolved.Remove(address);
            }
        }
    }

    /// <summary>
    /// 返回已闭合的缓存项数量，仅用于验证缓存语义。
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _resolvedNames.Count;
        }
    }
}
