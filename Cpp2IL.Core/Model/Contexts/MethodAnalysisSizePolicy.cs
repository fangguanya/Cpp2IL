using System;

namespace Cpp2IL.Core.Model.Contexts;

public static class MethodAnalysisSizePolicy
{
    public const int Unlimited = -1;
    public const int DefaultMaximumBytes = 64 * 1024;

    public static int ValidateMaximumBytes(int maximumBytes)
    {
        if (maximumBytes == Unlimited || maximumBytes > 0)
            return maximumBytes;

        throw new ArgumentOutOfRangeException(nameof(maximumBytes), "方法分析上限必须为正整数，或使用 -1 表示不限制。");
    }

    public static bool ExceedsMaximum(int methodBodySizeBytes, int maximumBytes)
    {
        if (methodBodySizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(methodBodySizeBytes), "方法体大小不得为负数。");

        maximumBytes = ValidateMaximumBytes(maximumBytes);
        return maximumBytes != Unlimited && methodBodySizeBytes > maximumBytes;
    }
}
