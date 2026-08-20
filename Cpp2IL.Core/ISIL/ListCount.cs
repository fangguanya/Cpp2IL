using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// 表示具体 <c>List&lt;T&gt;</c> 实例的公开 <c>Count</c> 属性读取。
/// </summary>
public class ListCount(LocalVariable value, GenericInstanceTypeAnalysisContext listType) : IOperand
{
    public LocalVariable Value = value;

    /// <summary>
    /// 保留具体泛型实参，供托管 IL 生成器构造精确的 <c>List&lt;T&gt;.get_Count</c> 调用。
    /// </summary>
    public GenericInstanceTypeAnalysisContext ListType { get; } = listType;

    public override string ToString() => $"{Value.Name}.Count";
}
