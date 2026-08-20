namespace Cpp2IL.Core;

/// <summary>
/// IL2CPP 运行时默认类型表中可由固定指针槽确认的值类型项。这里只登记已经由同一
/// 二进制中“强类型 System.Int32 值 + Object::Box 类句柄”交叉证明的 int32_class；
/// 未登记偏移继续保持未知，禁止按数值宽度猜测枚举、无符号整数或浮点类型。
/// </summary>
public static class Il2CppDefaultsUsefulOffsets
{
    private const int Int32ClassPointerIndex = 9;

    public static string? GetBoxedSystemTypeName(long byteOffset, int pointerSizeBytes)
    {
        if (pointerSizeBytes is not (4 or 8))
            return null;

        return byteOffset == Int32ClassPointerIndex * pointerSizeBytes
            ? "System.Int32"
            : null;
    }
}
