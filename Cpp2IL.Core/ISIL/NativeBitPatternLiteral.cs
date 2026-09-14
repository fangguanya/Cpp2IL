namespace Cpp2IL.Core.ISIL;

/// <summary>只读原生内存的完整位模式；不把低通道类型当作整个读取的类型。</summary>
public readonly record struct NativeBitPatternLiteral(ulong Low, ulong High, int WidthBits) : IOperand
{
    public override string ToString() => $"bits{WidthBits}(0x{High:X16}:0x{Low:X16})";
}
