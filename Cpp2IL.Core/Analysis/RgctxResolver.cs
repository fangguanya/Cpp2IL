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

    /// <summary>
    /// 证明调用隐藏参数来自 MethodInfo::rgctx_data 表项。
    /// 只接受两个连续的唯一无索引内存读取：先取固定 rgctx_data 字段，再按指针
    /// 对齐读取表项。普通 MethodInfo 参数、绝对元数据槽和歧义定义均不参与延迟绑定；
    /// 表项的最终 METHOD 身份仍由后续 RgctxResolver 与同一开放方法定义共同验收。
    /// </summary>
    internal static bool IsMethodRgctxEntryCarrier(
        MethodAnalysisContext containingMethod,
        IOperand operand)
    {
        if (operand is not LocalVariable carrier)
            return false;

        var pointerSize = containingMethod.AppContext.Binary.is32Bit ? 4 : 8;
        var methodRgctxOffset = containingMethod.AppContext.Binary.is32Bit ? 0x1C : 0x38;
        var definitions = containingMethod.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);
        if (definitions[carrier].Take(2).ToArray() is not
            [
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable _,
                        MemoryOperand
                        {
                            Index: null,
                            Scale: 0,
                            Base: LocalVariable tableCarrier
                        } entryMemory
                    ]
                }
            ]
            || entryMemory.Addend < 0
            || entryMemory.Addend % pointerSize != 0)
            return false;

        var accepted = ResolveMethodRgctxRoot(
            tableCarrier,
            definitions,
            methodRgctxOffset,
            []) != null;
        return accepted;
    }

    /// <summary>
    /// 沿唯一 Move/Phi 证明每条入边均读取同一个 MethodInfo 的 rgctx_data 字段。
    /// 初始化慢路径会把调用前后两次字段读取合并成 Phi，因此不能只接受直接读取。
    /// </summary>
    private static LocalVariable? ResolveMethodRgctxRoot(
        LocalVariable carrier,
        ILookup<LocalVariable, Instruction> definitions,
        long methodRgctxOffset,
        HashSet<LocalVariable> visited)
    {
        if (!visited.Add(carrier)
            || definitions[carrier].Take(2).ToArray() is not [{ } definition])
            return null;

        if (definition is
            {
                OpCode: OpCode.Move,
                Operands:
                [
                    LocalVariable _,
                    MemoryOperand
                    {
                        Index: null,
                        Scale: 0,
                        Base: LocalVariable methodInfoCarrier
                    } memory
                ]
            }
            && memory.Addend == methodRgctxOffset)
            return ResolveUniqueMoveRoot(methodInfoCarrier, definitions);

        if (definition is
            {
                OpCode: OpCode.Move,
                Operands: [LocalVariable _, LocalVariable previous]
            })
            return ResolveMethodRgctxRoot(previous, definitions, methodRgctxOffset, [.. visited]);

        if (definition.OpCode != OpCode.Phi
            || definition.Sources.OfType<LocalVariable>().ToArray() is not { Length: > 0 } sources
            || sources.Length != definition.Sources.Count)
            return null;

        LocalVariable? commonRoot = null;
        foreach (var source in sources)
        {
            var root = ResolveMethodRgctxRoot(source, definitions, methodRgctxOffset, [.. visited]);
            if (root == null || commonRoot != null && !ReferenceEquals(commonRoot, root))
                return null;
            commonRoot ??= root;
        }
        return commonRoot;
    }

    private static LocalVariable? ResolveUniqueMoveRoot(
        LocalVariable carrier,
        ILookup<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        var current = carrier;
        while (visited.Add(current))
        {
            var candidates = definitions[current].Take(2).ToArray();
            if (candidates.Length == 0)
                return current;
            if (candidates is not
                [
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable _, LocalVariable previous]
                    }
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
                MethodsEquivalent(a.OwnerMethod, b.OwnerMethod),
            (RuntimeMethodInfoAnalysisContext a, RuntimeMethodInfoAnalysisContext b) =>
                MethodsEquivalent(a.RepresentedMethod, b.RepresentedMethod),
            _ => GenericCallRebinder.TypesEquivalent(existing, candidate),
        };

    private static bool MethodsEquivalent(MethodAnalysisContext left, MethodAnalysisContext right)
    {
        if (!ReferenceEquals(BaseMethod(left), BaseMethod(right)))
            return false;

        var leftTypeArguments = left is ConcreteGenericMethodAnalysisContext leftConcrete
            ? leftConcrete.TypeGenericParameters
            : left.DeclaringType?.GenericParameters ?? [];
        var rightTypeArguments = right is ConcreteGenericMethodAnalysisContext rightConcrete
            ? rightConcrete.TypeGenericParameters
            : right.DeclaringType?.GenericParameters ?? [];
        var leftMethodArguments = left is ConcreteGenericMethodAnalysisContext
            {
                MethodGenericParameters.Count: > 0
            } leftConcreteMethod
                ? leftConcreteMethod.MethodGenericParameters
                : left.GenericParameters;
        var rightMethodArguments = right is ConcreteGenericMethodAnalysisContext
            {
                MethodGenericParameters.Count: > 0
            } rightConcreteMethod
                ? rightConcreteMethod.MethodGenericParameters
                : right.GenericParameters;
        return TypeListsEquivalent(leftTypeArguments, rightTypeArguments)
               && TypeListsEquivalent(leftMethodArguments, rightMethodArguments);
    }

    private static bool TypeListsEquivalent(
        IReadOnlyList<TypeAnalysisContext> left,
        IReadOnlyList<TypeAnalysisContext> right) =>
        left.Count == right.Count
        && left.Select((argument, index) =>
            GenericCallRebinder.TypesEquivalent(argument, right[index])).All(equivalent => equivalent);

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
            var instantiated = represented == null
                ? null
                : InstantiateMethodEntryTarget(represented, typeArguments, methodArguments);
            return instantiated == null
                ? null
                : new RuntimeMethodInfoAnalysisContext(
                    instantiated,
                    instantiated.DeclaringType!.DeclaringAssembly);
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

    /// <summary>
    /// 方法RGCTXData中的MethodSpec允许把当前外层方法参数作为目标声明类型的实参。
    /// 应用上下文先解析出的具体方法仍携带MethodSpec自身的MVAR/VAR；在消费该条目的
    /// 方法作用域内必须再实例化一次，防止共享原生体的具体代表类型覆盖外层开放参数。
    /// </summary>
    internal static MethodAnalysisContext InstantiateMethodEntryTarget(
        MethodAnalysisContext represented,
        IReadOnlyList<TypeAnalysisContext> typeArguments,
        IReadOnlyList<TypeAnalysisContext> methodArguments)
    {
        if (represented is not ConcreteGenericMethodAnalysisContext concrete)
            return represented;

        var instantiatedTypeArguments = concrete.TypeGenericParameters
            .Select(argument => GenericInstantiation.Instantiate(argument, typeArguments, methodArguments))
            .ToArray();
        var instantiatedMethodArguments = concrete.MethodGenericParameters
            .Select(argument => GenericInstantiation.Instantiate(argument, typeArguments, methodArguments))
            .ToArray();
        var changed = instantiatedTypeArguments
                          .Where((argument, index) => !ReferenceEquals(argument, concrete.TypeGenericParameters[index]))
                          .Any()
                      || instantiatedMethodArguments
                          .Where((argument, index) => !ReferenceEquals(argument, concrete.MethodGenericParameters[index]))
                          .Any();
        return changed
            ? new ConcreteGenericMethodAnalysisContext(
                concrete.BaseMethodContext,
                instantiatedTypeArguments,
                instantiatedMethodArguments)
            : represented;
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
