using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 IL2CPP 内联展开的“分配派生对象、调用基类构造器、写入只读字段”折回派生构造调用。
/// </summary>
public static class InlineConstructorRecovery
{
    /// <summary>
    /// 只在派生类型、唯一构造签名和连续只读字段初始化三项证据同时成立时改写。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is null)
            return 0;

        var recovered = 0;
        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            for (var allocationIndex = 0; allocationIndex < block.Instructions.Count; allocationIndex++)
            {
                var allocation = block.Instructions[allocationIndex];
                if (allocation is not { OpCode: OpCode.Newobj, Operands: [LocalVariable receiver, var typeOperand] }
                    || ResolveAllocatedType(typeOperand) is not { } allocatedType)
                    continue;

                var constructorMatch = FindConstructorCall(
                    method.ControlFlowGraph,
                    block,
                    allocationIndex,
                    receiver);
                if (constructorMatch is null)
                    continue;

                var currentConstructor = (MethodAnalysisContext)constructorMatch.Call.Operands[0];
                if (GenericCallRebinder.TypesEquivalent(currentConstructor.DeclaringType, allocatedType))
                    continue;

                if (TryRecoverStateMachineConstructor(
                        method.ControlFlowGraph,
                        constructorMatch,
                        receiver,
                        allocatedType))
                {
                    recovered++;
                    continue;
                }

                var stores = CollectReadonlyInitializers(
                    method.ControlFlowGraph,
                    constructorMatch.Block,
                    constructorMatch.Index + 1,
                    receiver,
                    allocatedType);
                if (stores.Count == 0
                    || ResolveUniqueConstructor(allocatedType, stores) is not { } recoveredConstructor)
                    continue;

                var operands = new List<IOperand> { recoveredConstructor, receiver };
                operands.AddRange(stores.Select(store => store.Source));
                constructorMatch.Call.SetOperands(operands);
                foreach (var store in stores)
                {
                    store.Instruction.OpCode = OpCode.Nop;
                    store.Instruction.SetOperands();
                }
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// 把编译器状态机中“分配对象后直接写入同名构造参数字段”折回真实构造调用。
    /// </summary>
    private static bool TryRecoverStateMachineConstructor(
        ISILControlFlowGraph graph,
        ConstructorMatch baseConstructor,
        LocalVariable receiver,
        TypeAnalysisContext allocatedType)
    {
        if (!allocatedType.Name.Contains(">d__", StringComparison.Ordinal))
            return false;

        var definition = allocatedType is GenericInstanceTypeAnalysisContext generic
            ? generic.GenericType
            : allocatedType;
        var matches = definition.Methods
            .Where(candidate => candidate.Name == ".ctor"
                && !candidate.IsStatic
                && candidate.Visibility == MethodAttributes.Public
                && candidate.Parameters.Count > 0)
            .Select(candidate => MatchStateMachineConstructor(
                graph,
                baseConstructor.Block,
                baseConstructor.Index + 1,
                receiver,
                allocatedType,
                candidate))
            .Where(match => match is not null)
            .Cast<StateMachineConstructorMatch>()
            .ToArray();
        if (matches.Length != 1)
            return false;

        var match = matches[0];
        var constructor = allocatedType is GenericInstanceTypeAnalysisContext genericOwner
            ? new ConcreteGenericMethodAnalysisContext(match.Constructor, genericOwner.GenericArguments, [])
            : match.Constructor;
        var operands = new List<IOperand> { constructor, receiver };
        operands.AddRange(match.Sources);
        baseConstructor.Call.SetOperands(operands);
        foreach (var store in match.Stores)
        {
            store.OpCode = OpCode.Nop;
            store.SetOperands();
        }

        return true;
    }

    /// <summary>
    /// 按构造参数顺序匹配紧邻分配点的同名私有字段写入，普通指令立即终止候选。
    /// </summary>
    private static StateMachineConstructorMatch? MatchStateMachineConstructor(
        ISILControlFlowGraph graph,
        Block startBlock,
        int startIndex,
        LocalVariable receiver,
        TypeAnalysisContext allocatedType,
        MethodAnalysisContext constructor)
    {
        var stores = new List<Instruction>();
        var sources = new List<IOperand>();
        var visited = new HashSet<Block> { startBlock };
        var block = startBlock;
        var instructionIndex = startIndex;
        foreach (var parameter in constructor.Parameters)
        {
            Instruction? fieldStore = null;
            while (true)
            {
                while (instructionIndex < block.Instructions.Count
                    && block.Instructions[instructionIndex].OpCode == OpCode.Nop)
                    instructionIndex++;
                if (instructionIndex < block.Instructions.Count)
                {
                    fieldStore = block.Instructions[instructionIndex++];
                    break;
                }

                var successors = block.Successors
                    .Where(successor => !ReferenceEquals(successor, graph.ExitBlock))
                    .ToArray();
                if (successors.Length != 1
                    || successors[0].Predecessors.Count != 1
                    || !visited.Add(successors[0]))
                    return null;
                block = successors[0];
                instructionIndex = 0;
            }

            if (fieldStore is not
                {
                    OpCode: OpCode.Move,
                    Operands: [FieldReference field, var source],
                }
                || !ReferenceEquals(field.Local, receiver)
                || !GenericCallRebinder.TypesEquivalent(field.Field.DeclaringType, allocatedType)
                || field.Field.Visibility != FieldAttributes.Private
                || !string.Equals(field.Field.Name, parameter.Name, StringComparison.Ordinal)
                || !GenericCallRebinder.TypesEquivalent(field.Field.FieldType, parameter.ParameterType))
                return null;

            stores.Add(fieldStore);
            sources.Add(source);
        }

        return new StateMachineConstructorMatch(constructor, stores, sources);
    }

    /// <summary>
    /// 沿唯一无汇合后继查找首个以新对象为接收者的构造调用。
    /// </summary>
    private static ConstructorMatch? FindConstructorCall(
        ISILControlFlowGraph graph,
        Block startBlock,
        int allocationIndex,
        LocalVariable receiver)
    {
        var visited = new HashSet<Block>();
        var block = startBlock;
        var startIndex = allocationIndex + 1;
        while (visited.Add(block))
        {
            for (var index = startIndex; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                if (instruction is
                    {
                        OpCode: OpCode.CallVoid,
                        Operands: [MethodAnalysisContext { Name: ".ctor" }, var candidateReceiver, ..],
                    }
                    && ReferenceEquals(candidateReceiver, receiver))
                    return new ConstructorMatch(block, index, instruction);
                if (instruction.OpCode is not (OpCode.Nop or OpCode.Jump))
                    return null;
            }

            var successors = block.Successors
                .Where(successor => !ReferenceEquals(successor, graph.ExitBlock))
                .ToArray();
            if (successors.Length != 1 || successors[0].Predecessors.Count != 1)
                return null;
            block = successors[0];
            startIndex = 0;
        }

        return null;
    }

    /// <summary>
    /// 收集构造调用后连续写入同一对象的私有只读字段；普通指令立即终止候选窗口。
    /// </summary>
    private static List<ReadonlyInitializer> CollectReadonlyInitializers(
        ISILControlFlowGraph graph,
        Block startBlock,
        int startIndex,
        LocalVariable receiver,
        TypeAnalysisContext allocatedType)
    {
        var result = new List<ReadonlyInitializer>();
        var visited = new HashSet<Block>();
        var block = startBlock;
        var index = startIndex;
        while (visited.Add(block))
        {
            for (; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                if (instruction.OpCode == OpCode.Nop)
                    continue;
                if (instruction is not { OpCode: OpCode.Move, Operands: [FieldReference field, var source] }
                    || !ReferenceEquals(field.Local, receiver)
                    || !GenericCallRebinder.TypesEquivalent(field.Field.DeclaringType, allocatedType)
                    || field.Field.Visibility != FieldAttributes.Private
                    || !field.Field.Attributes.HasFlag(FieldAttributes.InitOnly))
                    return result;

                result.Add(new ReadonlyInitializer(instruction, field.Field, source));
            }

            var successors = block.Successors
                .Where(successor => !ReferenceEquals(successor, graph.ExitBlock))
                .ToArray();
            if (successors.Length != 1 || successors[0].Predecessors.Count != 1)
                return result;
            block = successors[0];
            index = 0;
        }

        return result;
    }

    /// <summary>
    /// 以字段声明顺序和类型精确匹配唯一公开派生构造器。
    /// </summary>
    private static MethodAnalysisContext? ResolveUniqueConstructor(
        TypeAnalysisContext allocatedType,
        IReadOnlyList<ReadonlyInitializer> stores)
    {
        var definition = allocatedType is GenericInstanceTypeAnalysisContext generic
            ? generic.GenericType
            : allocatedType;
        var candidates = definition.Methods
            .Where(candidate => candidate.Name == ".ctor"
                && !candidate.IsStatic
                && candidate.Visibility == MethodAttributes.Public
                && candidate.Parameters.Count == stores.Count)
            .Where(candidate => candidate.Parameters
                .Select(parameter => parameter.ParameterType)
                .Zip(
                    stores.Select(store => store.Field.FieldType),
                    (parameterType, fieldType) => (First: parameterType, Second: fieldType))
                .All(pair => GenericCallRebinder.TypesEquivalent(pair.First, pair.Second)))
            .ToArray();
        if (candidates.Length != 1)
            return null;

        return allocatedType is GenericInstanceTypeAnalysisContext genericOwner
            ? new ConcreteGenericMethodAnalysisContext(candidates[0], genericOwner.GenericArguments, [])
            : candidates[0];
    }

    /// <summary>
    /// 从 Newobj 类型操作数提取真实分配类型。
    /// </summary>
    private static TypeAnalysisContext? ResolveAllocatedType(IOperand operand) => operand switch
    {
        RuntimeClassTypeAnalysisContext runtimeClass => runtimeClass.RepresentedType,
        TypeAnalysisContext type => type,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext runtimeClass } => runtimeClass.RepresentedType,
        _ => null,
    };

    private sealed record ConstructorMatch(Block Block, int Index, Instruction Call);

    private sealed record StateMachineConstructorMatch(
        MethodAnalysisContext Constructor,
        IReadOnlyList<Instruction> Stores,
        IReadOnlyList<IOperand> Sources);

    private sealed record ReadonlyInitializer(
        Instruction Instruction,
        FieldAnalysisContext Field,
        IOperand Source);
}
