using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Il2CppApiFunctions;

public class NewArm64KeyFunctionAddresses : BaseKeyFunctionAddresses
{
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
        _ = maxBytesBack;
        foreach (var address in FindDirectTailThunkAddresses(DisassembleTextSection(), addr, addressesToIgnore))
            yield return address;
    }

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
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext.Binary, typeIsInstanceOfType.MethodPointer, true);

        if (!TryGetObjectIsInstCallTarget(instructions, out var target))
        {
            Logger.VerboseNewline("Method uses managed GetType/IsAssignableFrom dispatch or lacks a direct IsInst call. Aborting.");
            return 0;
        }

        Logger.VerboseNewline($"Success. IsInst found at 0x{target:X}");
        return target;
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
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext.Binary, thunkPtr, true);

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
            _appContext.Binary,
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
