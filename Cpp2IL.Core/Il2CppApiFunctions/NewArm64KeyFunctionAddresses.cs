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
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext, thunkPtr, true);
