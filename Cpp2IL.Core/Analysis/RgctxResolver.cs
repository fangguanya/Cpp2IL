using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Follows the runtime generic context chain and maps to actual values.
/// </summary>
public static class RgctxResolver
{
    public static bool Run(MethodAnalysisContext method)
    {
        var is32Bit = method.AppContext.Binary.is32Bit;
        var klassOffset = is32Bit ? 0x10 : 0x20;
        var methodRgctxOffset = is32Bit ? 0x1C : 0x38;
        var rgctxOffset = is32Bit ? 0x60 : 0xC0;
        var pointerSize = is32Bit ? 4 : 8;
        var definitions = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination)
                continue;

            if (instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable source } memory)
                continue;

            // 泛型方法会先把隐藏 MethodInfo 保存到非易失寄存器，再在慢初始化分支后重载
            // rgctx。保存寄存器可能已被宽化为原生整数，必须沿唯一 SSA Move 定义取回
            // 运行时元数据身份，不能依赖当前局部的宽类型。
            var sourceType = ResolveForwardedRuntimeMetadataType(source, definitions) ?? source.Type;
            if (sourceType != null && !DescribesSameThing(source.Type, sourceType))
            {
                source.Type = sourceType;
                changed = true;
            }

            var resolved = sourceType switch
            {
                // MethodInfo::klass - the instance the method belongs to, which is what carries the RGCTX
                RuntimeMethodInfoAnalysisContext info when memory.Addend == klassOffset && info.RepresentedMethod.DeclaringType is { } declaring
                    => new RuntimeClassTypeAnalysisContext(declaring, declaring.DeclaringAssembly),

                RuntimeMethodInfoAnalysisContext info when memory.Addend == methodRgctxOffset
                    => new MethodRgctxTableTypeAnalysisContext(info.RepresentedMethod, info.CustomAttributeAssembly),

                RuntimeClassTypeAnalysisContext { RepresentedType: var owner } when memory.Addend == rgctxOffset
                    => new RgctxTableTypeAnalysisContext(owner, owner.DeclaringAssembly),

                RgctxTableTypeAnalysisContext { OwnerType: var instance } when memory.Addend % pointerSize == 0
                    => ResolveEntry(instance, (int)(memory.Addend / pointerSize)),

                MethodRgctxTableTypeAnalysisContext { OwnerMethod: var ownerMethod }
                    when memory.Addend % pointerSize == 0
                    => ResolveMethodEntry(ownerMethod, (int)(memory.Addend / pointerSize)),

                _ => null,
            };

            // Wrappers are not unique objects, so compare what they contain, not references
            // TODO maybe fix this? It's allocation spam if nothing else. Just make a canonical RuntimeClassTypeAnalysisContext/RgctxTableTypeAnalysisContext
            if (resolved == null || DescribesSameThing(destination.Type, resolved))
                continue;

            destination.Type = resolved;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 沿唯一的局部到局部 SSA Move 链恢复运行时元数据载体。普通整数、对象以及存在
    /// 多定义的局部均不晋级，避免把地址算术或控制流合并误认作 MethodInfo/RGCTXData。
    /// </summary>
    internal static TypeAnalysisContext? ResolveForwardedRuntimeMetadataType(
        LocalVariable source,
        ILookup<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        var current = source;
        while (visited.Add(current))
        {
            if (current.Type is RuntimeMethodInfoAnalysisContext
                or RuntimeClassTypeAnalysisContext
                or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext)
                return current.Type;

            if (definitions[current].Take(2).ToArray() is not [
                    { OpCode: OpCode.Move, Operands: [LocalVariable _, LocalVariable previous] }
                ])
                return null;

            current = previous;
        }

        return null;
    }

    internal static bool DescribesSameThing(TypeAnalysisContext? existing, TypeAnalysisContext candidate) =>
        (existing, candidate) switch
        {
            // 泛型膨胀会按需创建新的上下文实例；引用不同不表示类型不同。若这里只做
            // ReferenceEquals，同一个RGCTXData槽会在每轮被等价包装器覆盖并虚报变化。
            (RuntimeClassTypeAnalysisContext a, RuntimeClassTypeAnalysisContext b) =>
                GenericCallRebinder.TypesEquivalent(a.RepresentedType, b.RepresentedType),
            (RgctxTableTypeAnalysisContext a, RgctxTableTypeAnalysisContext b) =>
                GenericCallRebinder.TypesEquivalent(a.OwnerType, b.OwnerType),
            (MethodRgctxTableTypeAnalysisContext a, MethodRgctxTableTypeAnalysisContext b) =>
                ReferenceEquals(BaseMethod(a.OwnerMethod), BaseMethod(b.OwnerMethod)),
            _ => GenericCallRebinder.TypesEquivalent(existing, candidate),
        };

    private static MethodAnalysisContext BaseMethod(MethodAnalysisContext method)
        => method is ConcreteGenericMethodAnalysisContext concrete
            ? concrete.BaseMethodContext
            : method;

    private static TypeAnalysisContext? ResolveMethodEntry(MethodAnalysisContext method, int index)
    {
        var definition = method.Definition ?? BaseMethod(method).Definition;
        if (definition == null)
            return null;

        var entries = definition.RgctXs;
        if (index < 0 || index >= entries.Length)
            return null;

        var entry = entries[index];
        var typeArguments = method is ConcreteGenericMethodAnalysisContext concrete
            ? concrete.TypeGenericParameters
            : method.DeclaringType is GenericInstanceTypeAnalysisContext instance
                ? instance.GenericArguments
                : method.DeclaringType?.GenericParameters ?? [];
        var methodArguments = method is ConcreteGenericMethodAnalysisContext
        {
            MethodGenericParameters.Count: > 0
        } concreteMethod
            ? concreteMethod.MethodGenericParameters
            : method.GenericParameters;
        return ResolveEntryValue(method.AppContext, entry, typeArguments, methodArguments);
    }

    private static TypeAnalysisContext? ResolveEntryValue(
        ApplicationAnalysisContext app,
        Il2CppRGCTXDefinition entry,
        IReadOnlyList<TypeAnalysisContext> typeArguments,
        IReadOnlyList<TypeAnalysisContext> methodArguments)
    {
        if (entry.type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD)
        {
            var represented = app.ResolveContextForMethod(
                new Cpp2IlMethodRef(entry.MethodSpec));
            return represented == null
                ? null
                : new RuntimeMethodInfoAnalysisContext(
                    represented,
                    represented.DeclaringType!.DeclaringAssembly);
        }

        if (entry.type is not (Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS
            or Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE))
            return null;

        var entryType = app.ResolveIl2CppType(entry.Type);
        var inflated = GenericInstantiation.Instantiate(
            entryType,
            typeArguments,
            methodArguments);
        return new RuntimeClassTypeAnalysisContext(inflated, inflated.DeclaringAssembly);
    }

    internal static TypeAnalysisContext? ResolveEntry(TypeAnalysisContext instance, int index)
    {
        var definition = (instance as GenericInstanceTypeAnalysisContext)?.GenericType ?? instance;

        if (definition.Definition is not { } typeDefinition)
            return null;

        var entries = typeDefinition.RgctXs;

        if (index < 0 || index >= entries.Length)
            return null;

        var entry = entries[index];

        if (entry.type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD)
        {
            IReadOnlyList<TypeAnalysisContext> typeArguments = instance is GenericInstanceTypeAnalysisContext generic
                ? generic.GenericArguments
                : instance.GenericParameters.Cast<TypeAnalysisContext>().ToArray();
            return ResolveEntryValue(instance.AppContext, entry, typeArguments, []);
        }

        if (entry.type is not (Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS or Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE))
            return null;

        var entryType = instance.AppContext.ResolveIl2CppType(entry.Type);

        if (Inflate(entryType, instance) is not { } inflated)
            return null;

        return new RuntimeClassTypeAnalysisContext(inflated, inflated.DeclaringAssembly);
    }

    private static TypeAnalysisContext? Inflate(TypeAnalysisContext? type, TypeAnalysisContext instance)
    {
        if (instance is not GenericInstanceTypeAnalysisContext { GenericArguments: var arguments })
            return type;

        switch (type)
        {
            case GenericParameterTypeAnalysisContext { Index: var i }:
                return i >= 0 && i < arguments.Count ? arguments[i] : null;

            case GenericInstanceTypeAnalysisContext nested:
                // Self-reference inflates to the instance we already have.
                return nested.GenericArguments.All(a => a is GenericParameterTypeAnalysisContext)
                    ? instance
                    : null;

            default:
                return type;
        }
    }
}
