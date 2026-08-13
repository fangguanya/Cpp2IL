using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 使用已经确定的接收者和实参类型重新具体化共享泛型方法体，纠正IL2CPP把共享体登记为object实例的问题。
/// </summary>
public static class GenericCallRebinder
{
    public static bool Run(MethodAnalysisContext method)
    {
        // SSA中每个局部只有一个定义。调用签名本身传播出的object属于弱证据，
        // 必须优先沿定义链读取字段加载等权威来源，避免共享体类型反向污染接收者。
        var definitions = new Dictionary<LocalVariable, IOperand>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands.Count >= 2
                && instruction.Operands[0] is LocalVariable destination)
                definitions[destination] = instruction.Operands[1];
        }

        var changed = false;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            changed |= TryRebind(instruction, definitions);

        return changed;
    }

    internal static bool TryRebind(Instruction call)
        => TryRebind(call, null);

    internal static bool TryRebind(
        Instruction call,
        IReadOnlyDictionary<LocalVariable, IOperand>? definitions)
    {
        if (!call.IsCall
            || call.Operands.Count < 2
            || call.Operands[0] is not ConcreteGenericMethodAnalysisContext current)
            return false;

        var typeArguments = current.TypeGenericParameters.ToArray();
        var methodArguments = current.MethodGenericParameters.ToArray();
        var changed = false;
        var firstArgument = call.OpCode == OpCode.CallVoid ? 1 : 2;
        GenericInstanceTypeAnalysisContext? concreteReceiver = null;
        var receiverDefinesTypeArguments = false;

        if (!current.IsStatic
            && firstArgument < call.Operands.Count
            && OperandType(call.Operands[firstArgument], definitions) is GenericInstanceTypeAnalysisContext receiver
            && SameTypeDefinition(receiver.GenericType, current.BaseMethodContext.DeclaringType)
            && receiver.GenericArguments.Count == current.BaseMethodContext.DeclaringType!.GenericParameters.Count
            && !receiver.GenericArguments.Any(LocalVariables.ContainsUninstantiatedGenericParameter))
        {
            // 封闭接收者是声明类型实参的最高优先级证据。即便它与当前目标一致，也要阻止
            // List<object>.Add(string) 被普通实参错误收窄成 List<string>。
            receiverDefinesTypeArguments = true;
            if (!TypeListsEquivalent(receiver.GenericArguments, typeArguments))
            {
                typeArguments = receiver.GenericArguments.ToArray();
                concreteReceiver = receiver;
                changed = true;
            }
        }

        if (!receiverDefinesTypeArguments
            && current.BaseMethodContext.DeclaringType is { GenericParameters.Count: > 0 } declaringType
            && typeArguments.Any(LocalVariables.ContainsUninstantiatedGenericParameter)
            && TryInferTypeArguments(
                call,
                current.BaseMethodContext,
                declaringType.GenericParameters.Count,
                firstArgument,
                definitions,
                out var inferredTypeArguments)
            && !TypeListsEquivalent(inferredTypeArguments, typeArguments))
        {
            // 共享泛型实例的接收者可能已经被寄存器复用污染；此时由成员参数中的 VAR
            // 反推声明类型实参，例如 List<!0>.AddWithResize(string) -> List<string>。
            typeArguments = inferredTypeArguments;
            changed = true;
        }

        if (current.BaseMethodContext.GenericParameters.Count > 0
            && TryInferMethodArguments(call, current.BaseMethodContext, firstArgument, definitions, out var inferred)
            && !TypeListsEquivalent(inferred, methodArguments))
        {
            methodArguments = inferred;
            changed = true;
        }

        if (!changed)
            return false;

        var rebound = new ConcreteGenericMethodAnalysisContext(
            current.BaseMethodContext,
            typeArguments,
            methodArguments);
        call.SetOperand(0, rebound);

        // 接收者若只带着旧共享体的声明类型，则用定义链确认的具体类型覆盖；
        // 字段加载等更具体的既有类型保持不动。
        if (concreteReceiver != null
            && call.Operands[firstArgument] is LocalVariable receiverLocal
            && (receiverLocal.Type == null || TypesEquivalent(receiverLocal.Type, current.DeclaringType)))
            receiverLocal.Type = concreteReceiver;

        // 旧共享体传播出的object/Enumerator<object>不是独立证据；目标重绑定后以新签名覆盖该返回槽。
        if (call.Destination is LocalVariable destination
            && (destination.Type == null || TypesEquivalent(destination.Type, current.ReturnType)))
            destination.Type = rebound.ReturnType;

        return true;
    }

    private static bool TryInferMethodArguments(
        Instruction call,
        MethodAnalysisContext baseMethod,
        int firstArgument,
        IReadOnlyDictionary<LocalVariable, IOperand>? definitions,
        out TypeAnalysisContext[] inferred)
        => TryInferGenericArguments(
            call,
            baseMethod,
            baseMethod.GenericParameters.Count,
            firstArgument,
            definitions,
            Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
            out inferred);

    private static bool TryInferTypeArguments(
        Instruction call,
        MethodAnalysisContext baseMethod,
        int genericParameterCount,
        int firstArgument,
        IReadOnlyDictionary<LocalVariable, IOperand>? definitions,
        out TypeAnalysisContext[] inferred)
        => TryInferGenericArguments(
            call,
            baseMethod,
            genericParameterCount,
            firstArgument,
            definitions,
            Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            out inferred);

    private static bool TryInferGenericArguments(
        Instruction call,
        MethodAnalysisContext baseMethod,
        int genericParameterCount,
        int firstArgument,
        IReadOnlyDictionary<LocalVariable, IOperand>? definitions,
        Il2CppTypeEnum genericParameterKind,
        out TypeAnalysisContext[] inferred)
    {
        inferred = new TypeAnalysisContext[genericParameterCount];
        var parameterStart = firstArgument + (baseMethod.IsStatic ? 0 : 1);
        var matchedAny = false;

        for (var parameterIndex = 0; parameterIndex < baseMethod.Parameters.Count; parameterIndex++)
        {
            var operandIndex = parameterStart + parameterIndex;
            if (operandIndex >= call.Operands.Count
                || OperandType(call.Operands[operandIndex], definitions) is not { } actual)
                continue;

            if (!TryUnifyGenericParameters(
                    baseMethod.Parameters[parameterIndex].ParameterType,
                    actual,
                    genericParameterKind,
                    inferred,
                    ref matchedAny))
                return false;
        }

        return matchedAny && inferred.All(argument => argument != null);
    }

    private static bool TryUnifyGenericParameters(
        TypeAnalysisContext pattern,
        TypeAnalysisContext actual,
        Il2CppTypeEnum genericParameterKind,
        TypeAnalysisContext[] inferred,
        ref bool matchedAny)
    {
        if (pattern is GenericParameterTypeAnalysisContext
            {
                Type: var parameterKind,
                Index: var index
            }
            && parameterKind == genericParameterKind)
        {
            if (index < 0 || index >= inferred.Length)
                return false;

            if (inferred[index] != null && !TypesEquivalent(inferred[index], actual))
                return false;

            inferred[index] = actual;
            matchedAny = true;
            return true;
        }

        if (pattern is GenericInstanceTypeAnalysisContext genericPattern)
        {
            if (!TryProjectToGenericDefinition(actual, genericPattern.GenericType, out var projected)
                || projected.GenericArguments.Count != genericPattern.GenericArguments.Count)
                return false;

            for (var argumentIndex = 0; argumentIndex < genericPattern.GenericArguments.Count; argumentIndex++)
            {
                if (!TryUnifyGenericParameters(
                        genericPattern.GenericArguments[argumentIndex],
                        projected.GenericArguments[argumentIndex],
                        genericParameterKind,
                        inferred,
                        ref matchedAny))
                    return false;
            }

            return true;
        }

        if (pattern is SzArrayTypeAnalysisContext patternArray
            && actual is SzArrayTypeAnalysisContext actualArray)
            return TryUnifyGenericParameters(
                patternArray.ElementType,
                actualArray.ElementType,
                genericParameterKind,
                inferred,
                ref matchedAny);

        if (pattern is ByRefTypeAnalysisContext patternByRef
            && actual is ByRefTypeAnalysisContext actualByRef)
            return TryUnifyGenericParameters(
                patternByRef.ElementType,
                actualByRef.ElementType,
                genericParameterKind,
                inferred,
                ref matchedAny);

        return TypesEquivalent(pattern, actual);
    }

    private static bool TryProjectToGenericDefinition(
        TypeAnalysisContext actual,
        TypeAnalysisContext genericDefinition,
        out GenericInstanceTypeAnalysisContext projected)
    {
        var queue = new Queue<TypeAnalysisContext>();
        var visited = new HashSet<string>();
        queue.Enqueue(actual);

        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (!visited.Add(candidate.FullName))
                continue;

            if (candidate is GenericInstanceTypeAnalysisContext genericCandidate)
            {
                if (SameTypeDefinition(genericCandidate.GenericType, genericDefinition))
                {
                    projected = genericCandidate;
                    return true;
                }

                foreach (var parent in DirectParents(genericCandidate))
                    queue.Enqueue(parent);
            }
            else
            {
                if (candidate.BaseType != null)
                    queue.Enqueue(candidate.BaseType);
                foreach (var @interface in candidate.InterfaceContexts)
                    queue.Enqueue(@interface);
            }
        }

        projected = null!;
        return false;
    }

    private static IEnumerable<TypeAnalysisContext> DirectParents(GenericInstanceTypeAnalysisContext type)
    {
        if (type.GenericType.BaseType != null)
            yield return GenericInstantiation.Instantiate(type.GenericType.BaseType, type.GenericArguments, []);

        foreach (var @interface in type.GenericType.InterfaceContexts)
            yield return GenericInstantiation.Instantiate(@interface, type.GenericArguments, []);
    }

    private static TypeAnalysisContext? OperandType(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, IOperand>? definitions,
        HashSet<LocalVariable>? visited = null)
    {
        switch (operand)
        {
            case LocalVariable local:
                visited ??= [];
                if (definitions != null
                    && visited.Add(local)
                    && definitions.TryGetValue(local, out var source)
                    && OperandType(source, definitions, visited) is { } sourceType)
                    return sourceType;

                return local.Type;
            case AddressOf { Target: LocalVariable addressed }:
                return OperandType(addressed, definitions, visited);
            case FieldReference field:
                return field.Field.FieldType;
            case TypeAnalysisContext type:
                return type;
            default:
                return null;
        }
    }

    private static bool SameTypeDefinition(TypeAnalysisContext? left, TypeAnalysisContext? right)
        => left != null
           && right != null
           && (ReferenceEquals(left, right)
               || (left.Definition != null && ReferenceEquals(left.Definition, right.Definition)));

    internal static bool TypesEquivalent(TypeAnalysisContext? left, TypeAnalysisContext? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;

        if (left is GenericInstanceTypeAnalysisContext leftGeneric
            && right is GenericInstanceTypeAnalysisContext rightGeneric)
            return SameTypeDefinition(leftGeneric.GenericType, rightGeneric.GenericType)
                   && TypeListsEquivalent(leftGeneric.GenericArguments, rightGeneric.GenericArguments);

        if (left is SzArrayTypeAnalysisContext leftArray && right is SzArrayTypeAnalysisContext rightArray)
            return TypesEquivalent(leftArray.ElementType, rightArray.ElementType);

        if (left is ByRefTypeAnalysisContext leftByRef && right is ByRefTypeAnalysisContext rightByRef)
            return TypesEquivalent(leftByRef.ElementType, rightByRef.ElementType);

        return SameTypeDefinition(left, right) || left.FullName == right.FullName;
    }

    private static bool TypeListsEquivalent(
        IReadOnlyList<TypeAnalysisContext> left,
        IReadOnlyList<TypeAnalysisContext> right)
        => left.Count == right.Count
           && left.Select((type, index) => TypesEquivalent(type, right[index])).All(equal => equal);
}
