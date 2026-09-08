namespace Cpp2IL.Core.Utils;

/// <summary>未解决语义只形成恢复证据，不生成可伪装为成功的方法体。</summary>
internal sealed class UnresolvedCilSemanticException(string method, string operation, string evidence)
    : DecompilerException($"CIL 语义未解决：method={method}; operation={operation}; evidence={evidence}")
{
}
