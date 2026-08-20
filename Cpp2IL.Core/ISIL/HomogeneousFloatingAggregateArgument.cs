using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// 表示 AAPCS64 通过连续 V 寄存器传递的一个托管同质浮点聚合体实参。
/// 一个托管形参在 ISIL 中仍然只占一个操作数；各分量则保留为显式数据流源，
/// 供 SSA、死代码分析和 CIL 生成按元数据字段顺序精确重建值类型。
/// </summary>
public sealed class HomogeneousFloatingAggregateArgument(
    TypeAnalysisContext aggregateType,
    IEnumerable<IOperand> components) : IOperand
{
    public TypeAnalysisContext AggregateType { get; } = aggregateType;

    public List<IOperand> Components { get; } = components.ToList();

    public override string ToString()
        => $"HFA<{AggregateType.FullName}>({string.Join(", ", Components)})";
}
