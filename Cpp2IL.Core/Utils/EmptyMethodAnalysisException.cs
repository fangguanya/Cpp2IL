using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

/// <summary>空分析不是合法空方法的原生证据，独立归类而不合成默认实现。</summary>
internal sealed class EmptyMethodAnalysisException(MethodAnalysisContext method)
    : DecompilerException($"方法分析为空：method={method.FullNameWithSignature}; " +
                          $"nativeAddress=0x{method.UnderlyingPointer:X}; rawBytes={method.RawBytes.Length}。" +
                          "未取得可生成实现的指令证据。")
{
}
