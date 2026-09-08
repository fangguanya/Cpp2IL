using System;
using System.Collections.Generic;
using System.IO;

namespace Cpp2IL.Core.Utils;

/// <summary>验证特性数据范围及终止哨兵，不把末项当作真实特性对象。</summary>
internal static class MetadataAttributeRangeHelper
{
    internal static int[] BuildLengths(IReadOnlyList<uint> starts, int sectionSize)
    {
        if (sectionSize < 0 || starts.Count == 0 || starts[starts.Count - 1] > sectionSize)
            throw new InvalidDataException("特性数据区段缺少有效终止哨兵。");
        if (starts[0] != 0)
            throw new InvalidDataException("特性数据区段开头存在未归属字节。");
        var lengths = new int[starts.Count - 1];
        for (var index = 0; index < lengths.Length; index++)
        {
            if (starts[index] > starts[index + 1] || starts[index + 1] > sectionSize)
                throw new InvalidDataException("特性数据偏移逆序或越界。");
            lengths[index] = checked((int)(starts[index + 1] - starts[index]));
        }
        return lengths;
    }
}
