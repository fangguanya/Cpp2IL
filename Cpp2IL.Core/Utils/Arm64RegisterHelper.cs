using System;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Utils;

/// <summary>
/// 统一 ARM64 同一物理寄存器的不同宽度名称，并提供原始寄存器宽度。
/// </summary>
public static class Arm64RegisterHelper
{
    /// <summary>
    /// 判断在已确认的移动或存储源位置中是否为零寄存器。
    /// </summary>
    public static bool IsZeroRegister(Arm64Register register)
        => register is Arm64Register.W31 or Arm64Register.X31;

    /// <summary>
    /// 将 W0-W30 归一到 X0-X30，将标量浮点寄存器归一到对应 V 寄存器。
    /// </summary>
    public static string CanonicalName(Arm64Register register)
    {
        if (register is >= Arm64Register.W0 and <= Arm64Register.W30)
            return $"X{register - Arm64Register.W0}";

        if (register is >= Arm64Register.B0 and <= Arm64Register.B31)
            return $"V{register - Arm64Register.B0}";

        if (register is >= Arm64Register.H0 and <= Arm64Register.H31)
            return $"V{register - Arm64Register.H0}";

        if (register is >= Arm64Register.S0 and <= Arm64Register.S31)
            return $"V{register - Arm64Register.S0}";

        if (register is >= Arm64Register.D0 and <= Arm64Register.D31)
            return $"V{register - Arm64Register.D0}";

        return register.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// 返回寄存器在成对加载或存储中的元素字节数。
    /// </summary>
    public static int SizeBytes(Arm64Register register)
        => register switch
        {
            >= Arm64Register.B0 and <= Arm64Register.B31 => 1,
            >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
            >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
            >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
            >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
            >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
            >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(register), register, "未知 ARM64 寄存器宽度。"),
        };
}
