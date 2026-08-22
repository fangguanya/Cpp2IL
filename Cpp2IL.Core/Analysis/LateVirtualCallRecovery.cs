using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 使用退 SSA 后已经稳定的真实接收者类型恢复普通虚调用。
/// </summary>
/// <remarks>
/// 早期虚调用解析依赖 SSA 前驱闭包；大型方法中的共享接收者 Phi 可能直到字段、构造器和
/// 保存寄存器恢复完成后才获得具体类型。本阶段只消费间接调用现有的接收者、目标半槽和
/// MethodInfo 半槽三项最终证据，不重跑早期前驱分析。
/// </remarks>
public static class LateVirtualCallRecovery
{
    /// <summary>
    /// 恢复满足完整最终证据的普通虚调用，并返回本轮改写数。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var is32Bit = method.AppContext.Binary.is32Bit;
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var vtableOffset = Il2CppClassUsefulOffsets.GetVirtualInvokeDataVtableOffset(is32Bit);
        var invokeDataSize = pointerSize * 2L;
        var definitions = cfg.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var recovered = 0;

        foreach (var block in cfg.Blocks)
        {
            foreach (var transfer in block.Instructions.ToArray())
            {
                if (transfer.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump)
                    || transfer.Operands.Count <= 2
                    || ResolveLoad(transfer.Operands[0], definitions) is not { } targetLoad
                    || targetLoad is not
                    {
                        Base: LocalVariable klass,
                        Index: null,
                        Scale: 0,
                    }
                    || transfer.Operands[2] is not LocalVariable receiver
                    || ResolveFinalReceiverType(receiver, cfg.Instructions) is not { } receiverType)
                    continue;

                // 中文注释：已定型类指针必须与真实接收者相容；未定型类指针仍可由同一
                // VirtualInvokeData 的 methodPtr/MethodInfo 双半槽和具体接收者共同闭合。
                if (klass.Type is RuntimeClassTypeAnalysisContext { RepresentedType: var klassType })
                {
                    if (!TypesCompatible(receiverType, klassType))
                        continue;
                }
                else if (klass.Type != null)
                {
                    continue;
                }

                var relativeOffset = targetLoad.Addend - vtableOffset;
                if (relativeOffset < 0 || relativeOffset % invokeDataSize != 0)
                    continue;

                // 中文注释：晚期阶段必须同时看到同一 VirtualInvokeData 的第二半槽，
                // 以此区分普通函数指针和真实虚表分派。
                if (!transfer.Operands.Skip(2).Any(operand =>
                        ResolveLoad(operand, definitions) is { } methodInfoLoad
                        && SameLocal(methodInfoLoad.Base, klass)
                        && methodInfoLoad.Index == null
                        && methodInfoLoad.Scale == 0
                        && methodInfoLoad.Addend == targetLoad.Addend + pointerSize))
                    continue;

                var slot = checked((int)(relativeOffset / invokeDataSize));
                if (MetadataResolver.ResolveVTableSlot(method.AppContext, receiverType, slot) is not
                    {
                        IsStatic: false,
                        DeclaringType: { } declaringType,
                    } resolved
                    || !TypesCompatible(receiverType, declaringType))
                    continue;

                Logger.VerboseNewline(
                    $"晚期普通虚调用闭合：method={method.Name}，instruction={transfer.Index}，" +
                    $"receiver={receiverType.FullName}，slot={slot}，target={declaringType.FullName}.{resolved.Name}",
                    nameof(LateVirtualCallRecovery));
                IndirectTransferCallRewriter.Rewrite(method, transfer, block, resolved, receiver);
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// 只展开具有唯一局部定义的内存载荷；退 SSA 后的多定义局部保持原样。
    /// </summary>
    private static MemoryOperand? ResolveLoad(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
        => operand switch
        {
            MemoryOperand load => load,
            LocalVariable local when definitions.TryGetValue(local, out var definition)
                                     && definition is
                                     {
                                         OpCode: OpCode.Move,
                                         Operands: [_, MemoryOperand load],
                                     } => load,
            _ => null,
        };

    private static bool TypesCompatible(TypeAnalysisContext concrete, TypeAnalysisContext declared)
        => GenericCallRebinder.TypesEquivalent(concrete, declared)
           || concrete.IsAssignableTo(declared);

    /// <summary>
    /// 优先使用局部现有的具体引用类型；接口局部则要求全部最终定义都从同一具体类型复制。
    /// </summary>
    private static TypeAnalysisContext? ResolveFinalReceiverType(
        LocalVariable receiver,
        IEnumerable<Instruction> instructions)
    {
        if (receiver.Type is { IsValueType: false } direct
            && direct is not RuntimeClassTypeAnalysisContext
            && direct.FullName != "System.Object"
            && !IsInterface(direct))
            return direct;

        if (receiver.Type is not { IsValueType: false } declared || !IsInterface(declared))
            return null;

        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable destination
                                  && SameLocal(destination, receiver))
            .ToArray();
        if (definitions.Length == 0)
            return null;

        TypeAnalysisContext? consensus = null;
        foreach (var definition in definitions)
        {
            if (definition is not { OpCode: OpCode.Move, Operands: [_, var source] }
                || ResolveConcreteSourceType(source) is not { IsValueType: false } candidate
                || candidate.FullName == "System.Object"
                || IsInterface(candidate)
                || !TypesCompatible(candidate, declared))
                return null;

            if (consensus == null)
                consensus = candidate;
            else if (!GenericCallRebinder.TypesEquivalent(consensus, candidate))
                return null;
        }

        return consensus;
    }

    private static TypeAnalysisContext? ResolveConcreteSourceType(IOperand source)
        => source switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

    private static bool IsInterface(TypeAnalysisContext type)
        => type is GenericInstanceTypeAnalysisContext { GenericType.IsInterface: true }
           || type.IsInterface;

    private static bool SameLocal(IOperand? left, LocalVariable right)
        => left is LocalVariable local
           && (ReferenceEquals(local, right) || local.Register == right.Register);
}
