using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static class IlGenerator
{
    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        var assembly = context.DeclaringType!.DeclaringAssembly;
        var module = definition.DeclaringModule!;
        var importer = module.DefaultImporter;
        var factory = module.CorLibTypeFactory;

        var writeLine = factory.CorLibScope
            .CreateTypeReference("System", "Console")
            .CreateMemberReference("WriteLine", MethodSignature.CreateStatic(factory.Void, [factory.String]))
            .ImportWith(importer);

        var stringType = factory.CorLibScope.CreateTypeReference("System", "String");
        var stringCtor = stringType
            .CreateMemberReference(".ctor", MethodSignature.CreateStatic(stringType.ToTypeSignature(false), [factory.String]))
            .ImportWith(importer);

        // Change branch targets to instructions
        foreach (var instruction in context.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Block target)
            {
                if (target.Instructions.Count > 0)
                    instruction.SetOperand(0, target.Instructions[0]);
            }
        }

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = false // There's stack imbalance somewhere, but this works for now
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in context.ControlFlowGraph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            var elementOperand = operand is AddressOf { Target: ArrayAccess elementAddress } ? elementAddress : operand;

            if (elementOperand is ArrayAccess arrayAccess)
            {
                local = arrayAccess.Array;

                if (arrayAccess.Index is LocalVariable index && !context.Locals.Contains(index))
                    context.Locals.Add(index);
            }

            if (operand is ArrayLength arrayLength)
                local = arrayLength.Array;

            if (operand is StringLength stringLength)
                local = stringLength.Value;

            if (operand is ListCount listCount)
                local = listCount.Value;

            if (operand is AddressOf { Target: LocalVariable addressed })
                local = addressed;

            if (operand is HomogeneousFloatingAggregateArgument aggregate)
            {
                foreach (var componentLocal in aggregate.Components.OfType<LocalVariable>())
                    if (!context.Locals.Contains(componentLocal))
                        context.Locals.Add(componentLocal);
            }

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        // Map ISIL locals to IL
        Dictionary<LocalVariable, CilLocalVariable> locals = [];
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // Use object if type couldn't be determined
            if (local.Type != null)
                ilType = local.Type.ToTypeSignature(module);
            else
                ilType = module.CorLibTypeFactory.Object;

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        /* foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            body.Instructions.Add(CilOpCodes.Ldstr, instruction.ToString());
            body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!));
        }
        body.Instructions.Add(CilOpCodes.Ldstr, "-------------------------------------------------------------------------");
        body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!)); */

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            if (block == context.ControlFlowGraph.EntryBlock || block == context.ControlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals, writeLine, stringCtor);
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, context.ControlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
                if (falseSuccessor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, falseSuccessor));
            }

            else if (lastInstruction.OpCode != OpCode.Jump && lastInstruction.OpCode != OpCode.Return && lastInstruction.OpCode != OpCode.IndirectJump)
            {
                var successor = block.Successors.FirstOrDefault(s => s != context.ControlFlowGraph.ExitBlock);
                if (successor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, successor));
            }
        }
        // Set IL branch targets
        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                if (instruction.Operands[0] is Block targetBlock)
                {
                    // SSA 临界边可把原始目标改写为空块；必须穿透空块，不能把条件分支降为 nop 而泄漏栈值。
                    var targetInstruction = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
                    if (targetInstruction == null)
                        throw new DecompilerException($"无法解析分支目标块：{instruction} ({targetBlock})");

                    ilBranch.Operand = new CilInstructionLabel(targetInstruction);
                    continue;
                }

                var target = (Instruction)instruction.Operands[0];

                if (!instructionMap.ContainsKey(target))
                    throw new DecompilerException($"分支目标不在 ISIL 到 CIL 映射中：{instruction} --- {target}");

                ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);
            }
        }
        
        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
            {
                context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
                branchInstruction.OpCode = CilOpCodes.Nop;
                branchInstruction.Operand = null;
                continue;
            }

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        // Add analysis warnings
        var instructions = body.Instructions;
        foreach (var warning in context.AnalysisWarnings)
        {
            instructions.Add(CilOpCodes.Ldstr, "Warning: " + warning);
            instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
        }

        // 分析警告会被附加到方法尾部；若 void 方法尾部仍可顺序落出，则补齐合法的 CIL 返回终结点。
        // 这里仅修复方法体结构，不把未知的原生异常尾语义标记为已经恢复。
        if (RequiresTerminalReturn(context.IsVoid, instructions.LastOrDefault()))
            instructions.Add(CilOpCodes.Ret);
    }

    internal static bool RequiresTerminalReturn(bool isVoidMethod, CilInstruction? finalInstruction)
    {
        if (!isVoidMethod)
            return false;

        if (finalInstruction == null)
            return true;

        var opCode = finalInstruction.OpCode;
        return opCode != CilOpCodes.Ret
               && opCode != CilOpCodes.Throw
               && opCode != CilOpCodes.Rethrow
               && opCode != CilOpCodes.Br
               && opCode != CilOpCodes.Br_S
               && opCode != CilOpCodes.Leave
               && opCode != CilOpCodes.Leave_S
               && opCode != CilOpCodes.Jmp
               && opCode != CilOpCodes.Endfinally
               && opCode != CilOpCodes.Endfilter;
    }
    
    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    internal static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap, HashSet<Block>? visited = null)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        visited ??= [];
        if (!visited.Add(block))
            return null;

        foreach (var successor in block.Successors)
        {
            var resolved = ResolveBlockEntryInstruction(successor, blockEntryMap, visited);
            if (resolved != null)
                return resolved;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;
        var factory = module.CorLibTypeFactory;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                instructions.Add(CilOpCodes.Ldstr, $"Invalid instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.NotImplemented:
                instructions.Add(CilOpCodes.Ldstr, $"Not implemented instruction: {instruction.Operands[0]}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Interrupt:
            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    if (!field.Field.IsStatic)
                        LoadLocal(field.Local, method, locals);

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, field.Field.FieldType);
                    instructions.Add(field.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, field.Field.ToFieldDescriptor(module));
                    break;
                }

                // stelem needs array and index before the value, so like stfld it can't go through LoadOperand/StoreToOperand.
                // This also lets ILSpy handle it as a proper array initializer
                if (instruction.Operands[0] is ArrayAccess { Array.Type: SzArrayTypeAnalysisContext { ElementType: { } stored } } target)
                {
                    LoadLocal(target.Array, method, locals);
                    LoadOperand(target.Index, method, locals, writeLine, stringCtor);
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, stored);
                    instructions.Add(CilOpCodes.Stelem, importer.ImportTypeSignature(stored.ToTypeSignature(module)).ToTypeDefOrRef());
                    break;
                }

                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, DestinationType(instruction.Operands[0]));
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.NewArr:
                if (instruction.Operands is [_, SzArrayTypeAnalysisContext { ElementType: { } newArrayElement }, { } length])
                {
                    LoadOperand(length, method, locals, writeLine, stringCtor);
                    instructions.Add(CilOpCodes.Newarr, importer.ImportTypeSignature(newArrayElement.ToTypeSignature(module)).ToTypeDefOrRef());
                }
                else
                    instructions.Add(CilOpCodes.Ldnull);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj.
                // If we can't, just fall back to an Ldnull.
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    // 构造器融合和普通构造调用必须共用同一套实参装载规则；分别切片会在最后一个
                    // 可选形参落到栈上时少压一个值，最终把生产者错误延迟成 newobj 栈不平衡。
                    LoadCallParameters(
                        constructorCall.Operands,
                        2,
                        constructor,
                        method,
                        locals,
                        writeLine,
                        stringCtor);

                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor.ToMethodDescriptor(module)));
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    constructorCall.OpCode = OpCode.Nop;
                    constructorCall.SetOperands();
                }
                else
                {
                    if (DestinationType(instruction.Operands[0]) is { } destinationType)
                        PushDefaultOf(destinationType, instructions);
                    else
                        instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                break;

            case OpCode.Box:
                if (instruction.Operands is not
                    [{ } boxDestination, { } boxedValue, TypeAnalysisContext { IsValueType: true } boxedType])
                    throw new InvalidOperationException($"Box指令操作数不完整: {instruction}");

                LoadOperand(boxedValue, method, locals, writeLine, stringCtor, boxedType);
                instructions.Add(
                    CilOpCodes.Box,
                    importer.ImportTypeSignature(boxedType.ToTypeSignature(module)).ToTypeDefOrRef());
                StoreToOperand(boxDestination, method, locals, writeLine);
                break;

            case OpCode.Unbox:
                if (instruction.Operands is not
                    [{ } unboxDestination, { } boxedObject, TypeAnalysisContext unboxedType]
                    || !unboxedType.IsValueType && unboxedType is not GenericParameterTypeAnalysisContext)
                    throw new InvalidOperationException($"Unbox指令操作数不完整: {instruction}");

                LoadOperand(boxedObject, method, locals, writeLine, stringCtor, context.AppContext.SystemTypes.SystemObjectType);
                instructions.Add(
                    CilOpCodes.Unbox_Any,
                    importer.ImportTypeSignature(unboxedType.ToTypeSignature(module)).ToTypeDefOrRef());
                StoreToOperand(unboxDestination, method, locals, writeLine);
                break;

            case OpCode.CastClass:
                if (instruction.Operands is not
                    [{ } castDestination, { } castSource, TypeAnalysisContext { IsValueType: false } castType])
                    throw new InvalidOperationException($"CastClass指令操作数不完整: {instruction}");

                LoadOperand(castSource, method, locals, writeLine, stringCtor);
                instructions.Add(
                    CilOpCodes.Castclass,
                    importer.ImportTypeSignature(castType.ToTypeSignature(module)).ToTypeDefOrRef());
                StoreToOperand(castDestination, method, locals, writeLine);
                break;

            case OpCode.IsInst:
                if (instruction.Operands is not
                    [{ } isInstDestination, { } isInstSource, TypeAnalysisContext { IsValueType: false } isInstType])
                    throw new InvalidOperationException($"IsInst指令操作数不完整: {instruction}");

                LoadOperand(isInstSource, method, locals, writeLine, stringCtor);
                instructions.Add(
                    CilOpCodes.Isinst,
                    importer.ImportTypeSignature(isInstType.ToTypeSignature(module)).ToTypeDefOrRef());
                StoreToOperand(isInstDestination, method, locals, writeLine);
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType]
                    && exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0) is { } exceptionCtor)
                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(exceptionCtor.ToMethodDescriptor(module)));
                else
                    instructions.Add(CilOpCodes.Ldnull);

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                instructions.Add(CilOpCodes.Ldstr, $"Phi opcodes should not exist at this point in decompilation ({instruction})");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                {
                    if (instruction.Operands[0] is Immediate targetAddress)
                        instructions.Add(CilOpCodes.Ldstr, $"Method not found @{targetAddress.UnsignedValue:X}");
                    else // Probably key function
                        instructions.Add(CilOpCodes.Ldstr, $"Unknown call target operand: {instruction}");

                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    break;
                }

                var importedMethod = importer.ImportMethod(targetMethod.ToMethodDescriptor(module));

                // IL2CPP 会把对象分配与构造器调用拆成两个原生调用。若分析阶段只恢复了后一个调用，
                // 则对非 this 局部变量直接调用 .ctor 会生成不可由 C# 表达的伪成员；这里恢复成 newobj
                // 并把新实例写回原局部变量。this 上的基类构造调用仍沿用普通 call。
                if (instruction.OpCode == OpCode.CallVoid
                    && targetMethod.Name == ".ctor"
                    && instruction.Operands.Count > 1
                    && instruction.Operands[1] is LocalVariable { IsThis: false } constructedObject)
                {
                    LoadCallParameters(instruction.Operands, 2, targetMethod, method, locals, writeLine, stringCtor);
                    instructions.Add(CilOpCodes.Newobj, importedMethod);
                    StoreToOperand(constructedObject, method, locals, writeLine);
                    break;
                }

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
                GenericParameterTypeAnalysisContext? constrainedReceiver = null;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    if ((instruction.Operands.Count - 1) >= thisParamIndex)
                    {
                        var receiverOperand = instruction.Operands[thisParamIndex];
                        constrainedReceiver = ConstrainedReceiverType(receiverOperand);
                        if (constrainedReceiver != null && receiverOperand is LocalVariable receiverLocal)
                            LoadLocalAddress(receiverLocal, method, locals);
                        else
                            LoadOperand(receiverOperand, method, locals, writeLine, stringCtor, targetMethod.DeclaringType);
                    }
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr, $"Non static method called without 'this' param ({instruction})");
                        instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                        instructions.Add(CilOpCodes.Ldnull);
                    }
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // A call whose target was only identified after lifting still carries the operands the
                // unknown-callee convention gave it, which may be fewer than the method actually takes.
                // The stack still has to match the signature, so anything missing gets a placeholder.
                LoadCallParameters(instruction.Operands, callParamIndex, targetMethod, method, locals, writeLine, stringCtor);

                if (constrainedReceiver != null)
                {
                    // 对未知 T 的实例虚调用必须同时覆盖引用类型与值类型：地址接收者配合 constrained.
                    // 让 CLR 在运行时选择直接值类型实现或对象虚分派，不引入装箱后的错误 this 身份。
                    instructions.Add(
                        CilOpCodes.Constrained,
                        importer.ImportTypeSignature(constrainedReceiver.ToTypeSignature(module)).ToTypeDefOrRef());
                    instructions.Add(CilOpCodes.Callvirt, importedMethod);
                }
                else
                    instructions.Add(CilOpCodes.Call, importedMethod);

                if (instruction.OpCode == OpCode.Call) // Store return value
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                else if (!targetMethod.IsVoid)
                {
                    // ARM64调用结果只在寄存器仍有后续读取时才会提升为Call；结果未使用时ISIL
                    // 保持CallVoid。托管call仍按真实签名压入返回值，必须显式丢弃，否则该值会
                    // 穿过后续基本块残留到ret并破坏整条控制流的求值栈。
                    instructions.Add(CilOpCodes.Pop);
                }

                break;

            case OpCode.IndirectCall:
                instructions.Add(CilOpCodes.Ldstr, $"Indirect call: {instruction} (should have been resolved before IL gen)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Return:
                if (!context.IsVoid)
                {
                    if (instruction.Operands.Count == 1)
                        LoadOperand(instruction.Operands[0], method, locals, writeLine, stringCtor, context.ReturnType);
                    else if (TryEmitHomogeneousFloatingAggregateReturn(
                                 instruction,
                                 context,
                                 method,
                                 locals,
                                 writeLine,
                                 stringCtor))
                    {
                        // 聚合体已由各个 V 返回寄存器重组并压入求值栈。
                    }
                    else
                        instructions.Add(CilOpCodes.Ldnull); // ret still pops a value even if we lost track of it
                }
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.ConditionalSelect:
                EmitConditionalSelect(
                    instruction,
                    method,
                    locals,
                    writeLine,
                    stringCtor);
                break;

            case OpCode.MaximumNumber:
                EmitMaximumNumber(
                    instruction,
                    method,
                    locals,
                    writeLine,
                    stringCtor);
                break;

            case OpCode.AbsoluteNumber:
            case OpCode.AbsoluteDifference:
                EmitFloatingAbsolute(
                    instruction,
                    method,
                    locals,
                    writeLine,
                    stringCtor);
                break;

            case OpCode.IndirectJump:
                instructions.Add(CilOpCodes.Ldstr, $"Indirect jump: {instruction} (should have been resolved before IL gen)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.ShiftStack:
                instructions.Add(CilOpCodes.Ldstr, $"Stack shift: {instruction} (stack analysis should have removed these)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:
            case OpCode.CheckGreaterUnsigned:
            case OpCode.CheckLessUnsigned:
            case OpCode.CheckGreaterOrEqualUnsigned:
            case OpCode.CheckLessOrEqualUnsigned:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                if (TryEmitNullablePresenceTest(instruction, method, locals))
                {
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

                // 二元运算的零值或元数据类型操作数必须按另一侧的已知槽位类型发射。
                // 否则运行时句柄会被写成ldnull，托管引用的零值又会被写成整数0。
                var leftExpectedType = BinaryOperandRepresentationType(instruction.Operands[2], context);
                var rightExpectedType = BinaryOperandRepresentationType(instruction.Operands[1], context);
                LoadOperand(
                    instruction.Operands[1],
                    method,
                    locals,
                    writeLine,
                    stringCtor,
                    leftExpectedType);
                LoadOperand(
                    instruction.Operands[2],
                    method,
                    locals,
                    writeLine,
                    stringCtor,
                    rightExpectedType);

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;
                    case OpCode.CheckGreaterUnsigned: instructions.Add(CilOpCodes.Cgt_Un); break;
                    case OpCode.CheckLessUnsigned: instructions.Add(CilOpCodes.Clt_Un); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a >= b  ==  !(a < b)
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    // 无符号 a >= b 等价于 !(a < b)。
                    case OpCode.CheckGreaterOrEqualUnsigned:
                        instructions.Add(CilOpCodes.Clt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    // 无符号 a <= b 等价于 !(a > b)。
                    case OpCode.CheckLessOrEqualUnsigned:
                        instructions.Add(CilOpCodes.Cgt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;

                    case OpCode.ShiftLeft: instructions.Add(CilOpCodes.Shl); break;
                    case OpCode.ShiftRight: instructions.Add(CilOpCodes.Shr); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);

                if (instruction.OpCode == OpCode.Negate)
                    instructions.Add(CilOpCodes.Neg);
                else if (IsBoolean(instruction.Operands[1], context))
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Ceq);
                }
                else
                    instructions.Add(CilOpCodes.Not);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.ConvertFloatingPointPrecision:
            case OpCode.ConvertFloatToSignedInteger:
            case OpCode.ConvertSignedIntegerToFloat:
            case OpCode.RoundFloatTowardPositiveInfinity:
            case OpCode.RoundFloatTowardNegativeInfinity:
                {
                    if (instruction.Operands.Count < 3 || instruction.Operands[2] is not Immediate destinationWidth)
                        throw new InvalidOperationException($"数值转换指令缺少目标位宽：{instruction}");

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    switch (instruction.OpCode)
                    {
                        case OpCode.ConvertFloatingPointPrecision:
                            instructions.Add(destinationWidth.Value switch
                            {
                                32 => CilOpCodes.Conv_R4,
                                64 => CilOpCodes.Conv_R8,
                                _ => throw new InvalidOperationException($"浮点目标位宽无效：{destinationWidth.Value}"),
                            });
                            break;

                        case OpCode.ConvertFloatToSignedInteger:
                            instructions.Add(destinationWidth.Value switch
                            {
                                32 => CilOpCodes.Conv_I4,
                                64 => CilOpCodes.Conv_I8,
                                _ => throw new InvalidOperationException($"整数目标位宽无效：{destinationWidth.Value}"),
                            });
                            break;

                        case OpCode.ConvertSignedIntegerToFloat:
                            instructions.Add(destinationWidth.Value switch
                            {
                                32 => CilOpCodes.Conv_R4,
                                64 => CilOpCodes.Conv_R8,
                                _ => throw new InvalidOperationException($"浮点目标位宽无效：{destinationWidth.Value}"),
                            });
                            break;

                        case OpCode.RoundFloatTowardPositiveInfinity:
                        case OpCode.RoundFloatTowardNegativeInfinity:
                            if (destinationWidth.Value is not (32 or 64))
                                throw new InvalidOperationException($"舍入浮点位宽无效：{destinationWidth.Value}");

                            if (destinationWidth.Value == 32)
                                instructions.Add(CilOpCodes.Conv_R8);

                            var methodName = instruction.OpCode == OpCode.RoundFloatTowardPositiveInfinity
                                ? "Ceiling"
                                : "Floor";
                            var mathMethod = factory.CorLibScope
                                .CreateTypeReference("System", "Math")
                                .CreateMemberReference(
                                    methodName,
                                    MethodSignature.CreateStatic(factory.Double, [factory.Double]))
                                .ImportWith(importer);
                            instructions.Add(CilOpCodes.Call, mathMethod);

                            if (destinationWidth.Value == 32)
                                instructions.Add(CilOpCodes.Conv_R4);
                            break;
                    }

                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

            case OpCode.ReinterpretIntegerBitsAsFloat:
            case OpCode.ReinterpretFloatBitsAsInteger:
                {
                    if (instruction.Operands.Count != 3
                        || instruction.Operands[2] is not Immediate { Value: 32 or 64 } width)
                        throw new InvalidOperationException($"标量位重解释指令参数无效：{instruction}");

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
                    var descriptor = instruction.OpCode switch
                    {
                        OpCode.ReinterpretIntegerBitsAsFloat when width.Value == 32 =>
                            bitConverter.CreateMemberReference(
                                "Int32BitsToSingle",
                                MethodSignature.CreateStatic(factory.Single, [factory.Int32])),
                        OpCode.ReinterpretIntegerBitsAsFloat =>
                            bitConverter.CreateMemberReference(
                                "Int64BitsToDouble",
                                MethodSignature.CreateStatic(factory.Double, [factory.Int64])),
                        OpCode.ReinterpretFloatBitsAsInteger when width.Value == 32 =>
                            bitConverter.CreateMemberReference(
                                "SingleToInt32Bits",
                                MethodSignature.CreateStatic(factory.Int32, [factory.Single])),
                        OpCode.ReinterpretFloatBitsAsInteger =>
                            bitConverter.CreateMemberReference(
                                "DoubleToInt64Bits",
                                MethodSignature.CreateStatic(factory.Int64, [factory.Double])),
                        _ => throw new ArgumentOutOfRangeException(nameof(instruction.OpCode)),
                    };
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(descriptor));
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

            case OpCode.VectorAllLanesPredicate:
                {
                    if (instruction.Operands.Count != 8
                        || instruction.Operands[3] is not Immediate firstConstant
                        || instruction.Operands[4] is not Immediate secondConstant
                        || instruction.Operands[5] is not Immediate thirdConstant
                        || instruction.Operands[6] is not Immediate fourthConstant
                        || instruction.Operands[7] is not Immediate reverseLaneMask)
                    {
                        throw new InvalidOperationException($"向量全通道谓词参数无效：{instruction}");
                    }

                    var constants = new[]
                    {
                        firstConstant.Value,
                        secondConstant.Value,
                        thirdConstant.Value,
                        fourthConstant.Value,
                    };
                    for (var lane = 0; lane < constants.Length; lane++)
                    {
                        // 先读取累计H通道的最低位；Shr_Un避免最高通道受符号扩展影响。
                        LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                        if (lane != 0)
                        {
                            instructions.Add(CilOpCodes.Ldc_I4, lane * 16);
                            instructions.Add(CilOpCodes.Shr_Un);
                        }
                        instructions.Add(CilOpCodes.Ldc_I8, 1L);
                        instructions.Add(CilOpCodes.And);
                        instructions.Add(CilOpCodes.Ldc_I8, 0L);
                        instructions.Add(CilOpCodes.Cgt_Un);

                        // 正常通道为constant > scalar，掩码指定的通道使用scalar > constant。
                        LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                        instructions.Add(CilOpCodes.Ldc_I4, checked((int)constants[lane]));
                        instructions.Add((reverseLaneMask.Value & (1L << lane)) != 0
                            ? CilOpCodes.Cgt
                            : CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Or);

                        if (lane != 0)
                            instructions.Add(CilOpCodes.And);
                    }

                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

            case OpCode.VectorExtractUnsignedInt16:
                {
                    if (instruction.Operands.Count != 3
                        || instruction.Operands[2] is not Immediate { Value: >= 0 and < 4 } laneIndex)
                    {
                        throw new InvalidOperationException($"向量半字提取参数无效：{instruction}");
                    }

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    if (laneIndex.Value != 0)
                    {
                        instructions.Add(CilOpCodes.Ldc_I4, checked((int)laneIndex.Value * 16));
                        instructions.Add(CilOpCodes.Shr_Un);
                    }
                    instructions.Add(CilOpCodes.Ldc_I8, 0xFFFFL);
                    instructions.Add(CilOpCodes.And);
                    instructions.Add(CilOpCodes.Conv_I4);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Unknown instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
        }

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            var index = block.Instructions.IndexOf(newobj);
            if (index < 0)
                continue;

            for (var i = index + 1; i < block.Instructions.Count; i++)
            {
                var candidate = block.Instructions[i];
                if (candidate is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, _, ..] }
                    && ReferenceEquals(candidate.Operands[1], newObject))
                    return candidate;
            }

            return null;
        }

        return null;
    }

    private static void LoadOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor,
        TypeAnalysisContext? expectedType = null)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        // A null reference reaches us as an integer zero, which would otherwise be emitted as a literal 0
        // and read back as a cast from a number.
        if (expectedType is { IsValueType: false } && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        if (expectedType is { IsValueType: true } valueType
            && IsZeroConstant(operand)
            && RequiresInitializedValueType(valueType))
        {
            EmitInitializedValueType(valueType, method);
            return;
        }

        // ARM64的FCMP允许直接使用浮点零，ISIL仍以整数Immediate保存该编码值；
        // 二元运算另一侧已给出Single/Double时，必须按同一浮点域入栈，而不是发射I4零。
        if (operand is Immediate floatingImmediate
            && expectedType?.FullName is "System.Single" or "System.Double")
        {
            if (expectedType.FullName == "System.Single")
                instructions.Add(CilOpCodes.Ldc_R4, (float)floatingImmediate.Value);
            else
                instructions.Add(CilOpCodes.Ldc_R8, (double)floatingImmediate.Value);
            return;
        }

        switch (operand)
        {
            case Immediate { Value: >= int.MinValue and <= int.MaxValue } immediate:
                instructions.Add(CilOpCodes.Ldc_I4, (int)immediate.Value);
                break;
            case Immediate immediate:
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
                break;
            case FloatLiteral f:
                instructions.Add(CilOpCodes.Ldc_R4, f.Value);
                break;
            case DoubleLiteral d:
                instructions.Add(CilOpCodes.Ldc_R8, d.Value);
                break;
            case StringLiteral s:
                instructions.Add(CilOpCodes.Ldstr, s.Value);
                break;
            case HomogeneousFloatingAggregateArgument aggregate:
                EmitHomogeneousFloatingAggregateArgument(
                    aggregate,
                    method,
                    locals,
                    writeLine,
                    stringCtor,
                    expectedType);
                break;
            case LocalVariable local:
                LoadLocal(local, method, locals);
                if (ShouldBoxGenericArgument(local, expectedType))
                {
                    // 泛型实参传给 object 或接口形参时，box T 对引用类型保持原引用、对值类型执行真实装箱。
                    instructions.Add(
                        CilOpCodes.Box,
                        importer.ImportTypeSignature(local.Type!.ToTypeSignature(module)).ToTypeDefOrRef());
                }
                break;
            case ArrayLength arrayLength:
                LoadLocal(arrayLength.Array, method, locals);
                instructions.Add(CilOpCodes.Ldlen);
                instructions.Add(CilOpCodes.Conv_I4);
                break;
            case StringLength stringLength:
                LoadLocal(stringLength.Value, method, locals);
                var stringType = module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "String");
                if (stringLength.Value.Type?.FullName != "System.String")
                {
                    // 控制流合并可能把接收者类型擦除为object；公开属性调用前恢复精确的字符串栈类型。
                    instructions.Add(CilOpCodes.Castclass, importer.ImportType(stringType));
                }
                var lengthGetter = new MemberReference(
                    stringType,
                    "get_Length",
                    MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
                // callvirt同时保持原生字段解引用在空接收者上的NullReferenceException语义。
                instructions.Add(CilOpCodes.Callvirt, importer.ImportMethod(lengthGetter));
                break;
            case ListCount listCount:
                LoadLocal(listCount.Value, method, locals);
                var concreteListType = importer.ImportTypeSignature(listCount.ListType.ToTypeSignature(module));
                var concreteListTypeReference = concreteListType.ToTypeDefOrRef();
                if (listCount.Value.Type?.FullName != listCount.ListType.FullName)
                {
                    // 控制流合并可能擦除接收者类型；调用公开属性前恢复具体List<T>栈类型。
                    instructions.Add(CilOpCodes.Castclass, concreteListTypeReference);
                }
                var countGetter = new MemberReference(
                    concreteListTypeReference,
                    "get_Count",
                    MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
                // callvirt保持私有字段解引用在空接收者上的NullReferenceException语义。
                instructions.Add(CilOpCodes.Callvirt, importer.ImportMethod(countGetter));
                break;
            case AddressOf { Target: LocalVariable addressed }:
                if (expectedType is { IsValueType: false }
                    && expectedType is not (
                        ByRefTypeAnalysisContext
                        or PointerTypeAnalysisContext
                        or RuntimeClassTypeAnalysisContext
                        or RuntimeMethodInfoAnalysisContext
                        or StaticFieldStorageTypeAnalysisContext
                        or RgctxTableTypeAnalysisContext)
                    && addressed.Type is { IsValueType: false }
                    && addressed.Type is not (
                        ByRefTypeAnalysisContext
                        or PointerTypeAnalysisContext
                        or RuntimeClassTypeAnalysisContext
                        or RuntimeMethodInfoAnalysisContext
                        or StaticFieldStorageTypeAnalysisContext
                        or RgctxTableTypeAnalysisContext))
                {
                    // 中文注释：托管引用槽被错误提升为地址时，恢复原引用而非发射object*。
                    LoadLocal(addressed, method, locals);
                    break;
                }
                instructions.Add(CilOpCodes.Ldloca, locals[addressed]);
                break;
            case AddressOf { Target: ArrayAccess elementAddress }:
                LoadLocal(elementAddress.Array, method, locals);
                LoadOperand(elementAddress.Index, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Ldelema,
                    importer.ImportTypeSignature(((SzArrayTypeAnalysisContext)elementAddress.Array.Type!).ElementType.ToTypeSignature(module)).ToTypeDefOrRef());
                break;
            case ArrayAccess arrayAccess:
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Ldelem,
                    importer.ImportTypeSignature(((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType.ToTypeSignature(module)).ToTypeDefOrRef());
                break;
            case FieldReference field:
                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor(module));
                    break;
                }

                LoadFieldReceiver(field, method, locals);
                instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor(module));
                if (ShouldBoxGenericArgument(field, expectedType))
                {
                    // 泛型结构字段传入object或接口时必须按字段声明类型装箱。
                    instructions.Add(
                        CilOpCodes.Box,
                        importer.ImportTypeSignature(field.Field.FieldType.ToTypeSignature(module)).ToTypeDefOrRef());
                }
                break;
            case MemoryOperand memory:
                if (expectedType is RuntimeClassTypeAnalysisContext
                    && memory.Base is LocalVariable
                    {
                        Type: { IsValueType: false } baseType
                    }
                    && baseType is not RuntimeClassTypeAnalysisContext)
                {
                    // 中文注释：Il2CppClass布局探针不是托管实例字段，禁止把IEnumerator等引用压入nint槽。
                    PushDefaultOf(expectedType, instructions);
                    break;
                }
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    LoadLocal(local2, method, locals);
                    if (local2.Type is ByRefTypeAnalysisContext { ElementType: { } byRefElementType })
                    {
                        // ref/out 参数寄存器保存的是托管地址；读取 [参数] 必须解引用元素，不能把地址交给算术指令。
                        var importedElementType = importer.ImportTypeSignature(byRefElementType.ToTypeSignature(module));
                        instructions.Add(CilOpCodes.Ldobj, importedElementType.ToTypeDefOrRef());
                    }
                    break;
                }
                instructions.Add(CilOpCodes.Ldstr, "Unmanaged memory load: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, importer.ImportMethod(runtimeMethod.RepresentedMethod.ToMethodDescriptor(module)));
                    break;
                }

                //Not fully implemented, these basically shouldn't actually ever exist in the final IL.
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case TypeAnalysisContext type:
                if (expectedType?.FullName == "System.RuntimeTypeHandle")
                {
                    // typeof(T) 作为 RuntimeTypeHandle 实参时必须生成 ldtoken T；构造 T 会改变
                    // 原始语义，并会让抽象类型、私有构造器类型与无默认构造器类型产生非法 CIL。
                    var tokenType = importer
                        .ImportTypeSignature(type.ToTypeSignature(module))
                        .ToTypeDefOrRef();
                    instructions.Add(CilOpCodes.Ldtoken, tokenType);
                    break;
                }

                if (expectedType is RuntimeClassTypeAnalysisContext
                    or StaticFieldStorageTypeAnalysisContext
                    or RgctxTableTypeAnalysisContext
                    or RuntimeMethodInfoAnalysisContext
                    || expectedType?.FullName is "System.IntPtr" or "System.UIntPtr")
                {
                    // 中文注释：托管类型操作数落入原生整数槽时，它表示未知的原生地址载体；
                    // 生成原生零值，避免把托管构造器伪装成 nint 并阻塞恢复源码编译。
                    if (expectedType != null)
                        PushDefaultOf(expectedType, instructions);
                    else
                    {
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Conv_I);
                    }
                    break;
                }

                if (type.Name == "T")
                {
                    // idk what to do here
                    instructions.Add(CilOpCodes.Ldstr, "<T>");
                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(stringCtor));
                    break;
                }

                // Try to first get constructor without params
                var constructor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                constructor ??= type.Methods.FirstOrDefault(m => m.Name == ".ctor");

                if (constructor == null)
                {
                    instructions.Add(CilOpCodes.Ldstr, $"Constructor not found for: {operand} (probably static type)");
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    // 元数据类型操作数可能只是原生类地址占位；必须按目标槽位生成可赋值的默认值。
                    if (expectedType != null)
                        PushDefaultOf(expectedType, instructions);
                    else
                        instructions.Add(CilOpCodes.Ldnull);
                    break;
                }

                foreach (var param2 in constructor.Parameters)
                    instructions.Add(CilOpCodes.Ldstr, "Constructor param: " + param2);
                instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor.ToMethodDescriptor(module)));
                break;
            default:
                instructions.Add(CilOpCodes.Ldstr, "Unknown operand: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Ldnull);
                break;
        }
    }

    private static void EmitConditionalSelect(
        Instruction instruction,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (instruction.Operands.Count != 4)
            throw new DecompilerException($"条件选择操作数数量无效：{instruction}");

        var instructions = method.CilMethodBody!.Instructions;
        var falseLabel = new CilInstruction(CilOpCodes.Nop);
        var endLabel = new CilInstruction(CilOpCodes.Nop);
        var destinationType = DestinationType(instruction.Operands[0]);

        LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(falseLabel));
        LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor, destinationType);
        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(endLabel));
        instructions.Add(falseLabel);
        LoadOperand(instruction.Operands[3], method, locals, writeLine, stringCtor, destinationType);
        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
        instructions.Add(endLabel);
    }

    /// <summary>
    /// 生成 ARM64 FMAXNM 对应的 IEEE-754 maximumNumber 控制流。单边 NaN 返回数值端；
    /// 有序值返回较大端；相等零值用浮点相加合并符号，从而得到 +0/-0 的精确结果。
    /// </summary>
    private static void EmitMaximumNumber(
        Instruction instruction,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (instruction.Operands is not [var destination, var left, var right, Immediate width]
            || width.Value is not (32 or 64))
            throw new DecompilerException($"maximumNumber 操作数无效：{instruction}");

        var instructions = method.CilMethodBody!.Instructions;
        var leftIsNumber = new CilInstruction(CilOpCodes.Nop);
        var bothAreNumbers = new CilInstruction(CilOpCodes.Nop);
        var selectLeft = new CilInstruction(CilOpCodes.Nop);
        var selectRight = new CilInstruction(CilOpCodes.Nop);
        var end = new CilInstruction(CilOpCodes.Nop);
        var conversion = width.Value == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8;

        void LoadFloating(IOperand operand)
        {
            LoadOperand(operand, method, locals, writeLine, stringCtor);
            instructions.Add(conversion);
        }

        void StoreAndFinish(IOperand operand)
        {
            LoadFloating(operand);
            StoreToOperand(destination, method, locals, writeLine);
            instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));
        }

        // left == left 仅在 left 不是 NaN 时成立。
        LoadFloating(left);
        LoadFloating(left);
        instructions.Add(CilOpCodes.Ceq);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(leftIsNumber));
        StoreAndFinish(right);

        instructions.Add(leftIsNumber);
        LoadFloating(right);
        LoadFloating(right);
        instructions.Add(CilOpCodes.Ceq);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(bothAreNumbers));
        StoreAndFinish(left);

        instructions.Add(bothAreNumbers);
        LoadFloating(left);
        LoadFloating(right);
        instructions.Add(CilOpCodes.Cgt);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(selectLeft));
        LoadFloating(left);
        LoadFloating(right);
        instructions.Add(CilOpCodes.Clt);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(selectRight));

        // 相等的非零值直接取左端；只有正负零组合需要相加以确定结果符号。
        LoadFloating(left);
        if (width.Value == 32)
            instructions.Add(CilOpCodes.Ldc_R4, 0f);
        else
            instructions.Add(CilOpCodes.Ldc_R8, 0d);
        instructions.Add(CilOpCodes.Ceq);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(selectLeft));
        LoadFloating(left);
        LoadFloating(right);
        instructions.Add(CilOpCodes.Add);
        StoreToOperand(destination, method, locals, writeLine);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));

        instructions.Add(selectLeft);
        StoreAndFinish(left);
        instructions.Add(selectRight);
        StoreAndFinish(right);
        instructions.Add(end);
    }

    /// <summary>
    /// 生成 FABS/FABD 的标量浮点 CIL。位宽由原生指令携带，常量传播后的整数零也会先
    /// 转为 r4/r8；Math.Abs 负责清除符号位并保留 IEEE-754 零、无穷与 NaN 语义。
    /// </summary>
    private static void EmitFloatingAbsolute(
        Instruction instruction,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (instruction.Operands.Count < 3
            || instruction.Operands[^1] is not Immediate { Value: 32 or 64 } width)
            throw new DecompilerException($"浮点绝对值操作数无效：{instruction}");

        var module = method.DeclaringModule!;
        var factory = module.CorLibTypeFactory;
        var importer = module.DefaultImporter!;
        var instructions = method.CilMethodBody!.Instructions;
        var conversion = width.Value == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8;

        LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
        instructions.Add(conversion);
        if (instruction.OpCode == OpCode.AbsoluteDifference)
        {
            LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
            instructions.Add(conversion);
            instructions.Add(CilOpCodes.Sub);
        }

        var floatingType = width.Value == 32 ? factory.Single : factory.Double;
        var absMethod = factory.CorLibScope
            .CreateTypeReference("System", "Math")
            .CreateMemberReference(
                "Abs",
                MethodSignature.CreateStatic(floatingType, [floatingType]))
            .ImportWith(importer);
        instructions.Add(CilOpCodes.Call, absMethod);
        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
    }

    private static void EmitHomogeneousFloatingAggregateArgument(
        HomogeneousFloatingAggregateArgument aggregate,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor,
        TypeAnalysisContext? expectedType)
    {
        if (expectedType == null
            || !GenericCallRebinder.TypesEquivalent(expectedType, aggregate.AggregateType)
            || !Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                aggregate.AggregateType,
                out var fields)
            || fields.Count != aggregate.Components.Count)
            throw new DecompilerException($"HFA实参与托管形参不一致：{aggregate}");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var aggregateType = aggregate.AggregateType.ToTypeSignature(module);
        var aggregateLocal = new CilLocalVariable(aggregateType);
        method.CilMethodBody.LocalVariables.Add(aggregateLocal);

        // 一个托管值由连续 V 寄存器的独立浮点分量组成；先初始化完整结构，再逐字段写入，
        // 这样生成的 CIL 与托管构造器签名保持一一对应，也不会伪造整数到结构体的转换。
        instructions.Add(CilOpCodes.Ldloca, aggregateLocal);
        instructions.Add(CilOpCodes.Initobj, aggregateType.ToTypeDefOrRef());
        for (var index = 0; index < fields.Count; index++)
        {
            instructions.Add(CilOpCodes.Ldloca, aggregateLocal);
            LoadOperand(
                aggregate.Components[index],
                method,
                locals,
                writeLine,
                stringCtor,
                fields[index].FieldType);
            instructions.Add(CilOpCodes.Stfld, fields[index].ToFieldDescriptor(module));
        }

        instructions.Add(CilOpCodes.Ldloc, aggregateLocal);
    }

    private static bool RequiresInitializedValueType(TypeAnalysisContext type)
        => !type.IsEnumType
           && type.FullName is not (
               "System.Boolean" or "System.Char"
               or "System.SByte" or "System.Byte"
               or "System.Int16" or "System.UInt16"
               or "System.Int32" or "System.UInt32"
               or "System.Int64" or "System.UInt64"
               or "System.Single" or "System.Double"
               or "System.IntPtr" or "System.UIntPtr");

    private static void EmitInitializedValueType(TypeAnalysisContext type, MethodDefinition method)
    {
        var module = method.DeclaringModule!;
        var signature = type.ToTypeSignature(module);
        var local = new CilLocalVariable(signature);
        method.CilMethodBody!.LocalVariables.Add(local);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloca, local);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Initobj, signature.ToTypeDefOrRef());
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloc, local);
    }

    private static void PushDefaultOf(TypeAnalysisContext type, CilInstructionCollection instructions)
    {
        //TODO Remove this, we should be handling arguments correctly in ISIL resolution, this is a hack to emit balanced stacks.
        //TODO At the *very* least we should emit a console.writeline saying that we did this.
        if (type is RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext
            or StaticFieldStorageTypeAnalysisContext
            or RgctxTableTypeAnalysisContext)
        {
            // 四种合成上下文在分析层表示IL2CPP原生指针，虽然IsValueType为false，
            // 但ToTypeSignature会把它们精确降低为System.IntPtr；默认值必须与最终槽位一致。
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Conv_I);
            return;
        }

        if (!type.IsValueType)
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        switch (type.FullName)
        {
            case "System.Single": instructions.Add(CilOpCodes.Ldc_R4, 0f); break;
            case "System.Double": instructions.Add(CilOpCodes.Ldc_R8, 0d); break;
            case "System.Int64" or "System.UInt64": instructions.Add(CilOpCodes.Ldc_I8, 0L); break;
            case "System.IntPtr":
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case "System.UIntPtr":
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_U);
                break;
            default: instructions.Add(CilOpCodes.Ldc_I4_0); break;
        }
    }

    private static bool IsBoolean(IOperand operand, MethodAnalysisContext context) =>
        operand is LocalVariable { Type: { } type } && type == context.AppContext.SystemTypes.SystemBooleanType;

    private static bool TryEmitNullablePresenceTest(
        Instruction instruction,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (instruction.OpCode != OpCode.And
            || instruction.Operands.Count != 3
            || LocalVariables.NullableOperandType(instruction.Operands[1]) is not { } nullableType
            || instruction.Operands[2] is not Immediate { Value: 0xFF })
            return false;

        var instructions = method.CilMethodBody!.Instructions;
        var module = method.DeclaringModule!;
        switch (instruction.Operands[1])
        {
            case LocalVariable nullableLocal:
                LoadLocalAddress(nullableLocal, method, locals);
                break;
            case FieldReference nullableField:
                var fieldDescriptor = nullableField.Field.ToFieldDescriptor(module);
                if (nullableField.Field.IsStatic)
                    instructions.Add(CilOpCodes.Ldsflda, fieldDescriptor);
                else
                {
                    LoadFieldReceiver(nullableField, method, locals);
                    instructions.Add(CilOpCodes.Ldflda, fieldDescriptor);
                }
                break;
            default:
                return false;
        }

        var nullableOwner = nullableType.ToTypeSignature(module).ToTypeDefOrRef();
        var getter = new MemberReference(
            nullableOwner,
            "get_HasValue",
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Boolean));
        instructions.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(getter));
        instructions.Add(CilOpCodes.Ldc_I4, 0xFF);
        instructions.Add(CilOpCodes.And);
        return true;
    }

    private static void LoadLocalAddress(
        LocalVariable local,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == local.Name);
        if (parameter != null)
            instructions.Add(CilOpCodes.Ldarga, parameter);
        else
            instructions.Add(CilOpCodes.Ldloca, locals[local]);
    }

    private static bool TryEmitHomogeneousFloatingAggregateReturn(
        Instruction instruction,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (!Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                context.ReturnType,
                out var fields)
            || fields.Count != instruction.Operands.Count)
            return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var aggregateType = context.ReturnType.ToTypeSignature(module);
        var aggregateLocal = new CilLocalVariable(aggregateType);
        method.CilMethodBody.LocalVariables.Add(aggregateLocal);

        // 先构造零初始化的值类型局部，再按元数据字段顺序写入 V0..Vn 对应的浮点分量。
        instructions.Add(CilOpCodes.Ldloca, aggregateLocal);
        instructions.Add(CilOpCodes.Initobj, aggregateType.ToTypeDefOrRef());
        for (var index = 0; index < fields.Count; index++)
        {
            instructions.Add(CilOpCodes.Ldloca, aggregateLocal);
            LoadOperand(
                instruction.Operands[index],
                method,
                locals,
                writeLine,
                stringCtor,
                fields[index].FieldType);
            instructions.Add(CilOpCodes.Stfld, fields[index].ToFieldDescriptor(module));
        }

        instructions.Add(CilOpCodes.Ldloc, aggregateLocal);
        return true;
    }

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };

    internal static GenericParameterTypeAnalysisContext? ConstrainedReceiverType(IOperand operand)
        => operand is LocalVariable { Type: GenericParameterTypeAnalysisContext genericParameter }
            ? genericParameter
            : null;

    internal static bool ShouldBoxGenericArgument(IOperand operand, TypeAnalysisContext? expectedType)
        => OperandType(operand) is GenericParameterTypeAnalysisContext
           && expectedType is { IsValueType: false }
           && expectedType is not (GenericParameterTypeAnalysisContext
               or RuntimeClassTypeAnalysisContext
               or RuntimeMethodInfoAnalysisContext
               or StaticFieldStorageTypeAnalysisContext
               or RgctxTableTypeAnalysisContext);

    private static TypeAnalysisContext? OperandType(IOperand operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        _ => null
    };

    private static void LoadCallParameters(IReadOnlyList<IOperand> operands, int firstParameterIndex,
        MethodAnalysisContext targetMethod, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var availableArgs = operands.Count - firstParameterIndex;
        for (var i = 0; i < targetMethod.Parameters.Count; i++)
        {
            var parameterType = targetMethod.Parameters[i].ParameterType;
            if (i < availableArgs)
                LoadOperand(operands[firstParameterIndex + i], method, locals, writeLine, stringCtor, parameterType);
            else
                PushDefaultOf(parameterType, method.CilMethodBody!.Instructions);
        }
    }
    
    private static TypeAnalysisContext? DestinationType(IOperand destination) =>
        destination switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            // 中文注释：零偏移 [ref/out] 写入的目标是引用元素本身，而非 ByRef 地址。
            // 该元素类型同时决定整数零应发射为 ldnull 还是数值零。
            MemoryOperand
            {
                Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } elementType } },
                Index: null,
                Addend: 0,
                Scale: 0
            } => elementType,
            _ => null
        };

    private static TypeAnalysisContext? BinaryOperandRepresentationType(
        IOperand operand,
        MethodAnalysisContext context) =>
        operand switch
        {
            // 二元运算中的裸内存读数和元数据类型字面量都表示IL2CPP原生地址。
            // 把这个事实集中在操作数类型推导处，避免每种比较或算术指令重复判断。
            MemoryOperand or TypeAnalysisContext => context.AppContext.SystemTypes.SystemIntPtrType,
            _ => DestinationType(operand)
        };

    private static void LoadLocal(LocalVariable local, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        if (local.IsThis)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return;
        }

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == local.Name);

        if (parameter != null)
            instructions.Add(CilOpCodes.Ldarg, parameter);
        else
            instructions.Add(CilOpCodes.Ldloc, locals[local]);
    }

    private static void LoadFieldReceiver(
        FieldReference field,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (field.Local.Type?.IsValueType == true)
            LoadLocalAddress(field.Local, method, locals);
        else
            LoadLocal(field.Local, method, locals);
    }

    private static void StoreToOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (operand)
        {
            case LocalVariable local:
                instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                var fieldDescriptor = field.Field.ToFieldDescriptor(module);

                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Stsfld, fieldDescriptor);
                    break;
                }

                // stfld wants the object underneath the value, but the value is already on the stack, so
                // park it in a temporary while we load the object.
                var scratch = new CilLocalVariable(fieldDescriptor.Signature!.FieldType);
                method.CilMethodBody!.LocalVariables.Add(scratch);

                instructions.Add(CilOpCodes.Stloc, scratch);
                LoadFieldReceiver(field, method, locals);
                instructions.Add(CilOpCodes.Ldloc, scratch);
                instructions.Add(CilOpCodes.Stfld, fieldDescriptor);
                break;

            case ArrayAccess arrayAccess:
                // stelem needs array and index before the value, so the same trick as stfld
                var elementType = ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType;
                var elementScratch = new CilLocalVariable(elementType.ToTypeSignature(module));
                method.CilMethodBody!.LocalVariables.Add(elementScratch);

                instructions.Add(CilOpCodes.Stloc, elementScratch);
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine, writeLine);
                instructions.Add(CilOpCodes.Ldloc, elementScratch);
                instructions.Add(CilOpCodes.Stelem, importer.ImportTypeSignature(elementType.ToTypeSignature(module)).ToTypeDefOrRef());
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    if (local2.Type is ByRefTypeAnalysisContext { ElementType: { } byRefElementType })
                    {
                        // stobj要求地址位于值下方；先暂存结果，再载入ref/out地址并写回其元素。
                        var importedElementType = importer.ImportTypeSignature(byRefElementType.ToTypeSignature(module));
                        var byRefScratch = new CilLocalVariable(importedElementType);
                        method.CilMethodBody.LocalVariables.Add(byRefScratch);
                        instructions.Add(CilOpCodes.Stloc, byRefScratch);
                        LoadLocal(local2, method, locals);
                        instructions.Add(CilOpCodes.Ldloc, byRefScratch);
                        instructions.Add(CilOpCodes.Stobj, importedElementType.ToTypeDefOrRef());
                        break;
                    }

                    // Can pointer assignments just be ignored because it's C#? (Move [local], 123)
                    instructions.Add(CilOpCodes.Stloc, locals[local2]);
                    break;
                }
                instructions.Add(CilOpCodes.Pop);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Store into unknown operand: {operand}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Pop);
                break;
        }
    }
}
