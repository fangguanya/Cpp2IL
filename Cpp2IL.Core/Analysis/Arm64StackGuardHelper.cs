using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 统一承载 ARM64 TPIDR_EL0 栈保护形态的解码与删除规则。
/// 系统寄存器读先保留为显式 ISIL；只有完整栈保护证据闭合后才允许从托管语义中删除。
/// </summary>
internal static class Arm64StackGuardHelper
{
    internal const long TpidrEl0Encoding = 0xDE82;
    private const long StackGuardThreadOffset = 0x28;
    private const uint MrsTpidrEl0EncodingWithoutDestination = 0xD53BD040;

    /// <summary>
    /// 精确识别 MRS Xd, TPIDR_EL0。机器码、Disarm 操作数种类和值必须一致；
    /// 其他系统寄存器、W/零目标或不一致解码继续保持严格未解决。
    /// </summary>
    internal static bool TryCreateThreadPointerRead(
        Arm64Instruction instruction,
        uint machineCode,
        out Instruction recovered)
    {
        recovered = null!;
        if (instruction.Mnemonic != Arm64Mnemonic.MRS
            || instruction.Op0Kind != Arm64OperandKind.Register
            || instruction.Op0Reg is < Arm64Register.X0 or > Arm64Register.X30
            || instruction.Op1Kind != Arm64OperandKind.Immediate
            || instruction.Op1Imm != TpidrEl0Encoding
            || (machineCode & 0xFFFFFFE0u) != MrsTpidrEl0EncodingWithoutDestination
            || (machineCode & 0x1Fu) != (uint)(instruction.Op0Reg - Arm64Register.X0))
            return false;

        recovered = new Instruction(
            0,
            OpCode.ReadSystemRegister,
            new Register(null, Arm64RegisterHelper.CanonicalName(instruction.Op0Reg)),
            new Immediate(TpidrEl0Encoding))
        {
            IntegerWidthBits = 64,
        };
        return true;
    }

    /// <summary>
    /// 查找完整的 Clang 栈保护序言和全部尾声。只有线程指针、0x28 栈保护槽、
    /// 同一栈保存槽、NE 失败边和唯一前向失败调用全部闭合时才返回可删除索引。
    /// </summary>
    internal static HashSet<int> FindInjectedStackGuardInstructionIndices(
        IReadOnlyList<Arm64Instruction> instructions)
    {
        var result = new HashSet<int>();
        for (var mrsIndex = 0; mrsIndex < instructions.Count; mrsIndex++)
        {
            if (!IsTpidrEl0Read(instructions[mrsIndex]))
                continue;

            if (TryMatchStackGuard(instructions, mrsIndex, out var matched))
                result.UnionWith(matched);
        }

        return result;
    }

    private static bool TryMatchStackGuard(
        IReadOnlyList<Arm64Instruction> instructions,
        int mrsIndex,
        out HashSet<int> matched)
    {
        matched = [];
        var threadRegister = instructions[mrsIndex].Op0Reg;
        var prologueLoadIndex = FindThreadGuardLoadBeforeOverwrite(
            instructions,
            mrsIndex + 1,
            threadRegister);
        if (prologueLoadIndex < 0)
            return false;

        var guardRegister = instructions[prologueLoadIndex].Op0Reg;
        var prologueStoreIndex = FindIndex(
            instructions,
            prologueLoadIndex + 1,
            6,
            candidate => IsStackGuardStore(candidate, guardRegister));
        if (prologueStoreIndex < 0
            || WritesRegisterBetween(instructions, guardRegister, prologueLoadIndex + 1, prologueStoreIndex))
            return false;

        var prologueStore = instructions[prologueStoreIndex];
        var stackBase = prologueStore.MemBase;
        var stackOffset = prologueStore.MemOffset;
        var checkIndices = new HashSet<int>();
        var guardBranchIndices = new HashSet<int>();
        var failureTargetIndices = new HashSet<int>();
        var scanEndExclusive = instructions.Count;

        for (var currentLoadIndex = prologueStoreIndex + 1;
             currentLoadIndex < scanEndExclusive;
             currentLoadIndex++)
        {
            if (!IsPotentialThreadGuardLoad(instructions[currentLoadIndex]))
                continue;

            if (!TryMatchGuardCheck(
                    instructions,
                    currentLoadIndex,
                    stackBase,
                    stackOffset,
                    out var savedLoadIndex,
                    out var compareIndex,
                    out var branchIndex,
                    out var currentFailureTargetIndex))
                continue;

            if (currentFailureTargetIndex <= branchIndex)
                continue;
            if (failureTargetIndices.Count == 0
                && !HasReturnBetween(instructions, branchIndex, currentFailureTargetIndex))
                continue;
            if (failureTargetIndices.Count > 0
                && !failureTargetIndices.Contains(currentFailureTargetIndex))
                continue;

            guardBranchIndices.Add(branchIndex);
            failureTargetIndices.Add(currentFailureTargetIndex);

            // Cpp2IL 的原生窗口可能跨入相邻函数；唯一前向失败调用即本形态的局部上界。
            scanEndExclusive = currentFailureTargetIndex;
            checkIndices.UnionWith([currentLoadIndex, savedLoadIndex, compareIndex, branchIndex]);
        }

        if (guardBranchIndices.Count == 0
            || failureTargetIndices.Count != 1)
            return false;

        var failureTargetIndex = failureTargetIndices.Single();
        var unconditionalFailureBranches = new HashSet<int>();
        if (!TryCollectUnconditionalFailureBranches(
                instructions,
                failureTargetIndex,
                guardBranchIndices,
                unconditionalFailureBranches))
            return false;

        matched.UnionWith([mrsIndex, prologueLoadIndex, prologueStoreIndex, failureTargetIndex]);
        matched.UnionWith(checkIndices);
        matched.UnionWith(unconditionalFailureBranches);
        return true;
    }

    /// <summary>
    /// 在线程指针首次被覆盖前寻找 canary 读取。大栈帧可先把线程指针和参数搬运到栈中，
    /// 因而这里使用寄存器定义边界，而不是任意固定指令窗口。
    /// </summary>
    private static int FindThreadGuardLoadBeforeOverwrite(
        IReadOnlyList<Arm64Instruction> instructions,
        int start,
        Arm64Register threadRegister)
    {
        for (var index = start; index < instructions.Count; index++)
        {
            if (IsThreadGuardLoad(instructions[index], threadRegister))
                return index;
            if (WritesPrimaryRegister(instructions[index], threadRegister))
                return -1;
        }

        return -1;
    }

    private static bool HasReturnBetween(
        IReadOnlyList<Arm64Instruction> instructions,
        int branchIndex,
        int targetIndex)
    {
        for (var index = branchIndex + 1; index < targetIndex; index++)
        {
            if (instructions[index].Mnemonic == Arm64Mnemonic.RET)
                return true;
        }

        return false;
    }

    private static bool TryMatchGuardCheck(
        IReadOnlyList<Arm64Instruction> instructions,
        int currentLoadIndex,
        Arm64Register stackBase,
        long stackOffset,
        out int savedLoadIndex,
        out int compareIndex,
        out int branchIndex,
        out int failureTargetIndex)
    {
        savedLoadIndex = FindIndex(
            instructions,
            currentLoadIndex + 1,
            3,
            candidate => IsStackGuardLoad(candidate, stackBase, stackOffset));
        var usesDirectBridge = false;
        if (savedLoadIndex < 0
            && currentLoadIndex + 1 < instructions.Count
            && NewArmV8InstructionSet.TryGetNonCallDirectBranchTarget(
                instructions[currentLoadIndex + 1],
                out var bridgeTarget))
        {
            // 异常路径可先读取当前 canary，再用无条件 B 汇入共享的保存值比较尾声。
            var bridgedLoadIndex = FindIndexByAddress(instructions, bridgeTarget);
            if (bridgedLoadIndex > currentLoadIndex
                && IsStackGuardLoad(instructions[bridgedLoadIndex], stackBase, stackOffset))
            {
                savedLoadIndex = bridgedLoadIndex;
                usesDirectBridge = true;
            }
        }
        compareIndex = -1;
        branchIndex = -1;
        failureTargetIndex = -1;
        if (savedLoadIndex < 0)
            return false;

        var currentRegister = instructions[currentLoadIndex].Op0Reg;
        var savedRegister = instructions[savedLoadIndex].Op0Reg;
        compareIndex = FindIndex(
            instructions,
            savedLoadIndex + 1,
            4,
            candidate => IsGuardComparison(candidate, currentRegister, savedRegister));
        if (compareIndex < 0
            || !usesDirectBridge
            && WritesRegisterBetween(instructions, currentRegister, currentLoadIndex + 1, compareIndex)
            || WritesRegisterBetween(instructions, savedRegister, savedLoadIndex + 1, compareIndex)
            || compareIndex + 1 >= instructions.Count)
            return false;

        branchIndex = compareIndex + 1;
        var branch = instructions[branchIndex];
        if (branch.Mnemonic != Arm64Mnemonic.B
            || branch.MnemonicConditionCode != Arm64ConditionCode.NE)
            return false;

        var targetAddress = branch.BranchTarget;
        failureTargetIndex = FindIndexByAddress(instructions, targetAddress);
        return failureTargetIndex >= 0
               && instructions[failureTargetIndex].Mnemonic == Arm64Mnemonic.BL;
    }

    private static bool IsTpidrEl0Read(Arm64Instruction instruction)
        => instruction.Mnemonic == Arm64Mnemonic.MRS
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X30
           && instruction.Op1Kind == Arm64OperandKind.Immediate
           && instruction.Op1Imm == TpidrEl0Encoding;

    private static bool IsThreadGuardLoad(
        Arm64Instruction instruction,
        Arm64Register threadRegister)
        => instruction.Mnemonic == Arm64Mnemonic.LDR
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X30
           && instruction.Op1Kind == Arm64OperandKind.Memory
           && instruction.MemBase == threadRegister
           && instruction.MemAddendReg == Arm64Register.INVALID
           && instruction.MemOffset == StackGuardThreadOffset;

    private static bool IsPotentialThreadGuardLoad(Arm64Instruction instruction)
        => instruction.Mnemonic == Arm64Mnemonic.LDR
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X30
           && instruction.Op1Kind == Arm64OperandKind.Memory
           && instruction.MemBase is >= Arm64Register.X0 and <= Arm64Register.X30
           && instruction.MemAddendReg == Arm64Register.INVALID
           && instruction.MemOffset == StackGuardThreadOffset;

    private static bool IsStackGuardStore(
        Arm64Instruction instruction,
        Arm64Register guardRegister)
        => instruction.Mnemonic is Arm64Mnemonic.STR or Arm64Mnemonic.STUR
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg == guardRegister
           && instruction.Op1Kind == Arm64OperandKind.Memory
           // Disarm 在内存基址上下文中以 X31 表示 SP。
           && instruction.MemBase is Arm64Register.X31 or Arm64Register.X29
           && instruction.MemAddendReg == Arm64Register.INVALID
           && instruction.MemOffset % sizeof(long) == 0;

    private static bool IsStackGuardLoad(
        Arm64Instruction instruction,
        Arm64Register stackBase,
        long stackOffset)
        => instruction.Mnemonic is Arm64Mnemonic.LDR or Arm64Mnemonic.LDUR
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X30
           && instruction.Op1Kind == Arm64OperandKind.Memory
           && instruction.MemBase == stackBase
           && instruction.MemAddendReg == Arm64Register.INVALID
           && instruction.MemOffset == stackOffset;

    private static bool IsGuardComparison(
        Arm64Instruction instruction,
        Arm64Register currentRegister,
        Arm64Register savedRegister)
        => instruction.Mnemonic == Arm64Mnemonic.CMP
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op1Kind == Arm64OperandKind.Register
           && ((instruction.Op0Reg == currentRegister && instruction.Op1Reg == savedRegister)
               || (instruction.Op0Reg == savedRegister && instruction.Op1Reg == currentRegister));

    private static bool TryCollectUnconditionalFailureBranches(
        IReadOnlyList<Arm64Instruction> instructions,
        int failureTargetIndex,
        HashSet<int> matchedBranches,
        HashSet<int> unconditionalBranches)
    {
        var failureAddress = instructions[failureTargetIndex].Address;
        for (var index = 0; index < failureTargetIndex; index++)
        {
            if (matchedBranches.Contains(index))
                continue;
            if (NewArmV8InstructionSet.TryGetNonCallDirectBranchTarget(
                    instructions[index],
                    out var target)
                && target == failureAddress)
            {
                if (instructions[index].Mnemonic != Arm64Mnemonic.B
                    || !NewArmV8InstructionSet.IsUnconditionalBranchCode(
                        instructions[index].MnemonicConditionCode))
                    return false;

                // Clang 会把部分 no-return 异常落点直接并入同一栈保护失败汇点。
                unconditionalBranches.Add(index);
            }
        }

        return true;
    }

    private static bool WritesPrimaryRegister(
        Arm64Instruction instruction,
        Arm64Register register)
        => instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op0Reg == register
           && instruction.Mnemonic is not (
               Arm64Mnemonic.STR or Arm64Mnemonic.STUR or Arm64Mnemonic.STRB
               or Arm64Mnemonic.STRH or Arm64Mnemonic.STP
               or Arm64Mnemonic.CMP or Arm64Mnemonic.CMN
               or Arm64Mnemonic.TST or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ
               or Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ);

    private static bool WritesRegisterBetween(
        IReadOnlyList<Arm64Instruction> instructions,
        Arm64Register register,
        int startInclusive,
        int endExclusive)
    {
        for (var index = startInclusive; index < endExclusive; index++)
        {
            if (WritesPrimaryRegister(instructions[index], register))
                return true;
        }

        return false;
    }

    private static int FindIndex(
        IReadOnlyList<Arm64Instruction> instructions,
        int start,
        int maximumCount,
        System.Func<Arm64Instruction, bool> predicate)
    {
        var end = System.Math.Min(instructions.Count, start + maximumCount);
        for (var index = start; index < end; index++)
        {
            if (predicate(instructions[index]))
                return index;
        }

        return -1;
    }

    private static int FindIndexByAddress(
        IReadOnlyList<Arm64Instruction> instructions,
        ulong address)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Address == address)
                return index;
        }

        return -1;
    }
}
