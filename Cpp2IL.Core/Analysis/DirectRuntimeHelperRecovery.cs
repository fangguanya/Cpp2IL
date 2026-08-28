using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 当前 ARM64 构建把部分接口调用、强制类型转换和 foreach 清理提取成了共享原生辅助函数。
/// 这些函数不在 metadata 方法表中，必须同时验证调用 ABI 与辅助函数机器码语义后，才能
/// 恢复为托管 ISIL；仅凭地址或返回局部的既有类型会把寄存器复用误判成业务调用。
/// </summary>
public static class DirectRuntimeHelperRecovery
{
    internal enum HelperKind
    {
        None,
        DirectInterfaceInvoke,
        CastClass,
        DisposeIfSupported,
    }

    private const int MaxArm64HelperInstructionCount = 48;

    /// <summary>
    /// 使用应用级地址缓存执行生产恢复。同一共享辅助函数在大型方法中可能出现数十次，
    /// 机器码分类只允许计算一次，调用点仍逐一验证类型和槽位。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary.InstructionSetId != DefaultInstructionSets.ARM_V8)
            return 0;

        return Run(method, address => Classify(method.AppContext, address));
    }

    /// <summary>
    /// 测试入口允许固定分类结果，以独立覆盖基本、边界和异常 ABI，而不依赖外部二进制。
    /// </summary>
    internal static int Run(MethodAnalysisContext method, Func<ulong, HelperKind> classify)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        if (classify == null)
            throw new ArgumentNullException(nameof(classify));

        var rewritten = 0;
        var classificationByAddress = new Dictionary<ulong, HelperKind>();
        var allInstructions = method.ControlFlowGraph!.Blocks
            .SelectMany(block => block.Instructions)
            .OrderBy(instruction => instruction.Index)
            .ToList();
        var homeBlocks = method.ControlFlowGraph.Blocks
            .SelectMany(block => block.Instructions.Select(instruction => (instruction, block)))
            .ToDictionary(pair => pair.instruction, pair => pair.block);

        foreach (var instruction in allInstructions)
        {
            if (instruction.OpCode != OpCode.Call
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not Immediate target)
                continue;

            var disposeReceiver = default(LocalVariable);
            var candidateKind = GetCandidateKind(
                instruction,
                method,
                allInstructions,
                homeBlocks,
                out disposeReceiver);
            if (candidateKind == HelperKind.None)
                continue;

            if (!classificationByAddress.TryGetValue(target.UnsignedValue, out var helperKind))
            {
                helperKind = classify(target.UnsignedValue);
                classificationByAddress[target.UnsignedValue] = helperKind;
            }

            if (helperKind != candidateKind)
            {
                Logger.VerboseNewline(
                    $"{method.DeclaringType?.FullName}.{method.Name} 的 0x{target.UnsignedValue:X} " +
                    $"调用形状={candidateKind}、机器码分类={helperKind}，保持原调用。",
                    nameof(DirectRuntimeHelperRecovery));
                continue;
            }

            var changed = helperKind switch
            {
                HelperKind.DirectInterfaceInvoke => RewriteDirectInterfaceInvoke(method, instruction, homeBlocks),
                HelperKind.CastClass => RewriteCastClass(method, instruction, homeBlocks),
                HelperKind.DisposeIfSupported => RewriteDisposeIfSupported(instruction, disposeReceiver),
                _ => false,
            };
            if (changed)
                rewritten++;
        }

        if (rewritten > 0)
        {
            Logger.VerboseNewline(
                $"{method.DeclaringType?.FullName}.{method.Name} 恢复 {rewritten} 个直接运行时辅助调用。",
                nameof(DirectRuntimeHelperRecovery));
        }

        return rewritten;
    }

    private static HelperKind GetCandidateKind(
        Instruction instruction,
        MethodAnalysisContext method,
        IReadOnlyList<Instruction> allInstructions,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks,
        out LocalVariable? disposeReceiver)
    {
        disposeReceiver = null;
        if (IsDirectInterfaceInvokeShape(instruction))
            return HelperKind.DirectInterfaceInvoke;
        if (IsCastClassShape(instruction))
            return HelperKind.CastClass;
        if (IsDisposeIfSupportedShape(instruction)
            && ResolveDisposedEnumerator(method, instruction, allInstructions, homeBlocks) is { } resolvedReceiver)
        {
            disposeReceiver = resolvedReceiver;
            return HelperKind.DisposeIfSupported;
        }
        return HelperKind.None;
    }

    /// <summary>
    /// 直接接口尾调用 ABI：X0=槽位、X1=接口 Il2CppClass*、X2=接收者。
    /// 当前恢复器在 Call 中依次保存返回槽和这些原生参数。
    /// </summary>
    private static bool IsDirectInterfaceInvokeShape(Instruction instruction)
        => instruction.Operands is
           [Immediate, LocalVariable, Immediate { Value: >= 0 and <= ushort.MaxValue },
               TypeAnalysisContext { IsInterface: true } declaringInterface,
               LocalVariable { Type: { } receiverType }, ..]
           && GenericCallRebinder.TypesEquivalent(declaringInterface, receiverType);

    /// <summary>
    /// 快速 castclass ABI：X0 同时承载输入和输出，X1 为目标 Il2CppClass*。
    /// 晚期字段/属性恢复可能已经把X0输入折叠为强类型字段或属性结果，因此这里只要求
    /// 返回槽仍属于X0且输入具有托管引用证据；最终语义仍必须由辅助函数机器码分类确认。
    /// </summary>
    private static bool IsCastClassShape(Instruction instruction)
        => instruction.Operands is
           [Immediate, LocalVariable destination, { } source,
               TypeAnalysisContext { IsValueType: false }, ..]
           && destination.Register.Name == "X0"
           && IsManagedReferenceOperand(source);

    /// <summary>
    /// 判断晚期折叠后的调用输入是否仍带有确定的托管引用类型。仅接纳局部与实例字段，
    /// 不把整数、原生内存或无类型表达式提升为对象，避免普通原生二参数调用进入候选集。
    /// </summary>
    private static bool IsManagedReferenceOperand(IOperand operand)
        => operand switch
        {
            LocalVariable { Type: { IsValueType: false } } => true,
            FieldReference { Field.FieldType.IsValueType: false } => true,
            _ => false,
        };

    /// <summary>
    /// foreach 清理 ABI 的唯一托管实参是 X0 异常状态结构地址；X2 只是调用点残留寄存器，
    /// 不属于辅助函数语义。接收者必须另外从该调用前最近且可达的 IEnumerator.GetEnumerator
    /// 结果及其循环内实际使用闭合，禁止直接采用 X2。
    /// </summary>
    private static bool IsDisposeIfSupportedShape(Instruction instruction)
        => instruction.Operands is
           [Immediate, LocalVariable, AddressOf, ..];

    private static bool RewriteDirectInterfaceInvoke(
        MethodAnalysisContext method,
        Instruction instruction,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks)
    {
        if (instruction.Operands is not
            [Immediate, LocalVariable destination, Immediate slot,
                TypeAnalysisContext declaringInterface, LocalVariable receiver, ..]
            || InterfaceDispatchRecovery.ResolveInterfaceSlot(declaringInterface, checked((int)slot.Value)) is not { } resolved
            || resolved.IsStatic
            || resolved.Parameters.Count != 0)
            return false;

        if (resolved.IsVoid)
        {
            instruction.SetOperands(resolved, receiver);
            instruction.OpCode = OpCode.CallVoid;
        }
        else
        {
            var splitDestination = LocalLiveRangeHelper.SplitResultLiveRange(
                method,
                instruction,
                destination,
                resolved.ReturnType,
                homeBlocks,
                $"interface_{resolved.Name}");
            instruction.SetOperands(resolved, splitDestination, receiver);
        }

        return true;
    }

    private static bool RewriteCastClass(
        MethodAnalysisContext method,
        Instruction instruction,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks)
    {
        if (instruction.Operands is not
            [Immediate, LocalVariable destination, { } source,
                TypeAnalysisContext { IsValueType: false } targetType, ..])
            return false;

        var splitDestination = LocalLiveRangeHelper.SplitResultLiveRange(
            method,
            instruction,
            destination,
            targetType,
            homeBlocks,
            $"cast_{targetType.Name}");
        instruction.SetOperands(splitDestination, source, targetType);
        instruction.OpCode = OpCode.CastClass;
        return true;
    }

    private static bool RewriteDisposeIfSupported(
        Instruction instruction,
        LocalVariable? receiver)
    {
        if (receiver == null)
            return false;

        instruction.SetOperands(receiver);
        instruction.OpCode = OpCode.DisposeIfSupported;
        return true;
    }

    /// <summary>
    /// 从清理点之前最近的可达非泛型 IEnumerator 获取结果恢复 foreach 接收者。
    /// 候选必须在清理前被循环体真实读取，排除只因源码地址较近但属于其他分支的枚举器。
    /// </summary>
    private static LocalVariable? ResolveDisposedEnumerator(
        MethodAnalysisContext method,
        Instruction cleanup,
        IReadOnlyList<Instruction> allInstructions,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks)
    {
        if (!homeBlocks.TryGetValue(cleanup, out var cleanupBlock))
            return null;

        foreach (var candidate in allInstructions
                     .Where(instruction => instruction.Index >= 0 && instruction.Index < cleanup.Index)
                     .OrderByDescending(instruction => instruction.Index))
        {
            if (candidate is not
                {
                    OpCode: OpCode.Call,
                    Operands: [MethodAnalysisContext { Name: "GetEnumerator" } target,
                        LocalVariable { Type.FullName: "System.Collections.IEnumerator" } result, ..],
                }
                || target.ReturnType.FullName != "System.Collections.IEnumerator"
                || !homeBlocks.TryGetValue(candidate, out var candidateBlock)
                || !CanReach(candidateBlock, cleanupBlock))
                continue;

            var usedByLoop = allInstructions.Any(instruction =>
                instruction.Index > candidate.Index
                && instruction.Index < cleanup.Index
                && instruction.Sources.Any(source => ReferenceEquals(source, result)));
            if (usedByLoop)
                return result;
        }

        return null;
    }

    private static bool CanReach(Graphs.Block start, Graphs.Block target)
    {
        var visited = new HashSet<Graphs.Block>();
        var queue = new Queue<Graphs.Block>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            if (ReferenceEquals(current, target))
                return true;
            foreach (var successor in current.Successors)
                queue.Enqueue(successor);
        }

        return false;
    }

    private static HelperKind Classify(ApplicationAnalysisContext appContext, ulong address)
    {
        if (appContext.DirectRuntimeHelpersByAddress.TryGetValue(address, out var cached))
            return cached;

        HelperKind classified;
        try
        {
            var body = NewArm64Utils.GetArm64InstructionsAtVirtualAddress(
                appContext.Binary,
                address,
                MaxArm64HelperInstructionCount);
            var objectIsInst = appContext.GetOrCreateKeyFunctionAddresses().il2cpp_vm_object_is_inst;
            classified = ClassifyArm64Body(
                body,
                objectIsInst,
                target => ThrowHelperRecovery.GetThrownException(appContext, target)?.FullName);
        }
        catch (Exception exception)
        {
            Logger.VerboseNewline(
                $"运行时辅助函数 0x{address:X} 分类失败：{exception.Message}",
                nameof(DirectRuntimeHelperRecovery));
            classified = HelperKind.None;
        }

        return appContext.DirectRuntimeHelpersByAddress.GetOrAdd(address, classified);
    }

    /// <summary>
    /// 机器码分类只读取语义稳定的寄存器、类布局偏移、控制转移和异常类型。
    /// 地址仅用于确认同一函数内部的成功/失败块，不作为某个游戏版本的硬编码键。
    /// </summary>
    internal static HelperKind ClassifyArm64Body(
        IReadOnlyList<Arm64Instruction> body,
        ulong objectIsInstAddress,
        Func<ulong, string?> resolveThrownException)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (resolveThrownException == null)
            throw new ArgumentNullException(nameof(resolveThrownException));

        if (IsDirectInterfaceInvokeBody(body))
            return HelperKind.DirectInterfaceInvoke;
        if (IsCastClassBody(body, resolveThrownException))
            return HelperKind.CastClass;
        if (IsDisposeIfSupportedBody(body, objectIsInstAddress))
            return HelperKind.DisposeIfSupported;
        return HelperKind.None;
    }

    private static bool IsDirectInterfaceInvokeBody(IReadOnlyList<Arm64Instruction> body)
    {
        if (body.Count < 25
            || body[0].Mnemonic != Arm64Mnemonic.STP
            || !IsLoad(body[1], Arm64Mnemonic.LDR, Arm64Register.X8, Arm64Register.X2, 0)
            || !IsMove(body[2], Arm64Register.X19, Arm64Register.X2)
            || !IsMove(body[3], Arm64Register.W2, Arm64Register.W0)
            || !IsLoad(body[4], Arm64Mnemonic.LDRH, Arm64Register.W9, Arm64Register.X8, 302))
            return false;

        return body.Take(30).Any(instruction =>
                   instruction.Mnemonic == Arm64Mnemonic.LDP
                   && instruction.Op0Reg == Arm64Register.X2
                   && instruction.Op1Reg == Arm64Register.X1
                   && instruction.MemBase == Arm64Register.X0
                   && instruction.MemOffset == 0)
               && body.Take(30).Any(instruction => IsMove(instruction, Arm64Register.X0, Arm64Register.X19))
               && body.Take(30).Any(instruction =>
                   instruction.Mnemonic == Arm64Mnemonic.BR
                   && instruction.Op0Reg == Arm64Register.X2);
    }

    private static bool IsCastClassBody(
        IReadOnlyList<Arm64Instruction> body,
        Func<ulong, string?> resolveThrownException)
    {
        if (body.Count < 14
            || body[0].Mnemonic != Arm64Mnemonic.CBZ
            || body[0].Op0Reg != Arm64Register.X0
            || !IsLoad(body[1], Arm64Mnemonic.LDR, Arm64Register.X8, Arm64Register.X0, 0)
            || !IsLoad(body[2], Arm64Mnemonic.LDRB, Arm64Register.W9, Arm64Register.X1, 304)
            || !IsLoad(body[3], Arm64Mnemonic.LDRB, Arm64Register.W10, Arm64Register.X8, 304)
            || !IsLoad(body[6], Arm64Mnemonic.LDR, Arm64Register.X8, Arm64Register.X8, 200)
            || !IsLoad(body[8], Arm64Mnemonic.LDUR, Arm64Register.X8, Arm64Register.X8, -8))
            return false;

        var successAddress = unchecked((ulong)((long)body[0].Address + body[0].Op1Imm));
        var successReturn = body.FirstOrDefault(instruction =>
            instruction.Address == successAddress
            && instruction.Mnemonic == Arm64Mnemonic.RET);
        if (successReturn.Mnemonic != Arm64Mnemonic.RET)
            return false;

        var failureAddress = successReturn.Address + 4;
        var failureBranchesAgree = body.Take(14)
            .Where(instruction => instruction.Mnemonic == Arm64Mnemonic.B)
            .All(instruction => instruction.BranchTarget == failureAddress);
        var failureCall = body.Take(14).FirstOrDefault(instruction =>
            instruction.Address > failureAddress
            && instruction.Mnemonic == Arm64Mnemonic.BL
            && instruction.BranchTarget != 0);

        return failureBranchesAgree
               && failureCall.Mnemonic == Arm64Mnemonic.BL
               && resolveThrownException(failureCall.BranchTarget) == "System.InvalidCastException";
    }

    private static bool IsDisposeIfSupportedBody(
        IReadOnlyList<Arm64Instruction> body,
        ulong objectIsInstAddress)
    {
        if (body.Count < 42
            || body[0].Mnemonic != Arm64Mnemonic.STP
            || objectIsInstAddress == 0
            || !body.Take(16).Any(instruction => IsMove(instruction, Arm64Register.X19, Arm64Register.X0))
            || !body.Take(16).Any(instruction => IsLoad(
                instruction, Arm64Mnemonic.LDR, Arm64Register.X8, Arm64Register.X0, 8))
            || !body.Take(16).Any(instruction =>
                instruction.Mnemonic == Arm64Mnemonic.BL
                && instruction.BranchTarget == objectIsInstAddress)
            || !body.Take(36).Any(instruction => IsLoad(
                instruction, Arm64Mnemonic.LDRH, Arm64Register.W9, Arm64Register.X8, 302))
            || !body.Take(36).Any(instruction =>
                instruction.Mnemonic == Arm64Mnemonic.BLR
                && instruction.Op0Reg == Arm64Register.X8))
            return false;

        var normalReturnIndex = body.Take(44).ToList().FindIndex(instruction => instruction.Mnemonic == Arm64Mnemonic.RET);
        return normalReturnIndex > 0
               && normalReturnIndex + 1 < body.Count
               && body[normalReturnIndex + 1].Mnemonic == Arm64Mnemonic.BL
               && body.Take(normalReturnIndex).Any(instruction =>
                   instruction.Mnemonic == Arm64Mnemonic.CBNZ
                   && instruction.Op0Reg == Arm64Register.X0);
    }

    private static bool IsLoad(
        Arm64Instruction instruction,
        Arm64Mnemonic mnemonic,
        Arm64Register destination,
        Arm64Register source,
        int offset)
        => instruction.Mnemonic == mnemonic
           && instruction.Op0Reg == destination
           && instruction.MemBase == source
           && instruction.MemOffset == offset;

    private static bool IsMove(
        Arm64Instruction instruction,
        Arm64Register destination,
        Arm64Register source)
        => instruction.Mnemonic == Arm64Mnemonic.MOV
           && instruction.Op0Reg == destination
           && instruction.Op1Reg == source;
}
