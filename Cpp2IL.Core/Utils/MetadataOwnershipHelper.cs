using System;
using System.Collections.Generic;

namespace Cpp2IL.Core.Utils;

/// <summary>验证原始表由所属声明范围精确分割；每条记录只登记一次归属。</summary>
internal static class MetadataOwnershipHelper
{
    internal sealed record Range(int Owner, int Start, int Count);

    internal static int[] BuildOwners(int tableCount, IEnumerable<Range> ranges)
    {
        if (tableCount < 0)
            throw new InvalidOperationException("原始表长度为负数。");
        var owners = new int[tableCount];
        for (var index = 0; index < owners.Length; index++)
            owners[index] = -1;
        var declaredOwners = new HashSet<int>();
        var assigned = 0;
        foreach (var range in ranges)
        {
            if (range.Owner < 0 || !declaredOwners.Add(range.Owner) || range.Count < 0 ||
                range.Start < -1 || (range.Start == -1 && range.Count != 0) ||
                (long)range.Start + range.Count > tableCount)
                throw new InvalidOperationException($"原始表归属范围非法：{range}");
            for (var offset = 0; offset < range.Count; offset++)
            {
                var index = range.Start + offset;
                if (owners[index] != -1)
                    throw new InvalidOperationException($"原始表索引 {index} 有重叠归属。");
                owners[index] = range.Owner;
                assigned++;
            }
        }
        if (assigned != tableCount)
            throw new InvalidOperationException($"原始表有 {tableCount - assigned} 条记录缺少归属。");
        return owners;
    }
}
