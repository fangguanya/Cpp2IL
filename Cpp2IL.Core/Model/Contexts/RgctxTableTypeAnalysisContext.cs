using System.Collections.Generic;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Model.Contexts;

/// <summary>
/// Synthetic type for a value holding <c>Il2CppClass::rgctx_data</c>
/// </summary>
public class RgctxTableTypeAnalysisContext(TypeAnalysisContext ownerType, AssemblyAnalysisContext referencedFrom)
    : ReferencedTypeAnalysisContext(referencedFrom)
{
    /// <summary>The (usually inflated) type whose runtime generic context this is.</summary>
    public TypeAnalysisContext OwnerType { get; } = ownerType;

    // see RgctxResolver.GetOrResolveEntry, resolved slots must stay reference-stable across fixpoint passes
    internal readonly Dictionary<int, TypeAnalysisContext?> ResolvedEntries = [];

    public override Il2CppTypeEnum Type => Il2CppTypeEnum.IL2CPP_TYPE_I;

    public override string DefaultName => $"Il2CppRgctx<{OwnerType.FullName}>";

    public override string DefaultNamespace => "";

    public override bool IsValueType => false;
}

/// <summary>
/// 方法自身隐藏MethodInfo所携带的rgctx_data表。该表按方法token索引，
/// 与Il2CppClass上的类型RGCTXData表不是同一数据源。
/// </summary>
public sealed class MethodRgctxTableTypeAnalysisContext(
    MethodAnalysisContext ownerMethod,
    AssemblyAnalysisContext referencedFrom)
    : ReferencedTypeAnalysisContext(referencedFrom)
{
    public MethodAnalysisContext OwnerMethod { get; } = ownerMethod;

    public override Il2CppTypeEnum Type => Il2CppTypeEnum.IL2CPP_TYPE_I;

    public override string DefaultName => $"Il2CppMethodRgctx<{OwnerMethod.FullName}>";

    public override string DefaultNamespace => "";

    public override bool IsValueType => false;
}
