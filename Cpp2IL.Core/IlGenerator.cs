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

public static partial class IlGenerator
{
    /// <summary>
    /// 把二元数值 ISIL 操作码映射到唯一 CIL 操作码；逻辑右移必须使用 shr.un，
    /// 防止最高位为1的 ARM64 位模式在托管层被算术补符号位。
    /// </summary>
    internal static CilOpCode GetBinaryNumericCilOpCode(OpCode opCode) => opCode switch
    {
        OpCode.Add => CilOpCodes.Add,
        OpCode.Subtract => CilOpCodes.Sub,
        OpCode.Multiply => CilOpCodes.Mul,
        OpCode.Divide => CilOpCodes.Div,
        OpCode.DivideUnsigned => CilOpCodes.Div_Un,
        OpCode.ShiftLeft => CilOpCodes.Shl,
        OpCode.ShiftRight => CilOpCodes.Shr,
        OpCode.ShiftRightUnsigned => CilOpCodes.Shr_Un,
        OpCode.And => CilOpCodes.And,
        OpCode.Or => CilOpCodes.Or,
        OpCode.Xor => CilOpCodes.Xor,
        _ => throw new ArgumentOutOfRangeException(nameof(opCode), opCode, "不是二元数值操作码。"),
    };

    /// <summary>
    /// 把原生整数目标位宽转换为 CIL 结果规范化操作。ARM64 W 目标无条件只保留低32位，
    /// 即使输入仍是 I8 也必须在写回局部前生成 I4；X 目标和无位宽证据的托管运算保持原栈类型。
    /// </summary>
    internal static CilOpCode? GetIntegerResultNormalizationCilOpCode(int widthBits) => widthBits switch
    {
        0 or 64 => null,
        32 => CilOpCodes.Conv_I4,
        _ => throw new ArgumentOutOfRangeException(nameof(widthBits), widthBits, "不是 ARM64 标量整数目标位宽。"),
    };

    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        var controlFlowGraph = context.ControlFlowGraph
                               ?? throw new DecompilerException($"方法 {context.Name} 缺少控制流图，CIL 生成终止。");
        var emissionBlocks = GetReachableBlockEmissionOrder(controlFlowGraph);
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
        foreach (var instruction in emissionBlocks.SelectMany(block => block.Instructions))
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

        var managedSpans = new RecoveredManagedSpanCilState(controlFlowGraph, context.AppContext.Binary.PointerSizeBytes, definition);

        // 向量寄存器没有对应的稳定 Unity 运行库类型。以两个 UInt64 局部保存原始位模式，
        // 避免依赖 System.Runtime.Intrinsics 的运行时宽度，也避免把 V 寄存器误定型为某个业务结构。
        var vectorState = new RecoveredVectorCilState(
            emissionBlocks.SelectMany(block => block.Instructions).Where(instruction => !managedSpans.Contains(instruction)),
            body,
            factory.UInt64,
            context.AppContext.Binary.PointerSizeBytes);

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in emissionBlocks.SelectMany(block => block.Instructions).SelectMany(i => i.Operands))
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

        foreach (var block in emissionBlocks)
        {
            if (block == controlFlowGraph.EntryBlock || block == controlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                List<CilInstruction> generated;
                try
                {
                    generated = GenerateInstructions(instruction, context, definition, locals, writeLine, stringCtor, vectorState, managedSpans);
                }
                catch (UnresolvedCilSemanticException error)
                {
                    // 保持原失败分类，附加实际抛错指令；不靠扫描共现操作数推测发射失败位置。
                    throw new UnresolvedCilSemanticException(definition.FullName, error.Operation,
                        $"{error.Evidence}; isil={instruction}; integerWidth={instruction.IntegerWidthBits}; memoryWidth={instruction.MemoryAccessWidthBits}");
                }
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, controlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != controlFlowGraph.ExitBlock);
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
                throw new UnresolvedCilSemanticException(definition.FullName, "BRANCH_TARGET", targetBlock.ToString());

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        // 未分类分析警告不是业务指令，也不证明语义已闭合；原文进入统一恢复失败证据。
        if (context.AnalysisWarnings.Count != 0)
            throw new UnresolvedCilSemanticException(definition.FullName, "ANALYSIS_WARNINGS",
                string.Join("\n", context.AnalysisWarnings));

        var instructions = body.Instructions;
        // 只维持既有 void 方法结构规则，不借助运行期诊断输出补齐方法尾部。
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

    /// <summary>
    /// 按可达 CFG 的反向后序生成唯一块发射顺序。退 SSA 新建的临界边桥即使追加在
    /// <see cref="ISILControlFlowGraph.Blocks"/> 尾部，只要它位于一条前向边上，就会先于
    /// 消费 Phi 结果的后继发射；循环回边则自然留在循环头之后。遍历严格保留后继列表的
    /// 语义顺序，不使用易受分析阶段插块影响的块编号。
    /// </summary>
    internal static IReadOnlyList<Block> GetReachableBlockEmissionOrder(ISILControlFlowGraph graph)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        var registeredBlocks = new HashSet<Block>(graph.Blocks);
        if (!registeredBlocks.Contains(graph.EntryBlock))
            throw new DecompilerException("控制流图入口块未登记到块列表中。");
        if (!registeredBlocks.Contains(graph.ExitBlock))
            throw new DecompilerException("控制流图退出块未登记到块列表中。");

        // 大型恢复方法可能包含数千个块；使用显式栈避免递归 DFS 耗尽托管调用栈。
        var visited = new HashSet<Block> { graph.EntryBlock };
        var postOrder = new List<Block>(registeredBlocks.Count);
        var remaining = new Stack<(Block Block, int NextSuccessorIndex)>();
        remaining.Push((graph.EntryBlock, 0));

        while (remaining.Count > 0)
        {
            var (block, nextSuccessorIndex) = remaining.Pop();
            if (nextSuccessorIndex >= block.Successors.Count)
            {
                postOrder.Add(block);
                continue;
            }

            remaining.Push((block, nextSuccessorIndex + 1));
            var successor = block.Successors[nextSuccessorIndex];
            if (successor == null || !registeredBlocks.Contains(successor))
            {
                throw new DecompilerException(
                    $"可达控制流边指向未登记块：from={block.ID}，to={successor?.ID.ToString() ?? "null"}。");
            }

            if (visited.Add(successor))
                remaining.Push((successor, 0));
        }

        postOrder.Reverse();
        return postOrder;
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
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine,
        MemberReference stringCtor, RecoveredVectorCilState vectorState, RecoveredManagedSpanCilState managedSpans)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;
        if (managedSpans.TryEmit(instruction, context, method, locals))
            return instructions.Skip(startIndex).ToList();
        var emittedVectorDefinition = false;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;
        var factory = module.CorLibTypeFactory;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                throw new UnresolvedCilSemanticException(method.FullName, "Invalid", instruction.ToString());

            case OpCode.NotImplemented:
                throw new UnresolvedCilSemanticException(method.FullName, "NotImplemented", instruction.ToString());

            case OpCode.ReadSystemRegister:
                throw new UnresolvedCilSemanticException(method.FullName, "SYSTEM_REGISTER_READ", instruction.ToString());

            case OpCode.Interrupt:
                throw new UnresolvedCilSemanticException(method.FullName, "Interrupt", instruction.ToString());

            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (TryEmitRecoveredVectorMove(instruction, context, method, locals, vectorState))
                {
                    emittedVectorDefinition = instruction.Operands[0] is LocalVariable;
                    break;
                }

                if (instruction is { MemoryAccessWidthBits: > 0,
                    Operands: [MemoryOperand { Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType.IsValueType: true } } }, Immediate { Value: 0 }] })
                {
                    if (!ByRefZeroBlockRecovery.TryDescribe(instruction, context.AppContext.Binary.PointerSizeBytes, out var zeroBlock))
                        throw new UnresolvedCilSemanticException(method.FullName, "BYREF_ZERO_BLOCK", instruction.ToString());

                    // 保持原始写入位置和长度，包含字段间隙及尾部填充；不经值类型 stobj 改写范围。
                    LoadLocal(zeroBlock.Receiver, method, locals);
                    if (zeroBlock.Offset != 0)
                    {
                        instructions.Add(CilOpCodes.Ldc_I8, zeroBlock.Offset);
                        instructions.Add(CilOpCodes.Conv_I);
                        instructions.Add(CilOpCodes.Add);
                    }
                    instructions.Add(CilOpCodes.Ldc_I4, 0);
                    instructions.Add(CilOpCodes.Ldc_I4, zeroBlock.ByteCount);
                    instructions.Add(CilOpCodes.Unaligned, (byte)1);
                    instructions.Add(CilOpCodes.Initblk);
                    break;
                }
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    // 原生代码可按精确字节写入 readonly 字段；保持元数据属性，不伪装为 C# 字段赋值。
                    if (!field.Field.IsStatic && field.Local.Type is ByRefTypeAnalysisContext
                        && (field.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0)
                    {
                        if (!ByRefMemoryAccessHelper.TryDescribeReadonlyStore(instruction,
                                context.AppContext.Binary.PointerSizeBytes, out var store))
                            throw new UnresolvedCilSemanticException(context.FullName, "READONLY_NATIVE_STORE",
                                "只读字段原生写入缺少精确宽度或非托管布局证据。");
                        LoadLocal(field.Local, method, locals);
                        if (field.Offset != 0)
                        {
                            instructions.Add(CilOpCodes.Ldc_I8, (long)field.Offset);
                            instructions.Add(CilOpCodes.Conv_I);
                            instructions.Add(CilOpCodes.Add);
                        }
                        LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, field.Field.FieldType);
                        instructions.Add(CilOpCodes.Unaligned, (byte)1);
                        instructions.Add(store);
                        break;
                    }
                    if (!field.Field.IsStatic)
                        // 中文注释：值类型局部的字段写入必须取其托管地址（ldloca），
                        // 引用类型局部仍按原样载入对象本身。
                        LoadFieldReceiver(field, method, locals);

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

                if (instruction.Operands[0] is LocalVariable { Type: null }
                    && IsZeroConstant(instruction.Operands[1]))
                {
                    // 中文注释：未定型局部量在 CIL 局部表中按 object 声明；原生零值必须与
                    // 该实际槽类型一致地生成 null，否则会形成 object <- I4 的非法栈写入。
                    instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
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
                    throw new UnresolvedCilSemanticException(method.FullName, "ARRAY_ALLOCATION", instruction.ToString());

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Newobj:
                // 仅融合有调用点证据的分配与构造；缺少配对构造时记录恢复缺口。
                if (instruction.Operands.Count == 0)
                    throw new UnresolvedCilSemanticException(method.FullName, "OBJECT_ALLOCATION", instruction.ToString());
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    // 中文注释：IL2CPP 把分配和构造器拆开后，两者之间可能还有为构造实参
                    // 计算属性值的托管调用。若仍在分配点提前融合，CIL 会先读取尚未赋值的
                    // 局部量，再把 getter 错排到构造器之后。中间指令不读取新对象时，把
                    // 真正的 newobj 留在原构造调用位置，严格保留 ISIL 的求值顺序。
                    if (ShouldDeferConstructorFusion(context, instruction, constructorCall))
                    {
                        instructions.Add(CilOpCodes.Nop);
                        break;
                    }

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
                    throw new UnresolvedCilSemanticException(method.FullName, "OBJECT_ALLOCATION", instruction.ToString());
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

            case OpCode.DisposeIfSupported:
                if (instruction.Operands is not [{ } disposableCandidate])
                    throw new InvalidOperationException($"DisposeIfSupported指令操作数不完整: {instruction}");

                // IL2CPP 的非泛型 foreach 清理辅助函数等价于“as IDisposable”后条件调用。
                // dup/brfalse 两条路径分别消费同一个转换结果，确保空值、非 IDisposable 与
                // 实际 Dispose 调用三种路径在汇合处均保持空栈。
                var disposableType = factory.CorLibScope.CreateTypeReference("System", "IDisposable");
                var disposeMethod = disposableType
                    .CreateMemberReference("Dispose", MethodSignature.CreateInstance(factory.Void))
                    .ImportWith(importer);
                var noDispose = new CilInstruction(CilOpCodes.Nop);
                var afterDispose = new CilInstruction(CilOpCodes.Nop);

                LoadOperand(disposableCandidate, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Isinst, disposableType.ImportWith(importer));
                instructions.Add(CilOpCodes.Dup);
                instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(noDispose));
                instructions.Add(CilOpCodes.Callvirt, disposeMethod);
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel(afterDispose));
                instructions.Add(noDispose);
                instructions.Add(CilOpCodes.Pop);
                instructions.Add(afterDispose);
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType])
                {
                    var exceptionCtor = exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);
                    if (exceptionCtor == null)
                        throw new UnresolvedCilSemanticException(method.FullName, "THROW_CONSTRUCTOR", instruction.ToString());
                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(exceptionCtor.ToMethodDescriptor(module)));
                }
                else if (instruction.Operands is [{ } exceptionValue])
                    LoadOperand(exceptionValue, method, locals, writeLine, stringCtor, context.AppContext.SystemTypes.SystemObjectType);
                else
                    throw new UnresolvedCilSemanticException(method.FullName, "THROW_VALUE", instruction.ToString());

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                throw new UnresolvedCilSemanticException(method.FullName, "Phi", instruction.ToString());

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands.Count == 0 || instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                    throw new UnresolvedCilSemanticException(method.FullName, "CALL_TARGET", instruction.ToString());

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
                        else if (targetMethod.DeclaringType?.IsValueType == true
                                 && receiverOperand is LocalVariable valueTypeReceiver)
                        {
                            // 中文注释：值类型实例方法的 this 是托管地址。直接 ldloc 虽可通过旧栈计数门，
                            // 但 ILSpy 只能渲染为 Enumerator*；ldloca 才对应可编译的 receiver.Current。
                            LoadLocalAddress(valueTypeReceiver, method, locals);
                        }
                        else
                            LoadOperand(receiverOperand, method, locals, writeLine, stringCtor, targetMethod.DeclaringType);
                    }
                    else
                    throw new UnresolvedCilSemanticException(method.FullName, "CALL_RECEIVER", instruction.ToString());
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // 迟绑定目标同样必须拥有真实实参，缺项交给统一装载器记录失败。
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
                throw new UnresolvedCilSemanticException(method.FullName, "INDIRECT_CALL", instruction.ToString());

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
                        throw new UnresolvedCilSemanticException(method.FullName, "RETURN_VALUE", instruction.ToString());
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

            case OpCode.SquareRoot:
                EmitFloatingSquareRoot(
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
                throw new UnresolvedCilSemanticException(method.FullName, "IndirectJump", instruction.ToString());

            case OpCode.ShiftStack:
                throw new UnresolvedCilSemanticException(method.FullName, "ShiftStack", instruction.ToString());

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
            case OpCode.DivideUnsigned:
            case OpCode.VectorMultiplyByElement:
            case OpCode.VectorShiftLeftUnsignedVariable:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.ShiftRightUnsigned:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                if (VectorCilRecoveryHelper.TryDescribeUnsignedVariableShift(instruction, out var variableShift, out _))
                {
                    EmitVectorUnsignedVariableShift(variableShift, method, vectorState);
                    emittedVectorDefinition = true;
                    break;
                }

                if (VectorCilRecoveryHelper.TryDescribeFloatingBinary(instruction, out var vectorBinary, out _))
                {
                    if (instruction.IntegerWidthBits > 0)
                        EmitVectorIntegerBinary(vectorBinary, method, vectorState);
                    else
                        EmitVectorFloatingBinary(vectorBinary, method, locals, writeLine, stringCtor, vectorState);
                    emittedVectorDefinition = true;
                    break;
                }

                if (instruction.OpCode == OpCode.VectorMultiplyByElement)
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARITHMETIC_SHAPE", instruction.ToString());

                if (vectorState.ReferencesVectorValue(instruction)
                    && !IsProvenScalarFloatingBinary(instruction)
                    && !IsProvenScalarIntegerBinary(instruction))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARITHMETIC",
                        $"向量位模式已保留，但该运算仍缺少原生通道种类证据：{instruction}");

                if (ByRefMemoryAccessHelper.TryDescribeByteOffset(instruction, context.AppContext.Binary.PointerSizeBytes,
                        out var referenceBase, out var byteOffset))
                {
                    // 保留托管引用来源及 GC 跟踪；偏移按原生宽度加载，不按引用类型加载整数。
                    LoadLocal(referenceBase, method, locals);
                    instructions.Add(CilOpCodes.Ldc_I8, byteOffset);
                    instructions.Add(CilOpCodes.Conv_I);
                    instructions.Add(instruction.OpCode == OpCode.Subtract ? CilOpCodes.Sub : CilOpCodes.Add);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }
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

                    case OpCode.Add:
                    case OpCode.Subtract:
                    case OpCode.Multiply:
                    case OpCode.Divide:
                    case OpCode.DivideUnsigned:
                    case OpCode.ShiftLeft:
                    case OpCode.ShiftRight:
                    case OpCode.ShiftRightUnsigned:
                    case OpCode.And:
                    case OpCode.Or:
                    case OpCode.Xor:
                        instructions.Add(GetBinaryNumericCilOpCode(instruction.OpCode));
                        if (GetIntegerResultNormalizationCilOpCode(instruction.IntegerWidthBits) is { } normalization)
                            instructions.Add(normalization);
                        break;
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
            case OpCode.ConvertUnsignedIntegerToFloat:
            case OpCode.ConvertSignedIntegerWidth:
            case OpCode.RoundFloatTowardPositiveInfinity:
            case OpCode.RoundFloatTowardNegativeInfinity:
                {
                    if (instruction.Operands.Count < 3 || instruction.Operands[2] is not Immediate destinationWidth)
                        throw new InvalidOperationException($"数值转换指令缺少目标位宽：{instruction}");
                    if (instruction.OpCode == OpCode.ConvertSignedIntegerWidth
                        && (instruction.Operands.Count != 4
                            || instruction.Operands[3] is not Immediate { Value: 32 or 64 }))
                        throw new InvalidOperationException($"有符号整数位宽转换缺少有效源位宽：{instruction}");

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

                        case OpCode.ConvertUnsignedIntegerToFloat:
                            if (instruction.Operands.Count != 4
                                || instruction.Operands[3] is not Immediate { Value: 32 or 64 } sourceWidth)
                                throw new InvalidOperationException($"无符号整数到浮点转换缺少源位宽：{instruction}");
                            instructions.Add(sourceWidth.Value == 32
                                ? CilOpCodes.Conv_U4
                                : CilOpCodes.Conv_U8);
                            instructions.Add(CilOpCodes.Conv_R_Un);
                            instructions.Add(destinationWidth.Value switch
                            {
                                32 => CilOpCodes.Conv_R4,
                                64 => CilOpCodes.Conv_R8,
                                _ => throw new InvalidOperationException($"浮点目标位宽无效：{destinationWidth.Value}"),
                            });
                            break;

                        case OpCode.ConvertSignedIntegerWidth:
                            // CIL 的 conv.i8 对 int32 执行有符号扩展，正好对应 ARM64 SMADDL/SMULL 的 W 源语义。
                            instructions.Add(destinationWidth.Value switch
                            {
                                32 => CilOpCodes.Conv_I4,
                                64 => CilOpCodes.Conv_I8,
                                _ => throw new InvalidOperationException($"有符号整数目标位宽无效：{destinationWidth.Value}"),
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

            case OpCode.VectorDuplicate:
                EmitVectorDuplicate(
                    instruction,
                    context,
                    method,
                    locals,
                    writeLine,
                    stringCtor,
                    vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorCompareFloatingLessThanZero:
                if (!VectorCilRecoveryHelper.TryDescribeFloatingLessThanZero(
                        instruction, out var floatingComparison, out var floatingComparisonFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_SHAPE",
                        $"{floatingComparisonFailure} isil={instruction}");
                EmitVectorFloatingLessThanZero(floatingComparison, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorCompareFloatingGreaterThan:
                if (!VectorCilRecoveryHelper.TryDescribeFloatingBinary(
                        instruction, out var floatingGreaterThan, out var floatingGreaterThanFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_SHAPE",
                        $"{floatingGreaterThanFailure} isil={instruction}");
                EmitVectorFloatingGreaterThan(floatingGreaterThan, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorCompareFloatingEqual:
                if (!VectorCilRecoveryHelper.TryDescribeFloatingBinary(
                        instruction, out var floatingEqual, out var floatingEqualFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_SHAPE",
                        $"{floatingEqualFailure} isil={instruction}");
                EmitVectorFloatingEqual(floatingEqual, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorConvertFloatToSignedInteger:
                if (!VectorCilRecoveryHelper.TryDescribeFloatingToSignedInteger(
                        instruction, out var floatingConversion, out var floatingConversionFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_CONVERSION_SHAPE",
                        $"{floatingConversionFailure} isil={instruction}");
                EmitVectorFloatingToSignedInteger(floatingConversion, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorNarrowExtract:
            case OpCode.VectorNarrowExtractUpper:
                if (!VectorCilRecoveryHelper.TryDescribeNarrowExtract(
                        instruction, out var narrowExtract, out var narrowExtractFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_NARROW_SHAPE",
                        $"{narrowExtractFailure} isil={instruction}");
                EmitVectorNarrowExtract(narrowExtract, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorCompareUnsignedHigher:
                if (!VectorCilRecoveryHelper.TryDescribeUnsignedHigher(
                        instruction, out var unsignedHigher, out var unsignedHigherFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_UNSIGNED_SHAPE",
                        $"{unsignedHigherFailure} isil={instruction}");
                EmitVectorUnsignedComparison(unsignedHigher, method, vectorState, isHigherOrSame: false);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorCompareUnsignedHigherOrSame:
                if (!VectorCilRecoveryHelper.TryDescribeUnsignedHigherOrSame(
                        instruction, out var unsignedHigherOrSame, out var unsignedHigherOrSameFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_UNSIGNED_SHAPE",
                        $"{unsignedHigherOrSameFailure} isil={instruction}");
                EmitVectorUnsignedComparison(unsignedHigherOrSame, method, vectorState, isHigherOrSame: true);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorBitwiseInsert:
                if (!VectorCilRecoveryHelper.TryDescribeBitwiseInsert(
                        instruction, out var bitwiseInsert, out var bitwiseInsertFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_BITWISE_SHAPE",
                        $"{bitwiseInsertFailure} isil={instruction}");
                EmitVectorBitwiseInsert(bitwiseInsert, method, vectorState);
                emittedVectorDefinition = true;
                break;

            case OpCode.VectorBitwiseSelect:
                if (!VectorCilRecoveryHelper.TryDescribeBitwiseSelect(
                        instruction, out var bitwiseSelect, out var bitwiseSelectFailure))
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_BITWISE_SHAPE",
                        $"{bitwiseSelectFailure} isil={instruction}");
                EmitVectorBitwiseSelect(bitwiseSelect, method, vectorState);
                emittedVectorDefinition = true;
                break;

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
                        || instruction.Operands[2] is not Immediate { Value: >= 0 and < 8 } laneIndex)
                    {
                        throw new InvalidOperationException($"向量半字提取参数无效：{instruction}");
                    }

                    if (instruction.Operands[1] is LocalVariable vectorSource
                        && vectorState.TryGetStorage(vectorSource, out var vectorStorage))
                    {
                        var bitOffset = checked((int)laneIndex.Value * 16);
                        var sourceWord = bitOffset < 64 ? vectorStorage.Low : vectorStorage.High
                            ?? throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_EXTRACT_RANGE",
                                $"通道{laneIndex.Value}超出{vectorStorage.Shape.TotalWidthBits}位来源。{instruction}");
                        instructions.Add(CilOpCodes.Ldloc, sourceWord);
                        bitOffset %= 64;
                        if (bitOffset != 0)
                        {
                            instructions.Add(CilOpCodes.Ldc_I4, bitOffset);
                            instructions.Add(CilOpCodes.Shr_Un);
                        }
                    }
                    else
                    {
                        if (laneIndex.Value >= 4)
                            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_EXTRACT_RANGE",
                                $"六十四位标量来源不包含通道{laneIndex.Value}：{instruction}");
                        LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    }
                    if (instruction.Operands[1] is not LocalVariable knownVector
                        || !vectorState.TryGetStorage(knownVector, out _))
                    {
                        if (laneIndex.Value != 0)
                        {
                            instructions.Add(CilOpCodes.Ldc_I4, checked((int)laneIndex.Value * 16));
                            instructions.Add(CilOpCodes.Shr_Un);
                        }
                    }
                    instructions.Add(CilOpCodes.Ldc_I8, 0xFFFFL);
                    instructions.Add(CilOpCodes.And);
                    instructions.Add(CilOpCodes.Conv_I4);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

            default:
                throw new UnresolvedCilSemanticException(method.FullName, "UNKNOWN_OPCODE", instruction.ToString());
        }

        SynchronizeScalarAndVectorViews(instruction, emittedVectorDefinition, method, locals, vectorState);
        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }

    /// <summary>
    /// 标量浮点指令由原生解码器表示为三个操作数；结构化向量算术额外携带通道数和元素宽度。
    /// 四则要求浮点目标与双来源同精度；比较要求Boolean目标及同精度浮点来源。
    /// 原生比较立即数零的精度由另一来源证明；不由物理V槽、结果Boolean或任意整数常量猜测精度。
    /// </summary>
    internal static bool IsProvenScalarFloatingBinary(Instruction instruction)
    {
        if (instruction.Operands is not [LocalVariable { Type: { } targetType }, var left, var right])
            return false;

        if (instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
            return targetType.FullName is "System.Single" or "System.Double"
                   && GetScalarFloatingOperandType(left) == targetType.FullName
                   && GetScalarFloatingOperandType(right) == targetType.FullName;

        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual
                or OpCode.CheckGreater or OpCode.CheckLess or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
                or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
                or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned)
            || targetType.FullName != "System.Boolean")
            return false;

        var leftType = GetScalarFloatingOperandType(left);
        var rightType = GetScalarFloatingOperandType(right);
        return leftType != null && (leftType == rightType || right is Immediate { Value: 0 })
               || rightType != null && left is Immediate { Value: 0 };
    }

    private static string? GetScalarFloatingOperandType(IOperand operand) => operand switch
    {
        LocalVariable { Type.FullName: "System.Single" } or FloatLiteral => "System.Single",
        LocalVariable { Type.FullName: "System.Double" } or DoubleLiteral => "System.Double",
        _ => null,
    };

    /// <summary>
    /// 标量整型指令仍可能引用已经建立向量位模式视图的 V 局部量。
    /// 只有目标、来源和原生立即数宽度形成闭合整型合同时才解除向量保护。
    /// </summary>
    internal static bool IsProvenScalarIntegerBinary(Instruction instruction)
    {
        if (instruction.Operands is not [LocalVariable { Type: { } targetType }, var left, var right])
            return false;

        if (instruction.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned)
            return IsIntegerRepresentationType(targetType)
                   && IsScalarIntegerOperand(left, targetType)
                   && IsAnyScalarIntegerOperand(right);

        if (instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.DivideUnsigned or OpCode.And or OpCode.Or or OpCode.Xor)
            return IsIntegerRepresentationType(targetType)
                   && IsScalarIntegerOperand(left, targetType)
                   && IsScalarIntegerOperand(right, targetType);

        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual
                or OpCode.CheckGreater or OpCode.CheckLess or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
                or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
                or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned)
            || targetType.FullName != "System.Boolean")
            return false;

        var leftType = ScalarIntegerOperandType(left);
        var rightType = ScalarIntegerOperandType(right);
        return leftType != null && (leftType == rightType || IsUntypedIntegerLiteral(right))
               || rightType != null && IsUntypedIntegerLiteral(left);
    }

    private static bool IsScalarIntegerOperand(IOperand operand, TypeAnalysisContext expectedType) => operand switch
    {
        LocalVariable { Type: { } type } => IntegerRepresentationTypeName(type)
                                               == IntegerRepresentationTypeName(expectedType),
        Immediate => true,
        FloatLiteral => Is32BitIntegerType(expectedType),
        DoubleLiteral => Is64BitIntegerType(expectedType),
        _ => false,
    };

    private static bool IsAnyScalarIntegerOperand(IOperand operand) => operand switch
    {
        LocalVariable { Type: { } type } => IsIntegerRepresentationType(type),
        Immediate or FloatLiteral or DoubleLiteral => true,
        _ => false,
    };

    private static string? ScalarIntegerOperandType(IOperand operand) => operand switch
    {
        LocalVariable { Type: { } type } => IntegerRepresentationTypeName(type),
        FloatLiteral => "System.Int32",
        DoubleLiteral => "System.Int64",
        _ => null,
    };

    private static bool IsUntypedIntegerLiteral(IOperand operand) => operand is Immediate;

    private static bool IsIntegerRepresentationType(TypeAnalysisContext type) =>
        IntegerRepresentationTypeName(type) is not null;

    private static string? IntegerRepresentationTypeName(TypeAnalysisContext type)
    {
        var storageType = type.IsEnumType ? type.EnumUnderlyingType : type;
        return storageType?.FullName is
            "System.Boolean" or "System.Char"
            or "System.SByte" or "System.Byte"
            or "System.Int16" or "System.UInt16"
            or "System.Int32" or "System.UInt32"
            or "System.Int64" or "System.UInt64"
            or "System.IntPtr" or "System.UIntPtr"
            ? storageType.FullName
            : null;
    }

    private static bool Is32BitIntegerType(TypeAnalysisContext type) =>
        IntegerRepresentationTypeName(type) is
            "System.Boolean" or "System.Char"
            or "System.SByte" or "System.Byte"
            or "System.Int16" or "System.UInt16"
            or "System.Int32" or "System.UInt32";

    private static bool Is64BitIntegerType(TypeAnalysisContext type) =>
        IntegerRepresentationTypeName(type) is "System.Int64" or "System.UInt64";

    /// <summary>
    /// Cpp2IL 的控制流合并可让同一物理 SIMD 槽同时出现标量和向量定义。两套 CIL 存储都存在时，
    /// 每次定义后同步低通道：标量定义清零其余原生通道，向量定义把低通道还原到托管浮点局部。
    /// </summary>
    private static void SynchronizeScalarAndVectorViews(
        Instruction instruction,
        bool emittedVectorDefinition,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        RecoveredVectorCilState vectorState)
    {
        if (instruction.Destination is not LocalVariable { Type: { } type } destination
            || type.FullName is not ("System.Single" or "System.Double")
            || !locals.TryGetValue(destination, out var scalar)
            || !vectorState.TryGetStorage(destination, out var storage))
            return;

        if (emittedVectorDefinition)
        {
            EmitVectorFloatingLane(storage, 0,
                type.FullName == "System.Single" ? 32 : 64, method);
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Stloc, scalar);
            return;
        }

        var instructions = method.CilMethodBody!.Instructions;
        instructions.Add(CilOpCodes.Ldloc, scalar);
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
        var descriptor = type.FullName == "System.Single"
            ? bitConverter.CreateMemberReference("SingleToInt32Bits",
                MethodSignature.CreateStatic(factory.Int32, [factory.Single]))
            : bitConverter.CreateMemberReference("DoubleToInt64Bits",
                MethodSignature.CreateStatic(factory.Int64, [factory.Double]));
        instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(descriptor));
        if (type.FullName == "System.Single")
            instructions.Add(CilOpCodes.Conv_U4);
        instructions.Add(CilOpCodes.Conv_U8);
        instructions.Add(CilOpCodes.Stloc, storage.Low);
        if (storage.High != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, storage.High);
        }
    }

    private sealed class RecoveredVectorCilState
    {
        internal sealed record Storage(
            VectorCilRecoveryHelper.Shape Shape,
            CilLocalVariable Low,
            CilLocalVariable? High);

        private readonly Dictionary<LocalVariable, VectorCilRecoveryHelper.Shape> shapes = [];
        private readonly HashSet<LocalVariable> widthOnlyShapes = [];
        private readonly Dictionary<LocalVariable, Storage> storage = [];
        private readonly CilMethodBody body;
        private readonly TypeSignature wordType;

        internal RecoveredVectorCilState(
            IEnumerable<Instruction> instructions,
            CilMethodBody body,
            TypeSignature wordType,
            int pointerSize)
        {
            this.body = body;
            this.wordType = wordType;
            var materialized = instructions.ToArray();
            foreach (var instruction in materialized)
            {
                if (VectorCilRecoveryHelper.TryDescribe(instruction, out var duplicate, out _))
                {
                    BindShape(duplicate.Destination, duplicate.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeUnsignedVariableShift(instruction, out var variableShift, out _))
                {
                    BindShape(variableShift.Destination, variableShift.Shape);
                    BindShape(variableShift.Value, variableShift.Shape);
                    BindShape(variableShift.Shift, variableShift.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeFloatingLessThanZero(instruction, out var floatingComparison, out _))
                {
                    BindShape(floatingComparison.Destination, floatingComparison.Shape);
                    BindShape(floatingComparison.Source, floatingComparison.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeFloatingToSignedInteger(instruction, out var floatingConversion, out _))
                {
                    BindShape(floatingConversion.Destination, floatingConversion.Shape);
                    BindShape(floatingConversion.Source, floatingConversion.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeNarrowExtract(instruction, out var narrowExtract, out _))
                {
                    BindShape(narrowExtract.Destination, narrowExtract.DestinationShape);
                    if (narrowExtract.PreservedDestination != null)
                        BindShape(narrowExtract.PreservedDestination, narrowExtract.DestinationShape);
                    BindShape(narrowExtract.Source, narrowExtract.SourceShape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeUnsignedHigher(instruction, out var unsignedHigher, out _))
                {
                    BindShape(unsignedHigher.Destination, unsignedHigher.Shape);
                    BindShape(unsignedHigher.Left, unsignedHigher.Shape);
                    BindShape(unsignedHigher.Right, unsignedHigher.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeUnsignedHigherOrSame(
                        instruction, out var unsignedHigherOrSame, out _))
                {
                    BindShape(unsignedHigherOrSame.Destination, unsignedHigherOrSame.Shape);
                    BindShape(unsignedHigherOrSame.Left, unsignedHigherOrSame.Shape);
                    BindShape(unsignedHigherOrSame.Right, unsignedHigherOrSame.Shape);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeBitwiseInsert(instruction, out var bitwiseInsert, out _))
                {
                    BindWidthShape(bitwiseInsert.Destination, bitwiseInsert.Shape.TotalWidthBits);
                    BindWidthShape(bitwiseInsert.Value, bitwiseInsert.Shape.TotalWidthBits);
                    BindWidthShape(bitwiseInsert.Mask, bitwiseInsert.Shape.TotalWidthBits);
                    continue;
                }

                if (VectorCilRecoveryHelper.TryDescribeBitwiseSelect(instruction, out var bitwiseSelect, out _))
                {
                    BindWidthShape(bitwiseSelect.Destination, bitwiseSelect.Shape.TotalWidthBits);
                    BindWidthShape(bitwiseSelect.Mask, bitwiseSelect.Shape.TotalWidthBits);
                    BindWidthShape(bitwiseSelect.SelectedWhenSet, bitwiseSelect.Shape.TotalWidthBits);
                    BindWidthShape(bitwiseSelect.SelectedWhenClear, bitwiseSelect.Shape.TotalWidthBits);
                    continue;
                }

                if (!VectorCilRecoveryHelper.TryDescribeFloatingBinary(instruction, out var binary, out _))
                    continue;
                BindShape(binary.Destination, binary.Shape);
                BindShape(binary.Left, binary.Shape);
                // 按元素来源的载体宽度由其定义证明，不从目标的通道数反推。
                if (binary.RightLane < 0)
                    BindShape(binary.Right, binary.Shape);
            }

            foreach (var instruction in materialized)
            {
                if (instruction is not
                    {
                        OpCode: OpCode.Move,
                        MemoryAccessWidthBits: 64 or 128,
                        Operands.Count: 2,
                    })
                    continue;

                // 完整常量自身携带原生读取宽度，不通过寄存器名称推断其容量。
                if (instruction.Operands is [LocalVariable literalDestination, NativeBitPatternLiteral literal])
                    BindMemoryWidthShape(literalDestination, literal.WidthBits);

                // 连续多个真实数值元素证明这是完整位跨度，不依赖物理寄存器名称。
                if (ManagedArraySpanRecoveryHelper.TryDescribe(instruction, pointerSize, out var arraySpan)
                    && arraySpan.Count > 1)
                    BindMemoryWidthShape((LocalVariable)instruction.Operands[0], instruction.MemoryAccessWidthBits);

                // 字段的原始核心类型与完整跨度可证明这是标量访问，而非向量种子。
                // 若显式向量指令已经建立该局部的形状，仍走原有宽度冲突门，不以标量类型覆盖它。
                if (ManagedFieldSpanRecoveryHelper.IsExactScalarFloatingAccess(instruction, pointerSize)
                    && !instruction.Operands.OfType<LocalVariable>().Any(shapes.ContainsKey))
                    continue;

                // 物理V寄存器与同宽原生内存访问足以证明位载体宽度，但不能证明元素种类。
                // 更精确的DUP或向量算术形状已在上一轮绑定；这里只给尚未定形的载体补规范位宽形状。
                if (instruction.Operands[0] is LocalVariable destination
                    && IsSimdRegister(destination)
                    && instruction.Operands[1] is FieldReference or MemoryOperand)
                    BindMemoryWidthShape(destination, instruction.MemoryAccessWidthBits);
                if (instruction.Operands[1] is LocalVariable source
                    && IsSimdRegister(source)
                    && instruction.Operands[0] is FieldReference or MemoryOperand)
                    BindMemoryWidthShape(source, instruction.MemoryAccessWidthBits);
            }

            // SSA 中的纯复制保持同一向量位宽。反复传播直到闭包稳定，覆盖跨基本块边复制。
            bool changed;
            do
            {
                changed = false;
                foreach (var instruction in materialized)
                {
                    if (instruction.OpCode != OpCode.Move
                        || instruction.Operands is not [LocalVariable destination, LocalVariable source])
                        continue;
                    if (shapes.TryGetValue(source, out var sourceShape))
                        changed |= BindShape(destination, sourceShape);
                    if (shapes.TryGetValue(destination, out var destinationShape))
                        changed |= BindShape(source, destinationShape);
                }
            } while (changed);
        }

        private static bool IsSimdRegister(LocalVariable local) =>
            local.Register.Name.StartsWith("V", StringComparison.Ordinal);

        private void BindMemoryWidthShape(LocalVariable local, int widthBits)
        {
            if (shapes.TryGetValue(local, out var existing))
            {
                if (existing.TotalWidthBits != widthBits)
                    throw new DecompilerException(
                        $"向量局部量布局与原生内存宽度冲突：{local}, shape={existing}, memoryWidth={widthBits}。");
                return;
            }

            BindShape(local, widthBits == 64
                ? new VectorCilRecoveryHelper.Shape(1, 64)
                : new VectorCilRecoveryHelper.Shape(2, 64));
        }

        private void BindWidthShape(LocalVariable local, int widthBits)
        {
            if (shapes.TryGetValue(local, out var existing))
            {
                if (existing.TotalWidthBits != widthBits)
                    throw new DecompilerException(
                        $"向量局部量总位宽与BIT原生访问冲突：{local}, shape={existing}, width={widthBits}。");
                return;
            }

            var shape = widthBits == 64
                ? new VectorCilRecoveryHelper.Shape(1, 64)
                : new VectorCilRecoveryHelper.Shape(2, 64);
            if (BindShape(local, shape))
                widthOnlyShapes.Add(local);
        }

        private bool BindShape(LocalVariable local, VectorCilRecoveryHelper.Shape shape)
        {
            if (shapes.TryGetValue(local, out var existing))
            {
                if (existing.TotalWidthBits == shape.TotalWidthBits
                    && widthOnlyShapes.Remove(local))
                {
                    shapes[local] = shape;
                    return true;
                }

                if (existing != shape)
                    throw new DecompilerException(
                        $"同一 SSA 局部量出现冲突向量布局：{local}, existing={existing}, incoming={shape}。");
                return false;
            }

            shapes.Add(local, shape);
            return true;
        }

        internal bool IsVector(LocalVariable local) => shapes.ContainsKey(local);

        internal bool ReferencesVectorValue(Instruction instruction) =>
            instruction.Operands.Any(OperandReferencesVectorValue);

        private bool OperandReferencesVectorValue(IOperand operand) => operand switch
        {
            LocalVariable local => IsVector(local),
            MemoryOperand memory => memory.Base is LocalVariable baseLocal && IsVector(baseLocal)
                                    || memory.Index is LocalVariable indexLocal && IsVector(indexLocal),
            FieldReference field => IsVector(field.Local),
            AddressOf { Target: LocalVariable local } => IsVector(local),
            _ => false,
        };

        internal Storage GetStorage(LocalVariable local)
        {
            if (storage.TryGetValue(local, out var existing))
                return existing;
            if (!shapes.TryGetValue(local, out var shape))
                throw new InvalidOperationException($"局部量不是已证明的向量值：{local}。");

            var low = new CilLocalVariable(wordType);
            var high = shape.TotalWidthBits == 128 ? new CilLocalVariable(wordType) : null;
            body.LocalVariables.Add(low);
            if (high != null)
                body.LocalVariables.Add(high);
            var created = new Storage(shape, low, high);
            storage.Add(local, created);
            return created;
        }

        internal bool TryGetStorage(LocalVariable local, out Storage value)
        {
            if (!shapes.ContainsKey(local))
            {
                value = null!;
                return false;
            }
            value = GetStorage(local);
            return true;
        }
    }

    private static void EmitVectorDuplicate(
        Instruction instruction,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor,
        RecoveredVectorCilState vectorState)
    {
        if (!VectorCilRecoveryHelper.TryDescribe(instruction, out var duplicate, out var failure))
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_DUPLICATE_SHAPE",
                $"{failure} isil={instruction}");

        var destination = vectorState.GetStorage(duplicate.Destination);
        var instructions = method.CilMethodBody!.Instructions;
        if (duplicate.ReadsVectorLane)
        {
            if (duplicate.Source is LocalVariable source
                && vectorState.TryGetStorage(source, out var sourceStorage))
            {
                var bitOffset = checked(duplicate.SourceLane * duplicate.Shape.ElementWidthBits);
                var sourceWord = bitOffset < 64 ? sourceStorage.Low : sourceStorage.High
                    ?? throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_LANE_SOURCE_RANGE",
                        $"来源通道超出{sourceStorage.Shape.TotalWidthBits}位向量：{instruction}");
                instructions.Add(CilOpCodes.Ldloc, sourceWord);
                bitOffset %= 64;
                if (bitOffset != 0)
                {
                    instructions.Add(CilOpCodes.Ldc_I4, bitOffset);
                    instructions.Add(CilOpCodes.Shr_Un);
                }
            }
            else if (duplicate.SourceLane != 0
                     || !TryEmitScalarLaneBits(duplicate.Source, duplicate.Shape.ElementWidthBits,
                         context, method, locals, writeLine, stringCtor))
            {
                throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_LANE_SOURCE_STATE",
                    $"来源通道缺少同一 SSA 向量位模式或等宽标量首通道证据：{instruction}");
            }
        }
        else
        {
            LoadOperand(
                duplicate.Source,
                method,
                locals,
                writeLine,
                stringCtor,
                context.AppContext.SystemTypes.SystemUInt64Type);
            instructions.Add(CilOpCodes.Conv_U8);
        }

        var mask = VectorCilRecoveryHelper.GetElementMask(duplicate.Shape.ElementWidthBits);
        if (mask != ulong.MaxValue)
        {
            instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)mask));
            instructions.Add(CilOpCodes.And);
        }
        var multiplier = VectorCilRecoveryHelper.GetReplicationMultiplier(duplicate.Shape.ElementWidthBits);
        if (multiplier != 1)
        {
            instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)multiplier));
            instructions.Add(CilOpCodes.Mul);
        }
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High != null)
        {
            // 广播的上、下六十四位拥有相同重复位型，不再重新计算来源表达式。
            instructions.Add(CilOpCodes.Ldloc, destination.Low);
            instructions.Add(CilOpCodes.Stloc, destination.High);
        }
    }

    private static bool TryEmitScalarLaneBits(
        IOperand source,
        int elementWidthBits,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (source is not LocalVariable { Type: { } sourceType })
            return false;
        if (sourceType.IsEnumType)
            sourceType = sourceType.EnumUnderlyingType!;

        var sourceWidthBits = sourceType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => 8,
            "System.Char" or "System.Int16" or "System.UInt16" => 16,
            "System.Int32" or "System.UInt32" or "System.Single" => 32,
            "System.Int64" or "System.UInt64" or "System.Double" => 64,
            "System.IntPtr" or "System.UIntPtr" => context.AppContext.Binary.PointerSizeBytes * 8,
            _ => 0,
        };
        if (sourceWidthBits != elementWidthBits)
            return false;

        LoadOperand(source, method, locals, writeLine, stringCtor, sourceType);
        var instructions = method.CilMethodBody!.Instructions;
        if (sourceType.FullName is "System.Single" or "System.Double")
        {
            var factory = method.DeclaringModule!.CorLibTypeFactory;
            var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
            var descriptor = sourceWidthBits == 32
                ? bitConverter.CreateMemberReference(
                    "SingleToInt32Bits",
                    MethodSignature.CreateStatic(factory.Int32, [factory.Single]))
                : bitConverter.CreateMemberReference(
                    "DoubleToInt64Bits",
                    MethodSignature.CreateStatic(factory.Int64, [factory.Double]));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter.ImportMethod(descriptor));
        }
        instructions.Add(CilOpCodes.Conv_U8);
        return true;
    }

    private static void EmitVectorFloatingBinary(
        VectorCilRecoveryHelper.BinaryDescription binary,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(binary.Destination);
        var left = vectorState.GetStorage(binary.Left);
        var hasRightVector = vectorState.TryGetStorage(binary.Right, out var right);
        if (destination.Shape != binary.Shape || left.Shape != binary.Shape
            || binary.RightLane < 0 && (!hasRightVector || right.Shape != binary.Shape))
        {
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARITHMETIC_SHAPE",
                $"浮点向量四则运算的载体布局不一致：operation={binary.Operation}, shape={binary.Shape}。");
        }

        // 同一来源通道只读取一次；先保存标量，防止目标与来源共用存储时改变乘数。
        CilLocalVariable? element = null;
        if (binary.RightLane >= 0)
        {
            if (hasRightVector)
            {
                if ((binary.RightLane + 1) * binary.Shape.ElementWidthBits > right.Shape.TotalWidthBits)
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_LANE_SOURCE_RANGE", "乘法来源通道超出已证明载体。");
                EmitVectorFloatingLane(right, binary.RightLane, binary.Shape.ElementWidthBits, method);
            }
            else
            {
                if (binary.RightLane != 0 || binary.Right.Type?.FullName != "System.Single")
                    throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_LANE_SOURCE_STATE", "乘法来源缺少同一SSA向量或Single首通道证据。");
                LoadOperand(binary.Right, method, locals, writeLine, stringCtor, binary.Right.Type);
            }
            element = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.Single);
            method.CilMethodBody!.LocalVariables.Add(element);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc, element);
        }

        // 原生向量运算允许目标覆盖任一来源；先在独立结果载体完成全部通道，再提交目标。
        // 相比逐来源快照，此方案不重复复制别名来源，也覆盖左右来源同时相同的情形。
        var output = destination;
        var resultLow = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.UInt64);
        method.CilMethodBody!.LocalVariables.Add(resultLow);
        CilLocalVariable? resultHigh = null;
        if (destination.High != null)
        {
            resultHigh = new CilLocalVariable(method.DeclaringModule.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(resultHigh);
        }
        destination = new RecoveredVectorCilState.Storage(binary.Shape, resultLow, resultHigh);
        var instructions = method.CilMethodBody!.Instructions;
        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, destination.High);
        }

        var laneBits = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var bitConverter = method.DeclaringModule.CorLibTypeFactory.CorLibScope
            .CreateTypeReference("System", "BitConverter");
        var resultToBits = binary.Shape.ElementWidthBits == 32
            ? bitConverter.CreateMemberReference(
                "SingleToInt32Bits",
                MethodSignature.CreateStatic(method.DeclaringModule.CorLibTypeFactory.Int32,
                    [method.DeclaringModule.CorLibTypeFactory.Single]))
            : bitConverter.CreateMemberReference(
                "DoubleToInt64Bits",
                MethodSignature.CreateStatic(method.DeclaringModule.CorLibTypeFactory.Int64,
                    [method.DeclaringModule.CorLibTypeFactory.Double]));

        for (var lane = 0; lane < binary.Shape.LaneCount; lane++)
        {
            EmitVectorFloatingLane(left, lane, binary.Shape.ElementWidthBits, method);
            if (element != null)
                instructions.Add(CilOpCodes.Ldloc, element);
            else
                EmitVectorFloatingLane(right, lane, binary.Shape.ElementWidthBits, method);
            instructions.Add(GetBinaryNumericCilOpCode(binary.Operation));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(resultToBits));
            instructions.Add(CilOpCodes.Conv_U8);
            if (binary.Shape.ElementWidthBits == 32)
            {
                instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)uint.MaxValue));
                instructions.Add(CilOpCodes.And);
            }
            instructions.Add(CilOpCodes.Stloc, laneBits);

            var bitOffset = checked(lane * binary.Shape.ElementWidthBits);
            var destinationWord = bitOffset < 64 ? destination.Low : destination.High
                ?? throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARITHMETIC_RANGE",
                    $"目标通道超出{destination.Shape.TotalWidthBits}位载体：lane={lane}。");
            var wordOffset = bitOffset % 64;
            instructions.Add(CilOpCodes.Ldloc, destinationWord);
            instructions.Add(CilOpCodes.Ldloc, laneBits);
            if (wordOffset != 0)
            {
                instructions.Add(CilOpCodes.Ldc_I4, wordOffset);
                instructions.Add(CilOpCodes.Shl);
            }
            instructions.Add(CilOpCodes.Or);
            instructions.Add(CilOpCodes.Stloc, destinationWord);
        }
        instructions.Add(CilOpCodes.Ldloc, destination.Low);
        instructions.Add(CilOpCodes.Stloc, output.Low);
        if (destination.High != null)
        {
            instructions.Add(CilOpCodes.Ldloc, destination.High);
            instructions.Add(CilOpCodes.Stloc, output.High!);
        }
    }

    private static void EmitVectorFloatingLane(
        RecoveredVectorCilState.Storage source,
        int lane,
        int elementWidthBits,
        MethodDefinition method)
    {
        var bitOffset = checked(lane * elementWidthBits);
        var sourceWord = bitOffset < 64 ? source.Low : source.High
            ?? throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARITHMETIC_RANGE",
                $"来源通道超出{source.Shape.TotalWidthBits}位载体：lane={lane}。");
        var instructions = method.CilMethodBody!.Instructions;
        instructions.Add(CilOpCodes.Ldloc, sourceWord);
        var wordOffset = bitOffset % 64;
        if (wordOffset != 0)
        {
            instructions.Add(CilOpCodes.Ldc_I4, wordOffset);
            instructions.Add(CilOpCodes.Shr_Un);
        }

        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
        var bitsToValue = elementWidthBits == 32
            ? bitConverter.CreateMemberReference(
                "Int32BitsToSingle",
                MethodSignature.CreateStatic(factory.Single, [factory.Int32]))
            : bitConverter.CreateMemberReference(
                "Int64BitsToDouble",
                MethodSignature.CreateStatic(factory.Double, [factory.Int64]));
        if (elementWidthBits == 32)
            instructions.Add(CilOpCodes.Conv_I4);
        instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(bitsToValue));
    }

    private static void EmitVectorIntegerBinary(
        VectorCilRecoveryHelper.BinaryDescription binary,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        if (binary.Operation != OpCode.Add || binary.RightLane >= 0)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_INTEGER_ARITHMETIC",
                $"当前只接入带完整通道布局证据的整数向量ADD：{binary.Operation}。");

        var destination = vectorState.GetStorage(binary.Destination);
        var left = vectorState.GetStorage(binary.Left);
        var right = vectorState.GetStorage(binary.Right);
        if (destination.Shape != binary.Shape
            || left.Shape != binary.Shape
            || right.Shape != binary.Shape
            || binary.Shape.ElementWidthBits is not (8 or 16 or 32 or 64))
        {
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_INTEGER_ARITHMETIC_SHAPE",
                $"整数向量ADD的目标与来源布局不一致：shape={binary.Shape}。");
        }

        var instructions = method.CilMethodBody!.Instructions;
        var resultLow = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(resultLow);
        CilLocalVariable? resultHigh = null;
        if (destination.High != null)
        {
            resultHigh = new CilLocalVariable(method.DeclaringModule.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(resultHigh);
        }

        var output = new RecoveredVectorCilState.Storage(binary.Shape, resultLow, resultHigh);
        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, output.Low);
        if (output.High != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, output.High);
        }

        var laneBits = new CilLocalVariable(method.DeclaringModule.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        for (var lane = 0; lane < binary.Shape.LaneCount; lane++)
        {
            EmitVectorIntegerLaneBits(left, lane, binary.Shape.ElementWidthBits, method);
            EmitVectorIntegerLaneBits(right, lane, binary.Shape.ElementWidthBits, method);
            instructions.Add(CilOpCodes.Add);
            if (binary.Shape.ElementWidthBits < 64)
            {
                instructions.Add(CilOpCodes.Ldc_I8,
                    unchecked((long)VectorCilRecoveryHelper.GetElementMask(binary.Shape.ElementWidthBits)));
                instructions.Add(CilOpCodes.And);
            }
            instructions.Add(CilOpCodes.Stloc, laneBits);
            MergeVectorBits(
                output,
                laneBits,
                checked(lane * binary.Shape.ElementWidthBits),
                binary.Shape.ElementWidthBits,
                method);
        }

        instructions.Add(CilOpCodes.Ldloc, output.Low);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (output.High != null)
        {
            instructions.Add(CilOpCodes.Ldloc, output.High);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>
    /// 生成 ARM64 USHL 的逐通道位模式。移位来源按元素宽度解释为有符号量，
    /// 非负值执行无符号左移，负值执行逻辑右移，绝对值达到元素宽度时结果为零。
    /// </summary>
    private static void EmitVectorUnsignedVariableShift(
        VectorCilRecoveryHelper.VariableShiftDescription shift,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(shift.Destination);
        var value = vectorState.GetStorage(shift.Value);
        var shiftSource = vectorState.GetStorage(shift.Shift);
        if (destination.Shape != shift.Shape
            || value.Shape != shift.Shape
            || shiftSource.Shape != shift.Shape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_SHIFT_SHAPE",
                $"USHL 的目标与双来源布局不一致：shape={shift.Shape}。");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(outputLow);
        CilLocalVariable? outputHigh = null;
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, outputLow);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputHigh);
        }

        var valueBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        var signedShift = new CilLocalVariable(module.CorLibTypeFactory.Int64);
        var laneBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(valueBits);
        method.CilMethodBody.LocalVariables.Add(signedShift);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var output = new RecoveredVectorCilState.Storage(shift.Shape, outputLow, outputHigh);

        for (var lane = 0; lane < shift.Shape.LaneCount; lane++)
        {
            EmitVectorIntegerLaneBits(value, lane, shift.Shape.ElementWidthBits, method);
            instructions.Add(CilOpCodes.Stloc, valueBits);

            EmitVectorIntegerLaneBits(shiftSource, lane, shift.Shape.ElementWidthBits, method);
            if (shift.Shape.ElementWidthBits < 64)
            {
                instructions.Add(CilOpCodes.Conv_I4);
                var signExtensionShift = 32 - shift.Shape.ElementWidthBits;
                if (signExtensionShift != 0)
                {
                    instructions.Add(CilOpCodes.Ldc_I4, signExtensionShift);
                    instructions.Add(CilOpCodes.Shl);
                    instructions.Add(CilOpCodes.Ldc_I4, signExtensionShift);
                    instructions.Add(CilOpCodes.Shr);
                }
                instructions.Add(CilOpCodes.Conv_I8);
            }
            else
                instructions.Add(CilOpCodes.Conv_I8);
            instructions.Add(CilOpCodes.Stloc, signedShift);

            var negative = new CilInstruction(CilOpCodes.Nop);
            var zero = new CilInstruction(CilOpCodes.Nop);
            var end = new CilInstruction(CilOpCodes.Nop);

            instructions.Add(CilOpCodes.Ldloc, signedShift);
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Blt, new CilInstructionLabel(negative));
            instructions.Add(CilOpCodes.Ldloc, signedShift);
            instructions.Add(CilOpCodes.Ldc_I8, (long)shift.Shape.ElementWidthBits);
            instructions.Add(CilOpCodes.Bge, new CilInstructionLabel(zero));
            instructions.Add(CilOpCodes.Ldloc, valueBits);
            instructions.Add(CilOpCodes.Ldloc, signedShift);
            instructions.Add(CilOpCodes.Conv_I4);
            instructions.Add(CilOpCodes.Shl);
            EmitVectorElementMask(shift.Shape.ElementWidthBits, instructions);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));

            instructions.Add(negative);
            instructions.Add(CilOpCodes.Ldloc, signedShift);
            instructions.Add(CilOpCodes.Ldc_I8, -(long)shift.Shape.ElementWidthBits);
            instructions.Add(CilOpCodes.Ble, new CilInstructionLabel(zero));
            instructions.Add(CilOpCodes.Ldloc, valueBits);
            instructions.Add(CilOpCodes.Ldloc, signedShift);
            instructions.Add(CilOpCodes.Neg);
            instructions.Add(CilOpCodes.Conv_I4);
            instructions.Add(CilOpCodes.Shr_Un);
            EmitVectorElementMask(shift.Shape.ElementWidthBits, instructions);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));

            instructions.Add(zero);
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            instructions.Add(end);
            MergeVectorBits(output, laneBits,
                checked(lane * shift.Shape.ElementWidthBits),
                shift.Shape.ElementWidthBits,
                method);
        }

        instructions.Add(CilOpCodes.Ldloc, output.Low);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (output.High != null)
        {
            instructions.Add(CilOpCodes.Ldloc, output.High);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    private static void EmitVectorElementMask(int elementWidthBits, CilInstructionCollection instructions)
    {
        if (elementWidthBits == 64)
            return;
        instructions.Add(CilOpCodes.Ldc_I8,
            unchecked((long)VectorCilRecoveryHelper.GetElementMask(elementWidthBits)));
        instructions.Add(CilOpCodes.And);
    }

    /// <summary>把栈顶的布尔比较结果扩展为指定元素宽度的全一或全零掩码。</summary>
    private static void EmitVectorPredicateMask(
        int elementWidthBits,
        CilInstructionCollection instructions)
    {
        var falseResult = new CilInstruction(CilOpCodes.Nop);
        var endResult = new CilInstruction(CilOpCodes.Nop);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(falseResult));
        instructions.Add(CilOpCodes.Ldc_I8,
            unchecked((long)VectorCilRecoveryHelper.GetElementMask(elementWidthBits)));
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(endResult));
        instructions.Add(falseResult);
        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(endResult);
    }

    /// <summary>生成 FCMLT 的逐通道浮点比较；NaN 和正负零均按 ARM 条件比较产生零掩码。</summary>
    private static void EmitVectorFloatingLessThanZero(
        VectorCilRecoveryHelper.UnaryDescription comparison,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(comparison.Destination);
        var source = vectorState.GetStorage(comparison.Source);
        if (destination.Shape != comparison.Shape || source.Shape != comparison.Shape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_SHAPE",
                $"FCMLT 的目标与来源布局不一致：shape={comparison.Shape}。");

        var instructions = method.CilMethodBody!.Instructions;
        EmitVectorFloatingPredicate(
            destination,
            comparison.Shape,
            method,
            lane =>
            {
                EmitVectorFloatingLane(source, lane, comparison.Shape.ElementWidthBits, method);
                if (comparison.Shape.ElementWidthBits == 32)
                    instructions.Add(CilOpCodes.Ldc_R4, 0f);
                else
                    instructions.Add(CilOpCodes.Ldc_R8, 0d);
                instructions.Add(CilOpCodes.Clt);
            });
    }

    /// <summary>生成 FCMGT 的逐通道浮点大于比较；真值通道保持全元素掩码。</summary>
    private static void EmitVectorFloatingGreaterThan(
        VectorCilRecoveryHelper.BinaryDescription comparison,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
        => EmitVectorFloatingComparison(
            comparison,
            method,
            vectorState,
            CilOpCodes.Cgt,
            "FCMGT");

    /// <summary>生成 FCMEQ 的逐通道浮点相等比较；真值通道保持全元素掩码。</summary>
    private static void EmitVectorFloatingEqual(
        VectorCilRecoveryHelper.BinaryDescription comparison,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
        => EmitVectorFloatingComparison(
            comparison,
            method,
            vectorState,
            CilOpCodes.Ceq,
            "FCMEQ");

    private static void EmitVectorFloatingComparison(
        VectorCilRecoveryHelper.BinaryDescription comparison,
        MethodDefinition method,
        RecoveredVectorCilState vectorState,
        CilOpCode comparisonOpCode,
        string mnemonic)
    {
        var destination = vectorState.GetStorage(comparison.Destination);
        var left = vectorState.GetStorage(comparison.Left);
        var right = vectorState.GetStorage(comparison.Right);
        if (destination.Shape != comparison.Shape
            || left.Shape != comparison.Shape
            || right.Shape != comparison.Shape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_SHAPE",
                $"{mnemonic} 的目标与双来源布局不一致：shape={comparison.Shape}。");

        var instructions = method.CilMethodBody!.Instructions;
        EmitVectorFloatingPredicate(
            destination,
            comparison.Shape,
            method,
            lane =>
            {
                EmitVectorFloatingLane(left, lane, comparison.Shape.ElementWidthBits, method);
                EmitVectorFloatingLane(right, lane, comparison.Shape.ElementWidthBits, method);
                instructions.Add(comparisonOpCode);
            });
    }

    private static void EmitVectorFloatingPredicate(
        RecoveredVectorCilState.Storage destination,
        VectorCilRecoveryHelper.Shape shape,
        MethodDefinition method,
        Action<int> emitLaneComparison)
    {
        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(outputLow);
        CilLocalVariable? outputHigh = null;
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, outputLow);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputHigh);
        }

        var laneBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var output = new RecoveredVectorCilState.Storage(shape, outputLow, outputHigh);
        for (var lane = 0; lane < shape.LaneCount; lane++)
        {
            emitLaneComparison(lane);
            EmitVectorPredicateMask(shape.ElementWidthBits, instructions);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            MergeVectorBits(
                output,
                laneBits,
                checked(lane * shape.ElementWidthBits),
                shape.ElementWidthBits,
                method);
        }

        instructions.Add(CilOpCodes.Ldloc, outputLow);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldloc, outputHigh);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>生成 ARM FCVTZS 的逐通道向零有符号整数转换，输出保留原生整数位模式。</summary>
    private static void EmitVectorFloatingToSignedInteger(
        VectorCilRecoveryHelper.UnaryDescription conversion,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(conversion.Destination);
        var source = vectorState.GetStorage(conversion.Source);
        if (destination.Shape != conversion.Shape || source.Shape != conversion.Shape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_CONVERSION_SHAPE",
                $"FCVTZS 的目标与来源布局不一致：shape={conversion.Shape}。");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(outputLow);
        CilLocalVariable? outputHigh = null;
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, outputLow);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputHigh);
        }

        var laneBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var output = new RecoveredVectorCilState.Storage(conversion.Shape, outputLow, outputHigh);
        for (var lane = 0; lane < conversion.Shape.LaneCount; lane++)
        {
            EmitVectorFloatingLane(source, lane, conversion.Shape.ElementWidthBits, method);
            instructions.Add(conversion.Shape.ElementWidthBits == 32
                ? CilOpCodes.Conv_I4
                : CilOpCodes.Conv_I8);
            instructions.Add(CilOpCodes.Conv_U8);
            if (conversion.Shape.ElementWidthBits < 64)
            {
                instructions.Add(CilOpCodes.Ldc_I8,
                    unchecked((long)VectorCilRecoveryHelper.GetElementMask(conversion.Shape.ElementWidthBits)));
                instructions.Add(CilOpCodes.And);
            }

            instructions.Add(CilOpCodes.Stloc, laneBits);
            MergeVectorBits(
                output,
                laneBits,
                checked(lane * conversion.Shape.ElementWidthBits),
                conversion.Shape.ElementWidthBits,
                method);
        }

        instructions.Add(CilOpCodes.Ldloc, output.Low);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (output.High != null)
        {
            instructions.Add(CilOpCodes.Ldloc, output.High);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>生成 ARM XTN/XTN2 的逐通道低半宽截取，严格保留对应的半区写入语义。</summary>
    private static void EmitVectorNarrowExtract(
        VectorCilRecoveryHelper.NarrowDescription narrow,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(narrow.Destination);
        var source = vectorState.GetStorage(narrow.Source);
        if (destination.Shape != narrow.DestinationShape || source.Shape != narrow.SourceShape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_NARROW_SHAPE",
                $"XTN/XTN2 的目标与来源布局不一致：destination={narrow.DestinationShape}, source={narrow.SourceShape}。");

        var instructions = method.CilMethodBody!.Instructions;
        var module = method.DeclaringModule!;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        var outputHigh = narrow.IsUpper
            ? new CilLocalVariable(module.CorLibTypeFactory.UInt64)
            : null;
        method.CilMethodBody.LocalVariables.Add(outputLow);
        if (outputHigh != null)
            method.CilMethodBody.LocalVariables.Add(outputHigh);

        if (narrow.IsUpper)
        {
            if (narrow.PreservedDestination is not { } preservedLocal)
                throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_NARROW_DESTINATION_READ",
                    "XTN2 缺少目标旧值来源。");
            var preserved = vectorState.GetStorage(preservedLocal);
            if (preserved.Shape != narrow.DestinationShape)
                throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_NARROW_DESTINATION_SHAPE",
                    $"XTN2 目标旧值布局不一致：expected={narrow.DestinationShape}, actual={preserved.Shape}。");

            // XTN2 只改写高半区，低半区直接从目标旧值复制；高半区随后完整覆盖。
            instructions.Add(CilOpCodes.Ldloc, preserved.Low);
            instructions.Add(CilOpCodes.Stloc, outputLow);
        }
        else
        {
            // XTN 写入低半区并清理高半区；低位输出先从零开始累积。
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputLow);
        }

        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputHigh);
        }

        var laneBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var narrowedElementWidth = narrow.SourceShape.ElementWidthBits / 2;
        var output = new RecoveredVectorCilState.Storage(
            narrow.DestinationShape,
            outputLow,
            outputHigh);
        for (var lane = 0; lane < narrow.SourceShape.LaneCount; lane++)
        {
            EmitVectorIntegerLaneBits(source, lane, narrow.SourceShape.ElementWidthBits, method);
            EmitVectorElementMask(narrowedElementWidth, instructions);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            var bitOffset = narrow.IsUpper
                ? checked(64 + lane * narrowedElementWidth)
                : checked(lane * narrowedElementWidth);
            MergeVectorBits(output, laneBits, bitOffset, narrowedElementWidth, method);
        }

        instructions.Add(CilOpCodes.Ldloc, output.Low);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldloc, outputHigh);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>生成 ARM CMHI 与 CMHS 的逐通道无符号比较全元素掩码。</summary>
    private static void EmitVectorUnsignedComparison(
        VectorCilRecoveryHelper.IntegerComparisonDescription comparison,
        MethodDefinition method,
        RecoveredVectorCilState vectorState,
        bool isHigherOrSame)
    {
        var destination = vectorState.GetStorage(comparison.Destination);
        var left = vectorState.GetStorage(comparison.Left);
        var right = vectorState.GetStorage(comparison.Right);
        if (destination.Shape != comparison.Shape
            || left.Shape != comparison.Shape
            || right.Shape != comparison.Shape)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_COMPARE_UNSIGNED_SHAPE",
                $"无符号向量比较的目标与来源布局不一致：shape={comparison.Shape}。");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(outputLow);
        CilLocalVariable? outputHigh = null;
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, outputLow);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, outputHigh);
        }

        var laneBits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(laneBits);
        var output = new RecoveredVectorCilState.Storage(comparison.Shape, outputLow, outputHigh);
        for (var lane = 0; lane < comparison.Shape.LaneCount; lane++)
        {
            if (isHigherOrSame)
            {
                // CMHS 等价于 !(右值无符号大于左值)，避免重复读取并保持无符号宽度。
                EmitVectorIntegerLaneBits(right, lane, comparison.Shape.ElementWidthBits, method);
                EmitVectorIntegerLaneBits(left, lane, comparison.Shape.ElementWidthBits, method);
            }
            else
            {
                EmitVectorIntegerLaneBits(left, lane, comparison.Shape.ElementWidthBits, method);
                EmitVectorIntegerLaneBits(right, lane, comparison.Shape.ElementWidthBits, method);
            }
            instructions.Add(CilOpCodes.Cgt_Un);
            if (isHigherOrSame)
            {
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Ceq);
            }
            EmitVectorPredicateMask(comparison.Shape.ElementWidthBits, instructions);
            instructions.Add(CilOpCodes.Stloc, laneBits);
            MergeVectorBits(
                output,
                laneBits,
                checked(lane * comparison.Shape.ElementWidthBits),
                comparison.Shape.ElementWidthBits,
                method);
        }

        instructions.Add(CilOpCodes.Ldloc, output.Low);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (output.High != null)
        {
            instructions.Add(CilOpCodes.Ldloc, output.High);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>生成 ARM BIT 的逐字位选择：掩码位为一取目标旧值，否则取值来源。</summary>
    private static void EmitVectorBitwiseInsert(
        VectorCilRecoveryHelper.BitwiseDescription bitwise,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(bitwise.Destination);
        var value = vectorState.GetStorage(bitwise.Value);
        var mask = vectorState.GetStorage(bitwise.Mask);
        if (destination.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits
            || value.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits
            || mask.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_BITWISE_SHAPE",
                $"BIT 的目标与双来源总位宽不一致：shape={bitwise.Shape}。");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(outputLow);
        CilLocalVariable? outputHigh = null;
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        void EmitWord(
            CilLocalVariable destinationWord,
            CilLocalVariable valueWord,
            CilLocalVariable maskWord,
            CilLocalVariable outputWord)
        {
            // BIT = (目标旧值 & 掩码) | (值来源 & ~掩码)。
            instructions.Add(CilOpCodes.Ldloc, destinationWord);
            instructions.Add(CilOpCodes.Ldloc, maskWord);
            instructions.Add(CilOpCodes.And);
            instructions.Add(CilOpCodes.Ldloc, valueWord);
            instructions.Add(CilOpCodes.Ldloc, maskWord);
            instructions.Add(CilOpCodes.Not);
            instructions.Add(CilOpCodes.And);
            instructions.Add(CilOpCodes.Or);
            instructions.Add(CilOpCodes.Stloc, outputWord);
        }

        EmitWord(destination.Low, value.Low, mask.Low, outputLow);
        if (outputHigh != null)
            EmitWord(destination.High!, value.High!, mask.High!, outputHigh);

        instructions.Add(CilOpCodes.Ldloc, outputLow);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldloc, outputHigh);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    /// <summary>生成 ARM BSL 的逐位选择：掩码为一取置位来源，否则取清位来源。</summary>
    private static void EmitVectorBitwiseSelect(
        VectorCilRecoveryHelper.BitwiseSelectDescription bitwise,
        MethodDefinition method,
        RecoveredVectorCilState vectorState)
    {
        var destination = vectorState.GetStorage(bitwise.Destination);
        var mask = vectorState.GetStorage(bitwise.Mask);
        var selectedWhenSet = vectorState.GetStorage(bitwise.SelectedWhenSet);
        var selectedWhenClear = vectorState.GetStorage(bitwise.SelectedWhenClear);
        if (destination.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits
            || mask.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits
            || selectedWhenSet.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits
            || selectedWhenClear.Shape.TotalWidthBits != bitwise.Shape.TotalWidthBits)
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_BITWISE_SHAPE",
                $"BSL 的目标、掩码和两侧来源总位宽不一致：shape={bitwise.Shape}。");

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var outputLow = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        CilLocalVariable? outputHigh = null;
        method.CilMethodBody.LocalVariables.Add(outputLow);
        if (destination.High != null)
        {
            outputHigh = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
            method.CilMethodBody.LocalVariables.Add(outputHigh);
        }

        void EmitWord(
            CilLocalVariable maskWord,
            CilLocalVariable selectedWhenSetWord,
            CilLocalVariable selectedWhenClearWord,
            CilLocalVariable outputWord)
        {
            // BSL = (掩码 & 置位来源) | (~掩码 & 清位来源)。
            instructions.Add(CilOpCodes.Ldloc, maskWord);
            instructions.Add(CilOpCodes.Ldloc, selectedWhenSetWord);
            instructions.Add(CilOpCodes.And);
            instructions.Add(CilOpCodes.Ldloc, maskWord);
            instructions.Add(CilOpCodes.Not);
            instructions.Add(CilOpCodes.Ldloc, selectedWhenClearWord);
            instructions.Add(CilOpCodes.And);
            instructions.Add(CilOpCodes.Or);
            instructions.Add(CilOpCodes.Stloc, outputWord);
        }

        EmitWord(mask.Low, selectedWhenSet.Low, selectedWhenClear.Low, outputLow);
        if (outputHigh != null)
            EmitWord(mask.High!, selectedWhenSet.High!, selectedWhenClear.High!, outputHigh);

        instructions.Add(CilOpCodes.Ldloc, outputLow);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (outputHigh != null)
        {
            instructions.Add(CilOpCodes.Ldloc, outputHigh);
            instructions.Add(CilOpCodes.Stloc, destination.High!);
        }
    }

    private static void EmitVectorIntegerLaneBits(
        RecoveredVectorCilState.Storage source,
        int lane,
        int elementWidthBits,
        MethodDefinition method)
    {
        var bitOffset = checked(lane * elementWidthBits);
        var sourceWord = bitOffset < 64 ? source.Low : source.High
            ?? throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_INTEGER_ARITHMETIC_RANGE",
                $"整数向量来源通道超出已证明载体：lane={lane}。");
        var instructions = method.CilMethodBody!.Instructions;
        instructions.Add(CilOpCodes.Ldloc, sourceWord);
        var wordOffset = bitOffset % 64;
        if (wordOffset != 0)
        {
            instructions.Add(CilOpCodes.Ldc_I4, wordOffset);
            instructions.Add(CilOpCodes.Shr_Un);
        }
        if (elementWidthBits < 64)
        {
            instructions.Add(CilOpCodes.Ldc_I8,
                unchecked((long)VectorCilRecoveryHelper.GetElementMask(elementWidthBits)));
            instructions.Add(CilOpCodes.And);
        }
    }

    private static bool TryEmitRecoveredVectorMove(
        Instruction instruction,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        RecoveredVectorCilState vectorState)
    {
        var destination = instruction.Operands[0];
        var source = instruction.Operands[1];
        var sourceStorage = source is LocalVariable sourceLocal
                            && vectorState.TryGetStorage(sourceLocal, out var foundSource)
            ? foundSource
            : null;
        var destinationStorage = destination is LocalVariable destinationLocal
                                 && vectorState.TryGetStorage(destinationLocal, out var foundDestination)
            ? foundDestination
            : null;
        if (sourceStorage == null && destinationStorage == null)
            return false;

        var instructions = method.CilMethodBody!.Instructions;
        if (sourceStorage != null && destinationStorage != null)
        {
            if (sourceStorage.Shape != destinationStorage.Shape)
                throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_MOVE_SHAPE",
                    $"向量复制两侧布局不同：{instruction}");
            instructions.Add(CilOpCodes.Ldloc, sourceStorage.Low);
            instructions.Add(CilOpCodes.Stloc, destinationStorage.Low);
            if (sourceStorage.High != null && destinationStorage.High != null)
            {
                instructions.Add(CilOpCodes.Ldloc, sourceStorage.High);
                instructions.Add(CilOpCodes.Stloc, destinationStorage.High);
            }
            return true;
        }

        if (sourceStorage != null && destination is MemoryOperand storeMemory)
        {
            EmitVectorMemoryStore(instruction, storeMemory, sourceStorage, method, locals);
            return true;
        }

        if (sourceStorage != null && destination is FieldReference storeField)
        {
            EmitVectorFieldStore(instruction, storeField, sourceStorage, context, method, locals);
            return true;
        }

        if (destinationStorage != null && source is MemoryOperand loadMemory)
        {
            EmitVectorMemoryLoad(instruction, loadMemory, destinationStorage, method, locals);
            return true;
        }


        if (destinationStorage != null && source is FieldReference loadField)
        {
            EmitVectorFieldLoad(instruction, loadField, destinationStorage, context, method, locals);
            return true;
        }

        if (destinationStorage != null && source is ArrayAccess loadArray)
        {
            EmitVectorArrayLoad(instruction, loadArray, destinationStorage, context, method, locals);
            return true;
        }

        if (destinationStorage != null && source is NativeBitPatternLiteral literal)
        {
            if (literal.WidthBits is not (64 or 128)
                || literal.WidthBits != instruction.MemoryAccessWidthBits
                || literal.WidthBits != destinationStorage.Shape.TotalWidthBits
                || literal.WidthBits == 64 && literal.High != 0)
                throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_LITERAL_WIDTH",
                    $"常量完整位宽与原生访问或目标容量冲突：{instruction}");
            instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)literal.Low));
            instructions.Add(CilOpCodes.Stloc, destinationStorage.Low);
            if (destinationStorage.High != null)
            {
                instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)literal.High));
                instructions.Add(CilOpCodes.Stloc, destinationStorage.High);
            }
            return true;
        }

        if (destinationStorage != null && source is Immediate { Value: 0 })
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, destinationStorage.Low);
            if (destinationStorage.High != null)
            {
                instructions.Add(CilOpCodes.Ldc_I8, 0L);
                instructions.Add(CilOpCodes.Stloc, destinationStorage.High);
            }
            return true;
        }

        throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_MOVE_BRIDGE",
            $"向量位模式与托管值之间缺少精确布局桥接：{instruction}");
    }

    private static void EmitVectorFieldStore(
        Instruction instruction,
        FieldReference field,
        RecoveredVectorCilState.Storage source,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (TryDescribeVectorFieldSpan(instruction, field, source.Shape, context, method,
                "VECTOR_FIELD_STORE", out var span))
        {
            EmitVectorFieldSpanStore(field, span, source, context, method, locals);
            return;
        }

        EmitVectorFieldWordAddress(field, 0, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloc, source.Low);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stind_I8);
        if (source.High == null)
            return;

        EmitVectorFieldWordAddress(field, 8, method, locals);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloc, source.High);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stind_I8);
    }

    private static void EmitVectorFieldLoad(
        Instruction instruction,
        FieldReference field,
        RecoveredVectorCilState.Storage destination,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (TryDescribeVectorFieldSpan(instruction, field, destination.Shape, context, method,
                "VECTOR_FIELD_LOAD", out var span))
        {
            EmitVectorFieldSpanLoad(field, span, destination, context, method, locals);
            return;
        }

        EmitVectorFieldWordAddress(field, 0, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldind_I8);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High == null)
            return;

        EmitVectorFieldWordAddress(field, 8, method, locals);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldind_I8);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc, destination.High);
    }

    /// <summary>
    /// 多字段跨度必须由元数据偏移精确覆盖，并改写为逐字段位模式读写；单字段值类型继续只在自身
    /// 完整非托管布局与原生宽度相等时使用字段地址，任何部分字段、间隙或托管引用都失败关闭。
    /// </summary>
    private static bool TryDescribeVectorFieldSpan(
        Instruction instruction,
        FieldReference field,
        VectorCilRecoveryHelper.Shape shape,
        MethodAnalysisContext context,
        MethodDefinition method,
        string operation,
        out ManagedFieldSpanRecoveryHelper.Description span)
    {
        span = default;
        var bytes = shape.TotalWidthBits / 8;
        var pointerSize = context.AppContext.Binary.PointerSizeBytes;
        if (instruction.MemoryAccessWidthBits != shape.TotalWidthBits)
        {
            throw new UnresolvedCilSemanticException(method.FullName, operation,
                $"向量字段访问宽度与载体不一致：native={instruction.MemoryAccessWidthBits}, vector={shape.TotalWidthBits}；{instruction}");
        }

        if (ManagedFieldSpanRecoveryHelper.TryDescribe(field, pointerSize, bytes,
                out span, out var spanFailure,
                allowOpaqueSmallAggregate:
                    operation != "VECTOR_FIELD_STORE"
                    || (field.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) == 0))
        {
            if (CanUseExactUnmanagedAggregateAddress(field, span, context, pointerSize))
                return false;
            return true;
        }

        if (ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(field.Field.FieldType, pointerSize, bytes))
            return false;

        throw new UnresolvedCilSemanticException(method.FullName, operation,
            $"向量字段访问缺少精确的单字段布局或连续标量字段覆盖：{spanFailure}；{instruction}");
    }

    private static void EmitVectorFieldSpanStore(
        FieldReference reference,
        ManagedFieldSpanRecoveryHelper.Description span,
        RecoveredVectorCilState.Storage source,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        CilLocalVariable? wholeValue = null;
        if (span.WholeAggregateField != null)
        {
            ValidateWholeReadOnlyStore(reference, context, method);
            wholeValue = new CilLocalVariable(span.WholeAggregateField.FieldType.ToTypeSignature(method.DeclaringModule!));
            method.CilMethodBody!.LocalVariables.Add(wholeValue);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloca, wholeValue);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Initobj, wholeValue.VariableType.ToTypeDefOrRef());
        }
        if (wholeValue != null
            && span.Segments is [{ Kind: ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate } aggregate]
            && ReferenceEquals(aggregate.Field, reference.Field))
        {
            // 整字段不透明聚合先落入临时值，再一次提交根字段，避免把只读根字段误当作可写叶字段。
            EmitVectorBits(source, 0, checked(aggregate.SizeBytes * 8), method);
            EmitVectorBitsAsFieldValue(aggregate, method);
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Stloc, wholeValue);
        }
        else foreach (var segment in span.Segments)
        {
            var fieldReference = LoadRecoveredFieldSpanReceiver(reference, segment, context, method, locals, read: false, wholeValue);
            EmitVectorBits(source, checked(segment.RelativeOffsetBytes * 8), checked(segment.SizeBytes * 8), method);
            EmitVectorBitsAsFieldValue(segment, method);
            EmitRecoveredFieldOperation(fieldReference, context, method, read: false,
                segment.ParentFields is { Count: > 0 } parents ? parents[^1].FieldType : null);
        }
        if (wholeValue != null)
        {
            // 完整访问只提交一次原始整字段；部分访问从不进入此分支，避免改写未访问分量。
            if (!reference.Field.IsStatic) LoadFieldReceiver(reference, method, locals);
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloc, wholeValue);
            EmitRecoveredFieldOperation(reference, context, method, read: false);
        }
    }

    private static void EmitVectorFieldSpanLoad(
        FieldReference reference,
        ManagedFieldSpanRecoveryHelper.Description span,
        RecoveredVectorCilState.Storage destination,
        MethodAnalysisContext context,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;
        CilLocalVariable? wholeValue = null;
        if (span.WholeAggregateField != null)
        {
            wholeValue = new CilLocalVariable(span.WholeAggregateField.FieldType.ToTypeSignature(method.DeclaringModule!));
            method.CilMethodBody.LocalVariables.Add(wholeValue);
            if (!reference.Field.IsStatic) LoadFieldReceiver(reference, method, locals);
            EmitRecoveredFieldOperation(reference, context, method, read: true);
            instructions.Add(CilOpCodes.Stloc, wholeValue);
        }
        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, destination.High);
        }

        var bits = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.UInt64);
        method.CilMethodBody.LocalVariables.Add(bits);
        if (wholeValue != null
            && span.Segments is [{ Kind: ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate } aggregate]
            && ReferenceEquals(aggregate.Field, reference.Field))
        {
            // 整字段读取直接从已保留的临时聚合取位，不重新构造根字段接收者。
            instructions.Add(CilOpCodes.Ldloc, wholeValue);
            EmitFieldValueAsVectorBits(aggregate, method);
            instructions.Add(CilOpCodes.Stloc, bits);
            MergeVectorBits(destination, bits, checked(aggregate.RelativeOffsetBytes * 8),
                checked(aggregate.SizeBytes * 8), method);
        }
        else foreach (var segment in span.Segments)
        {
            var fieldReference = LoadRecoveredFieldSpanReceiver(reference, segment, context, method, locals, read: true, wholeValue);
            EmitRecoveredFieldOperation(fieldReference, context, method, read: true,
                segment.ParentFields is { Count: > 0 } parents ? parents[^1].FieldType : null);
            EmitFieldValueAsVectorBits(segment, method);
            instructions.Add(CilOpCodes.Stloc, bits);
            MergeVectorBits(destination, bits, checked(segment.RelativeOffsetBytes * 8),
                checked(segment.SizeBytes * 8), method);
        }
    }

    /// <summary>从六十四或一百二十八位载体中提取一个不超过六十四位的连续字段位段。</summary>
    private static void EmitVectorBits(
        RecoveredVectorCilState.Storage storage,
        int bitOffset,
        int bitWidth,
        MethodDefinition method)
    {
        var instructions = method.CilMethodBody!.Instructions;
        if (bitOffset < 64)
        {
            instructions.Add(CilOpCodes.Ldloc, storage.Low);
            if (bitOffset != 0)
            {
                instructions.Add(CilOpCodes.Ldc_I4, bitOffset);
                instructions.Add(CilOpCodes.Shr_Un);
            }

            if (bitOffset + bitWidth > 64)
            {
                if (storage.High == null)
                    throw new InvalidOperationException("字段位段越过六十四位载体边界。");
                instructions.Add(CilOpCodes.Ldloc, storage.High);
                instructions.Add(CilOpCodes.Ldc_I4, 64 - bitOffset);
                instructions.Add(CilOpCodes.Shl);
                instructions.Add(CilOpCodes.Or);
            }
        }
        else
        {
            if (storage.High == null)
                throw new InvalidOperationException("字段位段位于不存在的高六十四位载体中。");
            instructions.Add(CilOpCodes.Ldloc, storage.High);
            if (bitOffset != 64)
            {
                instructions.Add(CilOpCodes.Ldc_I4, bitOffset - 64);
                instructions.Add(CilOpCodes.Shr_Un);
            }
        }

        if (bitWidth < 64)
        {
            instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)((1UL << bitWidth) - 1)));
            instructions.Add(CilOpCodes.And);
        }
    }

    private static void EmitVectorBitsAsFieldValue(
        ManagedFieldSpanRecoveryHelper.Segment segment,
        MethodDefinition method)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        if (segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
        {
            EmitVectorBitsAsSmallAggregate(segment, method);
            return;
        }

        if (segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Single)
        {
            instructions.Add(CilOpCodes.Conv_I4);
            var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
            var descriptor = bitConverter.CreateMemberReference("Int32BitsToSingle",
                MethodSignature.CreateStatic(factory.Single, [factory.Int32]));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(descriptor));
            return;
        }

        if (segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Double)
        {
            var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
            var descriptor = bitConverter.CreateMemberReference("Int64BitsToDouble",
                MethodSignature.CreateStatic(factory.Double, [factory.Int64]));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(descriptor));
            return;
        }

        if (segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.NativeInteger)
        {
            instructions.Add(CilOpCodes.Conv_U);
            return;
        }

        if (segment.SizeBytes < 8)
            instructions.Add(CilOpCodes.Conv_I4);
    }

    private static void EmitFieldValueAsVectorBits(
        ManagedFieldSpanRecoveryHelper.Segment segment,
        MethodDefinition method)
    {
        if (segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
        {
            EmitSmallAggregateAsVectorBits(segment, method);
            return;
        }

        EmitScalarValueAsVectorBits(segment.Kind, segment.SizeBytes, method);
    }

    /// <summary>把不超过八字节的已闭合无托管聚合体按原始字节顺序转换为无符号位载体。</summary>
    private static void EmitSmallAggregateAsVectorBits(
        ManagedFieldSpanRecoveryHelper.Segment segment,
        MethodDefinition method)
    {
        if (segment.SizeBytes is < 1 or > 8)
            throw new InvalidOperationException("小型无托管聚合体的位宽必须介于一至八字节。");

        var module = method.DeclaringModule!;
        var body = method.CilMethodBody!;
        var value = new CilLocalVariable(segment.Field.FieldType.ToTypeSignature(module));
        body.LocalVariables.Add(value);
        body.Instructions.Add(CilOpCodes.Stloc, value);
        body.Instructions.Add(CilOpCodes.Ldloca, value);
        body.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        body.Instructions.Add(segment.SizeBytes switch
        {
            1 => CilOpCodes.Ldind_U1,
            2 => CilOpCodes.Ldind_U2,
            4 => CilOpCodes.Ldind_U4,
            8 => CilOpCodes.Ldind_I8,
            _ => throw new InvalidOperationException("原始聚合体尺寸不是受支持的整数位宽。")
        });
        body.Instructions.Add(CilOpCodes.Conv_U8);
    }

    /// <summary>把无符号位载体写回不超过八字节的已闭合无托管聚合体。</summary>
    private static void EmitVectorBitsAsSmallAggregate(
        ManagedFieldSpanRecoveryHelper.Segment segment,
        MethodDefinition method)
    {
        if (segment.SizeBytes is < 1 or > 8)
            throw new InvalidOperationException("小型无托管聚合体的位宽必须介于一至八字节。");

        var module = method.DeclaringModule!;
        var body = method.CilMethodBody!;
        var bits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        var value = new CilLocalVariable(segment.Field.FieldType.ToTypeSignature(module));
        body.LocalVariables.Add(bits);
        body.LocalVariables.Add(value);
        body.Instructions.Add(CilOpCodes.Stloc, bits);
        body.Instructions.Add(CilOpCodes.Ldloca, value);
        body.Instructions.Add(CilOpCodes.Initobj, value.VariableType.ToTypeDefOrRef());
        body.Instructions.Add(CilOpCodes.Ldloca, value);
        body.Instructions.Add(CilOpCodes.Ldloc, bits);
        if (segment.SizeBytes < 8)
            body.Instructions.Add(CilOpCodes.Conv_I4);
        else
            body.Instructions.Add(CilOpCodes.Conv_I8);
        body.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        body.Instructions.Add(segment.SizeBytes switch
        {
            1 => CilOpCodes.Stind_I1,
            2 => CilOpCodes.Stind_I2,
            4 => CilOpCodes.Stind_I4,
            8 => CilOpCodes.Stind_I8,
            _ => throw new InvalidOperationException("原始聚合体尺寸不是受支持的整数位宽。")
        });
        body.Instructions.Add(CilOpCodes.Ldloc, value);
    }

    private static void EmitScalarValueAsVectorBits(
        ManagedFieldSpanRecoveryHelper.ScalarKind kind,
        int sizeBytes,
        MethodDefinition method)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        if (kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Single)
        {
            var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
            var descriptor = bitConverter.CreateMemberReference("SingleToInt32Bits",
                MethodSignature.CreateStatic(factory.Int32, [factory.Single]));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(descriptor));
            instructions.Add(CilOpCodes.Conv_U4);
            instructions.Add(CilOpCodes.Conv_U8);
            return;
        }

        if (kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Double)
        {
            var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
            var descriptor = bitConverter.CreateMemberReference("DoubleToInt64Bits",
                MethodSignature.CreateStatic(factory.Int64, [factory.Double]));
            instructions.Add(CilOpCodes.Call, method.DeclaringModule.DefaultImporter!.ImportMethod(descriptor));
            instructions.Add(CilOpCodes.Conv_U8);
            return;
        }

        if (kind == ManagedFieldSpanRecoveryHelper.ScalarKind.NativeInteger)
            instructions.Add(CilOpCodes.Conv_U);
        if (sizeBytes < 8)
            instructions.Add(CilOpCodes.Conv_U4);
        instructions.Add(CilOpCodes.Conv_U8);
        if (sizeBytes < 8)
        {
            instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)((1UL << (sizeBytes * 8)) - 1)));
            instructions.Add(CilOpCodes.And);
        }
    }

    private static void MergeVectorBits(
        RecoveredVectorCilState.Storage destination,
        CilLocalVariable bits,
        int bitOffset,
        int bitWidth,
        MethodDefinition method)
    {
        var instructions = method.CilMethodBody!.Instructions;
        if (bitOffset < 64)
        {
            instructions.Add(CilOpCodes.Ldloc, destination.Low);
            instructions.Add(CilOpCodes.Ldloc, bits);
            if (bitOffset != 0)
            {
                instructions.Add(CilOpCodes.Ldc_I4, bitOffset);
                instructions.Add(CilOpCodes.Shl);
            }
            instructions.Add(CilOpCodes.Or);
            instructions.Add(CilOpCodes.Stloc, destination.Low);

            if (bitOffset + bitWidth <= 64)
                return;
            if (destination.High == null)
                throw new InvalidOperationException("字段位段越过六十四位载体边界。");
            instructions.Add(CilOpCodes.Ldloc, destination.High);
            instructions.Add(CilOpCodes.Ldloc, bits);
            instructions.Add(CilOpCodes.Ldc_I4, 64 - bitOffset);
            instructions.Add(CilOpCodes.Shr_Un);
            instructions.Add(CilOpCodes.Or);
            instructions.Add(CilOpCodes.Stloc, destination.High);
            return;
        }

        if (destination.High == null)
            throw new InvalidOperationException("字段位段位于不存在的高六十四位载体中。");
        instructions.Add(CilOpCodes.Ldloc, destination.High);
        instructions.Add(CilOpCodes.Ldloc, bits);
        if (bitOffset != 64)
        {
            instructions.Add(CilOpCodes.Ldc_I4, bitOffset - 64);
            instructions.Add(CilOpCodes.Shl);
        }
        instructions.Add(CilOpCodes.Or);
        instructions.Add(CilOpCodes.Stloc, destination.High);
    }

    private static void EmitVectorFieldWordAddress(
        FieldReference field,
        int wordOffset,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var fieldDescriptor = field.Field.ToFieldDescriptor(method.DeclaringModule!);
        if (field.Field.IsStatic)
            instructions.Add(CilOpCodes.Ldsflda, fieldDescriptor);
        else
        {
            LoadFieldReceiver(field, method, locals);
            instructions.Add(CilOpCodes.Ldflda, fieldDescriptor);
        }

        if (wordOffset == 0)
            return;
        instructions.Add(CilOpCodes.Ldc_I4, wordOffset);
        instructions.Add(CilOpCodes.Conv_I);
        instructions.Add(CilOpCodes.Add);
    }

    private static void EmitVectorMemoryStore(
        Instruction instruction,
        MemoryOperand memory,
        RecoveredVectorCilState.Storage source,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (instruction.MemoryAccessWidthBits != source.Shape.TotalWidthBits
            || memory is not { Base: LocalVariable { Type: ByRefTypeAnalysisContext } address, Index: null, Scale: 0 })
        {
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_MEMORY_STORE",
                $"向量写入缺少同宽托管引用证据：{instruction}");
        }

        EmitVectorWordAddress(memory, 0, address, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloc, source.Low);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stind_I8);
        if (source.High != null)
        {
            EmitVectorWordAddress(memory, 8, address, method, locals);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloc, source.High);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Stind_I8);
        }
    }

    private static void EmitVectorMemoryLoad(
        Instruction instruction,
        MemoryOperand memory,
        RecoveredVectorCilState.Storage destination,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (instruction.MemoryAccessWidthBits != destination.Shape.TotalWidthBits
            || memory is not { Base: LocalVariable { Type: ByRefTypeAnalysisContext } address, Index: null, Scale: 0 })
        {
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_MEMORY_LOAD",
                $"向量读取缺少同宽托管引用证据：{instruction}");
        }

        EmitVectorWordAddress(memory, 0, address, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldind_I8);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High != null)
        {
            EmitVectorWordAddress(memory, 8, address, method, locals);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Unaligned, (byte)1);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ldind_I8);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc, destination.High);
        }
    }

    private static void EmitVectorWordAddress(
        MemoryOperand memory,
        int wordOffset,
        LocalVariable address,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        LoadLocal(address, method, locals);
        var totalOffset = checked(memory.Addend + wordOffset);
        if (totalOffset == 0)
            return;
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldc_I8, totalOffset);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Conv_I);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Add);
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

    /// <summary>
    /// 当分配与构造调用之间存在独立求值且新对象尚未被读取时，把 CIL 的原子
    /// <c>newobj</c> 延后到构造调用位置；没有中间求值时仍沿用紧邻融合。
    /// </summary>
    internal static bool ShouldDeferConstructorFusion(
        MethodAnalysisContext context,
        Instruction newobj,
        Instruction constructorCall)
    {
        if (newobj.Operands.Count == 0 || newobj.Operands[0] is not LocalVariable newObject)
            return false;

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            var allocationIndex = block.Instructions.IndexOf(newobj);
            var constructorIndex = block.Instructions.IndexOf(constructorCall);
            if (allocationIndex < 0 || constructorIndex <= allocationIndex)
                continue;

            var hasMeaningfulIntermediate = false;
            for (var index = allocationIndex + 1; index < constructorIndex; index++)
            {
                var intermediate = block.Instructions[index];
                if (intermediate.OpCode == OpCode.Nop)
                    continue;

                hasMeaningfulIntermediate = true;
                if (intermediate.Operands.Any(operand => OperandReferencesLocal(operand, newObject)))
                    return false;
            }

            return hasMeaningfulIntermediate;
        }

        return false;
    }

    /// <summary>
    /// 递归检查复合 ISIL 操作数是否读取指定局部，避免把对象首次使用推到构造之前。
    /// </summary>
    private static bool OperandReferencesLocal(IOperand operand, LocalVariable local) => operand switch
    {
        LocalVariable candidate => ReferenceEquals(candidate, local),
        AddressOf address => OperandReferencesLocal(address.Target, local),
        FieldReference field => ReferenceEquals(field.Local, local),
        ArrayAccess access => ReferenceEquals(access.Array, local)
                              || OperandReferencesLocal(access.Index, local),
        ArrayLength length => ReferenceEquals(length.Array, local),
        MemoryOperand memory => memory.Base is not null && OperandReferencesLocal(memory.Base, local)
                                || memory.Index is not null && OperandReferencesLocal(memory.Index, local),
        HomogeneousFloatingAggregateArgument aggregate => aggregate.Components.Any(
            component => OperandReferencesLocal(component, local)),
        ListCount count => ReferenceEquals(count.Value, local),
        StringLength length => ReferenceEquals(length.Value, local),
        MetadataStringTableLookup lookup => OperandReferencesLocal(lookup.Index, local),
        ReadOnlyUInt16TableLookup lookup => OperandReferencesLocal(lookup.Index, local),
        _ => false,
    };

    private static void LoadOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor,
        TypeAnalysisContext? expectedType = null)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        // 常量的求值栈种类由使用位置的真实签名决定，不按数值大小猜测 I4。
        if (operand is Immediate typedImmediate && expectedType != null)
        {
            if (expectedType is ByRefTypeAnalysisContext)
                throw new UnresolvedCilSemanticException(method.FullName, "IMMEDIATE_MANAGED_BYREF",
                    $"整数常量没有可证明的托管引用来源：value={operand}; expectedType={expectedType.FullName}。");
            var storageType = expectedType.IsEnumType ? expectedType.EnumUnderlyingType : expectedType;
            if (expectedType is PointerTypeAnalysisContext || storageType?.FullName is "System.IntPtr" or "System.UIntPtr")
            {
                instructions.Add(CilOpCodes.Ldc_I8, typedImmediate.Value);
                instructions.Add(storageType?.FullName == "System.IntPtr" ? CilOpCodes.Conv_I : CilOpCodes.Conv_U);
                return;
            }
            if (storageType?.FullName is "System.Int64" or "System.UInt64")
            {
                instructions.Add(CilOpCodes.Ldc_I8, typedImmediate.Value);
                return;
            }
        }

        // 托管引用的整数零表示 null；非托管指针已在上方按 native int 发射。
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

        if (operand is FloatLiteral floatBits && expectedType != null && Is32BitIntegerType(expectedType))
        {
            // FloatLiteral在此处承载原生32位整型位模式；只有目标整型宽度已经闭合时才按位取回。
            instructions.Add(CilOpCodes.Ldc_I4, FloatingPointBitHelper.SingleToInt32Bits(floatBits.Value));
            return;
        }

        if (operand is DoubleLiteral doubleBits && expectedType != null && Is64BitIntegerType(expectedType))
        {
            // DoubleLiteral在此处承载原生64位整型位模式；目标必须是精确的64位整型。
            instructions.Add(CilOpCodes.Ldc_I8, BitConverter.DoubleToInt64Bits(doubleBits.Value));
            return;
        }

        // ARM64 的 W 寄存器写入会把低 32 位零扩展到宿主 long。比如源码中的 -1 会以
        // 0x00000000FFFFFFFF（4294967295）进入 ISIL；若按 long 直接发射 ldc.i8，随后调用
        // Int32/UInt32/短整数/布尔/字符形参时就会形成 I8 -> I4 的非法求值栈。托管签名已经
        // 给出目标栈类型，因此只在值可由同一 32 位位模式精确表达时收窄为 ldc.i4。
        if (operand is Immediate integerImmediate
            && TryGetI4Immediate(integerImmediate, expectedType, out var i4Value))
        {
            instructions.Add(CilOpCodes.Ldc_I4, i4Value);
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
            case MetadataStringTableLookup lookup:
                EmitMetadataStringTableLookup(
                    lookup,
                    method,
                    locals,
                    writeLine,
                    stringCtor);
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
                EmitLengthRepresentationConversion(expectedType, instructions);
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
                EmitLengthRepresentationConversion(expectedType, instructions);
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
                EmitLengthRepresentationConversion(expectedType, instructions);
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
                    throw new UnresolvedCilSemanticException(method.FullName, "RUNTIME_CLASS_LAYOUT", operand.ToString() ?? string.Empty);
                }
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    if (local2.Type is not ByRefTypeAnalysisContext { ElementType: { } byRefElementType })
                        throw new UnresolvedCilSemanticException(method.FullName, "MEMORY_LOAD", operand.ToString() ?? string.Empty);
                    // 读取托管引用必须解引用；普通局部的值不代表其指向地址的内容。
                    LoadLocal(local2, method, locals);
                    var importedElementType = importer.ImportTypeSignature(byRefElementType.ToTypeSignature(module));
                    instructions.Add(CilOpCodes.Ldobj, importedElementType.ToTypeDefOrRef());
                    break;
                }
                throw new UnresolvedCilSemanticException(method.FullName, "MEMORY_LOAD", operand.ToString() ?? string.Empty);
            case ReadOnlyUInt16TableLookup tableLookup:
                instructions.Add(CilOpCodes.Ldstr, tableLookup.Values);
                LoadOperand(tableLookup.Index, method, locals, writeLine, stringCtor,
                    Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type);
                var charsGetter = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType.Methods
                    .Single(candidate => candidate.Name == "get_Chars" && candidate.Parameters.Count == 1);
                instructions.Add(CilOpCodes.Callvirt, charsGetter.ToMethodDescriptor(module));
                break;
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, importer.ImportMethod(runtimeMethod.RepresentedMethod.ToMethodDescriptor(module)));
                    break;
                }

                throw new UnresolvedCilSemanticException(method.FullName, "RUNTIME_METHOD_HANDLE", operand.ToString() ?? string.Empty);
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

                // 类型描述不是对象实例或已解析原生地址，不按名称猜构造器或填入零值。
                throw new UnresolvedCilSemanticException(method.FullName, "TYPE_OPERAND", type.FullName);
            default:
                throw new UnresolvedCilSemanticException(method.FullName, "UNKNOWN_LOAD", operand.ToString() ?? string.Empty);
        }
    }

    /// <summary>
    /// 把已由 RELA 与 metadata usage 双重证明的字符串表生成成托管 switch。
    /// 每条分支恰好压入一个字符串，越界路径压入原生保护分支的默认值。
    /// </summary>
    private static void EmitMetadataStringTableLookup(
        MetadataStringTableLookup lookup,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (lookup.Values.Count == 0)
            throw new DecompilerException("字符串元数据表不得为空。");

        var instructions = method.CilMethodBody!.Instructions;
        var caseInstructions = lookup.Values
            .Select(_ => new CilInstruction(CilOpCodes.Nop))
            .ToArray();
        var end = new CilInstruction(CilOpCodes.Nop);
        var labels = caseInstructions
            .Select(instruction => (ICilLabel)new CilInstructionLabel(instruction))
            .ToArray();

        LoadOperand(lookup.Index, method, locals, writeLine, stringCtor);
        instructions.Add(CilOpCodes.Switch, labels);
        instructions.Add(CilOpCodes.Ldstr, lookup.DefaultValue.Value);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));

        for (var index = 0; index < lookup.Values.Count; index++)
        {
            instructions.Add(caseInstructions[index]);
            instructions.Add(CilOpCodes.Ldstr, lookup.Values[index].Value);
            instructions.Add(CilOpCodes.Br, new CilInstructionLabel(end));
        }

        instructions.Add(end);
    }

    /// <summary>
    /// 长度值写入退 SSA 后的原生或扩展整数载体时，按目标局部的精确表示补齐 CIL 转换。
    /// Int32 目标保持原样；引用和未知目标不添加转换，由栈验证继续报告真实类型缺口。
    /// </summary>
    private static void EmitLengthRepresentationConversion(
        TypeAnalysisContext? expectedType,
        CilInstructionCollection instructions)
    {
        CilOpCode? conversion = expectedType?.FullName switch
        {
            "System.IntPtr" => CilOpCodes.Conv_I,
            "System.UIntPtr" => CilOpCodes.Conv_U,
            "System.Int64" => CilOpCodes.Conv_I8,
            "System.UInt64" => CilOpCodes.Conv_U8,
            _ => null,
        };
        if (conversion != null)
            instructions.Add(conversion.Value);
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

    /// <summary>
    /// 生成 ARM64 FSQRT 对应的标量浮点 CIL。单精度先提升到双精度调用 Math.Sqrt，
    /// 再按原生目标宽度收窄；同一套实现覆盖 S/D 两种寄存器。
    /// </summary>
    private static void EmitFloatingSquareRoot(
        Instruction instruction,
        MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine,
        MemberReference stringCtor)
    {
        if (instruction.Operands is not [var destination, var source, Immediate { Value: 32 or 64 } width])
            throw new DecompilerException($"浮点平方根操作数无效：{instruction}");

        var module = method.DeclaringModule!;
        var factory = module.CorLibTypeFactory;
        var importer = module.DefaultImporter!;
        var instructions = method.CilMethodBody!.Instructions;

        LoadOperand(source, method, locals, writeLine, stringCtor);
        if (width.Value == 32)
            instructions.Add(CilOpCodes.Conv_R4);
        instructions.Add(CilOpCodes.Conv_R8);

        var sqrtMethod = factory.CorLibScope
            .CreateTypeReference("System", "Math")
            .CreateMemberReference(
                "Sqrt",
                MethodSignature.CreateStatic(factory.Double, [factory.Double]))
            .ImportWith(importer);
        instructions.Add(CilOpCodes.Call, sqrtMethod);
        if (width.Value == 32)
            instructions.Add(CilOpCodes.Conv_R4);
        StoreToOperand(destination, method, locals, writeLine);
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

    internal static bool TryGetI4Immediate(
        Immediate immediate,
        TypeAnalysisContext? expectedType,
        out int value)
    {
        value = default;
        if (expectedType == null)
            return false;

        var storageType = expectedType.IsEnumType
            ? expectedType.EnumUnderlyingType
            : expectedType;
        if (storageType?.FullName is not (
                "System.Boolean" or "System.Char"
                or "System.SByte" or "System.Byte"
                or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32"))
            return false;

        if (immediate.Value is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)immediate.Value;
            return true;
        }

        if (immediate.Value is >= 0 and <= uint.MaxValue)
        {
            value = unchecked((int)(uint)immediate.Value);
            return true;
        }

        return false;
    }

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
        // 形参缺失是原生实参恢复缺口，即使声明可选也不代表调用点传入默认值。
        if (availableArgs < targetMethod.Parameters.Count)
            throw new UnresolvedCilSemanticException(method.FullName, "CALL_ARGUMENTS",
                $"target={targetMethod.FullName}; required={targetMethod.Parameters.Count}; available={availableArgs}");
        for (var i = 0; i < targetMethod.Parameters.Count; i++)
            LoadOperand(operands[firstParameterIndex + i], method, locals, writeLine, stringCtor,
                targetMethod.Parameters[i].ParameterType);
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

                    // 原生地址写入与局部变量赋值不同；只有上方已证明的托管引用写入可直接发射。
                }
                throw new UnresolvedCilSemanticException(method.FullName, "MEMORY_STORE", operand.ToString() ?? string.Empty);

            default:
                throw new UnresolvedCilSemanticException(method.FullName, "UNKNOWN_STORE", operand.ToString() ?? string.Empty);
        }
    }
}
