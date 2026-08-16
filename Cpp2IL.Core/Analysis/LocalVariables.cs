using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

public static class LocalVariables
{
    public static int MaxTypePropagationLoopCount = 5000;

    private const long StaticFieldsOffset64 = 0xB8;
    private const long StaticFieldsOffset32 = 0x5C;

    public static void CreateAll(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;

        // Get all registers
        var registers = new List<Register>();
        foreach (var instruction in instructions)
            registers.AddRange(GetRegisters(instruction));

        // Remove duplicates
        registers = registers.Distinct().ToList();

        // Map those to locals
        var locals = new Dictionary<Register, LocalVariable>();
        for (var i = 0; i < registers.Count; i++)
        {
            var register = registers[i];
            locals.Add(register, new LocalVariable($"v{i}", register));
        }

        // Replace registers with locals
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is Register register)
                    instruction.SetOperand(i, locals[register]);

                if (operand is AddressOf { Target: Register addressed })
                    instruction.SetOperand(i, new AddressOf(locals[addressed]));

                if (operand is MemoryOperand memory)
                {
                    if (memory.Base != null)
                    {
                        var baseRegister = (Register)memory.Base;
                        memory.Base = locals[baseRegister];
                    }

                    if (memory.Index != null)
                    {
                        var index = (Register)memory.Index;
                        memory.Index = locals[index];
                    }

                    instruction.SetOperand(i, memory);
                }

                if (operand is HomogeneousFloatingAggregateArgument aggregate)
                {
                    for (var componentIndex = 0; componentIndex < aggregate.Components.Count; componentIndex++)
                    {
                        var component = aggregate.Components[componentIndex];
                        if (component is Register componentRegister)
                            aggregate.Components[componentIndex] = locals[componentRegister];
                        else if (component is MemoryOperand componentMemory)
                        {
                            if (componentMemory.Base is Register baseRegister)
                                componentMemory.Base = locals[baseRegister];
                            if (componentMemory.Index is Register indexRegister)
                                componentMemory.Index = locals[indexRegister];
                            aggregate.Components[componentIndex] = componentMemory;
                        }
                    }
                }
            }
        }

        method.Locals = locals.Select(kv => kv.Value).ToList();

        // Return local names
        var retValIndex = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Return || instruction.Operands.Count == 0)
                continue;

            for (var componentIndex = 0; componentIndex < instruction.Sources.Count; componentIndex++)
            {
                if (instruction.Sources[componentIndex] is not LocalVariable returnLocal)
                    continue;

                returnLocal.Name = instruction.Sources.Count == 1
                    ? $"returnVal{retValIndex + 1}"
                    : $"returnVal{retValIndex + 1}Component{componentIndex + 1}";
                returnLocal.IsReturn = true;
            }
            retValIndex++;
        }

        // Add parameter names
        var paramLocals = new List<LocalVariable>();

        var operandOffset = method.IsStatic ? 0 : 1; // 'this'

        // 'this' param
        if (!method.IsStatic && method.Locals.Count > 0 && method.ParameterOperands.Count > 0)
        {
            var thisOperand = (Register)method.ParameterOperands[0];
            var thisLocal = method.Locals.FirstOrDefault(l => l.Register.Number == thisOperand.Number && l.Register.Version == -1);

            if (thisLocal != null)
            {
                thisLocal.Name = "this";
                thisLocal.IsThis = true;
                paramLocals.Add(thisLocal);
            }
            else
            {
                method.AddWarning($"'this' local not found (operand: {thisOperand})");
            }
        }

        // Check if method has MethodInfo*
        var hasMethodInfo = (method.ParameterOperands.Count - operandOffset) > method.Parameters.Count;
        var methodInfoIndex = method.ParameterOperands.Count - 1;

        // Add normal parameter names
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var operandIndex = i + operandOffset;
            if (hasMethodInfo && operandIndex == methodInfoIndex)
                break; // Skip MethodInfo*

            if (operandIndex >= method.ParameterOperands.Count)
                break;

            if (method.ParameterOperands[operandIndex] is not Register reg)
                continue;

            var local = method.Locals.FirstOrDefault(l => l.Register.Number == reg.Number && l.Register.Version == -1);
            if (local == null)
                continue;

            local.Name = method.Parameters[i].ParameterName;
            paramLocals.Add(local);
        }

        // Add MethodInfo*
        if (hasMethodInfo)
        {
            var methodInfoOperand = (Register)method.ParameterOperands[methodInfoIndex];
            var methodInfoLocal = method.Locals.FirstOrDefault(l => l.Register.Number == methodInfoOperand.Number && l.Register.Version == -1);

            if (methodInfoLocal != null)
            {
                methodInfoLocal.Name = "methodInfo";
                methodInfoLocal.IsMethodInfo = true;
                paramLocals.Add(methodInfoLocal);
            }
        }

        method.ParameterLocals = paramLocals;

        // the hidden return buffer takes the first argument register. we type it as the return
        // type so stores into it resolve to fields
        if (method.AppContext.Binary.PointerSizeBytes == 8
            && CallingConventionResolver.HiddenReturnBufferRegister(method) is { } bufferRegister
            && method.Locals.FirstOrDefault(l => l.Register.Number == bufferRegister.Number && l.Register.Version == -1) is { } bufferLocal)
        {
            bufferLocal.Name = "returnBuffer";
            bufferLocal.Type = method.ReturnType;
        }
    }

    public static void RemoveUnused(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        cfg.BuildUseDefLists();

        var usedLocals = new HashSet<LocalVariable>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var usedVar in block.Use.OfType<LocalVariable>())
                usedLocals.Add(usedVar);

            foreach (var definedVar in block.Def.OfType<LocalVariable>())
                usedLocals.Add(definedVar);
        }

        method.Locals.RemoveAll(x => !usedLocals.Contains(x));
    }

    private static List<Register> GetRegisters(Instruction instruction)
    {
        var registers = new List<Register>();

        foreach (var operand in instruction.Operands)
        {
            if (operand is AddressOf { Target: Register addressed })
            {
                if (!registers.Contains(addressed))
                    registers.Add(addressed);
            }

            if (operand is Register register)
            {
                if (!registers.Contains(register))
                    registers.Add(register);
            }

            if (operand is MemoryOperand memory)
            {
                if (memory.Base != null)
                {
                    var baseRegister = (Register)memory.Base;
                    if (!registers.Contains(baseRegister))
                        registers.Add(baseRegister);
                }

                if (memory.Index != null)
                {
                    var index = (Register)memory.Index;
                    if (!registers.Contains(index))
                        registers.Add(index);
                }
            }

            if (operand is HomogeneousFloatingAggregateArgument aggregate)
            {
                foreach (var component in aggregate.Components)
                {
                    if (component is Register componentRegister && !registers.Contains(componentRegister))
                        registers.Add(componentRegister);
                    if (component is MemoryOperand componentMemory)
                    {
                        if (componentMemory.Base is Register baseRegister && !registers.Contains(baseRegister))
                            registers.Add(baseRegister);
                        if (componentMemory.Index is Register indexRegister && !registers.Contains(indexRegister))
                            registers.Add(indexRegister);
                    }
                }
            }
        }

        return registers;
    }

    /// <summary>
    /// Resolves field accesses and propagates types together, to a fixpoint, while the method is
    /// still in SSA form (every local has a single, version-stable definition).
    ///
    /// The two are mutually enabling and so cannot be ordered as separate passes: a typed base lets
    /// <see cref="MetadataResolver.ResolveFieldOffsets"/> turn <c>[base + offset]</c> into a
    /// <see cref="FieldReference"/>, a resolved field load types its result with the field's type,
    /// and that result is in turn the base of the next access (directly, or after flowing through
    /// moves/phis). Both steps are monotonic - each only ever resolves an operand or fills a
    /// previously-unknown type - so the loop converges.
    /// </summary>
    public static void ResolveTypesAndFields(MethodAnalysisContext method)
    {
        // Seed types from fixed ground truth - the method's own signature, and type-metadata global
        // loads. Applied once up front and, being applied first, they win over anything inferred later.
        PropagateFromReturn(method);
        PropagateFromParameters(method);
        // 开放泛型KeyValuePair等小型值类型在ARM64入口被拆为连续X寄存器；
        // 签名类型落定后立即把物理分量恢复为同一托管参数的字段读取。
        AggregateParameterRecovery.Run(method);
        SeedStackFrameBaseTypes(method);
        SeedRuntimeClassTypes(method);
        SeedNewobjResults(method);
        SeedMethodInfoTypes(method);
        SeedComparisonResults(method);
        SeedPackedHalfwordPredicateTypes(method);
        SeedPackedVectorExtractionTypes(method);
        SeedNullablePresenceTestResults(method);
        SeedNumericConversionTypes(method);
        SeedScalarFloatingMathTypes(method);
        SeedFloatingArithmeticTypes(method);

        // Everywhere there's a CallVoid after a Newobj, we can resolve the constructor call.
        MetadataResolver.ResolveConstructorCalls(method);

        // Everything else is mutually enabling and so runs to a fixpoint: a typed receiver lets an
        // ambiguous call resolve, a resolved call types its return value and arguments, a typed base
        // lets a field offset resolve, a field load types its result, and any of those can be the
        // receiver/base of the next step. Every pass is monotonic - it only resolves an operand or
        // fills a previously-unknown type - so the loop converges.
        var changed = true;
        var loopCount = 0;
        var lastChangingPasses = new List<string>();
        var lastChangingTypeDetails = new List<string>();

        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException(
                    $"Type and field resolution not settling! (looped {MaxTypePropagationLoopCount} times; "
                    + $"last changing passes: {string.Join(", ", lastChangingPasses)}; "
                    + $"type changes: {string.Join(" | ", lastChangingTypeDetails)})");

            changed = false;
            lastChangingPasses.Clear();
            lastChangingTypeDetails.Clear();
            changed |= RecordChangingPass(lastChangingPasses, nameof(MetadataResolver.ResolveCallsViaMethodInfo), MetadataResolver.ResolveCallsViaMethodInfo(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(MetadataResolver.ResolveAmbiguousCalls), MetadataResolver.ResolveAmbiguousCalls(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(MetadataResolver.ResolveVirtualCalls), MetadataResolver.ResolveVirtualCalls(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(GenericCallRebinder), GenericCallRebinder.Run(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateFromCallParameters), PropagateFromCallParameters(method));
            var captureFinalIteration = MaxTypePropagationLoopCount != -1
                && loopCount == MaxTypePropagationLoopCount;
            var typesBeforeAddressBinding = captureFinalIteration ? CaptureLocalTypes(method) : null;
            var addressBindingChanges = captureFinalIteration ? new List<string>() : null;
            var addressBindingChanged = BindAddressCarrierTypes(
                method.ControlFlowGraph!.Instructions,
                addressBindingChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(BindAddressCarrierTypes), addressBindingChanged);
            if (addressBindingChanged && typesBeforeAddressBinding != null)
                lastChangingTypeDetails.AddRange(DescribeTypeChanges(
                    nameof(BindAddressCarrierTypes), typesBeforeAddressBinding, method));
            if (addressBindingChanges != null)
                lastChangingTypeDetails.AddRange(addressBindingChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(MetadataResolver.ResolveFieldOffsets), MetadataResolver.ResolveFieldOffsets(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(RgctxResolver), RgctxResolver.Run(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateStaticFieldStorage), PropagateStaticFieldStorage(method));
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateBooleanBitTestTypes), PropagateBooleanBitTestTypes(method));
            var typesBeforePropagation = captureFinalIteration ? CaptureLocalTypes(method) : null;
            var propagationChanges = captureFinalIteration ? new List<string>() : null;
            var propagationChanged = PropagateTypesOnce(method, propagationChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateTypesOnce), propagationChanged);
            if (propagationChanged && typesBeforePropagation != null)
                lastChangingTypeDetails.AddRange(DescribeTypeChanges(
                    nameof(PropagateTypesOnce), typesBeforePropagation, method));
            if (propagationChanges != null)
                lastChangingTypeDetails.AddRange(propagationChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(AggregateStackCopyRecovery), AggregateStackCopyRecovery.Run(method));
        }
    }

    private static Dictionary<LocalVariable, TypeAnalysisContext?> CaptureLocalTypes(MethodAnalysisContext method) =>
        method.Locals.ToDictionary(local => local, local => local.Type);

    /// <summary>
    /// 仅在不动点达到硬上限的最后一轮生成确定性差异，避免正常方法承担诊断开销。
    /// </summary>
    private static IEnumerable<string> DescribeTypeChanges(
        string pass,
        IReadOnlyDictionary<LocalVariable, TypeAnalysisContext?> before,
        MethodAnalysisContext method)
    {
        return method.Locals
            .Select(local => new
            {
                Local = local,
                Before = before.TryGetValue(local, out var oldType) ? oldType : null,
                After = local.Type,
            })
            .Where(change => !ReferenceEquals(change.Before, change.After))
            .OrderBy(change => change.Local.Name, StringComparer.Ordinal)
            .ThenBy(change => change.Local.Register.Number)
            .Select(change => $"{pass}:{change.Local.Name}@{change.Local.Register}:"
                + $"{DescribeType(change.Before)}->{DescribeType(change.After)}");
    }

    private static string DescribeType(TypeAnalysisContext? type) => type == null
        ? "<null>"
        : $"{type.GetType().Name}[{type.FullName}]";

    /// <summary>
    /// 接口与委托分派在主类型不动点之后才把间接调用改写成真实方法。这里重放调用签名、
    /// 地址载体、字段偏移和普通复制四种互相依赖的传播边，直到没有新类型或字段引用。
    /// 后置调用的返回值可能正是下一条字段读取的基址，因此字段解析必须与类型传播处于同一不动点；
    /// 否则合法的<c>[result + fieldOffset]</c>会进入IL生成器并退化为原生零值。
    /// </summary>
    public static void ResolveLateCallTypesAndAddressCarriers(MethodAnalysisContext method)
    {
        var changed = true;
        var loopCount = 0;
        var lastChangingPasses = new List<string>();
        var lastChangingTypeDetails = new List<string>();

        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException(
                    $"Late call and address type resolution not settling! (looped {MaxTypePropagationLoopCount} times; "
                    + $"last changing passes: {string.Join(", ", lastChangingPasses)}; "
                    + $"type changes: {string.Join(" | ", lastChangingTypeDetails)})");

            changed = false;
            lastChangingPasses.Clear();
            lastChangingTypeDetails.Clear();
            var captureFinalIteration = MaxTypePropagationLoopCount != -1
                && loopCount == MaxTypePropagationLoopCount;

            // 接口派发、字段偏移和聚合复制会在主类型循环之后提供新的具体类型证据。
            // 必须先据此重绑定共享泛型调用，再让调用签名向局部变量传播，避免
            // List<!0>.AddWithResize(T) 一直把开放 VAR 反向写回后续 SSA 局部。
            changed |= RecordChangingPass(
                lastChangingPasses,
                nameof(GenericCallRebinder),
                GenericCallRebinder.Run(method));

            var typesBeforeCalls = captureFinalIteration ? CaptureLocalTypes(method) : null;
            var callChanges = PropagateFromCallParameters(method);
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateFromCallParameters), callChanges);
            if (callChanges && typesBeforeCalls != null)
                lastChangingTypeDetails.AddRange(DescribeTypeChanges(
                    nameof(PropagateFromCallParameters), typesBeforeCalls, method));

            var typesBeforeAddressBinding = captureFinalIteration ? CaptureLocalTypes(method) : null;
            var addressBindingChanges = captureFinalIteration ? new List<string>() : null;
            var addressBindingChanged = BindAddressCarrierTypes(
                method.ControlFlowGraph!.Instructions,
                addressBindingChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(BindAddressCarrierTypes), addressBindingChanged);
            if (addressBindingChanged && typesBeforeAddressBinding != null)
                lastChangingTypeDetails.AddRange(DescribeTypeChanges(
                    nameof(BindAddressCarrierTypes), typesBeforeAddressBinding, method));
            if (addressBindingChanges != null)
                lastChangingTypeDetails.AddRange(addressBindingChanges);

            changed |= RecordChangingPass(
                lastChangingPasses,
                nameof(MetadataResolver.ResolveFieldOffsets),
                MetadataResolver.ResolveFieldOffsets(method));
            changed |= RecordChangingPass(
                lastChangingPasses,
                nameof(PropagateBooleanBitTestTypes),
                PropagateBooleanBitTestTypes(method));

            var typesBeforePropagation = captureFinalIteration ? CaptureLocalTypes(method) : null;
            var propagationChanges = captureFinalIteration ? new List<string>() : null;
            var propagationChanged = PropagateTypesOnce(method, propagationChanges);
            changed |= RecordChangingPass(lastChangingPasses, nameof(PropagateTypesOnce), propagationChanged);
            if (propagationChanged && typesBeforePropagation != null)
                lastChangingTypeDetails.AddRange(DescribeTypeChanges(
                    nameof(PropagateTypesOnce), typesBeforePropagation, method));
            if (propagationChanges != null)
                lastChangingTypeDetails.AddRange(propagationChanges);
        }
    }

    private static bool RecordChangingPass(List<string> changingPasses, string name, bool changed)
    {
        if (changed)
            changingPasses.Add(name);

        return changed;
    }

    /// <summary>
    /// ARM64标准函数序言通过<c>SUB X31, X31, #frameSize</c>建立当前栈帧基址。
    /// 该定义是原生指针的权威来源；若不先固定类型，后续栈槽读写可能把同一SSA局部量
    /// 误传播成某个托管值类型，最终把<c>SP + offset</c>生成为非法托管算术。
    /// </summary>
    private static void SeedStackFrameBaseTypes(MethodAnalysisContext method)
    {
        var nativePointerType = method.AppContext.SystemTypes.SystemIntPtrType;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            BindStackFrameBaseTypes(instruction, nativePointerType);
    }

    internal static bool BindStackFrameBaseTypes(
        Instruction instruction,
        TypeAnalysisContext nativePointerType)
    {
        if (instruction.OpCode != OpCode.Subtract
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination
            || instruction.Operands[1] is not LocalVariable source
            || instruction.Operands[2] is not Immediate { Value: > 0 }
            || destination.Register.Name != "X31"
            || source.Register.Name != "X31")
            return false;

        var changed = destination.Type != nativePointerType || source.Type != nativePointerType;
        destination.Type = nativePointerType;
        source.Type = nativePointerType;
        return changed;
    }

    // A type-metadata global load (Move local, typeof(T)) puts the runtime class pointer for T into
    // the local - an Il2CppClass*, not an instance of T. That is known exactly from the instruction,
    // so it is seeded as ground truth (overriding any prior guess) before the inference fixpoint,
    // rather than letting a monotonic pass first mistype the local as T itself.
    private static void SeedRuntimeClassTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is TypeAnalysisContext type and not RuntimeMethodInfoAnalysisContext)
                destination.Type = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);
        }
    }

    private static void SeedNewobjResults(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Newobj || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination && InstantiatedType(instruction.Operands[1]) is { } type)
                destination.Type = type;
        }
    }

    /// <summary>
    /// 元数据槽闭合后，以 Newobj 的最终类型操作数重新校准分配结果，并且只重绑定直接使用
    /// 这些结果的共享泛型调用。早期类型传播看到的运行时类可能仍是 List&lt;object&gt;，
    /// 而后期 typeof(List&lt;T&gt;) 已经精确；分配指令是该实例类型的最终权威证据。
    /// </summary>
    internal static bool RefreshResolvedNewobjTypesAndCalls(MethodAnalysisContext method)
    {
        var changedAllocations = new HashSet<LocalVariable>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Newobj
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination
                || InstantiatedType(instruction.Operands[1]) is not { } instantiatedType
                || GenericCallRebinder.TypesEquivalent(destination.Type, instantiatedType))
                continue;

            destination.Type = instantiatedType;
            changedAllocations.Add(destination);
        }

        if (changedAllocations.Count == 0)
            return false;

        var changed = true;
        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            var receiverIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            if (receiverIndex < instruction.Operands.Count
                && instruction.Operands[receiverIndex] is LocalVariable receiver
                && changedAllocations.Contains(receiver))
                changed |= GenericCallRebinder.TryRebind(instruction);
        }

        return changed;
    }

    private static TypeAnalysisContext? InstantiatedType(IOperand classOperand) =>
        classOperand switch
        {
            LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var t } } => t,
            RuntimeClassTypeAnalysisContext { RepresentedType: var t } => t,
            TypeAnalysisContext type => type, //not sure this is actually valid but for completeness
            _ => null,
        };

    // A method-metadata global load (Move local, methodof(M)) puts a MethodInfo* for M into the local.
    // MetadataResolver already resolved the address to a RuntimeMethodInfoAnalysisContext naming the
    // method; that same context is the local's type (a runtime handle, recoverable via its
    // RepresentedMethod).
    private static void SeedMethodInfoTypes(MethodAnalysisContext method)
    {
        // 泛型方法以及泛型类型上的共享方法都通过末尾隐藏MethodInfo访问方法RGCTXData。
        // 参数局部在CreateAll阶段已被识别，必须在解析[methodInfo+rgctx_data]之前绑定真实方法身份。
        if ((method.GenericParameters.Count > 0 || method.DeclaringType?.GenericParameters.Count > 0)
            && method.ParameterLocals.FirstOrDefault(local => local.IsMethodInfo) is { } hiddenMethodInfo)
            hiddenMethodInfo.Type = new RuntimeMethodInfoAnalysisContext(
                method,
                method.DeclaringType!.DeclaringAssembly);

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination && instruction.Operands[1] is RuntimeMethodInfoAnalysisContext methodInfo)
                destination.Type = methodInfo;
        }
    }

    // A comparison (CheckEqual, CheckLess, ...) writes a 0/1 result into its destination, so that local
    // is a System.Boolean regardless of what the compared operands are.
    private static void SeedComparisonResults(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned)
                continue;

            if (instruction.Destination is LocalVariable destination)
                destination.Type = booleanType;
        }
    }

    private static void SeedPackedHalfwordPredicateTypes(MethodAnalysisContext method)
    {
        var systemTypes = method.AppContext.SystemTypes;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.VectorAllLanesPredicate || instruction.Operands.Count != 8)
                continue;

            if (instruction.Operands[0] is LocalVariable destination)
                destination.Type = systemTypes.SystemBooleanType;
            if (instruction.Operands[1] is LocalVariable accumulatedHalfwords)
                accumulatedHalfwords.Type = systemTypes.SystemUInt64Type;
            if (instruction.Operands[2] is LocalVariable scalarValue)
                scalarValue.Type = systemTypes.SystemInt32Type;
        }
    }

    private static void SeedPackedVectorExtractionTypes(MethodAnalysisContext method)
    {
        var systemTypes = method.AppContext.SystemTypes;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.VectorExtractUnsignedInt16 || instruction.Operands.Count != 3)
                continue;

            if (instruction.Operands[0] is LocalVariable destination)
                destination.Type = systemTypes.SystemInt32Type;
            if (instruction.Operands[1] is LocalVariable packedVector)
                packedVector.Type = systemTypes.SystemUInt64Type;
        }
    }

    /// <summary>
    /// ARM64的<c>AND Wd, Wn, #1</c>位测试会被翻译为名为
    /// <c>TEST_BIT_VALUE</c>的ISIL临时量。该指令在SSA中是布尔值的权威定义；
    /// 如果同一物理返回寄存器随后又承载<see cref="Nullable{T}"/>，普通单调传播会把
    /// 当前版本误标成可空结构，最终生成对结构执行按位与的非法IL。
    /// </summary>
    private static bool PropagateBooleanBitTestTypes(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        var changed = false;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            changed |= BindBooleanBitTestOperands(instruction, booleanType);
        return changed;
    }

    internal static bool BindBooleanBitTestOperands(
        Instruction instruction,
        TypeAnalysisContext booleanType)
    {
        if (instruction.OpCode != OpCode.And
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination
            || instruction.Operands[1] is not LocalVariable source
            || instruction.Operands[2] is not Immediate { Value: 1 }
            || (destination.Type != booleanType
                && source.Type != booleanType
                && !destination.Register.Name.StartsWith("TEST_BIT_VALUE", StringComparison.Ordinal)))
            return false;

        // 运行时元数据载体来自确定的原生结构字段，优先级高于寄存器名启发式；
        // 位测试只把结果绑定为布尔值，禁止反向覆盖静态字段、类、方法或rgctx指针。
        var bindSource = source.Type is not (
            StaticFieldStorageTypeAnalysisContext
            or RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext
            or RgctxTableTypeAnalysisContext);
        var changed = destination.Type != booleanType;
        destination.Type = booleanType;
        // 中文注释：位测试只权威定义目标。源局部若已有整数转换或字段类型，说明同一物理
        // 寄存器的前一生命期已经定型；此时保留源类型，避免 Boolean 与 Int32 在不动点振荡。
        // 只有空类型或 object ABI 占位仍可由布尔返回值证据收窄。
        if (bindSource
            && (source.Type == null
                || source.Type.FullName == "System.Object")
            && source.Type != booleanType)
        {
            source.Type = booleanType;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// ARM64用<c>AND Wd, Wn, #0xFF</c>读取Nullable值的低字节存在标志；
    /// 目标是布尔值，而源仍必须保留原始Nullable类型供IL生成器读取HasValue。
    /// </summary>
    private static void SeedNullablePresenceTestResults(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            BindNullablePresenceTestResult(instruction, booleanType);
    }

    internal static bool BindNullablePresenceTestResult(
        Instruction instruction,
        TypeAnalysisContext booleanType)
    {
        if (instruction.OpCode != OpCode.And
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination
            || NullableOperandType(instruction.Operands[1]) == null
            || instruction.Operands[2] is not Immediate { Value: 0xFF })
            return false;

        if (destination.Type == booleanType)
            return false;

        destination.Type = booleanType;
        return true;
    }

    /// <summary>
    /// 返回局部量或已解析字段操作数携带的 Nullable&lt;T&gt; 类型。复制合并可能把字段加载
    /// 直接内联到按位测试，因此类型分析和 IL 生成必须共用同一操作数分类规则。
    /// </summary>
    internal static GenericInstanceTypeAnalysisContext? NullableOperandType(IOperand operand)
        => operand switch
        {
            LocalVariable
            {
                Type: GenericInstanceTypeAnalysisContext
                {
                    GenericType.FullName: "System.Nullable`1"
                } nullableType
            } => nullableType,
            FieldReference
            {
                Field.FieldType: GenericInstanceTypeAnalysisContext
                {
                    GenericType.FullName: "System.Nullable`1"
                } nullableType
            } => nullableType,
            _ => null,
        };
    
    //Handles typing of locals for ref/out params
    public static void TypeAddressedLocals(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // the receiver of a value type's instance method is a pointer to the value
            if (!calledMethod.IsStatic && firstArg < instruction.Operands.Count
                && instruction.Operands[firstArg] is AddressOf { Target: LocalVariable receiver }
                && calledMethod.DeclaringType is { IsValueType: true } declaringType)
                SetTypeIfUnknown(receiver, declaringType);

            var paramOffset = firstArg + (calledMethod.IsStatic ? 0 : 1);

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                if (instruction.Operands[i] is AddressOf { Target: LocalVariable referenced }
                    && calledMethod.Parameters[parameterIndex].ParameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType })
                    SetTypeIfUnknown(referenced, referencedType);
            }
        }
    }

    // Fills in a local's type only when it is currently unknown, keeping propagation monotonic (a
    // type, once set, is never changed) so the fixpoint terminates. Returns whether it set anything.
    private static bool SetTypeIfUnknown(LocalVariable local, TypeAnalysisContext? type)
    {
        if (type == null || local.Type != null)
            return false;

        local.Type = type;
        return true;
    }

    private static bool PropagateStaticFieldStorage(MethodAnalysisContext method)
    {
        var staticFieldsOffset = method.AppContext.Binary.is32Bit ? StaticFieldsOffset32 : StaticFieldsOffset64;
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination || destination.Type is StaticFieldStorageTypeAnalysisContext)
                continue;

            if (instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0 } memory || memory.Addend != staticFieldsOffset)
                continue;

            if (memory.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var owner } })
                continue;

            destination.Type = new StaticFieldStorageTypeAnalysisContext(owner, owner.DeclaringAssembly);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// post-27运行时会把常用TypeInfo集中放入启动时初始化的二级表；该表位于BSS时，离线文件中
    /// 没有可供元数据解析器读取的第二级指针。这里单次扫描全部指令，从封闭调用形参、已定型局部
    /// 或已解析字段写入取得预期类型；只有自类型静态字段、精确static_fields偏移和完整二级表链
    /// 同时闭合时才写入字段引用。定义表只构建一次，每个操作数只裁决一次。
    /// </summary>
    public static int ResolveExpectedSelfTypedStaticFields(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var uniqueDefinitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var staticFieldsOffset = method.AppContext.Binary.is32Bit ? StaticFieldsOffset32 : StaticFieldsOffset64;
        var resolvedCount = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count >= 2)
            {
                var expectedType = instruction.Operands[0] switch
                {
                    LocalVariable { Type: { } localType } => localType,
                    FieldReference fieldReference => fieldReference.Field.FieldType,
                    _ => null,
                };

                if (expectedType != null
                    && TryResolveSelfTypedStaticFieldLoad(
                        instruction.Operands[1],
                        expectedType,
                        uniqueDefinitions,
                        staticFieldsOffset,
                        out var resolvedMoveField))
                {
                    instruction.SetOperand(1, resolvedMoveField!);
                    resolvedCount++;
                }
            }

            if (!instruction.IsCall || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var argumentOffset = instruction.OpCode == OpCode.Call ? 2 : 1;
            if (!calledMethod.IsStatic)
                argumentOffset++;

            for (var operandIndex = argumentOffset; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                var parameterIndex = operandIndex - argumentOffset;
                if (parameterIndex >= calledMethod.Parameters.Count)
                    break;

                if (!TryResolveSelfTypedStaticFieldLoad(
                    instruction.Operands[operandIndex],
                    calledMethod.Parameters[parameterIndex].ParameterType,
                    uniqueDefinitions,
                    staticFieldsOffset,
                    out var resolvedField))
                    continue;

                instruction.SetOperand(operandIndex, resolvedField!);
                resolvedCount++;
            }
        }

        return resolvedCount;
    }

    /// <summary>
    /// 把形如<c>[staticStorage + fieldOffset]</c>的值绑定到“字段类型与所有者类型相同”的静态字段。
    /// 该窄规则覆盖System.String.Empty等自类型静态值，同时排除普通对象字段、宽System.Object
    /// 预期类型、错位静态存储以及已存在的冲突类型。
    /// </summary>
    internal static bool TryResolveSelfTypedStaticFieldLoad(
        IOperand argument,
        TypeAnalysisContext expectedValueType,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions,
        long staticFieldsOffset,
        out FieldReference? resolvedField)
    {
        resolvedField = null;
        if (expectedValueType is ByRefTypeAnalysisContext or GenericParameterTypeAnalysisContext
            || expectedValueType.IsValueType
            || argument is not MemoryOperand
            {
                Base: LocalVariable staticStorage,
                Index: null,
                Scale: 0,
                Addend: >= 0
            } fieldLoad
            || !uniqueDefinitions.TryGetValue(staticStorage, out var storageDefinition)
            || storageDefinition is not
            {
                OpCode: OpCode.Move,
                Operands: [_, MemoryOperand
                {
                    Base: LocalVariable runtimeClass,
                    Index: null,
                    Scale: 0
                } staticStorageLoad]
            }
            || staticStorageLoad.Addend != staticFieldsOffset
            || !IsPost27RuntimeClassTableLoad(runtimeClass, uniqueDefinitions))
            return false;

        var representedField = expectedValueType.Fields.FirstOrDefault(field =>
            field.IsStatic
            && field.Offset == fieldLoad.Addend
            && GenericCallRebinder.TypesEquivalent(field.FieldType, expectedValueType));
        if (representedField == null)
            return false;

        if (runtimeClass.Type is { } runtimeClassType
            && (runtimeClassType is not RuntimeClassTypeAnalysisContext existingRuntimeClass
                || !GenericCallRebinder.TypesEquivalent(existingRuntimeClass.RepresentedType, expectedValueType)))
            return false;

        if (staticStorage.Type is { } staticStorageType
            && (staticStorageType is not StaticFieldStorageTypeAnalysisContext existingStaticStorage
                || !GenericCallRebinder.TypesEquivalent(existingStaticStorage.OwnerType, expectedValueType)))
            return false;

        if (runtimeClass.Type == null)
            runtimeClass.Type = new RuntimeClassTypeAnalysisContext(
                expectedValueType,
                expectedValueType.DeclaringAssembly);

        if (staticStorage.Type == null)
            staticStorage.Type = new StaticFieldStorageTypeAnalysisContext(
                expectedValueType,
                expectedValueType.DeclaringAssembly);

        resolvedField = new FieldReference(representedField, staticStorage, (int)fieldLoad.Addend);
        return true;
    }

    /// <summary>
    /// 确认运行时类局部量确实来自post-27二级表：先从绝对槽读取表基址，再从表项读取类指针。
    /// 绝对地址和表项偏移均保持为证据，不把任何单一游戏地址写入生产器规则。
    /// </summary>
    private static bool IsPost27RuntimeClassTableLoad(
        LocalVariable runtimeClass,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions)
    {
        if (!uniqueDefinitions.TryGetValue(runtimeClass, out var runtimeClassDefinition)
            || runtimeClassDefinition is not
            {
                OpCode: OpCode.Move,
                Operands: [_, MemoryOperand
                {
                    Base: LocalVariable tableBase,
                    Index: null,
                    Scale: 0,
                    Addend: >= 0
                }]
            }
            || !uniqueDefinitions.TryGetValue(tableBase, out var tableBaseDefinition)
            || tableBaseDefinition is not
            {
                OpCode: OpCode.Move,
                Operands: [_, MemoryOperand
                {
                    Base: null,
                    Index: null,
                    Scale: 0,
                    Addend: >= 0
                }]
            })
            return false;

        return true;
    }

    // A single propagation sweep over every move and phi. Returns whether it filled in any type.
    private static bool PropagateTypesOnce(MethodAnalysisContext method, List<string>? changeDetails = null)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            var operandTypesBefore = changeDetails == null
                ? null
                : instruction.Operands.OfType<LocalVariable>()
                    .Distinct()
                    .ToDictionary(local => local, local => local.Type);
            var instructionChanged = false;
            switch (instruction.OpCode)
            {
                case OpCode.Move:
                    instructionChanged = PropagateMove(instruction, method.AppContext.Binary.PointerSizeBytes);
                    break;
                case OpCode.Phi:
                    instructionChanged = PropagatePhi(instruction);
                    break;
                case OpCode.ConditionalSelect:
                    instructionChanged = PropagateConditionalSelect(instruction);
                    break;
                case OpCode.Box:
                    instructionChanged = PropagateBox(instruction, method);
                    break;
                case OpCode.Unbox:
                    instructionChanged = PropagateUnbox(instruction);
                    break;
                case OpCode.CastClass:
                    instructionChanged = PropagateCastClass(instruction);
                    break;
                case OpCode.IsInst:
                    instructionChanged = PropagateIsInst(instruction);
                    break;
                case OpCode.Not:
                    instructionChanged = BindBooleanNotResult(
                        instruction,
                        method.AppContext.SystemTypes.SystemBooleanType);
                    break;
                case OpCode.ConvertFloatingPointPrecision:
                case OpCode.ConvertFloatToSignedInteger:
                case OpCode.ConvertSignedIntegerToFloat:
                case OpCode.ReinterpretIntegerBitsAsFloat:
                case OpCode.ReinterpretFloatBitsAsInteger:
                case OpCode.RoundFloatTowardPositiveInfinity:
                case OpCode.RoundFloatTowardNegativeInfinity:
                    instructionChanged = PropagateNumericConversion(instruction, method);
                    break;
                case OpCode.AbsoluteNumber:
                case OpCode.AbsoluteDifference:
                case OpCode.MaximumNumber:
                    instructionChanged = BindScalarFloatingMathTypes(instruction, method.AppContext);
                    break;
                case OpCode.Add:
                case OpCode.Subtract:
                case OpCode.Multiply:
                case OpCode.Divide:
                    instructionChanged = BindFloatingArithmeticTypes(instruction, method.AppContext);
                    break;
            }

            changed |= instructionChanged;
            if (!instructionChanged || operandTypesBefore == null)
                continue;

            foreach (var pair in operandTypesBefore)
            {
                if (ReferenceEquals(pair.Value, pair.Key.Type))
                    continue;
                changeDetails!.Add($"PropagateTypesOnce:IL_{instruction.Index}:"
                    + $"{instruction.OpCode}:{pair.Key.Name}@{pair.Key.Register}:"
                    + $"{DescribeType(pair.Value)}->{DescribeType(pair.Key.Type)}");
            }
        }

        return changed;
    }

    private static bool PropagateBox(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable value, TypeAnalysisContext boxedType])
            return false;

        var changed = false;
        if (!GenericCallRebinder.TypesEquivalent(value.Type, boxedType))
        {
            value.Type = boxedType;
            changed = true;
        }

        var objectType = method.AppContext.SystemTypes.SystemObjectType;
        if (!GenericCallRebinder.TypesEquivalent(destination.Type, objectType))
        {
            destination.Type = objectType;
            changed = true;
        }

        return changed;
    }

    private static bool PropagateCastClass(Instruction instruction)
    {
        if (instruction.Operands is not
            [LocalVariable destination, _, TypeAnalysisContext { IsValueType: false } castType])
            return false;

        if (GenericCallRebinder.TypesEquivalent(destination.Type, castType))
            return false;

        destination.Type = castType;
        return true;
    }

    private static bool PropagateUnbox(Instruction instruction)
    {
        if (instruction.Operands is not
            [LocalVariable destination, _, TypeAnalysisContext unboxedType]
            || !unboxedType.IsValueType && unboxedType is not GenericParameterTypeAnalysisContext)
            return false;

        if (GenericCallRebinder.TypesEquivalent(destination.Type, unboxedType))
            return false;

        destination.Type = unboxedType;
        return true;
    }

    private static bool PropagateIsInst(Instruction instruction)
    {
        if (instruction.Operands is not
            [LocalVariable destination, _, TypeAnalysisContext { IsValueType: false } testedType])
            return false;

        if (GenericCallRebinder.TypesEquivalent(destination.Type, testedType))
            return false;

        destination.Type = testedType;
        return true;
    }

    internal static bool BindBooleanNotResult(
        Instruction instruction,
        TypeAnalysisContext booleanType)
    {
        // ARM64 的逻辑非和整数按位非共用同一个 ISIL 操作码；只有操作数已经被比较、位测试
        // 或方法签名精确证明为布尔值时，结果才允许收敛为布尔类型。
        if (instruction.OpCode != OpCode.Not
            || instruction.Operands.Count != 2
            || instruction.Operands[0] is not LocalVariable destination
            || instruction.Operands[1] is not LocalVariable { Type: { } sourceType }
            || !GenericCallRebinder.TypesEquivalent(sourceType, booleanType))
            return false;

        // 操作语义比寄存器复用留下的 object 占位类型更精确。该赋值只会收敛到唯一的
        // System.Boolean，不会在定点迭代中来回改变类型。
        if (GenericCallRebinder.TypesEquivalent(destination.Type, booleanType))
            return false;

        destination.Type = booleanType;
        return true;
    }

    private static bool PropagateNumericConversion(
        Instruction instruction,
        MethodAnalysisContext method)
        => BindNumericConversionTypes(instruction, method.AppContext);

    /// <summary>
    /// 数值转换操作码精确规定目标位宽和数值域，因此目标类型是权威定义，而不是可被
    /// 先到 Phi 或聚合寄存器复用占位抢占的弱推断。源操作数仍只填补空类型，避免覆盖
    /// 调用返回值、字段读取等更具体的已有证据。
    /// </summary>
    internal static bool BindNumericConversionTypes(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.Operands.Count < 3 ||
            instruction.Operands[0] is not LocalVariable destination ||
            instruction.Operands[1] is not LocalVariable source ||
            instruction.Operands[2] is not Immediate destinationWidth)
            return false;

        var systemTypes = appContext.SystemTypes;
        var destinationType = instruction.OpCode == OpCode.ConvertFloatToSignedInteger
            ? destinationWidth.Value switch
            {
                32 => systemTypes.SystemInt32Type,
                64 => systemTypes.SystemInt64Type,
                _ => null,
            }
            : destinationWidth.Value switch
            {
                32 => systemTypes.SystemSingleType,
                64 => systemTypes.SystemDoubleType,
                _ => null,
            };

        var sourceWidth = instruction.Operands.Count >= 4 && instruction.Operands[3] is Immediate explicitSourceWidth
            ? explicitSourceWidth.Value
            : destinationWidth.Value;
        var sourceType = instruction.OpCode is OpCode.ConvertSignedIntegerToFloat
            or OpCode.ReinterpretIntegerBitsAsFloat
            ? sourceWidth switch
            {
                32 => systemTypes.SystemInt32Type,
                64 => systemTypes.SystemInt64Type,
                _ => null,
            }
            : instruction.OpCode == OpCode.ReinterpretFloatBitsAsInteger
                ? sourceWidth switch
                {
                    32 => systemTypes.SystemSingleType,
                    64 => systemTypes.SystemDoubleType,
                    _ => null,
                }
                : sourceWidth switch
            {
                32 => systemTypes.SystemSingleType,
                64 => systemTypes.SystemDoubleType,
                _ => null,
            };

        if (instruction.OpCode == OpCode.ReinterpretFloatBitsAsInteger)
        {
            destinationType = destinationWidth.Value switch
            {
                32 => systemTypes.SystemInt32Type,
                64 => systemTypes.SystemInt64Type,
                _ => null,
            };
        }

        var changed = destinationType != null
            && SetAuthoritativeNumericType(destination, destinationType);
        changed |= SetTypeIfUnknown(source, sourceType);
        return changed;
    }

    /// <summary>
    /// 在任何 Phi 双向传播前固定全部数值转换定义。ARM64 的 V0 等物理寄存器会在结构
    /// Enumerator 搬运和浮点运算之间复用；若等到顺序扫描转换指令，前面的 Phi 已可能
    /// 把 Enumerator 类型反向扩散到浮点定义，随后单调传播便失去纠正机会。
    /// </summary>
    private static void SeedNumericConversionTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (
                    OpCode.ConvertFloatingPointPrecision
                    or OpCode.ConvertFloatToSignedInteger
                    or OpCode.ConvertSignedIntegerToFloat
                    or OpCode.ReinterpretIntegerBitsAsFloat
                    or OpCode.ReinterpretFloatBitsAsInteger
                    or OpCode.RoundFloatTowardPositiveInfinity
                    or OpCode.RoundFloatTowardNegativeInfinity))
                continue;

            BindNumericConversionTypes(instruction, method.AppContext);
        }
    }

    /// <summary>
    /// FMAXNM 操作码携带原生标量精度，三个局部操作数均属于同一浮点域；该证据在 Phi
    /// 传播前落定，避免零寄存器或旧生命期类型抢占输入和目标类型。
    /// </summary>
    internal static bool BindScalarFloatingMathTypes(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.Operands.Count < 3
            || instruction.Operands[0] is not LocalVariable destination
            || instruction.Operands[^1] is not Immediate width)
            return false;

        var floatingType = width.Value switch
        {
            32 => appContext.SystemTypes.SystemSingleType,
            64 => appContext.SystemTypes.SystemDoubleType,
            _ => null,
        };
        if (floatingType == null)
            return false;

        var changed = SetAuthoritativeNumericType(destination, floatingType);
        for (var operandIndex = 1; operandIndex < instruction.Operands.Count - 1; operandIndex++)
            if (instruction.Operands[operandIndex] is LocalVariable sourceLocal)
                changed |= SetTypeIfUnknown(sourceLocal, floatingType);
        return changed;
    }

    private static void SeedScalarFloatingMathTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.OpCode is
                OpCode.AbsoluteNumber or OpCode.AbsoluteDifference or OpCode.MaximumNumber)
                BindScalarFloatingMathTypes(instruction, method.AppContext);
    }

    private static bool SetExactType(LocalVariable local, TypeAnalysisContext type)
    {
        if (GenericCallRebinder.TypesEquivalent(local.Type, type))
            return false;

        local.Type = type;
        return true;
    }

    /// <summary>
    /// 原生数值操作码可覆盖引用、结构或 object ABI 占位，却不覆盖已经落定的另一数值域。
    /// 同一个 SSA 局部偶尔会因 SIMD 子寄存器别名同时出现在 S/D 或 W/X 生命期；保留第一项
    /// 原生种子使传播严格单调，后续 IL 生成仍按每条操作码携带的位宽执行显式转换。
    /// </summary>
    private static bool SetAuthoritativeNumericType(LocalVariable local, TypeAnalysisContext type)
    {
        if (GenericCallRebinder.TypesEquivalent(local.Type, type)
            || IsFinalScalarType(local.Type))
            return false;

        local.Type = type;
        return true;
    }

    /// <summary>
    /// 以二元算术源操作数中的唯一 Single/Double 证据绑定完整算术结果。字段、字面量和
    /// 已定型局部均可提供证据；混合精度保持开放，避免猜测隐式转换方向。
    /// </summary>
    internal static bool BindFloatingArithmeticTypes(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination)
            return false;

        var sourceKinds = instruction.Operands
            .Skip(1)
            .Select(FloatingKind)
            .Where(kind => kind != 0)
            .Distinct()
            .ToArray();
        if (sourceKinds.Length != 1)
            return false;

        var floatingType = sourceKinds[0] == 32
            ? appContext.SystemTypes.SystemSingleType
            : appContext.SystemTypes.SystemDoubleType;
        var changed = SetAuthoritativeNumericType(destination, floatingType);
        for (var operandIndex = 1; operandIndex < instruction.Operands.Count; operandIndex++)
            if (instruction.Operands[operandIndex] is LocalVariable sourceLocal)
                changed |= SetTypeIfUnknown(sourceLocal, floatingType);
        return changed;
    }

    private static int FloatingKind(IOperand operand) => operand switch
    {
        FloatLiteral => 32,
        DoubleLiteral => 64,
        LocalVariable { Type.FullName: "System.Single" } => 32,
        LocalVariable { Type.FullName: "System.Double" } => 64,
        FieldReference { Field.FieldType.FullName: "System.Single" } => 32,
        FieldReference { Field.FieldType.FullName: "System.Double" } => 64,
        _ => 0,
    };

    private static void SeedFloatingArithmeticTypes(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
                BindFloatingArithmeticTypes(instruction, method.AppContext);
    }

    /// <summary>
    /// 退SSA与复制合并会引入新的边复制，并把同一物理寄存器的多个控制流值汇合到一个局部量。
    /// 这里仅使用已定型比较操作数和ARM64指令携带的精确整数位宽恢复最终标量载体；引用、地址、
    /// 运行时元数据和没有位宽证据的运算均保持原类型。
    /// </summary>
    public static bool ResolveFinalScalarCarrierTypes(MethodAnalysisContext method)
    {
        var changed = false;
        var instructions = method.ControlFlowGraph!.Instructions;

        // 比较的一侧若已有精确标量类型，另一侧局部量必处于同一数值域；先恢复循环计数器等载体。
        foreach (var instruction in instructions)
            changed |= BindFinalComparisonOperandTypes(instruction, method.AppContext);

        // 原生目标寄存器位宽随后裁决算术结果；该顺序避免仅凭小立即数猜测32/64位。
        foreach (var instruction in instructions)
            changed |= BindSizedIntegerOperationTypes(instruction, method.AppContext);

        return changed;
    }

    /// <summary>
    /// 数组、List 与字符串长度直到布局恢复阶段才会成为专用操作数，因此必须在这些恢复器之后
    /// 单独回填与长度比较的归纳变量。这里不重复字段或算术推导，只消费刚生成的长度事实。
    /// </summary>
    public static bool ResolveRecoveredLengthComparisonCarrierTypes(MethodAnalysisContext method)
    {
        var changed = false;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            changed |= BindRecoveredLengthComparisonOperandTypes(instruction, method.AppContext);
        return changed;
    }

    internal static bool BindFinalComparisonOperandTypes(
        Instruction instruction,
        ApplicationAnalysisContext? appContext = null)
    {
        if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned
            || instruction.Operands.Count != 3)
            return false;

        var left = instruction.Operands[1];
        var right = instruction.Operands[2];
        var leftScalar = FinalScalarOperandType(left, appContext);
        var rightScalar = FinalScalarOperandType(right, appContext);
        var changed = false;
        if (left is LocalVariable leftLocal && rightScalar != null)
            changed |= BindReplaceableComparisonCarrier(leftLocal, rightScalar);
        if (right is LocalVariable rightLocal && leftScalar != null)
            changed |= BindReplaceableComparisonCarrier(rightLocal, leftScalar);
        return changed;
    }

    internal static bool BindRecoveredLengthComparisonOperandTypes(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned
            || instruction.Operands.Count != 3)
            return false;

        var left = instruction.Operands[1];
        var right = instruction.Operands[2];
        if (left is LocalVariable leftLocal && IsRecoveredLengthOperand(right))
            return BindReplaceableComparisonCarrier(leftLocal, appContext.SystemTypes.SystemInt32Type);
        if (right is LocalVariable rightLocal && IsRecoveredLengthOperand(left))
            return BindReplaceableComparisonCarrier(rightLocal, appContext.SystemTypes.SystemInt32Type);
        return false;
    }

    private static bool IsRecoveredLengthOperand(IOperand operand) =>
        operand is ArrayLength or ListCount or StringLength;

    /// <summary>
    /// 字段、数组长度、List.Count 与字符串长度都携带托管层的精确标量类型。比较指令可直接消费
    /// 这些操作数，因此循环归纳变量无需先经过一个额外 Move 才能获得类型。
    /// </summary>
    private static TypeAnalysisContext? FinalScalarOperandType(
        IOperand operand,
        ApplicationAnalysisContext? appContext)
    {
        var type = operand switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            ArrayLength or ListCount or StringLength when appContext != null =>
                appContext.SystemTypes.SystemInt32Type,
            _ => null,
        };
        return IsFinalScalarType(type) ? type : null;
    }

    /// <summary>
    /// 精确比较操作数只替换尚未定型、object 或布尔 ABI 占位；另一数值域和普通托管引用保持不变。
    /// </summary>
    private static bool BindReplaceableComparisonCarrier(
        LocalVariable local,
        TypeAnalysisContext targetType)
    {
        if (IsFinalScalarType(local.Type)
            || local.Type?.FullName is not (null or "System.Object" or "System.Boolean"))
            return false;
        return SetAuthoritativeNumericType(local, targetType);
    }

    internal static bool BindSizedIntegerOperationTypes(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.OpCode is not (
                OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.ShiftLeft or OpCode.ShiftRight
                or OpCode.And or OpCode.Or or OpCode.Xor)
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination)
            return false;

        var targetType = instruction.IntegerWidthBits switch
        {
            32 => appContext.SystemTypes.SystemInt32Type,
            64 => appContext.SystemTypes.SystemInt64Type,
            _ => ExactIntegerDestinationType(instruction, destination, appContext),
        };
        if (targetType == null)
            return false;
        var locals = instruction.Operands.OfType<LocalVariable>().Distinct().ToArray();
        if (locals.Any(local => !IsReplaceableFinalIntegerCarrier(local.Type, targetType, appContext)))
            return false;

        var hasExactIntegerEvidence = locals.Any(local =>
            GenericCallRebinder.TypesEquivalent(local.Type, targetType));
        var hasNonBooleanMask = instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor
            && instruction.Operands.OfType<Immediate>().Any(immediate => immediate.Value is < 0 or > 1);
        if (!hasExactIntegerEvidence && !hasNonBooleanMask)
            return false;

        var changed = false;
        foreach (var local in locals)
            changed |= SetExactType(local, targetType);
        return changed;
    }

    /// <summary>
    /// CFG 重写偶尔会丢失原生 W/X 位宽；此时只允许非布尔位掩码从已经精确定型的目标
    /// 反向约束同一条位运算，避免把普通引用算术猜成整数。
    /// </summary>
    private static TypeAnalysisContext? ExactIntegerDestinationType(
        Instruction instruction,
        LocalVariable destination,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.OpCode is not (OpCode.And or OpCode.Or or OpCode.Xor)
            || !instruction.Operands.OfType<Immediate>().Any(immediate => immediate.Value is < 0 or > 1))
            return null;

        if (GenericCallRebinder.TypesEquivalent(destination.Type, appContext.SystemTypes.SystemInt32Type))
            return appContext.SystemTypes.SystemInt32Type;
        if (GenericCallRebinder.TypesEquivalent(destination.Type, appContext.SystemTypes.SystemInt64Type))
            return appContext.SystemTypes.SystemInt64Type;
        return null;
    }

    private static bool IsFinalScalarType(TypeAnalysisContext? type) => type?.FullName is
        "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
        or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"
        or "System.Single" or "System.Double";

    private static bool IsReplaceableFinalIntegerCarrier(
        TypeAnalysisContext? type,
        TypeAnalysisContext targetType,
        ApplicationAnalysisContext appContext)
    {
        return type == null
            || GenericCallRebinder.TypesEquivalent(type, targetType)
            || GenericCallRebinder.TypesEquivalent(type, appContext.SystemTypes.SystemBooleanType)
            || GenericCallRebinder.TypesEquivalent(type, appContext.SystemTypes.SystemObjectType);
    }

    private static bool PropagateConditionalSelect(Instruction select)
    {
        if (select.Operands is not
            [LocalVariable destination, _, LocalVariable whenTrue, LocalVariable whenFalse])
            return false;

        var changed = SetTypeIfUnknown(destination, whenTrue.Type ?? whenFalse.Type);
        if (destination.Type != null)
        {
            changed |= SetTypeIfUnknown(whenTrue, destination.Type);
            changed |= SetTypeIfUnknown(whenFalse, destination.Type);
        }

        return changed;
    }

    private static bool PropagateMove(Instruction move, int pointerSize)
    {
        var destination = move.Operands[0];
        var source = move.Operands[1];

        // Move local, local: copy a known type in whichever direction is missing it.
        if (destination is LocalVariable destLocal && source is LocalVariable sourceLocal)
            return SetTypeIfUnknown(destLocal, sourceLocal.Type) || SetTypeIfUnknown(sourceLocal, destLocal.Type);

        // Move local, field: a field load types its result with the field's type. This is the edge
        // that lets the loaded value go on to be the base of a further field access.
        if (destination is LocalVariable loadDest && source is FieldReference loadField)
            return BindResolvedFieldLoadType(loadDest, loadField.Field.FieldType);

        // Move field, local: a field store types the stored value with the field's type.
        if (destination is FieldReference storeField && source is LocalVariable storeSource)
            return SetTypeIfUnknown(storeSource, storeField.Field.FieldType);

        // An element of T[] is a T, whether we loaded it (reference arrays) or only computed its address
        if (destination is LocalVariable { Type: null } elementDest
            && source is MemoryOperand { Base: LocalVariable { Type: SzArrayTypeAnalysisContext { ElementType: { } elementType } } } elementAccess
            && (elementAccess.Index != null || elementAccess.Addend >= 4L * pointerSize))
            return SetTypeIfUnknown(elementDest, elementType);

        // Move local, [obj]: offset 0 of a reference-typed value is its klass pointer.
        if (destination is LocalVariable { Type: null } klassDest
            && source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: { } baseType } }
            && baseType is not (RuntimeClassTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext or RuntimeMethodInfoAnalysisContext)
            && !baseType.IsValueType)
            return SetTypeIfUnknown(klassDest, new RuntimeClassTypeAnalysisContext(baseType, baseType.DeclaringAssembly));

        return false;
    }

    /// <summary>
    /// 已解析字段的声明类型是加载结果的权威证据。调用形参传播可能先把同一SSA局部
    /// 收敛为<c>System.Object</c>、Unity基类或接口；若字段类型可赋给该宽类型，就恢复更精确的字段类型。
    /// 不相容类型仍保持原样，避免用偏移猜测覆盖其它权威定义。
    /// </summary>
    internal static bool BindResolvedFieldLoadType(
        LocalVariable destination,
        TypeAnalysisContext fieldType)
    {
        if (GenericCallRebinder.TypesEquivalent(destination.Type, fieldType))
            return false;

        if (destination.Type != null && !fieldType.IsAssignableTo(destination.Type))
            return false;

        destination.Type = fieldType;
        return true;
    }

    /// <summary>
    /// 恢复由<c>Move pointer, &amp;slot</c>建立的托管地址关系，并让通过该地址完成的读写与
    /// 栈槽元素类型双向收敛。地址载体本身必须是<c>T&amp;</c>，而不是槽内的<c>T</c>；否则
    /// IL生成阶段会把<c>ldloca</c>写入对象局部，反编译后形成<c>object* -&gt; object</c>非法转换。
    /// </summary>
    internal static bool BindAddressCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        List<string>? changeDetails = null)
    {
        var addressedSlots = new Dictionary<LocalVariable, LocalVariable>();
        var forwardCopies = new Dictionary<LocalVariable, List<LocalVariable>>();
        var pendingCarriers = new Queue<LocalVariable>();

        // SSA局部量通常只有一个定义，但内联类型检查恢复会在原复制局部量上追加CastClass等
        // 语义定义。此类定义会结束原来的地址别名；若仍沿旧Move边传播，地址绑定与强制转换
        // 会在每轮类型分析中互相覆盖，最终使大型方法无法达到不动点。
        var semanticallyDefinedLocals = instructions
            .Where(instruction => instruction.OpCode != OpCode.Move
                && instruction.Destination is LocalVariable)
            .Select(instruction => (LocalVariable)instruction.Destination!)
            .ToHashSet();

        // 一次扫描同时建立直接取址根和局部量复制邻接表，后续用队列沿源到目标方向传播。
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination)
                continue;

            // 非复制指令已经为该局部量建立了新的值语义，旧Move不得再把它归入地址链。
            if (semanticallyDefinedLocals.Contains(destination))
                continue;

            if (instruction.Operands[1] is AddressOf { Target: LocalVariable slot })
            {
                if (!addressedSlots.ContainsKey(destination))
                {
                    addressedSlots.Add(destination, slot);
                    pendingCarriers.Enqueue(destination);
                }
                continue;
            }

            if (instruction.Operands[1] is not LocalVariable source)
                continue;

            if (!forwardCopies.TryGetValue(source, out var destinations))
                forwardCopies[source] = destinations = [];
            destinations.Add(destination);
        }

        while (pendingCarriers.Count > 0)
        {
            var source = pendingCarriers.Dequeue();
            if (!forwardCopies.TryGetValue(source, out var destinations))
                continue;

            foreach (var destination in destinations)
            {
                if (addressedSlots.ContainsKey(destination))
                    continue;

                addressedSlots.Add(destination, addressedSlots[source]);
                pendingCarriers.Enqueue(destination);
            }
        }

        if (addressedSlots.Count == 0)
            return false;

        // 只有真正作为内存基址解引用的地址链才是托管地址载体。原生代码经常把栈地址直接
        // 当作整数实参或算术操作数；若仅凭AddressOf就改成T&，会把这些合法的整数槽位
        // 错写成另一个调用留下的引用类型。按槽位归组后，只保留至少存在一次直接解引用的链。
        var dereferencedSlots = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            foreach (var operand in instruction.Operands)
            {
                if (operand is MemoryOperand
                    {
                        Base: LocalVariable carrier,
                        Index: null,
                        Addend: 0,
                        Scale: 0
                    }
                    && addressedSlots.TryGetValue(carrier, out var slot))
                    dereferencedSlots.Add(slot);
            }
        }

        if (dereferencedSlots.Count == 0)
            return false;

        addressedSlots = addressedSlots
            .Where(pair => dereferencedSlots.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var changed = false;

        // 再单次扫描直接解引用写入，从写入值确定槽位T。
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not MemoryOperand
                {
                    Base: LocalVariable carrier,
                    Index: null,
                    Addend: 0,
                    Scale: 0
                }
                || !addressedSlots.TryGetValue(carrier, out var slot)
                || instruction.Operands[1] is not LocalVariable { Type: { } storedType })
                continue;

            changed |= SetTypeIfUnknown(slot, storedType);
        }

        // 槽位类型确定后，结构性取址事实优先于此前从寄存器复用得到的普通对象猜测。
        foreach (var pair in addressedSlots)
        {
            var carrier = pair.Key;
            var slot = pair.Value;
            if (slot.Type == null)
                continue;

            var expectedCarrierType = slot.Type.MakeByReferenceType();
            if (!GenericCallRebinder.TypesEquivalent(carrier.Type, expectedCarrierType))
            {
                changeDetails?.Add($"BindAddressCarrierTypes:{carrier.Name}@{carrier.Register}:"
                    + $"{DescribeType(carrier.Type)}->{DescribeType(expectedCarrierType)}:"
                    + $"slot={slot.Name}@{slot.Register}");
                carrier.Type = expectedCarrierType;
                changed = true;
            }
        }

        // 通过T&读取的值就是T；该边用于继续解析读取结果后的字段和虚调用。
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination
                || instruction.Operands[1] is not MemoryOperand
                {
                    Base: LocalVariable carrier,
                    Index: null,
                    Addend: 0,
                    Scale: 0
                }
                || !addressedSlots.TryGetValue(carrier, out var slot))
                continue;

            changed |= SetTypeIfUnknown(destination, slot.Type);
        }

        return changed;
    }

    // A phi is a copy from each predecessor's value, so types flow both ways across it - mirroring
    // the bidirectional Move copies it decays into once SSA is destroyed.
    internal static bool PropagatePhi(Instruction phi)
    {
        if (phi.Operands[0] is not LocalVariable destination)
            return false;

        var inputs = phi.Operands
            .Skip(1)
            .OfType<LocalVariable>()
            .ToArray();
        if (inputs.Length == 0 || inputs.Any(input => input.Type == null))
            return false;

        var consensusType = inputs[0].Type!;
        if (inputs.Skip(1).Any(input =>
                !GenericCallRebinder.TypesEquivalent(input.Type, consensusType)))
            return false;

        // Phi 只表达同一个托管值在控制流汇合处的选择。若已知目标类型与完整入边共识
        // 冲突，该 Phi 来自物理寄存器复用，保持各自类型而不再双向污染。
        if (destination.Type != null
            && !GenericCallRebinder.TypesEquivalent(destination.Type, consensusType))
        {
            // 中文注释：共享泛型登记的 object 实例只是 ABI 占位；全部入边已经形成同一具体
            // 泛型类型时，以入边共识覆盖占位，恢复委托缓存和集合在分支汇合处的真实类型。
            if (!GenericCallRebinder.IsSharedObjectPlaceholder(destination.Type, consensusType))
                return false;

            destination.Type = consensusType;
            return true;
        }

        return SetTypeIfUnknown(destination, consensusType);
    }

    private static bool PropagateFromCallParameters(MethodAnalysisContext method)
    {
        var changed = false;

        // 后置接口恢复仍处于SSA，但个别语义重写可能为同一局部追加定义；只沿唯一Move/Phi
        // 定义反向追踪，避免把一次调用的接口约束扩散到不确定的多定义值。
        var uniqueDefinitions = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

        // A lea and the call it's passed to are still separate here. The address only gets folded into the
        // call later, so an argument's address-of has to be found through the local carrying it.
        var addressesOf = new Dictionary<LocalVariable, LocalVariable>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable pointer
                && instruction.Operands[1] is AddressOf { Target: LocalVariable pointee })
                addressesOf[pointer] = pointee;
        }

        LocalVariable? Addressed(IOperand operand) => operand switch
        {
            AddressOf { Target: LocalVariable direct } => direct,
            LocalVariable local when addressesOf.TryGetValue(local, out var indirect) => indirect,
            _ => null
        };

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            // Object::New 已经把目标局部标记为 Newobj，但原生分配入口本身没有托管构造器
            // 的返回值语义。真实实例类型以紧随其后的 .ctor 接收者为准；若此前因栈帧
            // 寄存器复用留下了值类型猜测，这里必须用构造器声明类型覆盖，避免生成
            // Unsafe.AsPointer(ref *(?*)new Dictionary(...)) 一类非法源码。
            if (instruction.OpCode == OpCode.CallVoid
                && calledMethod.Name == ".ctor"
                && !calledMethod.IsStatic
                && calledMethod.DeclaringType is { } constructedType
                && instruction.Operands.Count > 1
                && instruction.Operands[1] is LocalVariable constructedReceiver)
            {
                changed |= SetConstructedReceiverType(constructedReceiver, constructedType, uniqueDefinitions);
            }

            // Return value: a constructor yields its declaring type, otherwise the declared return type.
            if (instruction.Destination is LocalVariable returnValue)
            {
                changed |= SetTypeIfUnknown(returnValue,
                    calledMethod.Name is ".ctor" or ".cctor" ? calledMethod.DeclaringType : calledMethod.ReturnType);
            }


            // Call operands
            // 0. Target
            // 1. ReturnValue
            // 2. thisParam
            // ... parameters

            // CallVoid operands
            // 0. Target
            // 1. thisParam
            // ... parameters
            var thisParamIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // 'this' param
            if (!calledMethod.IsStatic
                && instruction.Operands[thisParamIndex] is LocalVariable thisParam)
            {
                changed |= BindResolvedInstanceReceiverCopySources(
                    thisParam,
                    calledMethod,
                    uniqueDefinitions);
            }

            // Value type instance method, first arg is address of value, but we need to type the value
            if (!calledMethod.IsStatic
                && Addressed(instruction.Operands[thisParamIndex]) is { } addressedReceiver
                && calledMethod.DeclaringType is { IsValueType: true } valueType)
            {
                changed |= SetTypeIfUnknown(addressedReceiver, valueType);
            }

            // Remaining arguments map positionally onto the callee's declared parameters.
            var paramOffset = calledMethod.IsStatic ? 1 : 2;
            if (instruction.OpCode == OpCode.Call) // Skip the return value operand
                paramOffset += 1;

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                var parameterType = calledMethod.Parameters[parameterIndex].ParameterType;

                if (instruction.Operands[i] is HomogeneousFloatingAggregateArgument aggregate
                    && Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                        parameterType,
                        out var aggregateFields)
                    && aggregateFields.Count == aggregate.Components.Count)
                {
                    for (var componentIndex = 0; componentIndex < aggregateFields.Count; componentIndex++)
                        if (aggregate.Components[componentIndex] is LocalVariable component)
                            changed |= SetTypeIfUnknown(component, aggregateFields[componentIndex].FieldType);
                    continue;
                }

                if (parameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType }
                    && Addressed(instruction.Operands[i]) is { } referenced)
                {
                    changed |= SetTypeFromClosedCallParameter(referenced, referencedType);
                    continue;
                }

                if (instruction.Operands[i] is LocalVariable local)
                    changed |= SetTypeFromClosedCallParameter(local, parameterType);
            }
        }

        return changed;
    }

    private static bool SetConstructedReceiverType(
        LocalVariable receiver,
        TypeAnalysisContext constructedType,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions)
    {
        // 先完整确认接收者确实来自 Object::New。构造器也会被显式调用在既有实例、
        // 可空值与共享寄存器上；若边走边写类型，会与 IsInst/字段证据相互覆盖并振荡。
        var allocationChain = new List<LocalVariable>();
        var current = receiver;
        var visited = new HashSet<LocalVariable>();
        while (visited.Add(current))
        {
            allocationChain.Add(current);
            if (!uniqueDefinitions.TryGetValue(current, out var definition))
                return false;

            if (definition.OpCode == OpCode.Newobj)
                break;

            if (definition is not { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
                return false;
            current = source;
        }

        if (!uniqueDefinitions.TryGetValue(current, out var allocation)
            || allocation.OpCode != OpCode.Newobj)
            return false;

        // 正常 Object::New 已经给出封闭托管类型，构造器解析只负责消除原生地址载体
        // 被误投影成 Newobj 结果的缺口。若分配链没有 ByRef/Pointer 证据，保留既有类型，
        // 避免把共享构造器声明类型扩散到所有正常对象分配。
        if (!allocationChain.Any(local => local.Type is ByRefTypeAnalysisContext or PointerTypeAnalysisContext))
            return false;

        var targetType = receiver.Type is { } receiverType
                         && HasClosedGenericProjection(receiverType, constructedType)
            ? receiverType
            : constructedType;
        var changed = false;
        foreach (var allocated in allocationChain)
        {
            if (!GenericCallRebinder.TypesEquivalent(allocated.Type, targetType))
            {
                allocated.Type = targetType;
                changed = true;
            }
        }

        return changed;
    }

    private static bool HasClosedGenericProjection(TypeAnalysisContext? actual, TypeAnalysisContext expected)
        => actual is GenericInstanceTypeAnalysisContext actualGeneric
           && expected is GenericInstanceTypeAnalysisContext expectedGeneric
           && actualGeneric.GenericParameters.All(parameter => !ContainsUninstantiatedGenericParameter(parameter))
           && GenericCallRebinder.TypesEquivalent(actualGeneric.GenericType, expectedGeneric.GenericType);

    /// <summary>
    /// 已重绑定调用的封闭参数签名可以取代局部变量上的开放泛型占位符。
    /// 具体局部类型仍保持不变；参数自身仍开放时也不传播，从而维持单调收敛并拒绝冲突推断。
    /// </summary>
    internal static bool SetTypeFromClosedCallParameter(
        LocalVariable local,
        TypeAnalysisContext? parameterType)
    {
        if (parameterType == null || ContainsUninstantiatedGenericParameter(parameterType))
            return false;

        if (local.Type == null)
            return SetTypeIfUnknown(local, parameterType);

        if (!ContainsUninstantiatedGenericParameter(local.Type))
            return false;

        local.Type = parameterType;
        return true;
    }

    /// <summary>
    /// 已解析接口调用的接收者可能只是X0参数副本，真实长期值保存在X19-X28或Phi输入中。
    /// 若只修正调用点副本，SSA简化会再次以内联源值替换它。沿唯一Move/Phi定义反向绑定，
    /// 让调用点及所有确切入边共享同一接口类型；遇到非复制定义立即停止。
    /// </summary>
    internal static bool BindResolvedInstanceReceiverCopySources(
        LocalVariable receiver,
        MethodAnalysisContext calledMethod,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions)
    {
        // 共享泛型方法会把隐藏 MethodInfo 保存到非易失寄存器，再把它搬入 X0 调用
        // 原生 rgctx 初始化函数。该原生地址可能与当前托管方法共享并被暂时绑定为伪递归；
        // 若先按实例调用传播，保存寄存器会被误写成声明类型，随后 [MethodInfo+rgctx]
        // 又会被字段偏移解析器错误投影成同偏移业务字段。必须在写入任何接收者类型前，
        // 对完整唯一 Move/Phi 复制闭包执行一次运行时元数据身份预检。
        if (ContainsRuntimeMetadataCarrier(receiver, uniqueDefinitions))
            return false;

        var changed = false;
        var pending = new Queue<LocalVariable>();
        var visited = new HashSet<LocalVariable>();
        pending.Enqueue(receiver);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
                continue;

            changed |= BindResolvedInstanceReceiverType(current, calledMethod);
            if (!uniqueDefinitions.TryGetValue(current, out var definition))
                continue;

            if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
            {
                pending.Enqueue(source);
                continue;
            }

            if (definition.OpCode != OpCode.Phi)
                continue;

            for (var operandIndex = 1; operandIndex < definition.Operands.Count; operandIndex++)
                if (definition.Operands[operandIndex] is LocalVariable input)
                    pending.Enqueue(input);
        }

        return changed;
    }

    /// <summary>
    /// 沿唯一复制闭包判断接收者是否源自 IL2CPP 运行时元数据载体。任一入边携带
    /// MethodInfo、RuntimeClass、RGCTXData表或静态字段存储身份时，整个闭包均不得按
    /// 托管实例接收者定型；普通业务对象和没有唯一定义的局部保持原有传播规则。
    /// </summary>
    internal static bool ContainsRuntimeMetadataCarrier(
        LocalVariable receiver,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions)
    {
        var pending = new Queue<LocalVariable>();
        var visited = new HashSet<LocalVariable>();
        pending.Enqueue(receiver);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
                continue;

            if (current.Type is RuntimeMethodInfoAnalysisContext
                or RuntimeClassTypeAnalysisContext
                or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext
                or StaticFieldStorageTypeAnalysisContext)
                return true;

            if (!uniqueDefinitions.TryGetValue(current, out var definition))
                continue;

            if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
            {
                pending.Enqueue(source);
                continue;
            }

            if (definition.OpCode != OpCode.Phi)
                continue;

            for (var operandIndex = 1; operandIndex < definition.Operands.Count; operandIndex++)
                if (definition.Operands[operandIndex] is LocalVariable input)
                    pending.Enqueue(input);
        }

        return false;
    }

    /// <summary>
    /// 已解析托管实例调用的声明类型是接收者的权威下界。普通具体类型仍沿用单调的“只填空值”
    /// 传播；接口分派只覆盖含未实例化泛型参数的占位类型。这样既保留object、IConvertible等
    /// 运行时合法接收者，也能修正共享泛型查表把IEnumerator误写成List&lt;T&gt;的情况。
    /// </summary>
    internal static bool BindResolvedInstanceReceiverType(
        LocalVariable receiver,
        MethodAnalysisContext calledMethod)
    {
        var declaringType = calledMethod.DeclaringType;
        if (declaringType == null)
            return false;

        if (receiver.Type == null)
            return SetTypeIfUnknown(receiver, declaringType);

        if (GenericCallRebinder.TypesEquivalent(receiver.Type, declaringType))
            return false;

        var declaringInterface = declaringType is GenericInstanceTypeAnalysisContext declaringGenericInstance
            ? declaringGenericInstance.GenericType.IsInterface
            : declaringType.IsInterface;
        if (!ContainsUninstantiatedGenericParameter(receiver.Type))
            return false;

        // 同一个泛型定义的开放接收者可以由已经重绑定的封闭调用目标精确闭合；这覆盖
        // List<!0>.AddWithResize(string) 等非接口实例成员，同时不跨类型改写 IDisposable。
        if (receiver.Type is GenericInstanceTypeAnalysisContext receiverGenericInstance
            && declaringType is GenericInstanceTypeAnalysisContext closedDeclaringInstance
            && GenericCallRebinder.TypesEquivalent(
                receiverGenericInstance.GenericType,
                closedDeclaringInstance.GenericType)
            && !ContainsUninstantiatedGenericParameter(closedDeclaringInstance))
        {
            receiver.Type = declaringType;
            return true;
        }

        // 一个长期局部可能以 IList<T>、ICollection<T> 等多个继承接口分别发起调用。
        // 它已有接口身份时，调用目标只提供行为约束而非新的静态声明类型；继续覆盖会让
        // 不同接口调用在每轮传播中来回改写同一局部，破坏单调不动点。
        var receiverIsInterface = receiver.Type is GenericInstanceTypeAnalysisContext receiverInterfaceInstance
            ? receiverInterfaceInstance.GenericType.IsInterface
            : receiver.Type.IsInterface;
        if (receiverIsInterface)
            return false;

        if (!declaringInterface || receiver.Type.IsAssignableTo(declaringType))
            return false;

        receiver.Type = declaringType;
        return true;
    }

    /// <summary>
    /// 递归识别仍携带类型/方法泛型参数的开放实例；封闭泛型和普通引用类型均不属于占位类型。
    /// </summary>
    internal static bool ContainsUninstantiatedGenericParameter(TypeAnalysisContext type)
        => type is GenericParameterTypeAnalysisContext
           || type is GenericInstanceTypeAnalysisContext genericInstance
           && genericInstance.GenericArguments.Any(ContainsUninstantiatedGenericParameter);

    private static void PropagateFromParameters(MethodAnalysisContext method)
    {
        // 'this'
        if (!method.IsStatic)
        {
            var thisLocal = method.ParameterLocals.FirstOrDefault(p => p.IsThis);
            if (thisLocal != null)
                thisLocal.Type = method.DeclaringType;
        }

        if (method.Parameters.Count == 0)
            return;

        // Normal params
        var paramIndex = 0;
        foreach (var local in method.ParameterLocals)
        {
            if (local.IsThis || local.IsMethodInfo)
                continue;

            if (paramIndex >= method.Parameters.Count)
                break;

            local.Type = method.Parameters[paramIndex].ParameterType;
            paramIndex++;
        }
    }

    private static void PropagateFromReturn(MethodAnalysisContext method)
    {
        var returns = method.ControlFlowGraph!.Instructions.Where(i => i.OpCode == OpCode.Return);

        foreach (var instruction in returns)
        {
            if (instruction.Operands.Count == 1 && instruction.Operands[0] is LocalVariable local)
            {
                local.Type = method.ReturnType;
                continue;
            }

            if (!Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                    method.ReturnType,
                    out var fields)
                || instruction.Operands.Count != fields.Count)
                continue;

            for (var index = 0; index < fields.Count; index++)
            {
                if (instruction.Operands[index] is LocalVariable component)
                    component.Type = fields[index].FieldType;
            }
        }
    }
}
