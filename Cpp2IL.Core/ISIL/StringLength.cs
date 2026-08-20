namespace Cpp2IL.Core.ISIL;

/// <summary>
/// 表示托管字符串的公开 <c>Length</c> 属性读取。
/// </summary>
public class StringLength(LocalVariable value) : IOperand
{
    public LocalVariable Value = value;

    public override string ToString() => $"{Value.Name}.Length";
}
