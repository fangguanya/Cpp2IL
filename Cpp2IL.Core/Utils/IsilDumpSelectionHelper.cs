using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core.Utils;

public static class IsilDumpSelectionHelper
{
    /// <summary>
    /// 从精确根节点递归扩展全部后代，并按候选集合的原始顺序返回唯一闭包。
    /// </summary>
    public static IReadOnlyList<T> ExpandDescendantClosure<T>(
        IEnumerable<T> candidates,
        IEnumerable<T> selectedRoots,
        Func<T, IEnumerable<T>> directChildrenSelector)
    {
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        if (selectedRoots == null)
            throw new ArgumentNullException(nameof(selectedRoots));
        if (directChildrenSelector == null)
            throw new ArgumentNullException(nameof(directChildrenSelector));

        var candidateList = candidates.ToList();
        var candidateSet = new HashSet<T>(candidateList);
        var rootList = selectedRoots.ToList();
        if (rootList.Any(root => !candidateSet.Contains(root)))
            throw new ArgumentException("所选根节点必须属于候选集合。", nameof(selectedRoots));

        var closure = new HashSet<T>();
        var pending = new Stack<T>(rootList);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!closure.Add(current))
                continue;

            var children = directChildrenSelector(current)
                           ?? throw new InvalidOperationException("子节点选择器返回了空集合引用。");
            foreach (var child in children)
            {
                if (!candidateSet.Contains(child))
                    throw new InvalidOperationException("子节点选择器返回了候选集合之外的节点。");
                if (!closure.Contains(child))
                    pending.Push(child);
            }
        }

        return candidateList.Where(closure.Contains).ToList();
    }

    public static IReadOnlyList<string> NormalizeFilters(IEnumerable<string>? filters, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("筛选标签不得为空。", nameof(label));
        if (filters == null)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawFilter in filters)
        {
            if (string.IsNullOrWhiteSpace(rawFilter))
                throw new ArgumentException($"{label} filter contains an empty value.", nameof(filters));

            var filter = rawFilter.Trim();
            if (!seen.Add(filter))
                throw new ArgumentException($"{label} filter contains duplicate value '{filter}'.", nameof(filters));
            result.Add(filter);
        }

        return result;
    }

    public static IReadOnlyList<T> SelectExact<T>(
        IEnumerable<T> candidates,
        IEnumerable<string>? filters,
        Func<T, string> identitySelector,
        string label)
    {
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        if (identitySelector == null)
            throw new ArgumentNullException(nameof(identitySelector));
        var candidateList = candidates.ToList();
        var normalizedFilters = NormalizeFilters(filters, label);
        if (normalizedFilters.Count == 0)
            return candidateList;

        var candidatesByIdentity = candidateList
            .GroupBy(identitySelector, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var filter in normalizedFilters)
        {
            if (!candidatesByIdentity.TryGetValue(filter, out var matches))
                throw new InvalidOperationException($"{label} filter '{filter}' did not match any candidate.");
            if (matches.Count != 1)
                throw new InvalidOperationException($"{label} filter '{filter}' matched {matches.Count} candidates.");
        }

        var accepted = new HashSet<string>(normalizedFilters, StringComparer.Ordinal);
        return candidateList.Where(candidate => accepted.Contains(identitySelector(candidate))).ToList();
    }
}
