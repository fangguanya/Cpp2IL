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

    /// <summary>
    /// 在所有晚期接收者恢复完成后，只扫描仍以 object 共享实例为声明目标的实例调用。
    /// 该入口复用唯一的 TryRebind 裁决，不再次执行字段定义链或实参泛型推断。
    /// </summary>
    internal static int RunLateSharedReceiverTargets(MethodAnalysisContext method)
    {
        var reboundCount = 0;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!TryRebindLateSharedReceiverTarget(instruction))
                continue;

            reboundCount++;
        }

        return reboundCount;
    }

    /// <summary>
    /// 在退 SSA 与业务枚举实参全部稳定后，闭合仍含 Int32Enum 的共享泛型调用链。
    /// 这里只消费调用签名、调用结果槽和当前实参三类最终证据，不再读取已经失去 SSA 唯一性的
    /// 字段定义链；每次晋级都会永久移除至少一个 Int32Enum 占位，因此循环严格单调收敛。
    /// </summary>
    internal static int RunLateSharedEnumClosure(MethodAnalysisContext method)
    {
        var calls = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.IsCall)
            .ToArray();
        if (calls.Length == 0)
            return 0;

        var mutationCount = 0;
        for (var pass = 0; pass <= calls.Length; pass++)
        {
            var changed = false;
            foreach (var call in calls)
            {
                if (!CallContainsSharedEnumPlaceholder(call))
                    continue;

                changed |= SynchronizeSharedOperandsFromConcreteTarget(call);
                changed |= TryRebind(call);
            }

            if (!changed)
                return mutationCount;

            mutationCount++;
        }

        throw new DecompilerException(
            $"Late shared enum generic closure not settling after {calls.Length + 1} passes");
    }

    private static bool CallContainsSharedEnumPlaceholder(Instruction call)
    {
        if (call.Operands.Count == 0
            || call.Operands[0] is not MethodAnalysisContext calledMethod)
            return false;

        if (calledMethod is ConcreteGenericMethodAnalysisContext concrete
            && (concrete.TypeGenericParameters.Any(ContainsSharedEnumPlaceholder)
                || concrete.MethodGenericParameters.Any(ContainsSharedEnumPlaceholder)))
            return true;

        if (call.Destination is LocalVariable { Type: { } destinationType }
            && ContainsSharedEnumPlaceholder(destinationType))
            return true;

        return call.Operands.OfType<LocalVariable>()
            .Any(local => local.Type != null && ContainsSharedEnumPlaceholder(local.Type));
    }

    /// <summary>
    /// 调用目标可能已在较早阶段具体化，而退 SSA 复制又把操作数局部保留成共享占位。
    /// 此处只允许同形泛型中的 object/Int32Enum 向目标签名的具体类型晋级；具体类型冲突保持红门。
    /// </summary>
    private static bool SynchronizeSharedOperandsFromConcreteTarget(Instruction call)
    {
        if (call.Operands.Count < 2
            || call.Operands[0] is not MethodAnalysisContext calledMethod)
            return false;

        var changed = false;
        var firstArgument = call.OpCode == OpCode.CallVoid ? 1 : 2;
        if (!calledMethod.IsStatic
            && firstArgument < call.Operands.Count
            && call.Operands[firstArgument] is LocalVariable receiver
            && calledMethod.DeclaringType is { } declaringType
            && IsSharedIl2CppGenericPlaceholder(receiver.Type, declaringType))
        {
            receiver.Type = declaringType;
            changed = true;
        }

        if (call.Destination is LocalVariable destination
            && IsSharedIl2CppGenericPlaceholder(destination.Type, calledMethod.ReturnType))
        {
            destination.Type = calledMethod.ReturnType;
            changed = true;
        }

        var parameterStart = firstArgument + (calledMethod.IsStatic ? 0 : 1);
        for (var parameterIndex = 0; parameterIndex < calledMethod.Parameters.Count; parameterIndex++)
        {
            var operandIndex = parameterStart + parameterIndex;
            if (operandIndex >= call.Operands.Count
                || call.Operands[operandIndex] is not LocalVariable parameter
                || !IsSharedIl2CppGenericPlaceholder(
                    parameter.Type,
                    calledMethod.Parameters[parameterIndex].ParameterType))
                continue;

            parameter.Type = calledMethod.Parameters[parameterIndex].ParameterType;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 仅当调用目标是共享 object 泛型实例、接收者是同一泛型定义的具体实例时执行末次重绑定。
    /// 已经具体化但互相冲突的目标与接收者保持原样，避免末次扫描覆盖业务侧具体类型。
    /// </summary>
    internal static bool TryRebindLateSharedReceiverTarget(Instruction call)
    {
        if (!call.IsCall
            || call.Operands.Count < 2
            || call.Operands[0] is not ConcreteGenericMethodAnalysisContext current
            || current.IsStatic)
            return false;

        var firstArgument = call.OpCode == OpCode.CallVoid ? 1 : 2;
        if (firstArgument >= call.Operands.Count
            || OperandType(call.Operands[firstArgument], null) is not GenericInstanceTypeAnalysisContext receiver
            || current.DeclaringType is not GenericInstanceTypeAnalysisContext currentOwner
            || !IsSharedObjectPlaceholder(currentOwner, receiver))
            return false;

        return TryRebind(call);
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
            && !receiver.GenericArguments.Any(LocalVariables.ContainsUninstantiatedGenericParameter)
            && !receiver.GenericArguments.Any(ContainsSharedEnumPlaceholder))
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
            && typeArguments.Any(argument =>
                LocalVariables.ContainsUninstantiatedGenericParameter(argument)
                || ContainsSharedEnumPlaceholder(argument))
            && TryInferTypeArguments(
                call,
                current.BaseMethodContext,
                declaringType.GenericParameters.Count,
                firstArgument,
                definitions,
                out var inferredTypeArguments)
            && CanAdoptInferredArguments(typeArguments, inferredTypeArguments)
            && !TypeListsEquivalent(inferredTypeArguments, typeArguments))
        {
            // 共享泛型实例的接收者可能已经被寄存器复用污染；此时由成员参数中的 VAR
            // 反推声明类型实参，例如 List<!0>.AddWithResize(string) -> List<string>。
            typeArguments = inferredTypeArguments;
            changed = true;
        }

        if (current.BaseMethodContext.GenericParameters.Count > 0
            && TryInferMethodArguments(call, current.BaseMethodContext, firstArgument, definitions, out var inferred)
            && CanAdoptInferredArguments(methodArguments, inferred)
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
        if (!current.IsStatic
            && call.Operands[firstArgument] is LocalVariable receiverLocal
            && rebound.DeclaringType is { } reboundDeclaringType
            && (receiverLocal.Type == null
                || TypesEquivalent(receiverLocal.Type, current.DeclaringType)
                || IsSharedIl2CppGenericPlaceholder(receiverLocal.Type, reboundDeclaringType)))
            receiverLocal.Type = concreteReceiver ?? reboundDeclaringType;

        // 旧共享体传播出的object/Enumerator<object>不是独立证据；目标重绑定后以新签名覆盖该返回槽。
        if (call.Destination is LocalVariable destination
            && (destination.Type == null
                || TypesEquivalent(destination.Type, current.ReturnType)
                || IsSharedIl2CppGenericPlaceholder(destination.Type, rebound.ReturnType)))
            destination.Type = rebound.ReturnType;

        // 中文注释：共享泛型目标曾传播到实参局部的旧 object 签名属于弱证据；目标重绑定后，
        // 仅当局部仍精确等于旧参数类型时同步新签名，真实业务侧的独立具体类型保持不动。
        var parameterStart = firstArgument + (current.IsStatic ? 0 : 1);
        for (var parameterIndex = 0; parameterIndex < current.Parameters.Count; parameterIndex++)
        {
            var operandIndex = parameterStart + parameterIndex;
            if (operandIndex < call.Operands.Count
                && call.Operands[operandIndex] is LocalVariable parameterLocal
                && (TypesEquivalent(parameterLocal.Type, current.Parameters[parameterIndex].ParameterType)
                    || IsSharedIl2CppGenericPlaceholder(
                        parameterLocal.Type,
                        rebound.Parameters[parameterIndex].ParameterType)))
                parameterLocal.Type = rebound.Parameters[parameterIndex].ParameterType;
        }

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
        // 中文注释：Enumerable 共享体常把委托中的 TSource 擦除成 object，而首个序列实参仍保留
        // 精确 IEnumerable<TSource>；只在该标准库声明类型内把后续 object 视为弱占位。
        var allowObjectFallback = baseMethod.DeclaringType?.FullName == "System.Linq.Enumerable";

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
                    ref matchedAny,
                    allowObjectFallback))
                return false;
        }

        // 中文注释：调用结果槽也是泛型方法返回类型的权威使用点。IL2CPP 共享体常把
        // Select/ToList 的前向结果登记成 Int32Enum；后续 Contains(具体枚举) 先恢复集合，
        // 下一轮再由该集合结果反向闭合 ToList 和 Select，形成无猜测的调用链不动点。
        if (call.Destination is LocalVariable { Type: { } destinationType }
            && !TryUnifyGenericParameters(
                baseMethod.ReturnType,
                destinationType,
                genericParameterKind,
                inferred,
                ref matchedAny,
                allowObjectFallback))
            return false;

        return matchedAny && inferred.All(argument => argument != null);
    }

    private static bool TryUnifyGenericParameters(
        TypeAnalysisContext pattern,
        TypeAnalysisContext actual,
        Il2CppTypeEnum genericParameterKind,
        TypeAnalysisContext[] inferred,
        ref bool matchedAny,
        bool allowObjectFallback)
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
            {
                if (!allowObjectFallback)
                    return false;

                // 中文注释：Enumerable 共享签名中的 object 与 Int32Enum 都是弱 ABI 证据。
                // 具体类型可以覆盖弱占位，弱占位也不能推翻已经形成的具体共识；
                // Int32Enum 仅能被真实枚举覆盖，普通 Int32 不具备该语义资格。
                if (IsWeakSharedArgument(actual, inferred[index]))
                    return true;
                if (IsWeakSharedArgument(inferred[index], actual))
                {
                    inferred[index] = actual;
                    matchedAny = true;
                    return true;
                }

                return false;
            }

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
                        ref matchedAny,
                        allowObjectFallback))
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
                ref matchedAny,
                allowObjectFallback);

        if (pattern is ByRefTypeAnalysisContext patternByRef
            && actual is ByRefTypeAnalysisContext actualByRef)
            return TryUnifyGenericParameters(
                patternByRef.ElementType,
                actualByRef.ElementType,
                genericParameterKind,
                inferred,
                ref matchedAny,
                allowObjectFallback);

        return TypesEquivalent(pattern, actual);
    }

    private static bool TryProjectToGenericDefinition(
        TypeAnalysisContext actual,
        TypeAnalysisContext genericDefinition,
        out GenericInstanceTypeAnalysisContext projected)
    {
        if (actual is SzArrayTypeAnalysisContext array
            && genericDefinition.FullName is
                "System.Collections.Generic.IEnumerable`1"
                or "System.Collections.Generic.ICollection`1"
                or "System.Collections.Generic.IList`1"
                or "System.Collections.Generic.IReadOnlyCollection`1"
                or "System.Collections.Generic.IReadOnlyList`1")
        {
            // 中文注释：CLR 的一维零基数组直接实现以上五个泛型接口；用数组元素类型实例化
            // 目标接口，使 Enumerable.Select(string[], Func<string,...>) 能按真实签名闭合。
            projected = genericDefinition.MakeGenericInstanceType([array.ElementType]);
            return true;
        }

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

    /// <summary>
    /// 判断现有类型是否只是同一泛型定义的 object 共享实例，而候选类型已经给出更具体的实参。
    /// IL2CPP 共享泛型方法体会把 Func&lt;T,...&gt;、List&lt;T&gt; 等寄存器先登记为 object 实例；
    /// 该类型仅是 ABI 占位，不能压过来自全部 Phi 入边的具体类型共识。
    /// </summary>
    internal static bool IsSharedObjectPlaceholder(
        TypeAnalysisContext? existing,
        TypeAnalysisContext? concrete)
    {
        if (existing is not GenericInstanceTypeAnalysisContext existingGeneric
            || concrete is not GenericInstanceTypeAnalysisContext concreteGeneric
            || !TypesEquivalent(existingGeneric.GenericType, concreteGeneric.GenericType)
            || existingGeneric.GenericArguments.Count != concreteGeneric.GenericArguments.Count)
            return false;

        var replacedObject = false;
        for (var index = 0; index < existingGeneric.GenericArguments.Count; index++)
        {
            var existingArgument = existingGeneric.GenericArguments[index];
            var concreteArgument = concreteGeneric.GenericArguments[index];
            if (TypesEquivalent(existingArgument, concreteArgument))
                continue;

            if (existingArgument.FullName != "System.Object"
                || concreteArgument.FullName == "System.Object")
                return false;

            replacedObject = true;
        }

        return replacedObject;
    }

    /// <summary>
    /// 判断类型树中是否仍含 IL2CPP 为值类型共享泛型生成的 Int32Enum 占位。
    /// 该内部运行时类型不是业务枚举，只有来自调用实参或结果槽的真实枚举证据才能替换它。
    /// </summary>
    internal static bool ContainsSharedEnumPlaceholder(TypeAnalysisContext type)
        => type.FullName == "System.Int32Enum"
           || type is GenericInstanceTypeAnalysisContext generic
           && generic.GenericArguments.Any(ContainsSharedEnumPlaceholder)
           || type is SzArrayTypeAnalysisContext array
           && ContainsSharedEnumPlaceholder(array.ElementType)
           || type is ByRefTypeAnalysisContext byRef
           && ContainsSharedEnumPlaceholder(byRef.ElementType);

    /// <summary>
    /// 判断现有类型是否仅因 object 或 Int32Enum 共享实参而比候选类型更宽。
    /// 泛型定义和类型树形状必须完全一致；任一具体实参冲突都会关闭晋级通道。
    /// </summary>
    internal static bool IsSharedIl2CppGenericPlaceholder(
        TypeAnalysisContext? existing,
        TypeAnalysisContext? concrete)
    {
        if (existing == null || concrete == null || TypesEquivalent(existing, concrete))
            return false;

        if (IsWeakSharedArgument(existing, concrete))
            return true;

        if (existing is GenericInstanceTypeAnalysisContext existingGeneric
            && concrete is GenericInstanceTypeAnalysisContext concreteGeneric
            && TypesEquivalent(existingGeneric.GenericType, concreteGeneric.GenericType)
            && existingGeneric.GenericArguments.Count == concreteGeneric.GenericArguments.Count)
        {
            var replacedAny = false;
            for (var index = 0; index < existingGeneric.GenericArguments.Count; index++)
            {
                var existingArgument = existingGeneric.GenericArguments[index];
                var concreteArgument = concreteGeneric.GenericArguments[index];
                if (TypesEquivalent(existingArgument, concreteArgument))
                    continue;
                if (!IsSharedIl2CppGenericPlaceholder(existingArgument, concreteArgument))
                    return false;
                replacedAny = true;
            }

            return replacedAny;
        }

        if (existing is SzArrayTypeAnalysisContext existingArray
            && concrete is SzArrayTypeAnalysisContext concreteArray)
            return IsSharedIl2CppGenericPlaceholder(existingArray.ElementType, concreteArray.ElementType);

        if (existing is ByRefTypeAnalysisContext existingByRef
            && concrete is ByRefTypeAnalysisContext concreteByRef)
            return IsSharedIl2CppGenericPlaceholder(existingByRef.ElementType, concreteByRef.ElementType);

        return false;
    }

    private static bool IsWeakSharedArgument(
        TypeAnalysisContext weak,
        TypeAnalysisContext concrete)
        => weak.FullName == "System.Object"
           && concrete.FullName != "System.Object"
           || weak.FullName == "System.Int32Enum"
           && concrete.FullName != "System.Int32Enum"
           && concrete.IsEnumType;

    private static bool CanAdoptInferredArguments(
        IReadOnlyList<TypeAnalysisContext> current,
        IReadOnlyList<TypeAnalysisContext> inferred)
    {
        if (current.Count != inferred.Count)
            return false;

        for (var index = 0; index < current.Count; index++)
        {
            if (!ContainsSharedEnumPlaceholder(current[index]))
                continue;
            if (!CanRefineSharedEnumPlaceholder(current[index], inferred[index]))
                return false;
        }

        return true;
    }

    private static bool CanRefineSharedEnumPlaceholder(
        TypeAnalysisContext existing,
        TypeAnalysisContext inferred)
    {
        if (existing.FullName == "System.Int32Enum")
            return inferred.FullName == "System.Int32Enum" || inferred.IsEnumType;

        if (existing is GenericInstanceTypeAnalysisContext existingGeneric
            && inferred is GenericInstanceTypeAnalysisContext inferredGeneric
            && TypesEquivalent(existingGeneric.GenericType, inferredGeneric.GenericType)
            && existingGeneric.GenericArguments.Count == inferredGeneric.GenericArguments.Count)
            return existingGeneric.GenericArguments.Select((argument, index) =>
                    !ContainsSharedEnumPlaceholder(argument)
                    || CanRefineSharedEnumPlaceholder(argument, inferredGeneric.GenericArguments[index]))
                .All(valid => valid);

        if (existing is SzArrayTypeAnalysisContext existingArray
            && inferred is SzArrayTypeAnalysisContext inferredArray)
            return CanRefineSharedEnumPlaceholder(existingArray.ElementType, inferredArray.ElementType);

        if (existing is ByRefTypeAnalysisContext existingByRef
            && inferred is ByRefTypeAnalysisContext inferredByRef)
            return CanRefineSharedEnumPlaceholder(existingByRef.ElementType, inferredByRef.ElementType);

        return false;
    }

    private static bool TypeListsEquivalent(
        IReadOnlyList<TypeAnalysisContext> left,
        IReadOnlyList<TypeAnalysisContext> right)
        => left.Count == right.Count
           && left.Select((type, index) => TypesEquivalent(type, right[index])).All(equal => equal);
}
