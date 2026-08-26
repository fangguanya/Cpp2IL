namespace Cpp2IL.Core.ISIL;

/// <summary>
/// 已由二进制只读段证明的 UInt16 常量表索引读取。
/// </summary>
public readonly record struct ReadOnlyUInt16TableLookup(string Values, IOperand Index) : IOperand
{
    public override string ToString() => $"readonly-u16-table[{Index}]";
}
