using System;
using LibCpp2IL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

internal static class Arm64MethodBodyReader
{
    internal readonly record struct RawMethodRange(int Start, int Length);

    public static bool TryReadManagedMethodBody(ApplicationAnalysisContext appContext, ulong startVirtualAddress, out BinarySlice body)
    {
        var binary = appContext.Binary;
        var endVirtualAddress = appContext.GetAddressOfNextFunctionStart(startVirtualAddress);
        if (endVirtualAddress == 0)
        {
            body = BinarySlice.Empty;
            return false;
        }

        // 方法指针属于虚拟地址域；BinarySlice 接受文件偏移，必须通过二进制格式映射器转换。
        var rawStart = binary.MapVirtualAddressToRaw(startVirtualAddress);
        var rawEnd = binary.MapVirtualAddressToRaw(endVirtualAddress);
        var range = ResolveRawRange(startVirtualAddress, endVirtualAddress, rawStart, rawEnd, binary.RawLength);

        body = new BinarySlice(binary, range.Start, range.Length);
        return true;
    }

    internal static RawMethodRange ResolveRawRange(
        ulong startVirtualAddress,
        ulong endVirtualAddress,
        long rawStart,
        long rawEnd,
        long rawLength)
    {
        if ((startVirtualAddress & 3) != 0 || (endVirtualAddress & 3) != 0)
            throw new ArgumentException("ARM64 方法虚拟地址必须按 4 字节对齐。");

        if (endVirtualAddress <= startVirtualAddress)
            throw new ArgumentOutOfRangeException(nameof(endVirtualAddress), "ARM64 方法结束地址必须大于起始地址。");

        if (rawLength <= 0 || rawLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rawLength), "ARM64 二进制文件长度必须处于 BinarySlice 可表达范围内。");

        if (rawStart < 0 || rawStart >= rawLength)
            throw new ArgumentOutOfRangeException(nameof(rawStart), "ARM64 方法起始文件偏移超出二进制范围。");

        if (rawEnd <= rawStart || rawEnd > rawLength)
            throw new ArgumentOutOfRangeException(nameof(rawEnd), "ARM64 方法结束文件偏移未形成有效的文件内区间。");

        var length = rawEnd - rawStart;
        if ((rawStart & 3) != 0 || (length & 3) != 0)
            throw new ArgumentException("ARM64 方法文件区间必须按 4 字节指令宽度对齐。");

        return new RawMethodRange(checked((int)rawStart), checked((int)length));
    }
}
