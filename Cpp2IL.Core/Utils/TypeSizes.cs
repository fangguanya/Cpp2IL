using System;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

public static class TypeSizes
{
    // Unboxed size of a value type, so the metadata's boxed size - the two pointer fields in the header. 0 if we
    // don't know (no definition, e.g. an open generic).
    public static long UnboxedSize(TypeAnalysisContext type, int pointerSize)
    {
        var definition = type.Definition
                         ?? (type as GenericInstanceTypeAnalysisContext)?.GenericType.Definition;
        var boxed = definition?.RawSizes?.instance_size ?? 0;
        return UnboxedSizeFromBoxedSize(boxed, pointerSize);
    }

    internal static long UnboxedSizeFromBoxedSize(long boxedSize, int pointerSize)
    {
        if (pointerSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointerSize), pointerSize, "指针尺寸必须为正数。");

        var header = checked(2L * pointerSize);
        return boxedSize > header ? boxedSize - header : 0;
    }
}
