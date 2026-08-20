using System.Collections.Generic;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// 表示由只读原生重定位表完整证明的托管字符串选择。
/// 索引超出表长时返回原生分支已经证明的默认字符串。
/// </summary>
public sealed class MetadataStringTableLookup(
    IOperand index,
    IReadOnlyList<StringLiteral> values,
    StringLiteral defaultValue) : IOperand
{
    public IOperand Index { get; } = index;

    public IReadOnlyList<StringLiteral> Values { get; } = values;

    public StringLiteral DefaultValue { get; } = defaultValue;

    public override string ToString()
        => $"metadata_string_table({Index}, count={Values.Count})";
}
