using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core.Utils;

/// <summary>以原始程序集及 token 校验盘点结果；地址只用于共享组，不作为方法身份。</summary>
internal static class MethodIndexCompletenessHelper
{
    internal sealed record Identity(string Assembly, uint Token);
    internal sealed record Entry(Identity Identity, ulong Address);
    internal sealed record Summary(int ExpectedMethods, int IndexedMethods, int ZeroAddressMethods,
        int SharedAddressGroups, int SharedAddressMethods);

    internal static Summary Validate(IEnumerable<Identity> expected, IEnumerable<Entry> actual)
    {
        var remaining = new HashSet<Identity>();
        foreach (var identity in expected)
        {
            ValidateIdentity(identity);
            if (!remaining.Add(identity))
                throw new InvalidOperationException($"原始方法身份重复：{identity}");
        }

        var expectedCount = remaining.Count;
        var addresses = new Dictionary<ulong, int>();
        var indexed = 0;
        var zeroAddresses = 0;
        foreach (var entry in actual)
        {
            ValidateIdentity(entry.Identity);
            if (!remaining.Remove(entry.Identity))
                throw new InvalidOperationException($"索引出现重复或范围外方法：{entry.Identity}");
            indexed++;
            if (entry.Address == 0)
                zeroAddresses++;
            else
            {
                addresses.TryGetValue(entry.Address, out var count);
                addresses[entry.Address] = count + 1;
            }
        }

        if (remaining.Count != 0)
            throw new InvalidOperationException($"方法索引遗漏 {remaining.Count} 项原始身份。");

        var shared = addresses.Values.Where(count => count > 1).ToArray();
        return new Summary(expectedCount, indexed, zeroAddresses, shared.Length, shared.Sum());
    }

    private static void ValidateIdentity(Identity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Assembly) ||
            (identity.Token & 0xFF000000) != 0x06000000 || (identity.Token & 0xFFFFFF) == 0)
            throw new InvalidOperationException($"原始方法身份非法：{identity}");
    }
}
