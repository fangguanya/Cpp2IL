using System;

namespace Cpp2IL.Core.Utils;

/// <summary>
/// 集中执行浮点值与原始位模式之间的无损转换，保证所有目标框架使用相同语义。
/// </summary>
internal static class FloatingPointBitHelper
{
    public static float Int32BitsToSingle(int bits)
        => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    public static int SingleToInt32Bits(float value)
        => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
}
