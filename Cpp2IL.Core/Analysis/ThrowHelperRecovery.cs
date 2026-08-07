using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Finds exception throw helper functions. These eventually look up the type they throw by name
/// (via <c>Class::FromName(corlib, "System", "NullReferenceException")</c>), so the type name appears
/// as a plain C string in the helper's body.
/// </summary>
public static class ThrowHelperRecovery
{
    private const int MaxDepth = 5;
    private const int MaxStringLength = 64;
    private const int MaxArm64InstructionCount = 32;

    internal enum Arm64ThrowHelperOperation
    {
        Other,
        Adrp,
        AddImmediate,
        Call,
        Branch,
        Return,
    }

    /// <summary>
    /// 仅保留异常辅助函数识别所需的 ARM64 指令字段，避免分析阶段重复解释完整指令语义。
    /// </summary>
    internal readonly record struct Arm64ThrowHelperInstruction(
        Arm64ThrowHelperOperation Operation,
        ulong Address = 0,
        Arm64Register Destination = Arm64Register.INVALID,
        Arm64Register Source = Arm64Register.INVALID,
        long Immediate = 0,
        ulong Target = 0);

    public static TypeAnalysisContext? GetThrownException(ApplicationAnalysisContext appContext, ulong address)
    {
        var name = ResolveName(appContext, address, 0);

        if (name == null)
            return null;

        var type = appContext.LibCpp2IlContext.ReflectionCache.GetType(name);

        return type == null ? null : appContext.ResolveContextForType(type);
    }

    private static string? ResolveName(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        if (appContext.ThrowHelperNamesByAddress.TryGetValue(address, out var cached))
            return cached;

        if (address == 0 || depth >= MaxDepth)
            return null;

        return appContext.ThrowHelperNamesByAddress.Resolve(address, () => ResolveNameUncached(appContext, address, depth));
    }

    private static string? ResolveNameUncached(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        if (appContext.Binary.InstructionSetId == DefaultInstructionSets.ARM_V8)
            return ResolveArm64NameUncached(appContext, address, depth);

        return ResolveX86NameUncached(appContext, address, depth);
    }

    private static string? ResolveX86NameUncached(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        InstructionList body;

        try
        {
            body = X86Utils.GetMethodBodyAtVirtAddressNew(address, true, appContext.Binary);
        }
        catch
        {
            return null;
        }

        var name = FindExceptionName(appContext, body);

        if (name == null)
        {
            foreach (var instruction in body)
            {
                if (instruction.Mnemonic != Mnemonic.Call || instruction.Op0Kind != OpKind.NearBranch64)
                    continue;

                name = ResolveName(appContext, instruction.NearBranchTarget, depth + 1);

                if (name != null)
                    break;
            }
        }

        return name;
    }

    private static string? ResolveArm64NameUncached(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        IReadOnlyList<Arm64Instruction> body;

        try
        {
            body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(
                appContext.Binary,
                address,
                managed: false,
                count: MaxArm64InstructionCount);
        }
        catch
        {
            return null;
        }

        var projectedBody = new Arm64ThrowHelperInstruction[body.Count];

        for (var i = 0; i < body.Count; i++)
            projectedBody[i] = ProjectArm64Instruction(body[i]);

        return FindArm64ExceptionName(
            projectedBody,
            candidateAddress => ReadCStringAtVirtualAddress(appContext, candidateAddress),
            target => ResolveName(appContext, target, depth + 1));
    }

    private static Arm64ThrowHelperInstruction ProjectArm64Instruction(Arm64Instruction instruction)
    {
        if (instruction.Mnemonic == Arm64Mnemonic.ADRP
            && instruction.Op0Kind == Arm64OperandKind.Register
            && instruction.Op1Kind == Arm64OperandKind.Immediate)
        {
            return new(
                Arm64ThrowHelperOperation.Adrp,
                instruction.Address,
                instruction.Op0Reg,
                Immediate: instruction.Op1Imm);
        }

        if (instruction.Mnemonic == Arm64Mnemonic.ADD
            && instruction.Op0Kind == Arm64OperandKind.Register
            && instruction.Op1Kind == Arm64OperandKind.Register
            && instruction.Op2Kind == Arm64OperandKind.Immediate)
        {
            return new(
                Arm64ThrowHelperOperation.AddImmediate,
                instruction.Address,
                instruction.Op0Reg,
                instruction.Op1Reg,
                instruction.Op2Imm);
        }

        if (instruction.Mnemonic == Arm64Mnemonic.BL && instruction.BranchTarget != 0)
            return new(Arm64ThrowHelperOperation.Call, instruction.Address, Target: instruction.BranchTarget);

        if (instruction.Mnemonic == Arm64Mnemonic.B
            && instruction.BranchTarget != 0
            && NewArmV8InstructionSet.IsUnconditionalBranchCode(instruction.MnemonicConditionCode))
        {
            return new(Arm64ThrowHelperOperation.Branch, instruction.Address, Target: instruction.BranchTarget);
        }

        if (instruction.Mnemonic is Arm64Mnemonic.RET or Arm64Mnemonic.BR)
            return new(Arm64ThrowHelperOperation.Return, instruction.Address);

        return new(Arm64ThrowHelperOperation.Other, instruction.Address);
    }

    /// <summary>
    /// 沿 ARM64 的 ADRP/ADD 字符串地址和直接调用链解析异常名；尾调用是当前路径的唯一后继。
    /// </summary>
    internal static string? FindArm64ExceptionName(
        IReadOnlyList<Arm64ThrowHelperInstruction> body,
        Func<ulong, string?> readCString,
        Func<ulong, string?> resolveTarget)
    {
        // netstandard2.0 不提供 ThrowIfNull；显式校验保持所有发布目标的异常类型与参数名一致。
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (readCString == null)
            throw new ArgumentNullException(nameof(readCString));
        if (resolveTarget == null)
            throw new ArgumentNullException(nameof(resolveTarget));

        var absoluteAddresses = new Dictionary<Arm64Register, ulong>();

        foreach (var instruction in body)
        {
            switch (instruction.Operation)
            {
                case Arm64ThrowHelperOperation.Adrp:
                    absoluteAddresses[instruction.Destination] = NewArmV8InstructionSet.ResolveAdrpPageAddress(
                        instruction.Address,
                        instruction.Immediate);
                    break;
                case Arm64ThrowHelperOperation.AddImmediate:
                    if (!absoluteAddresses.TryGetValue(instruction.Source, out var baseAddress))
                        break;

                    var stringAddress = unchecked(baseAddress + unchecked((ulong)instruction.Immediate));
                    absoluteAddresses[instruction.Destination] = stringAddress;

                    if (readCString(stringAddress) is { } text
                        && text.EndsWith("Exception", StringComparison.Ordinal))
                    {
                        return text;
                    }

                    break;
                case Arm64ThrowHelperOperation.Call:
                    if (resolveTarget(instruction.Target) is { } calledName)
                        return calledName;
                    break;
                case Arm64ThrowHelperOperation.Branch:
                    return resolveTarget(instruction.Target);
                case Arm64ThrowHelperOperation.Return:
                    return null;
            }
        }

        return null;
    }

    private static string? FindExceptionName(ApplicationAnalysisContext appContext, InstructionList body)
    {
        foreach (var instruction in body)
        {
            if (instruction.Mnemonic != Mnemonic.Lea || !instruction.IsIPRelativeMemoryOperand)
                continue;

            if (ReadCStringAtVirtualAddress(appContext, instruction.IPRelativeMemoryAddress) is { } text && text.EndsWith("Exception", StringComparison.Ordinal))
                return text;
        }

        return null;
    }

    //TODO didn't we have a helper for this somewhere? Can't find it. Maybe got deleted. Maybe it's just too late
    private static string? ReadCStringAtVirtualAddress(ApplicationAnalysisContext appContext, ulong address)
    {
        long offset;

        try
        {
            offset = appContext.Binary.MapVirtualAddressToRaw(address, false);
        }
        catch
        {
            return null;
        }

        if (offset <= 0)
            return null;

        var content = appContext.Binary.GetRawBinaryContent();
        var end = offset;

        while (end < content.Length && end - offset < MaxStringLength && content[(int)end] != 0)
        {
            var c = content[(int)end];

            if (c < 32 || c >= 127)
                return null;

            end++;
        }

        if (end == offset || end >= content.Length || content[(int)end] != 0)
            return null;

        var characters = new char[end - offset];

        for (var i = 0; i < characters.Length; i++)
            characters[i] = (char)content[(int)offset + i];

        return new string(characters);
    }
}
