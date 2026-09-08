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
    {
        var binary = appContext.Binary;
        if (managed)
        {
            var startOfNext = appContext.GetAddressOfNextFunctionStart(virtAddress);

            //We have to fall through to default behavior for the last method because we cannot accurately pinpoint its end
            if (startOfNext > 0)
            {
                var rawStartOfNextMethod = binary.MapVirtualAddressToRaw(startOfNext);

                var rawStart = binary.MapVirtualAddressToRaw(virtAddress);
                if (rawStartOfNextMethod < rawStart)
                    rawStartOfNextMethod = binary.RawLength;

                var bytes = binary.GetRawBinaryContent().Slice((int)rawStart, (int)(rawStartOfNextMethod - rawStart));

                return Disassemble(bytes, virtAddress);
            }
        }

        //Unmanaged function, look for first b
        var pos = (int)binary.MapVirtualAddressToRaw(virtAddress);
        var allBytes = binary.GetRawBinaryContent();
        var span = allBytes.Slice(pos, 4);
        List<Arm64Instruction> ret = [];

        while ((count == -1 || ret.Count < count) && !ret.Any(i => i.Mnemonic is Arm64Mnemonic.B || i.Mnemonic is Arm64Mnemonic.INVALID))
        {
            ret = Disassemble(span, virtAddress);

            //All arm64 instructions are 4 bytes
            span = allBytes.Slice(pos, span.Length + 4);
        }

        return ret;
    }

    private static List<Arm64Instruction> Disassemble(ReadOnlySpan<byte> bytes, ulong virtAddress)
    {
        try
        {
            return Disassembler.Disassemble(bytes, virtAddress, new Disassembler.Options(true, true, false)).ToList();
        }
        catch (Exception e)
        {
            throw new($"Failed to disassemble method body: {string.Join(", ", bytes.ToArray().Select(b => "0x" + b.ToString("X2")))}", e);
        }
    }

    public static List<Arm64Instruction> ToList(this Disassembler.SpanEnumerator enumerator)
    {
        var ret = new List<Arm64Instruction>();
        while (enumerator.MoveNext())
        {
            ret.Add(enumerator.Current);
        }

        return ret;
    }

    public static Arm64Instruction LastValid(this List<Arm64Instruction> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Mnemonic is not (Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED))
                return list[i];
        }

        return list[^1];
    }
}
