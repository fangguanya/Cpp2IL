using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
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

        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException(
                    $"Late call and address type resolution not settling! (looped {MaxTypePropagationLoopCount} times)");

            changed = PropagateFromCallParameters(method);
            changed |= BindAddressCarrierTypes(method.ControlFlowGraph!.Instructions);
            changed |= MetadataResolver.ResolveFieldOffsets(method);
            changed |= PropagateBooleanBitTestTypes(method);
            changed |= PropagateTypesOnce(method);
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
        var changed = destination.Type != booleanType
            || (bindSource && source.Type != booleanType);
        destination.Type = booleanType;
        if (bindSource)
            source.Type = booleanType;
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
            || instruction.Operands[1] is not LocalVariable
            {
                Type: GenericInstanceTypeAnalysisContext
                {
                    GenericType.FullName: "System.Nullable`1"
                }
            }
            || instruction.Operands[2] is not Immediate { Value: 0xFF })
            return false;

        if (destination.Type == booleanType)
            return false;

        destination.Type = booleanType;
        return true;
    }
    
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
                case OpCode.Box:
                    instructionChanged = PropagateBox(instruction, method);
                    break;
                case OpCode.CastClass:
                    instructionChanged = PropagateCastClass(instruction);
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
    {
        if (instruction.Operands.Count < 3 ||
            instruction.Operands[0] is not LocalVariable destination ||
            instruction.Operands[1] is not LocalVariable source ||
            instruction.Operands[2] is not Immediate destinationWidth)
            return false;

        var systemTypes = method.AppContext.SystemTypes;
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

        var changed = SetTypeIfUnknown(destination, destinationType);
        changed |= SetTypeIfUnknown(source, sourceType);
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
            return SetTypeIfUnknown(loadDest, loadField.Field.FieldType);

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
    private static bool PropagatePhi(Instruction phi)
    {
        if (phi.Operands[0] is not LocalVariable destination)
            return false;

        var changed = false;

        // Forward: an untyped phi result takes the type of any typed input.
        if (destination.Type == null)
        {
            for (var i = 1; i < phi.Operands.Count; i++)
            {
                if (phi.Operands[i] is LocalVariable { Type: { } inputType })
                {
                    changed = SetTypeIfUnknown(destination, inputType);
                    break;
                }
            }
        }

        // Backward: a typed phi result types each of its still-untyped inputs.
        if (destination.Type != null)
        {
            for (var i = 1; i < phi.Operands.Count; i++)
            {
                if (phi.Operands[i] is LocalVariable input)
                    changed |= SetTypeIfUnknown(input, destination.Type);
            }
        }

        return changed;
    }

    private static bool PropagateFromCallParameters(MethodAnalysisContext method)
    {
        var changed = false;

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
                changed |= SetTypeIfUnknown(thisParam, calledMethod.DeclaringType);
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
                    changed |= SetTypeIfUnknown(referenced, referencedType);
                    continue;
                }

                if (instruction.Operands[i] is LocalVariable local)
                    changed |= SetTypeIfUnknown(local, parameterType);
            }
        }

        return changed;
    }

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
