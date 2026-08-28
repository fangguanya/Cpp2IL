using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将跳往纯复制尾返回适配器的无条件边恢复为直接返回。
/// IL2CPP常让数百个命中分支先写共享局部，再跳到Move/Return尾链；保留该形态会让
/// C#反编译器生成超深else树。这里只折叠无副作用且返回值来源唯一的透明尾链。
/// </summary>
public static class TailReturnRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var contracts = BuildReturnContracts(graph.Blocks);
        var changed = 0;

        foreach (var block in graph.Blocks.ToList())
        {
            var jump = block.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop);
            if (jump is not { OpCode: OpCode.Jump, Operands.Count: 1 }
                || jump.Operands[0] is not Block target
                || block.Successors.Count != 1
                || !ReferenceEquals(block.Successors[0], target)
                || !contracts.TryGetValue(target, out var returnedOperand)
                || returnedOperand == null
                || !HasProvenReturnValue(method, block, jump, returnedOperand))
                continue;

            jump.OpCode = OpCode.Return;
            jump.SetOperands(returnedOperand);

            ISILControlFlowGraph.RemovePredecessorAndPhiInputs(target, block);
            block.Successors.Clear();
            if (!block.Successors.Contains(graph.ExitBlock))
                block.Successors.Add(graph.ExitBlock);
            if (!graph.ExitBlock.Predecessors.Contains(block))
                graph.ExitBlock.Predecessors.Add(block);
            block.CalculateBlockType();
            changed++;
        }

        if (changed > 0)
            graph.RemoveUnreachableBlocks();

        return changed;
    }

    /// <summary>
    /// 证明把当前边折叠为 Return 后，返回操作数在该点一定已赋值且类型合法。
    /// 常量和入口参数天然支配当前边；普通局部必须在当前基本块中找到最近的显式定义，
    /// 不能借用其他前驱对共享返回槽的赋值，否则空桥会生成未赋值的伪早退。
    /// </summary>
    private static bool HasProvenReturnValue(
        MethodAnalysisContext method,
        Block block,
        Instruction jump,
        IOperand returnedOperand)
    {
        if (Instruction.IsConstantValue(returnedOperand))
            return IsOperandCompatible(method, returnedOperand);

        if (returnedOperand is not LocalVariable returnedLocal)
            return false;

        var jumpIndex = block.Instructions.IndexOf(jump);
        for (var index = jumpIndex - 1; index >= 0; index--)
        {
            var definition = block.Instructions[index];
            if (!ReferenceEquals(definition.Destination, returnedLocal))
                continue;

            return IsDefinitionCompatible(method, definition);
        }

        return method.ParameterLocals.Contains(returnedLocal)
               && IsOperandCompatible(method, returnedLocal);
    }

    /// <summary>
    /// 使用定义指令的真实生产值校验类型，避免仅凭已污染的目标局部类型放行错误返回。
    /// </summary>
    private static bool IsDefinitionCompatible(
        MethodAnalysisContext method,
        Instruction definition)
    {
        if (definition.OpCode == OpCode.Move && definition.Operands is [_, { } source])
            return IsOperandCompatible(method, source);

        if (definition.OpCode == OpCode.Call
            && definition.Operands.FirstOrDefault() is MethodAnalysisContext calledMethod)
            return IsTypeCompatible(calledMethod.ReturnType, method.ReturnType);

        if (definition.OpCode == OpCode.Newobj
            && definition.Operands is [_, TypeAnalysisContext allocatedType, ..])
            return IsTypeCompatible(
                allocatedType is RuntimeClassTypeAnalysisContext runtimeClass
                    ? runtimeClass.RepresentedType
                    : allocatedType,
                method.ReturnType);

        return false;
    }

    private static bool IsOperandCompatible(MethodAnalysisContext method, IOperand operand)
    {
        var systemTypes = method.AppContext.SystemTypes;
        return operand switch
        {
            LocalVariable { Type: { } type } => IsTypeCompatible(type, method.ReturnType),
            StringLiteral => IsTypeCompatible(systemTypes.SystemStringType, method.ReturnType),
            FloatLiteral => IsTypeCompatible(systemTypes.SystemSingleType, method.ReturnType),
            DoubleLiteral => IsTypeCompatible(systemTypes.SystemDoubleType, method.ReturnType),
            TypeAnalysisContext => IsTypeCompatible(systemTypes.SystemTypeType, method.ReturnType),
            Immediate immediate => IsImmediateCompatible(method, immediate),
            _ => false,
        };
    }

    private static bool IsImmediateCompatible(MethodAnalysisContext method, Immediate immediate)
    {
        var returnType = method.ReturnType;
        if (immediate.Value == 0 && IsManagedReferenceType(returnType))
            return true;

        var systemTypes = method.AppContext.SystemTypes;
        return returnType.IsEnumType
               || systemTypes.TryGetIl2CppTypeEnum(returnType, out var primitiveType)
               && primitiveType is
                   Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN
                   or Il2CppTypeEnum.IL2CPP_TYPE_CHAR
                   or Il2CppTypeEnum.IL2CPP_TYPE_I1
                   or Il2CppTypeEnum.IL2CPP_TYPE_U1
                   or Il2CppTypeEnum.IL2CPP_TYPE_I2
                   or Il2CppTypeEnum.IL2CPP_TYPE_U2
                   or Il2CppTypeEnum.IL2CPP_TYPE_I4
                   or Il2CppTypeEnum.IL2CPP_TYPE_U4
                   or Il2CppTypeEnum.IL2CPP_TYPE_I8
                   or Il2CppTypeEnum.IL2CPP_TYPE_U8
                   or Il2CppTypeEnum.IL2CPP_TYPE_I
                   or Il2CppTypeEnum.IL2CPP_TYPE_U;
    }

    private static bool IsTypeCompatible(TypeAnalysisContext producedType, TypeAnalysisContext returnType)
        => GenericCallRebinder.TypesEquivalent(producedType, returnType)
           || IsManagedReferenceType(producedType)
           && IsManagedReferenceType(returnType)
           && GenericCallRebinder.IsAssignableToManagedProjection(producedType, returnType);

    /// <summary>
    /// 与托管引用恢复器使用同一边界：排除原生地址、运行时元数据和静态存储包装；
    /// 泛型参数仅在元数据明确声明引用类型约束时视为托管引用。
    /// </summary>
    private static bool IsManagedReferenceType(TypeAnalysisContext type)
        => !type.IsValueType
           && type is not (ByRefTypeAnalysisContext
               or PointerTypeAnalysisContext
               or RuntimeClassTypeAnalysisContext
               or RuntimeMethodInfoAnalysisContext
               or RuntimeFieldInfoAnalysisContext
               or StaticFieldStorageTypeAnalysisContext
               or RgctxTableTypeAnalysisContext
               or MethodRgctxTableTypeAnalysisContext
               or SentinelTypeAnalysisContext)
           && (type is not GenericParameterTypeAnalysisContext genericParameter
               || genericParameter.Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));

    /// <summary>
    /// 为每个透明尾块计算“从该块进入最终会返回哪个当前可用操作数”。
    /// 记忆化状态保证每块只分析一次；访问中的块再次出现即为循环，不建立契约。
    /// </summary>
    private static Dictionary<Block, IOperand?> BuildReturnContracts(IReadOnlyList<Block> blocks)
    {
        var contracts = new Dictionary<Block, IOperand?>();
        var states = new Dictionary<Block, VisitState>();

        foreach (var block in blocks)
            ResolveContract(block, contracts, states);

        return contracts;
    }

    private static IOperand? ResolveContract(
        Block block,
        Dictionary<Block, IOperand?> contracts,
        Dictionary<Block, VisitState> states)
    {
        if (states.TryGetValue(block, out var state))
            return state == VisitState.Completed ? contracts[block] : null;

        states[block] = VisitState.Visiting;
        var effective = block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToList();
        IOperand? contract = null;

        if (effective is [{ OpCode: OpCode.Return, Operands.Count: 1 } terminalReturn])
        {
            contract = terminalReturn.Operands[0];
        }
        else if (TryGetOnlySuccessor(block, effective, out var body, out var successor)
                 && ResolveContract(successor, contracts, states) is { } successorContract)
        {
            if (body.Count == 0)
            {
                contract = successorContract;
            }
            else if (body is
                     [
                         {
                             OpCode: OpCode.Move,
                             Operands: [LocalVariable destination, IOperand source]
                         }
                     ]
                     && ReferenceEquals(destination, successorContract))
            {
                contract = source;
            }
        }

        states[block] = VisitState.Completed;
        contracts[block] = contract;
        return contract;
    }

    /// <summary>
    /// 提取透明块的唯一后继和去掉无条件跳转后的有效主体；条件边及多后继块均不参与。
    /// </summary>
    private static bool TryGetOnlySuccessor(
        Block block,
        IReadOnlyList<Instruction> effective,
        out IReadOnlyList<Instruction> body,
        out Block successor)
    {
        body = [];
        successor = null!;
        if (block.Successors.Count != 1)
            return false;

        successor = block.Successors[0];
        if (effective.Count > 0 && effective[^1].OpCode == OpCode.Jump)
        {
            if (effective[^1].Operands.Count != 1
                || effective[^1].Operands[0] is not Block jumpTarget
                || !ReferenceEquals(jumpTarget, successor))
                return false;

            body = effective.Take(effective.Count - 1).ToList();
            return true;
        }

        body = effective;
        return true;
    }

    private enum VisitState
    {
        Visiting,
        Completed,
    }
}
