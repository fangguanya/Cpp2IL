using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Disarm;
using LibCpp2IL;

namespace Cpp2IL.Core.Utils;

public static class NewArm64Utils
{
    /// <summary>
    /// 从指定虚拟地址精确解码固定数量的 ARM64 指令。运行时辅助函数常包含条件分支，
    /// 不能使用“遇到首个 B 即停止”的非托管函数扫描，否则会截断失败分支与尾调用证据。
    /// </summary>
    public static List<Arm64Instruction> GetArm64InstructionsAtVirtualAddress(
        Il2CppBinary binary,
        ulong virtAddress,
        int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "ARM64 指令数量必须大于零。");

        var rawStart = checked((int)binary.MapVirtualAddressToRaw(virtAddress));
        var allBytes = binary.GetRawBinaryContent();
        var byteCount = checked(count * 4);
        if (rawStart < 0 || rawStart > allBytes.Length - byteCount)
            throw new ArgumentOutOfRangeException(nameof(virtAddress), virtAddress, "ARM64 固定窗口超出二进制边界。");

        return Disassemble(allBytes.Slice(rawStart, byteCount), virtAddress);
    }

    public static List<Arm64Instruction> GetArm64MethodBodyAtVirtualAddress(ApplicationAnalysisContext appContext, ulong virtAddress, bool managed = true, int count = -1)
