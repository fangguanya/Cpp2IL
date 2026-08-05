using System;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

internal enum Arm64FlagState
{
    None,
    ZeroOnly,
    Comparison
}

public class NewArmV8InstructionSet : Cpp2IlInstructionSet
{
    [ThreadStatic]
    private static Dictionary<Arm64Register, ulong> adrpOffsets = new();

    private static Immediate Imm(long value) => new(value);
    private static Immediate Imm(ulong value) => new(unchecked((long)value));

    internal static bool IsBranchOutsideMethod(ulong target, ulong methodStart, int methodLength)
    {
        if (methodLength < 0)
            throw new ArgumentOutOfRangeException(nameof(methodLength));

        var methodEndExclusive = checked(methodStart + (ulong)methodLength);
        return target < methodStart || target >= methodEndExclusive;
    }

    internal static bool? ShouldInvertZeroFlag(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.EQ => false,
            Arm64ConditionCode.NE => true,
            _ => null
        };
    }

    internal static bool IsScalarLoadMnemonic(Arm64Mnemonic mnemonic)
    {
        return mnemonic is Arm64Mnemonic.LDR
            or Arm64Mnemonic.LDRB
            or Arm64Mnemonic.LDRH
            or Arm64Mnemonic.LDRSW
            or Arm64Mnemonic.LDUR
            or Arm64Mnemonic.LDURH;
    }

    internal static bool IsScalarStoreMnemonic(Arm64Mnemonic mnemonic)
    {
        return mnemonic is Arm64Mnemonic.STR
            or Arm64Mnemonic.STRB
            or Arm64Mnemonic.STRH
            or Arm64Mnemonic.STUR
            or Arm64Mnemonic.STURH;
    }

    internal static bool IsExactlyRepresentableMovi(Arm64OperandKind immediateKind, long immediate)
    {
        return immediateKind == Arm64OperandKind.Immediate && immediate == 0;
    }

    internal static bool IsUnconditionalBranchCode(Arm64ConditionCode conditionCode)
    {
        return conditionCode is Arm64ConditionCode.NONE
            or Arm64ConditionCode.AL
            or Arm64ConditionCode.NV;
    }

    internal static OpCode? GetIndirectBranchOpCode(Arm64Mnemonic mnemonic)
    {
        return mnemonic switch
        {
            Arm64Mnemonic.BLR => OpCode.IndirectCall,
            Arm64Mnemonic.BR => OpCode.IndirectJump,
            _ => null
        };
    }

    internal static OpCode? GetRelationalBranchOpCode(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.GT => OpCode.CheckGreater,
            Arm64ConditionCode.LT => OpCode.CheckLess,
            Arm64ConditionCode.GE => OpCode.CheckGreaterOrEqual,
            Arm64ConditionCode.LE => OpCode.CheckLessOrEqual,
            Arm64ConditionCode.HI => OpCode.CheckGreaterUnsigned,
            Arm64ConditionCode.CC => OpCode.CheckLessUnsigned,
            Arm64ConditionCode.CS => OpCode.CheckGreaterOrEqualUnsigned,
            Arm64ConditionCode.LS => OpCode.CheckLessOrEqualUnsigned,
            _ => null
        };
    }

    internal static OpCode? GetConditionalSetRelationalOpCode(
        Arm64ConditionCode conditionCode,
        Arm64FlagState flagState)
    {
        return flagState == Arm64FlagState.Comparison
            ? GetRelationalBranchOpCode(conditionCode)
            : null;
    }

    internal static bool CanEmitConditionalBranch(
        Arm64ConditionCode conditionCode,
        Arm64FlagState flagState)
    {
        if (flagState == Arm64FlagState.None)
            return false;

        if (ShouldInvertZeroFlag(conditionCode) is not null)
            return true;

        return flagState == Arm64FlagState.Comparison &&
               GetRelationalBranchOpCode(conditionCode) is not null;
    }

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var binary = context.AppContext.Binary;

        // 普通托管方法使用相邻虚拟地址确定动态边界，再由二进制格式映射器得到文件区间。
        if (context is not ConcreteGenericMethodAnalysisContext &&
            Arm64MethodBodyReader.TryReadManagedMethodBody(binary, context.UnderlyingPointer, out var body))
            return body;

        var result = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(binary, context.UnderlyingPointer);
        var lastInsn = result.LastValid();

        var start = (int)binary.MapVirtualAddressToRaw(context.UnderlyingPointer);
        // Map the last instruction (always within segment) and add 4 (ARM64 instruction size).
        // This avoids mapping endVa which may land exactly at a segment boundary gap.
        var end = (int)binary.MapVirtualAddressToRaw(lastInsn.Address) + 4;

        //Sanity check
        if (start < 0 || end < 0 || start >= binary.RawLength || end >= binary.RawLength)
            throw new Exception($"Failed to map virtual address 0x{context.UnderlyingPointer:X} to raw address for method {context!.DeclaringType?.FullName}/{context.Name} - start: 0x{start:X}, end: 0x{end:X} are out of bounds for length {binary.RawLength}.");

        return new BinarySlice(binary, start, end - start);
    }

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        // Is this correct (?)
        return GetArgumentOperandsForCall(context);
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext.Binary, context.UnderlyingPointer);

        if (adrpOffsets == null!) // initializers for ThreadStatic fields only run on the first thread
            adrpOffsets = new();
        else
            adrpOffsets.Clear();

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();
        var flagState = Arm64FlagState.None;

        foreach (var instruction in insns)
            ConvertInstructionStatement(instruction, instructions, addresses, context, ref flagState);

        // fix branches
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode != OpCode.Jump && instruction.OpCode != OpCode.ConditionalJump)
                continue;

            var targetAddress = ((Immediate)instruction.Operands[0]).UnsignedValue;
            var targetIndex = addresses.FindIndex(addr => addr == targetAddress);

            if (targetIndex == -1)
            {
                instruction.OpCode = OpCode.Invalid;
                instruction.SetOperands(new StringLiteral($"Jump target not found in method: 0x{targetAddress:X4}"));
                continue;
            }

            var targetInstruction = instructions[targetIndex];

            instruction.SetOperand(0, targetInstruction);
        }

        adrpOffsets.Clear();
        return instructions;
    }

    private void ConvertInstructionStatement(
        Arm64Instruction instruction,
        List<Instruction> instructions,
        List<ulong> addresses,
        MethodAnalysisContext context,
        ref Arm64FlagState flagState)
    {
        var address = instruction.Address;

        Instruction Add(ulong address, OpCode opCode, params List<IOperand> operands)
        {
            addresses.Add(address);
            var newInstruction = new Instruction(instructions.Count, opCode, operands);
            instructions.Add(newInstruction);
            return newInstruction;
        }
        
        void AddCall(MethodAnalysisContext context, ulong address, ulong target)
        {
            if (!context.AppContext.MethodsByAddress.TryGetValue(target, out var methodsAtAddress))
            {
                // 原生目标的签名尚未解析时保留 X0 返回值与全部 AAPCS64 参数，供后续 key function、
                // 接口分派和委托恢复器统一裁决；未使用的返回值会由死代码消除器删除。
                var unknownCall = Add(
                    address,
                    OpCode.Call,
                    Imm(target),
                    new Register(null, nameof(Arm64Register.X0)));
                unknownCall.AddOperands(Arm64CallingConventionResolver.ResolveForUnmanaged());
                return;
            }

            var calledMethod = methodsAtAddress.Count == 1 ? methodsAtAddress[0] : context;
            var returnRegister = GetReturnRegisterForContext(calledMethod);
            var call = returnRegister == null
                ? Add(address, OpCode.CallVoid, Imm(target))
                : Add(address, OpCode.Call, Imm(target), returnRegister);

            call.AddOperands(GetArgumentOperandsForCall(methodsAtAddress.First()));
        }

        void AddIndirectTransfer(Arm64Mnemonic mnemonic, ulong address, IOperand target)
        {
            var opCode = GetIndirectBranchOpCode(mnemonic)
                         ?? throw new ArgumentOutOfRangeException(nameof(mnemonic));
            var transfer = Add(
                address,
                opCode,
                target,
                new Register(null, nameof(Arm64Register.X0)));
            transfer.AddOperands(Arm64CallingConventionResolver.ResolveForUnmanaged());
        }

        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.MOV:
            case Arm64Mnemonic.MOVZ:
            case Arm64Mnemonic.FMOV:
            case Arm64Mnemonic.SXTW: // move and sign extend Wn to Xd
            case var scalarLoad when IsScalarLoadMnemonic(scalarLoad):
                //Load and move are (dest, src)

                if (instruction.MemIsPreIndexed) //  such as  X8, [X19,#0x30]! 
                {
                    //Regardless of anything else, we're trashing any possible ADRP offsets in the dest here, so let's clear that
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var operate = ConvertOperand(instruction, 1);
                    if (operate is MemoryOperand operand)
                    {
                        var register = (Register)operand.Base!;
                        // X19= X19, #0x30
                        Add(address, OpCode.Add, register, register, Imm(operand.Addend));
                        //X8 = [X19]
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(new Register(null, register.ToString()!.ToUpperInvariant())));
                        break;
                    }
                }

                if (instruction.Op1Kind == Arm64OperandKind.Memory && adrpOffsets.TryGetValue(instruction.MemBase, out var page) && instruction.MemOffset != 0 && instruction.MemAddendReg == Arm64Register.INVALID)
                {
                    //Maybe this is a bit hacky? But I really don't want to write paged load handling into ISIL itself, it's an Arm64 quirk
                    //LDR X0, [X1, #0x1000], where X1 was previously loaded with a page address via an ADRP instruction
                    //We just return the final address, it makes ISIL happier.
                    //TODO check if this is correct
                    var offset = instruction.MemOffset + (long)page;

                    //We're also trashing any possible ADRP offsets in the dest here, so let's clear that now we've possibly grabbed the value if we need it (it's common to store the page and final address in the same register)
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(addend: offset));
                    break;
                }

                //And again here we're trashing any possible ADRP offsets in the dest here, so let's clear that
                if (instruction.Op0Kind == Arm64OperandKind.Register)
                    adrpOffsets.Remove(instruction.Op0Reg);

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertMoveSourceOperand(instruction));
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), ConvertOperand(instruction, 0), Imm(0));
                break;
            case Arm64Mnemonic.MOVN:
                {
                    // dest = ~src

                    //See above re: ADRP offsets
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var temp2 = new Register(null, "TEMP");
                    Add(address, OpCode.Move, temp2, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Not, temp2, temp2);
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), temp2);
                    break;
                }
            case Arm64Mnemonic.MOVI:
                {
                    // 当前ISIL以托管值表达向量清零；其他向量立即数需保留元素复制语义后再接入。
                    if (!IsExactlyRepresentableMovi(instruction.Op1Kind, instruction.Op1Imm))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction MOVI immediate {instruction.Op1Imm} not yet implemented."));
                        break;
                    }

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(0));
                    break;
                }
            case var scalarStore when IsScalarStoreMnemonic(scalarStore):
                //Store is (src, dest)
                Add(address, OpCode.Move, ConvertOperand(instruction, 1), ConvertStoreSourceOperand(instruction));
                break;
            case Arm64Mnemonic.STP:
                // store pair of registers (reg1, reg2, dest)
                {
                    var dest3 = ConvertOperand(instruction, 2);
                    if (dest3 is Register { Name: "X31" }) // if stack
                    {
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 0));
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1));
                    }
                    else if (dest3 is MemoryOperand memory)
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister); // [REG + offset] = REG1
                        memory = new MemoryOperand((Register)memory.Base!, addend: memory.Addend + size);
                        dest3 = memory;
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // [REG + offset + size] = REG2
                    }
                    else // reg pointer
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister);
                        Add(address, OpCode.Add, dest3, dest3, Imm(size));
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1));
                    }
                }
                break;
            case Arm64Mnemonic.ADRP:
                //Just handle as a move
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                var pageAddress = address & ~0xFFFUL;
                adrpOffsets[instruction.Op0Reg] = (ulong)((long)pageAddress + instruction.Op1Imm);
                break;
            case Arm64Mnemonic.LDP when instruction.Op2Kind == Arm64OperandKind.Memory:
                //LDP (dest1, dest2, [mem]) - basically just treat as two loads, with the second offset by the length of the first
                var destRegSize = instruction.Op0Reg switch
                {
                    //vector (128 bit)
                    >= Arm64Register.V0 and <= Arm64Register.V31 => 16, //TODO check if this is accurate
                    //double
                    >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
                    //single
                    >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
                    //half
                    >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
                    //word
                    >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
                    //x
                    >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
                    _ => throw new($"Unknown register size for LDP: {instruction.Op0Reg}")
                };

                var dest1 = ConvertOperand(instruction, 0);
                var dest2 = ConvertOperand(instruction, 1);
                var mem = ConvertOperand(instruction, 2);

                //TODO clean this mess up
                var memInternal = mem as MemoryOperand?;
                var mem2 = new MemoryOperand((Register)memInternal!.Value.Base!, addend: memInternal.Value.Addend + destRegSize);

                Add(address, OpCode.Move, dest1, mem);
                Add(address, OpCode.Move, dest2, mem2);
                break;
            case Arm64Mnemonic.BL:
                AddCall(context, address, instruction.BranchTarget);
                break;
            case Arm64Mnemonic.RET:
                var returnRegister = GetReturnRegisterForContext(context);
                if (returnRegister == null)
                    Add(address, OpCode.Return);
                else
                    Add(address, OpCode.Return, returnRegister);
                break;
            case Arm64Mnemonic.B:
                var target = instruction.BranchTarget;
                var branchConditionCode = instruction.MnemonicConditionCode;

                if (!IsUnconditionalBranchCode(branchConditionCode))
                {
                    if (IsBranchOutsideMethod(target, context.UnderlyingPointer, context.RawBytes.Length))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Conditional branch {branchConditionCode} leaves the current method."));
                        break;
                    }

                    if (!CanEmitConditionalBranch(branchConditionCode, flagState))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Conditional branch {branchConditionCode} has no exactly modeled flag producer."));
                        break;
                    }

                    IOperand branchCondition;
                    var invertZeroFlag = ShouldInvertZeroFlag(branchConditionCode);
                    if (invertZeroFlag is not null)
                    {
                        branchCondition = new Register(null, "Z");
                        if (invertZeroFlag.Value)
                        {
                            var invertedCondition = new Register(null, "BRANCH_CONDITION");
                            Add(address, OpCode.Not, invertedCondition, branchCondition);
                            branchCondition = invertedCondition;
                        }
                    }
                    else
                    {
                        var comparisonOpCode = GetRelationalBranchOpCode(branchConditionCode)!.Value;
                        var comparisonResult = new Register(null, "BRANCH_CONDITION");
                        Add(
                            address,
                            comparisonOpCode,
                            comparisonResult,
                            new Register(null, "FLAG_COMPARE_LEFT"),
                            new Register(null, "FLAG_COMPARE_RIGHT"));
                        branchCondition = comparisonResult;
                    }

                    Add(address, OpCode.ConditionalJump, Imm(target), branchCondition);
                    break;
                }

                if (IsBranchOutsideMethod(target, context.UnderlyingPointer, context.RawBytes.Length))
                {
                    // 方法区间采用左闭右开语义；跳到相邻方法首地址属于尾调用，随后返回当前方法。
                    var returnRegister2 = GetReturnRegisterForContext(context);
                    AddCall(context, address, target);

                    if (returnRegister2 == null)
                        Add(address, OpCode.Return);
                    else
                        Add(address, OpCode.Return, returnRegister2);
                }
                else
                {
                    Add(address, OpCode.Jump, Imm(instruction.BranchTarget));
                }

                break;
            case Arm64Mnemonic.BLR:
            case Arm64Mnemonic.BR:
                // BLR 是带返回地址的间接调用；BR 是不返回当前点的间接尾跳转。
                AddIndirectTransfer(instruction.Mnemonic, address, ConvertOperand(instruction, 0));
                break;
            case Arm64Mnemonic.CBNZ:
            case Arm64Mnemonic.CBZ:
                {
                    // CBZ/CBNZ 不写 NZCV，因此使用独立条件寄存器，保留此前 CMP/SUBS 的标志状态。
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op1Imm);
                    var conditionRegister = new Register(null, "COMPARE_AND_BRANCH_CONDITION");
                    var comparisonOpCode = instruction.Mnemonic == Arm64Mnemonic.CBZ
                        ? OpCode.CheckEqual
                        : OpCode.CheckNotEqual;
                    Add(address, comparisonOpCode, conditionRegister, ConvertOperand(instruction, 0), Imm(0));
                    Add(address, OpCode.ConditionalJump, Imm(targetAddr), conditionRegister);
                }
                break;

            case Arm64Mnemonic.CMP:
            case Arm64Mnemonic.FCMP:
                var compareLeft = new Register(null, "FLAG_COMPARE_LEFT");
                var compareRight = new Register(null, "FLAG_COMPARE_RIGHT");
                Add(address, OpCode.Move, compareLeft, ConvertOperand(instruction, 0));
                Add(address, OpCode.Move, compareRight, ConvertOperand(instruction, 1));
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), compareLeft, compareRight);
                flagState = Arm64FlagState.Comparison;
                break;

            case Arm64Mnemonic.CSET:
                {
                    var destination = ConvertOperand(instruction, 0);
                    var relationalOpCode = GetConditionalSetRelationalOpCode(
                        instruction.FinalOpConditionCode,
                        flagState);
                    if (relationalOpCode is not null)
                    {
                        Add(
                            address,
                            relationalOpCode.Value,
                            destination,
                            new Register(null, "FLAG_COMPARE_LEFT"),
                            new Register(null, "FLAG_COMPARE_RIGHT"));
                        break;
                    }

                    var invertZeroFlag = ShouldInvertZeroFlag(instruction.FinalOpConditionCode);
                    if (invertZeroFlag is null)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CSET condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    var zeroFlag = new Register(null, "Z");
                    if (invertZeroFlag.Value)
                        Add(address, OpCode.Not, destination, zeroFlag);
                    else
                        Add(address, OpCode.Move, destination, zeroFlag);
                    break;
                }

            case Arm64Mnemonic.CSEL:
                {
                    var invertZeroFlag = ShouldInvertZeroFlag(instruction.FinalOpConditionCode);
                    if (invertZeroFlag is null)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CSEL condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    var destination = ConvertOperand(instruction, 0);
                    var trueValue = ConvertOperand(instruction, 1);
                    var falseValue = ConvertOperand(instruction, 2);
                    IOperand condition = new Register(null, "Z");
                    if (invertZeroFlag.Value)
                    {
                        var invertedCondition = new Register(null, "TEMP_CONDITION");
                        Add(address, OpCode.Not, invertedCondition, condition);
                        condition = invertedCondition;
                    }

                    // 条件成立时保留第一个源值并跳过假值写入；目标是下一条 ARM64 指令的首个 ISIL。
                    Add(address, OpCode.Move, destination, trueValue);
                    Add(address, OpCode.ConditionalJump, Imm(address + 4), condition);
                    Add(address, OpCode.Move, destination, falseValue);
                    break;
                }

            case Arm64Mnemonic.TBNZ:
            // TBNZ R<t>, #imm, label
            // test bit and branch if NonZero
            case Arm64Mnemonic.TBZ:
                // TBZ R<t>, #imm, label
                // test bit and branch if Zero
                {
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op2Imm);
                    var bit = 1L << (int)instruction.Op1Imm;
                    var maskedBit = new Register(null, "TEST_BIT_VALUE");
                    var conditionRegister = new Register(null, "TEST_BIT_CONDITION");
                    var src = ConvertOperand(instruction, 0);
                    Add(address, OpCode.And, maskedBit, src, Imm(bit));
                    var comparisonOpCode = instruction.Mnemonic == Arm64Mnemonic.TBZ
                        ? OpCode.CheckEqual
                        : OpCode.CheckNotEqual;
                    Add(address, comparisonOpCode, conditionRegister, maskedBit, Imm(0));
                    Add(address, OpCode.ConditionalJump, Imm(targetAddr), conditionRegister);
                }
                break;
            case Arm64Mnemonic.UBFM:
                // UBFM dest, src, #<immr>, #<imms>
                // dest = (src >> #<immr>) & ((1 << #<imms>) - 1)
                {
                    var dest3 = ConvertOperand(instruction, 0);
                    Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // dest = src
                    Add(address, OpCode.ShiftRight, dest3, dest3, ConvertOperand(instruction, 2)); // dest = dest >> #<immr>
                    var imms = (int)instruction.Op3Imm;
                    Add(address, OpCode.And, dest3, dest3, Imm((1 << imms) - 1)); // dest = dest & constexpr { ((1 << #<imms>) - 1) }
                }
                break;

            case Arm64Mnemonic.MUL:
            case Arm64Mnemonic.FMUL:
                //Multiply is (dest, src1, src2)
                Add(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.ADD:
                // ADD 的扩展寄存器和移位寄存器格式必须先恢复第三操作数语义。
                if (!Arm64AddOperandHelper.TryEmit(
                        instruction,
                        ConvertOperand(instruction, 2),
                        (opCode, operands) => Add(address, opCode, operands),
                        out var addRight))
                {
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction ADD modifier {instruction.FinalOpExtendType}/{instruction.FinalOpShiftType} is not exactly modeled."));
                    break;
                }

                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), addRight);
                break;
            case Arm64Mnemonic.FADD:
                // 浮点加法没有整数扩展寄存器格式。
                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.SUB:
            case Arm64Mnemonic.FSUB:
                //Sub is (dest, src1, src2)
                Add(address, OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.AND:
                //And is (dest, src1, src2)
                Add(address, OpCode.And, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.ADDS:
            case Arm64Mnemonic.SUBS:
            case Arm64Mnemonic.ANDS:
                {
                    var dest = ConvertOperand(instruction, 0);
                    var src1 = ConvertOperand(instruction, 1);
                    var src2 = ConvertOperand(instruction, 2);

                    var opCode = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.ADDS => OpCode.Add,
                        Arm64Mnemonic.SUBS => OpCode.Subtract,
                        Arm64Mnemonic.ANDS => OpCode.And,
                        _ => OpCode.Invalid
                    };

                    if (instruction.Mnemonic == Arm64Mnemonic.SUBS)
                    {
                        // SUBS 的目标寄存器可能覆盖源寄存器，必须在运算前保存比较操作数。
                        var subsCompareLeft = new Register(null, "FLAG_COMPARE_LEFT");
                        var subsCompareRight = new Register(null, "FLAG_COMPARE_RIGHT");
                        Add(address, OpCode.Move, subsCompareLeft, src1);
                        Add(address, OpCode.Move, subsCompareRight, src2);
                        Add(address, opCode, dest, subsCompareLeft, subsCompareRight);
                        flagState = Arm64FlagState.Comparison;
                    }
                    else
                    {
                        Add(address, opCode, dest, src1, src2);
                        flagState = Arm64FlagState.ZeroOnly;
                    }

                    Add(address, OpCode.CheckEqual, new Register(null, "Z"), dest, Imm(0));
                    break;
                }

            case Arm64Mnemonic.ORR:
                //Orr is (dest, src1, src2)
                Add(address, OpCode.Or, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.EOR:
                //Eor (aka xor) is (dest, src1, src2)
                Add(address, OpCode.Xor, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            default:
                Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} not yet implemented."));
                break;
        }
    }

    private IOperand ConvertOperand(Arm64Instruction instruction, int operand)
    {
        var kind = operand switch
        {
            0 => instruction.Op0Kind,
            1 => instruction.Op1Kind,
            2 => instruction.Op2Kind,
            3 => instruction.Op3Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
        };

        if (kind is Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative)
        {
            var imm = operand switch
            {
                0 => instruction.Op0Imm,
                1 => instruction.Op1Imm,
                2 => instruction.Op2Imm,
                3 => instruction.Op3Imm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            if (kind == Arm64OperandKind.ImmediatePcRelative)
                imm += (long)instruction.Address + 4; //Add 4 to the address to get the address of the next instruction (PC-relative addressing is relative to the address of the next instruction, not the current one

            return new Immediate(imm);
        }

        if (kind == Arm64OperandKind.FloatingPointImmediate)
        {
            var imm = operand switch
            {
                0 => instruction.Op0FpImm,
                1 => instruction.Op1FpImm,
                2 => instruction.Op2FpImm,
                3 => instruction.Op3FpImm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            return new DoubleLiteral(imm);
        }

        if (kind == Arm64OperandKind.Register)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            return new Register(null, Arm64RegisterHelper.CanonicalName(reg));
        }

        if (kind == Arm64OperandKind.Memory)
        {
            var reg = instruction.MemBase;
            var offset = instruction.MemOffset;
            var isPreIndexed = instruction.MemIsPreIndexed;

            if (reg == Arm64Register.INVALID)
                //Offset only
                return new MemoryOperand(addend: offset);

            //TODO Handle more stuff here
            return new MemoryOperand(new Register(null, Arm64RegisterHelper.CanonicalName(reg)), addend: offset);
        }

        if (kind == Arm64OperandKind.VectorRegisterElement)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var vectorElement = operand switch
            {
                0 => instruction.Op0VectorElement,
                1 => instruction.Op1VectorElement,
                2 => instruction.Op2VectorElement,
                3 => instruction.Op3VectorElement,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var width = vectorElement.Width switch
            {
                Arm64VectorElementWidth.B => "B",
                Arm64VectorElementWidth.H => "H",
                Arm64VectorElementWidth.S => "S",
                Arm64VectorElementWidth.D => "D",
                _ => throw new ArgumentOutOfRangeException(nameof(vectorElement.Width), $"Unknown vector element width {vectorElement.Width}")
            };

            var name = $"{reg.ToString().ToUpperInvariant()}.{width}{vectorElement.Index}";
            return new Register(null, name);
        }

        return new StringLiteral($"<UNIMPLEMENTED OPERAND TYPE {kind}>");
    }

    private IOperand ConvertMoveSourceOperand(Arm64Instruction instruction)
    {
        if (instruction.Op1Kind == Arm64OperandKind.Register
            && Arm64RegisterHelper.IsZeroRegister(instruction.Op1Reg))
            return Imm(0);

        return ConvertOperand(instruction, 1);
    }

    private IOperand ConvertStoreSourceOperand(Arm64Instruction instruction)
    {
        if (instruction.Op0Kind == Arm64OperandKind.Register
            && Arm64RegisterHelper.IsZeroRegister(instruction.Op0Reg))
            return Imm(0);

        return ConvertOperand(instruction, 0);
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new NewArm64KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context) => context.RawBytes.Length <= 0 ? "" : string.Join("\n", Disassembler.Disassemble(context.RawBytes.AsSpan(), context.UnderlyingPointer, new Disassembler.Options(true, true, false)).ToList());

    private IOperand? GetReturnRegisterForContext(MethodAnalysisContext context)
    {
        var returnType = context.ReturnType;
        if (returnType.Namespace == nameof(System))
        {
            return returnType.Name switch
            {
                "Void" => null, //Void is no return
                "Double" => new Register(null, nameof(Arm64Register.V0)), //Builtin double is v0
                "Single" => new Register(null, nameof(Arm64Register.V0)), //Builtin float is v0
                _ => new Register(null, nameof(Arm64Register.X0)), //All other system types are x0 like any other pointer
            };
        }

        //TODO Do certain value types have different return registers?

        //Any user type is returned in x0
        return new Register(null, nameof(Arm64Register.X0));
    }

    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingCalled)
    {
        var vectorCount = 0;
        var nonVectorCount = 0;

        var ret = new List<IOperand>();

        //Handle 'this' if it's an instance method
        if (!contextBeingCalled.IsStatic)
        {
            ret.Add(new Register(null, nameof(Arm64Register.X0)));
            nonVectorCount++;
        }

        foreach (var parameter in contextBeingCalled.Parameters)
        {
            var paramType = parameter.ParameterType;
            if (paramType.Namespace == nameof(System))
            {
                switch (paramType.Name)
                {
                    case "Single":
                    case "Double":
                        ret.Add(new Register(null, (Arm64Register.V0 + vectorCount++).ToString().ToUpperInvariant()));
                        break;
                    default:
                        ret.Add(new Register(null, (Arm64Register.X0 + nonVectorCount++).ToString().ToUpperInvariant()));
                        break;
                }
            }
            else
            {
                ret.Add(new Register(null, (Arm64Register.X0 + nonVectorCount++).ToString().ToUpperInvariant()));
            }
        }

        return ret;
    }
    
    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingAnalyzed, ulong callAddr)
    {
        if (!contextBeingAnalyzed.AppContext.MethodsByAddress.TryGetValue(callAddr, out var methodsAtAddress))
            //TODO
            return [];

        return GetArgumentOperandsForCall(methodsAtAddress.First());
    }
}
