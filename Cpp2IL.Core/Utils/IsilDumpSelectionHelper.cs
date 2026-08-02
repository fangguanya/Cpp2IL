using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core.Utils;

public static class IsilDumpSelectionHelper
{
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
