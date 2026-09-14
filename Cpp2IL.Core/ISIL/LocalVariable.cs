using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

public class LocalVariable(string name, Register register, TypeAnalysisContext? type = null) : IOperand
{
    public string Name = name;
    public Register Register = register;

    /// <summary>
    /// null if typeprop has not been done yet, or if the type could not be determined.
    /// </summary>
    public TypeAnalysisContext? Type = type;

    /// <summary>
    /// 来自原始形参的身份；由 ABI 入口绑定，独立于显示名称和压缩后的局部变量顺序。
    /// </summary>
    public ParameterAnalysisContext? SourceParameter { get; internal set; }

    public bool IsThis = false;
    public bool IsReturn = false;
    public bool IsMethodInfo = false;

    public override string ToString() => Type == null ? $"{Name} @ {Register}" : $"{Name} @ {Register} ({Type.FullName})";
}
