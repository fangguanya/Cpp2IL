using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Il2CppApiFunctions;

public class NewArm64KeyFunctionAddresses : BaseKeyFunctionAddresses
{
    /// <summary>
    /// 从ARM64方法体中返回首个BL直接调用目标，用于识别元数据初始化函数。
    /// </summary>
    protected override ulong FindFirstCallTargetInMethod(ulong methodVa)
    {
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext, methodVa, false);
        var call = instructions.FirstOrDefault(instruction => instruction.Mnemonic == Arm64Mnemonic.BL);
        return call.Mnemonic == Arm64Mnemonic.BL ? call.BranchTarget : 0;
    }

    private List<Arm64Instruction>? _cachedDisassembledBytes;

    private List<Arm64Instruction> DisassembleTextSection()
    {
        if (_cachedDisassembledBytes == null)
        {
            var binary = _appContext.Binary;
            var toDisasm = binary.GetEntirePrimaryExecutableSection();
            _cachedDisassembledBytes = Disassembler.Disassemble(toDisasm, binary.GetVirtualAddressOfPrimaryExecutableSection(), new(true, true, false)).ToList();
        }

        return _cachedDisassembledBytes;
    }

    protected override IEnumerable<ulong> FindAllThunkFunctions(ulong addr, uint maxBytesBack = 0, params ulong[] addressesToIgnore)
    {
        // ARM64没有x86的0xCC函数填充。无条件B会完整转发参数与返回值，正是可安全识别的尾调用thunk；
        // BL只是普通子调用，纳入会把包含目标调用的大函数误判成运行时helper。
        // 部分Unity ARM64运行时把实际可调用入口放在尾跳前一条无副作用的自移动指令上。
        // 只有调用者计数能唯一证明此前入口时才回溯，避免把相邻函数末尾误并入thunk。
        foreach (var address in FindDirectTailThunkEntryAddresses(
                     DisassembleTextSection(),
                     addr,
                     maxBytesBack,
                     addressesToIgnore))
            yield return address;
    }

    internal static IReadOnlyList<ulong> FindDirectTailThunkEntryAddresses(
        IReadOnlyList<Arm64Instruction> instructions,
        ulong target,
        uint maxBytesBack = 0,
        IEnumerable<ulong>? addressesToIgnore = null)
    {
        var ignored = new HashSet<ulong>(addressesToIgnore ?? Enumerable.Empty<ulong>());
        var ordered = instructions.OrderBy(instruction => instruction.Address).ToArray();
        var indexByAddress = ordered
            .Select((instruction, index) => (instruction.Address, index))
            .GroupBy(pair => pair.Address)
            .ToDictionary(group => group.Key, group => group.First().index);
        var callerCounts = ordered
            .Where(instruction => instruction.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.BL
                && instruction.BranchTarget != 0)
            .GroupBy(instruction => instruction.BranchTarget)
            .ToDictionary(group => group.Key, group => group.Count());
        var maximumInstructionsBack = checked((int)(maxBytesBack / sizeof(uint)));
        var entries = new List<ulong>();

        foreach (var branchAddress in FindDirectTailThunkAddresses(ordered, target, ignored))
        {
            if (maximumInstructionsBack == 0 || !indexByAddress.TryGetValue(branchAddress, out var branchIndex))
            {
                entries.Add(branchAddress);
                continue;
            }

            var candidates = new List<ulong> { branchAddress };
            var currentAddress = branchAddress;
            for (var offset = 1; offset <= maximumInstructionsBack && branchIndex - offset >= 0; offset++)
            {
                var previous = ordered[branchIndex - offset];
                if (previous.Address + sizeof(uint) != currentAddress
                    || !IsNoOpSelfMove(previous)
                    || ignored.Contains(previous.Address))
                    break;

                candidates.Add(previous.Address);
                currentAddress = previous.Address;
            }

            var rankedCandidates = candidates
                .Select(candidate => (
                    Address: candidate,
                    CallerCount: callerCounts.TryGetValue(candidate, out var count) ? count : 0))
                .ToArray();
            var strongestCallerCount = rankedCandidates.Max(candidate => candidate.CallerCount);
            var strongest = rankedCandidates
                .Where(candidate => candidate.CallerCount == strongestCallerCount)
                .Select(candidate => candidate.Address)
                .ToArray();

            // 零调用者或并列强度都不能证明入口边界，保持精确尾跳地址。
            entries.Add(strongestCallerCount > 0 && strongest.Length == 1
                ? strongest[0]
                : branchAddress);
        }

        return entries.Distinct().ToArray();
    }

    private static bool IsNoOpSelfMove(Arm64Instruction instruction) =>
        instruction.Mnemonic == Arm64Mnemonic.MOV
        && instruction.Op0Reg == instruction.Op1Reg;

    internal static IReadOnlyList<ulong> FindDirectTailThunkAddresses(
        IEnumerable<Arm64Instruction> instructions,
        ulong target,
        IEnumerable<ulong>? addressesToIgnore = null)
    {
        var ignored = new HashSet<ulong>(addressesToIgnore ?? Enumerable.Empty<ulong>());

        return instructions
            .Where(instruction => instruction.Mnemonic == Arm64Mnemonic.B
                && instruction.BranchTarget == target
                && !ignored.Contains(instruction.Address))
            .Select(instruction => instruction.Address)
            .Distinct()
            .ToArray();
    }

    protected override ulong GetObjectIsInstFromSystemType()
    {
        Logger.Verbose("\tTrying to use System.Type::IsInstanceOfType to find il2cpp::vm::Object::IsInst...");
        var typeIsInstanceOfType = ReflectionCache.GetType("Type", "System")?.Methods?.FirstOrDefault(m => m.Name == "IsInstanceOfType");
        if (typeIsInstanceOfType == null)
        {
            Logger.VerboseNewline("Type or method not found, aborting.");
            return 0;
        }

        //IsInstanceOfType is a very simple ICall, that looks like this:
        //  Il2CppClass* klass = vm::Class::FromIl2CppType(type->type.type);
        //  return il2cpp::vm::Object::IsInst(obj, klass) != NULL;
        //The last call is to Object::IsInst

        Logger.Verbose($"IsInstanceOfType found at 0x{typeIsInstanceOfType.MethodPointer:X}...");
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext, typeIsInstanceOfType.MethodPointer, true);

        if (!TryGetObjectIsInstCallTarget(instructions, out var target))
        {
            Logger.VerboseNewline("Method uses managed GetType/IsAssignableFrom dispatch; trying the ARM64 assignability-tail-thunk route...");
            var assignabilityExport = _appContext.Binary.GetVirtualAddressOfExportedFunctionByName(
                "il2cpp_class_is_assignable_from");
            var assignabilityCore = assignabilityExport == 0
                ? 0
                : FindFunctionThisIsAThunkOf(assignabilityExport);
            if (assignabilityCore == 0
                || !TryFindObjectIsInstTailThunk(
                    DisassembleTextSection(),
                    assignabilityCore,
                    out target))
            {
                Logger.VerboseNewline("No unique Object::IsInst tail thunk was proven.");
                return 0;
            }

            Logger.VerboseNewline($"Success. IsInst tail thunk found at 0x{target:X}");
            return target;
        }

        Logger.VerboseNewline($"Success. IsInst found at 0x{target:X}");
        return target;
    }

    /// <summary>
    /// 新版运行时的 Object::IsInst 不是导出函数；代码生成入口以单条 B 尾跳到其实现，
    /// 实现内部调用 class-assignability 核心。只接受调用者数量唯一最大的尾跳板，
    /// 避免把同一核心中的内部标签或低频辅助函数误登记为全局键函数。
    /// </summary>
    internal static bool TryFindObjectIsInstTailThunk(
        IReadOnlyList<Arm64Instruction> instructions,
        ulong assignabilityCore,
        out ulong thunk)
    {
        thunk = 0;
        if (assignabilityCore == 0 || instructions.Count == 0)
            return false;

        var ordered = instructions.OrderBy(instruction => instruction.Address).ToArray();
        var indexByAddress = ordered
            .Select((instruction, index) => (instruction.Address, index))
            .GroupBy(pair => pair.Address)
            .ToDictionary(group => group.Key, group => group.First().index);

        var candidates = new List<(ulong Thunk, int Callers)>();
        foreach (var branch in ordered.Where(instruction =>
                     instruction.Mnemonic == Arm64Mnemonic.B
                     && instruction.BranchTarget != 0))
        {
            if (!indexByAddress.TryGetValue(branch.BranchTarget, out var targetIndex)
                || !BodyCallsAssignabilityCore(ordered, targetIndex, assignabilityCore))
                continue;

            var callers = ordered.Count(instruction =>
                instruction.Mnemonic == Arm64Mnemonic.BL
                && instruction.BranchTarget == branch.Address);
            if (callers > 0)
                candidates.Add((branch.Address, callers));
        }

        if (candidates.Count == 0)
            return false;

        var maximumCallers = candidates.Max(candidate => candidate.Callers);
        var strongest = candidates
            .Where(candidate => candidate.Callers == maximumCallers)
            .Select(candidate => candidate.Thunk)
            .Distinct()
            .ToArray();
        if (strongest.Length != 1)
            return false;

        thunk = strongest[0];
        return true;
    }

    private static bool BodyCallsAssignabilityCore(
        IReadOnlyList<Arm64Instruction> ordered,
        int startIndex,
        ulong assignabilityCore)
    {
        const int MaximumBodyInstructions = 192;
        for (var offset = 0; offset < MaximumBodyInstructions && startIndex + offset < ordered.Count; offset++)
        {
            var instruction = ordered[startIndex + offset];
            if (instruction.Mnemonic == Arm64Mnemonic.BL
                && instruction.BranchTarget == assignabilityCore)
                return true;
            if (instruction.Mnemonic == Arm64Mnemonic.RET)
                return false;
        }

        return false;
    }

    /// <summary>
    /// 仅把以普通返回结束的直接调用识别为 Object::IsInst。
    /// 新版运行时会先调用 Object.GetType，再经 BR 尾调 Type.IsAssignableFrom；该 BL 绝不能登记为键函数。
    /// </summary>
    internal static bool TryGetObjectIsInstCallTarget(
        IReadOnlyList<Arm64Instruction> instructions,
        out ulong target)
    {
        for (var index = instructions.Count - 1; index >= 0; index--)
        {
            var instruction = instructions[index];
            if (instruction.Mnemonic != Arm64Mnemonic.BL || instruction.BranchTarget == 0)
                continue;

            for (var suffix = index + 1; suffix < instructions.Count; suffix++)
            {
                // 间接尾调用证明此前 BL 只是为后续托管虚调用准备接收者，不是 IsInst 本体。
                if (instructions[suffix].Mnemonic == Arm64Mnemonic.BR)
                {
                    target = 0;
                    return false;
                }
            }

            target = instruction.BranchTarget;
            return true;
        }

        target = 0;
        return false;
    }

    protected override ulong FindFunctionThisIsAThunkOf(ulong thunkPtr, bool prioritiseCall = false)
    {
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext, thunkPtr, true);

        var target = prioritiseCall ? Arm64Mnemonic.BL : Arm64Mnemonic.B;
        var matchingCall = instructions.FirstOrDefault(i => i.Mnemonic == target);

        if (matchingCall.Mnemonic == Arm64Mnemonic.INVALID)
        {
            target = target == Arm64Mnemonic.BL ? Arm64Mnemonic.B : Arm64Mnemonic.BL;
            matchingCall = instructions.FirstOrDefault(i => i.Mnemonic == target);
        }

        return matchingCall.Mnemonic != Arm64Mnemonic.INVALID ? matchingCall.BranchTarget : 0;
    }

    protected override int GetCallerCount(ulong toWhere)
    {
        //Disassemble .text
        var disassembly = DisassembleTextSection();

        //Find all jumps to the target address
        return disassembly.Count(i => i.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.BL && i.BranchTarget == toWhere);
    }

    protected override void AttemptInstructionAnalysisToFillGaps()
    {
        TryGetArm64InitMetadataFromException();
    }

    private void TryGetArm64InitMetadataFromException()
    {
        Logger.VerboseNewline("\t正在通过 System.Exception.get_Message 定位 ARM64 元数据初始化函数……");

        var exceptionType = ReflectionCache.GetType("Exception", "System");
        var getMessage = exceptionType?.Methods?.FirstOrDefault(method => method.Name == "get_Message");
        if (getMessage == null || getMessage.MethodPointer == 0)
        {
            Logger.VerboseNewline("\t\t类型或方法已被裁剪，未写入元数据初始化函数地址。");
            return;
        }

        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(
            _appContext,
            getMessage.MethodPointer);
        if (!TryGetFirstDirectCallTarget(instructions, out var target))
        {
            Logger.WarnNewline("System.Exception.get_Message 中未发现 ARM64 直接调用，未写入元数据初始化函数地址。");
            return;
        }

        if (_appContext.MetadataVersion < 27)
        {
            il2cpp_codegen_initialize_method = target;
            Logger.VerboseNewline($"\t\til2cpp_codegen_initialize_method => 0x{target:X}");
        }
        else
        {
            il2cpp_codegen_initialize_runtime_metadata = target;
            Logger.VerboseNewline($"\t\til2cpp_codegen_initialize_runtime_metadata => 0x{target:X}");
        }
    }

    internal static bool TryGetFirstDirectCallTarget(IEnumerable<Arm64Instruction> instructions, out ulong target)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Mnemonic != Arm64Mnemonic.BL || instruction.BranchTarget == 0)
                continue;

            target = instruction.BranchTarget;
            return true;
        }

        target = 0;
        return false;
    }
}
