using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
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

            var local = FindParameterLocal(
                method.Locals,
                reg,
                method.ControlFlowGraph);
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

    /// <summary>
    /// 查找参数在 SSA 局部中的入口身份。SSA 明确以 <c>Version=-1</c> 表示方法入口活值，
    /// 任何非负版本都是方法体内的定义，不得仅因版本号最小就当作参数。
    /// </summary>
    internal static LocalVariable? FindParameterLocal(
        IEnumerable<LocalVariable> locals,
        Register parameterRegister,
        ISILControlFlowGraph? graph = null)
    {
        var entryLocals = locals
            .Where(local => local.Register.Version == -1)
            .ToList();

        // 先保留历史上的精确寄存器身份，避免对其他架构扩大匹配范围。
        var exact = entryLocals.FirstOrDefault(local =>
            local.Register.Number == parameterRegister.Number
            && local.Register.Name == parameterRegister.Name);

        // ARM64 窄参数可由 Wn 传入，而后续 ISIL 以 Xn 读取同一物理寄存器。
        // 只有唯一的未定义入口别名才构成身份证明；存在歧义时保持未绑定。
        var parameterPhysicalName = CanonicalParameterRegisterName(parameterRegister.Name);
        var aliases = entryLocals
            .Where(local => CanonicalParameterRegisterName(local.Register.Name) == parameterPhysicalName)
            .Take(2)
            .ToArray();
        // ARM64 返回寄存器与首参寄存器重合时，出口 Phi 会合并“原参数”和
        // “分支返回值”。退 SSA 前必须把该 Phi 作为参数身份保护，否则复制传播会让
        // 早期参数读取变成 default。只接受唯一的、由 Phi/Move 链确切连回入口局部的正版本。
        if (graph != null)
        {
            var entrySet = new HashSet<LocalVariable>(entryLocals);
            var definitions = graph.Blocks
                .SelectMany(block => block.Instructions)
                .Where(instruction => instruction.Destination is LocalVariable)
                .GroupBy(instruction => (LocalVariable)instruction.Destination!)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var provenEntryPhis = locals
                .Where(local => local.IsReturn
                                && local.Register.Version >= 0
                                && CanonicalParameterRegisterName(local.Register.Name) == parameterPhysicalName
                                && IsEntryPhiCarrier(local, entrySet, definitions))
                .Take(2)
                .ToArray();
            if (provenEntryPhis.Length == 1)
                return provenEntryPhis[0];
        }

        if (exact != null)
            return exact;
        return aliases.Length == 1 ? aliases[0] : null;
    }

    /// <summary>
    /// 判断候选是否为确切合并入口参数的 Phi。根候选必须由唯一 Phi 定义；
    /// 内部只沿唯一 Move/Phi 定义回溯，任何算术、调用或多定义都不构成入口身份。
    /// </summary>
    internal static bool IsEntryPhiCarrier(
        LocalVariable candidate,
        ISet<LocalVariable> entryLocals,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions)
    {
        if (!definitions.TryGetValue(candidate, out var rootDefinitions)
            || rootDefinitions is not [{ OpCode: OpCode.Phi }])
            return false;

        return ReachesEntry(candidate, entryLocals, definitions, []);
    }

    private static bool ReachesEntry(
        LocalVariable current,
        ISet<LocalVariable> entryLocals,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        HashSet<LocalVariable> visited)
    {
        if (entryLocals.Contains(current))
            return true;
        if (!visited.Add(current)
            || !definitions.TryGetValue(current, out var currentDefinitions)
            || currentDefinitions.Length != 1
            || currentDefinitions[0].OpCode is not (OpCode.Move or OpCode.Phi))
            return false;

        var reachesEntry = currentDefinitions[0].Sources
            .OfType<LocalVariable>()
            .Any(source => ReachesEntry(source, entryLocals, definitions, visited));
        visited.Remove(current);
        return reachesEntry;
    }

    /// <summary>
    /// ARM64 调用约定可能以 Wn 描述窄参数，而指令恢复统一以 Xn 保存同一物理寄存器。
    /// 这里只归一 W0-W30，其他架构与特殊寄存器名称保持原样。
    /// </summary>
    internal static string CanonicalParameterRegisterName(string name)
    {
        if (name.Length < 2 || name[0] != 'W')
            return name;

        var index = 0;
        for (var characterIndex = 1; characterIndex < name.Length; characterIndex++)
        {
            // ARM64 寄存器名称只接受十进制数字；符号、空白和其他字符均不是物理寄存器别名。
            var digit = name[characterIndex] - '0';
            if ((uint)digit > 9)
                return name;

            index = index * 10 + digit;
            if (index > 30)
                return name;
        }

        return $"X{index}";
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
            var captureFinalIteration = MaxTypePropagationLoopCount != -1
                && loopCount == MaxTypePropagationLoopCount;
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

    /// <summary>
    /// 退 SSA 保守简化后只闭合同址 TypeInfo、static_fields、字段类型与受其驱动的泛型调用。
    /// </summary>
    public static void ResolveRuntimeClassSlotFieldClosure(
        MethodAnalysisContext method,
        IReadOnlyList<RuntimeClassSlotIdentityRecovery.SlotGroup> runtimeClassSlotGroups)
    {
        if (runtimeClassSlotGroups.Count == 0)
            return;

        var changed = true;
        var loopCount = 0;
        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException(
                    $"Runtime class slot field closure not settling! (looped {MaxTypePropagationLoopCount} times)");

            changed = RuntimeClassSlotIdentityRecovery.Run(runtimeClassSlotGroups) > 0;
            changed |= PropagateStaticFieldStorage(method);
            changed |= MetadataResolver.ResolveFieldOffsets(method);
            changed |= PropagateTypesOnce(method);
            changed |= GenericCallRebinder.Run(method);
            changed |= PropagateFromCallParameters(method);
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
        var instructions = method.ControlFlowGraph!.Instructions.ToArray();
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);
        var resolvedClassTypes = new Dictionary<LocalVariable, TypeAnalysisContext?>();
        var changedAllocations = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Newobj
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination
                || ResolveInstantiatedType(
                    instruction.Operands[1],
                    definitions,
                    resolvedClassTypes,
                    []) is not { } instantiatedType
                || GenericCallRebinder.TypesEquivalent(destination.Type, instantiatedType))
                continue;

            destination.Type = instantiatedType;
            changedAllocations.Add(destination);
        }

        if (changedAllocations.Count == 0)
            return false;

        var changed = true;
        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall)
                continue;

            var receiverIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            if (receiverIndex < instruction.Operands.Count
                && instruction.Operands[receiverIndex] is LocalVariable receiver
                && changedAllocations.Contains(receiver))
                changed |= GenericCallRebinder.TryRebind(instruction, method);
        }

        return changed;
    }

    /// <summary>
    /// 从 Newobj 的类载体恢复实例类型。活跃 Move/Phi 入边比载体上残留的寄存器复用类型更强；
    /// Phi 的每条入边必须给出同一 Il2CppClass&lt;T&gt;，任一缺失或冲突均保持未解析。
    /// </summary>
    private static TypeAnalysisContext? ResolveInstantiatedType(
        IOperand classOperand,
        ILookup<LocalVariable, Instruction> definitions,
        IDictionary<LocalVariable, TypeAnalysisContext?> memo,
        HashSet<LocalVariable> visiting)
    {
        if (classOperand is not LocalVariable local)
            return InstantiatedType(classOperand);
        if (memo.TryGetValue(local, out var cached))
            return cached;
        if (!visiting.Add(local))
            return null;

        var localDefinitions = definitions[local].Take(2).ToArray();
        TypeAnalysisContext? resolved;
        if (localDefinitions is [{ OpCode: OpCode.Move, Operands: [LocalVariable _, var source] }])
        {
            resolved = ResolveInstantiatedType(source, definitions, memo, visiting);
            // 原生内存读取本身没有托管类型操作数；此时解析器已经写入目标局部的
            // RuntimeClassType，允许把它作为该次读取的精确结果。其他未知来源不得借用陈旧类型。
            if (resolved == null && source is MemoryOperand)
                resolved = InstantiatedType(local);
        }
        else if (localDefinitions is [{ OpCode: OpCode.Phi } phi])
        {
            var sources = phi.Operands.Skip(1).ToArray();
            var sourceTypes = sources
                .Select(source => ResolveInstantiatedType(source, definitions, memo, visiting))
                .ToArray();
            resolved = sourceTypes.Length > 0
                       && sourceTypes.All(type => type != null)
                       && sourceTypes.Skip(1).All(type =>
                           GenericCallRebinder.TypesEquivalent(type, sourceTypes[0]))
                ? sourceTypes[0]
                : null;
        }
        else if (localDefinitions.Length == 0)
        {
            resolved = InstantiatedType(local);
        }
        else
        {
            resolved = null;
        }

        visiting.Remove(local);
        memo[local] = resolved;
        return resolved;
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

            var paramOffset = CallArgumentTrimmer.FirstParameterOperandIndex(instruction, calledMethod);

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

        bool ResolveOperand(Instruction owner, int operandIndex, TypeAnalysisContext expectedType)
        {
            if (operandIndex >= owner.Operands.Count
                || !TryResolveSelfTypedStaticFieldLoad(
                    owner.Operands[operandIndex],
                    expectedType,
                    uniqueDefinitions,
                    staticFieldsOffset,
                    out var resolvedField))
                return false;

            // 中文注释：Move、Return和调用形参共享同一条字段证明与提交路径，避免三处
            // 分别维护静态表闭包后产生规则漂移或重复计数。
            owner.SetOperand(operandIndex, resolvedField!);
            resolvedCount++;
            return true;
        }

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.Return && instruction.Operands.Count == 1)
            {
                // 中文注释：ARM64会把String.Empty直接从TypeInfo静态区装入X0后返回；
                // 返回签名就是精确消费者，必须与Move和调用形参走相同的自类型字段闭包。
                ResolveOperand(instruction, 0, method.ReturnType);
            }

            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count >= 2)
            {
                var expectedType = instruction.Operands[0] switch
                {
                    LocalVariable { Type: { } localType } => localType,
                    FieldReference fieldReference => fieldReference.Field.FieldType,
                    _ => null,
                };

                if (expectedType != null)
                    ResolveOperand(instruction, 1, expectedType);
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

                ResolveOperand(
                    instruction,
                    operandIndex,
                    calledMethod.Parameters[parameterIndex].ParameterType);
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
                case OpCode.ConvertSignedIntegerWidth:
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
        var destinationType = instruction.OpCode is OpCode.ConvertFloatToSignedInteger
            or OpCode.ConvertSignedIntegerWidth
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
            or OpCode.ConvertSignedIntegerWidth
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
                    or OpCode.ConvertSignedIntegerWidth
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
    /// 退 SSA 与集合恢复会在主类型不动点之后引入新的复制结果，浮点源类型也可能直到
    /// 字段和字面量恢复后才落定。这里仅重放既有浮点算术规则，使结果与唯一的
    /// Single/Double 源证据一致，不重新执行字段、调用或控制流分析。
    /// </summary>
    public static bool ResolveFinalFloatingArithmeticCarrierTypes(MethodAnalysisContext method)
    {
        var changed = false;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
                changed |= BindFloatingArithmeticTypes(instruction, method.AppContext);
        return changed;
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
        var instructions = method.ControlFlowGraph!.Instructions;

        // 中文注释：数组长度在 CIL 中天然产生 native unsigned，但普通 Count/Length 属性产生 Int32。
        // ARM64 数组循环常用 X 寄存器承载索引；若长度复制随后与 nint/nuint 比较，则长度局部必须
        // 保持同一原生整数表示，否则 ldlen 的值会被写入 object 或与原生索引形成非法栈合并。
        foreach (var instruction in instructions)
            changed |= BindRecoveredLengthMoveCarrierType(instruction, instructions, method.AppContext);

        foreach (var instruction in instructions)
            changed |= BindRecoveredLengthComparisonOperandTypes(instruction, method.AppContext);
        return changed;
    }

    internal static bool BindRecoveredLengthMoveCarrierType(
        Instruction instruction,
        IReadOnlyList<Instruction> instructions,
        ApplicationAnalysisContext appContext)
    {
        if (instruction is not
            {
                OpCode: OpCode.Move,
                Operands: [LocalVariable destination, var source],
            }
            || !IsRecoveredLengthOperand(source)
            || destination.Type?.FullName is not (null or "System.Object" or "System.Boolean"))
            return false;

        var comparisonTypes = instructions
            .Where(candidate => candidate.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned
                && candidate.Operands.Count == 3
                && candidate.Operands.Skip(1).Contains(destination))
            .SelectMany(candidate => candidate.Operands.Skip(1))
            .Where(operand => !ReferenceEquals(operand, destination))
            .OfType<LocalVariable>()
            .Select(local => local.Type)
            .Where(IsLengthCarrierType)
            .Cast<TypeAnalysisContext>()
            .GroupBy(type => type.FullName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        // 中文注释：冲突的比较宽度不做猜测；没有外部宽度证据时使用托管 Count/Length 的 Int32。
        if (comparisonTypes.Length > 1)
            return false;
        var targetType = comparisonTypes.Length == 1
            ? comparisonTypes[0]
            : appContext.SystemTypes.SystemInt32Type;
        return SetExactType(destination, targetType);
    }

    private static bool IsLengthCarrierType(TypeAnalysisContext? type) =>
        IsFinalScalarType(type) || type?.FullName is "System.IntPtr" or "System.UIntPtr";

    /// <summary>
    /// 私有字段在布局恢复后可能被重新物化为公开属性getter；单次收集这些新产生的精确标量结果，
    /// 同时回填比较操作数和普通整数算术结果，不重复早期原生位宽、字段或属性扫描。
    /// </summary>
    public static bool ResolveRecoveredPropertyScalarCarrierTypes(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var getterResults = new HashSet<LocalVariable>(instructions
            .Where(instruction => instruction is
            {
                OpCode: OpCode.Call,
                Operands: [MethodAnalysisContext { Name: var name }, LocalVariable { Type: { } type }, ..]
            } && name.StartsWith("get_", StringComparison.Ordinal)
              && (IsFinalScalarType(type) || type.FullName == "System.Boolean"))
            .Select(instruction => (LocalVariable)instruction.Operands[1]));
        if (getterResults.Count == 0)
            return false;

        var changed = false;
        foreach (var instruction in instructions)
        {
            if (!instruction.Operands
                .Skip(instruction.Destination is LocalVariable ? 1 : 0)
                .OfType<LocalVariable>()
                .Any(getterResults.Contains))
                continue;
            if (instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned)
                changed |= BindFinalComparisonOperandTypes(instruction, method.AppContext);
            else
                changed |= BindSizedIntegerOperationTypes(instruction, method.AppContext);
        }
        return changed;
    }

    /// <summary>
    /// 退 SSA、集合长度与属性恢复全部完成后，以局部到局部的 Move 为无向等价边，一次性计算
    /// 连通分量中的唯一标量类型。闭合托管调用的形参也是权威类型种子，但仅靠调用种子定型时，
    /// 分量中的每一项定义都必须是同类型复制或范围相容的字面量。引用定义、开放泛型形参或两个
    /// 不同数值域会否决整个分量，避免把共享物理寄存器的另一段生命期误写成标量。
    /// </summary>
    public static bool ResolveFinalScalarCopyCarrierTypes(MethodAnalysisContext method)
    {
        var adjacency = new Dictionary<LocalVariable, HashSet<LocalVariable>>();
        var instructions = method.ControlFlowGraph!.Instructions;
        foreach (var instruction in instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands: [LocalVariable destination, LocalVariable source]
                })
                continue;

            if (!adjacency.TryGetValue(destination, out var destinationEdges))
                adjacency[destination] = destinationEdges = [];
            if (!adjacency.TryGetValue(source, out var sourceEdges))
                adjacency[source] = sourceEdges = [];
            destinationEdges.Add(source);
            sourceEdges.Add(destination);
        }

        var callConstraints = CollectFinalManagedCallScalarConstraints(instructions);
        foreach (var constrained in callConstraints.Keys)
            if (!adjacency.ContainsKey(constrained))
                adjacency[constrained] = [];

        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var visited = new HashSet<LocalVariable>();
        var changed = false;
        foreach (var root in adjacency.Keys)
        {
            if (!visited.Add(root))
                continue;

            var component = new List<LocalVariable>();
            var pending = new Queue<LocalVariable>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                component.Add(current);
                foreach (var neighbor in adjacency[current])
                    if (visited.Add(neighbor))
                        pending.Enqueue(neighbor);
            }

            var scalarTypes = component
                .Select(local => local.Type)
                .Where(IsFinalScalarType)
                .Cast<TypeAnalysisContext>()
                .Concat(component
                    .Where(callConstraints.ContainsKey)
                    .SelectMany(local => callConstraints[local]))
                .GroupBy(type => type.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (scalarTypes.Length != 1)
                continue;

            var consensusType = scalarTypes[0];
            if (component.Any(local => !IsReplaceableFinalCopyCarrier(local.Type, consensusType)))
                continue;

            // 中文注释：既有精确局部类型已经由字段、长度或原生位宽证明；调用形参只负责给
            // 完全未定型的多定义分量补种子，后者必须先逐项证明所有写入与形参存储类型一致。
            var hasExistingScalarSeed = component.Any(local => IsFinalScalarType(local.Type));
            if (!hasExistingScalarSeed
                && !HasOnlyCompatibleFinalCallScalarDefinitions(
                    component,
                    definitions,
                    consensusType))
                continue;

            foreach (var local in component)
                if (!GenericCallRebinder.TypesEquivalent(local.Type, consensusType))
                {
                    local.Type = consensusType;
                    changed = true;
                }
        }

        return changed;
    }

    private static Dictionary<LocalVariable, HashSet<TypeAnalysisContext>>
        CollectFinalManagedCallScalarConstraints(IReadOnlyList<Instruction> instructions)
    {
        var constraints = new Dictionary<LocalVariable, HashSet<TypeAnalysisContext>>();
        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall
                || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var firstParameter = CallArgumentTrimmer.FirstParameterOperandIndex(instruction, calledMethod);
            var availableParameters = Math.Min(
                calledMethod.Parameters.Count,
                instruction.Operands.Count - firstParameter);
            for (var parameterIndex = 0; parameterIndex < availableParameters; parameterIndex++)
            {
                if (instruction.Operands[firstParameter + parameterIndex] is not LocalVariable local)
                    continue;

                var parameterType = calledMethod.Parameters[parameterIndex].ParameterType;
                if (!IsFinalManagedCallScalarType(parameterType)
                    || ContainsUninstantiatedGenericParameter(parameterType))
                    continue;

                if (!constraints.TryGetValue(local, out var localConstraints))
                    constraints[local] = localConstraints = [];
                localConstraints.Add(parameterType);
            }
        }

        return constraints;
    }

    private static bool HasOnlyCompatibleFinalCallScalarDefinitions(
        IReadOnlyCollection<LocalVariable> component,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        TypeAnalysisContext consensusType)
    {
        var componentSet = new HashSet<LocalVariable>(component);
        foreach (var local in component)
        {
            if (!definitions.TryGetValue(local, out var localDefinitions)
                || localDefinitions.Length == 0)
                return false;

            foreach (var definition in localDefinitions)
            {
                if (definition is not { OpCode: OpCode.Move, Operands.Count: 2 })
                    return false;

                var source = definition.Operands[1];
                if (source is LocalVariable sourceLocal)
                {
                    if (!componentSet.Contains(sourceLocal)
                        && !GenericCallRebinder.TypesEquivalent(sourceLocal.Type, consensusType))
                        return false;
                    continue;
                }

                if (!IsCompatibleFinalCallScalarLiteral(source, consensusType))
                    return false;
            }
        }

        return true;
    }

    private static bool IsFinalManagedCallScalarType(TypeAnalysisContext? type) =>
        IsFinalScalarType(type)
        || type?.FullName is "System.Boolean" or "System.Char" or "System.IntPtr" or "System.UIntPtr"
        || type?.IsEnumType == true;

    private static bool IsCompatibleFinalCallScalarLiteral(IOperand operand, TypeAnalysisContext targetType)
    {
        var storageType = targetType.IsEnumType
            ? targetType.EnumUnderlyingType
            : targetType;
        if (storageType == null)
            return false;

        if (operand is FloatLiteral)
            return storageType.FullName == "System.Single";
        if (operand is DoubleLiteral)
            return storageType.FullName == "System.Double";
        if (operand is not Immediate { Value: var value })
            return false;

        return storageType.FullName switch
        {
            "System.Boolean" => value is 0 or 1,
            "System.Char" or "System.UInt16" => value is >= ushort.MinValue and <= ushort.MaxValue,
            "System.SByte" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            "System.Byte" => value is >= byte.MinValue and <= byte.MaxValue,
            "System.Int16" => value is >= short.MinValue and <= short.MaxValue,
            "System.Int32" => value is >= int.MinValue and <= int.MaxValue,
            "System.UInt32" => value is >= uint.MinValue and <= uint.MaxValue,
            "System.Int64" or "System.IntPtr" => true,
            "System.UInt64" or "System.UIntPtr" => value >= 0,
            _ => false,
        };
    }

    /// <summary>
    /// 退 SSA 后恢复由布尔调用结果、零值边复制和 AND/OR/XOR 组成的完整布尔分量。
    /// </summary>
    public static bool ResolveFinalBooleanBitwiseCarrierTypes(MethodAnalysisContext method) =>
        BindFinalBooleanBitwiseComponentTypes(
            method.ControlFlowGraph!.Instructions,
            method.AppContext.SystemTypes.SystemBooleanType);

    /// <summary>
    /// 退 SSA 后，由权威 Boolean 来源或完整 0/1 定义组成，且只被布尔分支消费的局部表示控制流布尔状态。
    /// 该规则不接受算术、字段、调用、引用或非布尔状态码定义，因此不会把普通计数器或对象槽猜成布尔值。
    /// </summary>
    public static bool ResolveFinalBooleanBranchCarrierTypes(MethodAnalysisContext method) =>
        BindFinalBooleanBranchCarrierTypes(
            method.ControlFlowGraph!.Instructions,
            method.AppContext.SystemTypes.SystemBooleanType);

    /// <summary>
    /// ARM64 的条件选择与退 SSA 边复制会把布尔条件和非布尔状态码汇合到同一物理寄存器。
    /// 只有全部定义均由 Int32 范围字面量或 Boolean 值组成、至少存在一个非布尔状态码，且
    /// 全部读取都只是与这些状态码或零比较时，才把最终载体恢复成 Int32。引用、算术、调用、
    /// 越界字面量和其它消费者会否决该局部，避免覆盖同一寄存器的独立托管生命期。
    /// </summary>
    public static IntegerControlStatePlan ResolveFinalIntegerControlStateCarrierTypes(MethodAnalysisContext method)
    {
        BindFinalIntegerControlStateCarrierTypes(
            method.ControlFlowGraph!.Instructions,
            method.AppContext.SystemTypes.SystemInt32Type,
            method.AppContext.SystemTypes.SystemBooleanType,
            out var plan);
        return plan;
    }

    /// <summary>
    /// 退 SSA 与集合重写全部完成后，循环计数器可能只剩“整数范围初始化、同局部自更新、
    /// 与整数立即数比较”三类证据。只有定义和读取完整闭合且全部自更新保留相同 ARM64 位宽时，
    /// 才把 Object/Boolean ABI 占位按 W32 恢复为 Int32、按 X64 恢复为 IntPtr；混合位宽、调用、
    /// 字段和引用消费者保持原类型。
    /// </summary>
    public static bool ResolveFinalIntegerInductionCarrierTypes(MethodAnalysisContext method) =>
        BindFinalIntegerInductionCarrierTypes(
            method.ControlFlowGraph!.Instructions,
            method.AppContext.SystemTypes.SystemInt32Type,
            method.AppContext.SystemTypes.SystemIntPtrType,
            method.AppContext.SystemTypes.SystemBooleanType);

    internal static bool BindFinalIntegerInductionCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext int32Type,
        TypeAnalysisContext intPtrType,
        TypeAnalysisContext booleanType)
    {
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var changed = false;

        foreach (var pair in definitions)
        {
            var local = pair.Key;
            var updates = pair.Value
                .Where(definition => TryGetClosedIntegerInductionUpdateWidth(definition, local, out _))
                .ToArray();
            var updateWidths = updates
                .Select(update => update.IntegerWidthBits)
                .Distinct()
                .ToArray();
            if (updates.Length == 0 || updateWidths.Length != 1)
                continue;

            var targetType = updateWidths[0] switch
            {
                32 => int32Type,
                64 => intPtrType,
                _ => null,
            };
            if (targetType == null
                || !IsReplaceableFinalInductionCarrier(local.Type, targetType, booleanType))
                continue;

            var initializations = pair.Value
                .Where(definition => IsClosedIntegerInductionInitialization(
                    definition,
                    local,
                    updateWidths[0]))
                .ToArray();
            var updateSet = new HashSet<Instruction>(updates);
            if (initializations.Length == 0
                || initializations.Length + updateSet.Count != pair.Value.Length)
                continue;

            var uses = instructions
                .Where(instruction => instruction.Operands
                    .Skip(instruction.Destination is LocalVariable ? 1 : 0)
                    .Any(operand => ReferenceEquals(operand, local)))
                .ToArray();
            if (uses.Length == 0
                || uses.Any(use => !updateSet.Contains(use)
                    && !IsClosedIntegerInductionComparison(use, local, updateWidths[0])))
                continue;

            local.Type = targetType;
            changed = true;
        }

        return changed;
    }

    private static bool IsClosedIntegerInductionInitialization(
        Instruction instruction,
        LocalVariable local,
        int width) =>
        instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, Immediate immediate] }
        && ReferenceEquals(destination, local)
        && IsImmediateInIntegerInductionRange(immediate, width);

    private static bool TryGetClosedIntegerInductionUpdateWidth(
        Instruction instruction,
        LocalVariable local,
        out int width)
    {
        width = instruction.IntegerWidthBits;
        if (width is not (32 or 64)
            || instruction.Operands.Count != 3
            || !ReferenceEquals(instruction.Operands[0], local))
            return false;

        if (instruction.OpCode == OpCode.Subtract)
            return ReferenceEquals(instruction.Operands[1], local)
                && instruction.Operands[2] is Immediate immediate
                && immediate.Value != 0
                && IsImmediateInIntegerInductionRange(immediate, width);

        if (instruction.OpCode != OpCode.Add)
            return false;

        return ReferenceEquals(instruction.Operands[1], local)
                && instruction.Operands[2] is Immediate rightImmediate
                && rightImmediate.Value != 0
                && IsImmediateInIntegerInductionRange(rightImmediate, width)
            || ReferenceEquals(instruction.Operands[2], local)
                && instruction.Operands[1] is Immediate leftImmediate
                && leftImmediate.Value != 0
                && IsImmediateInIntegerInductionRange(leftImmediate, width);
    }

    private static bool IsClosedIntegerInductionComparison(
        Instruction instruction,
        LocalVariable local,
        int width)
    {
        if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned
            || instruction.Operands.Count != 3)
            return false;

        var comparedValues = instruction.Operands
            .Skip(1)
            .Where(operand => !ReferenceEquals(operand, local))
            .ToArray();
        return comparedValues.Length == 1
            && comparedValues[0] is Immediate immediate
            && IsImmediateInIntegerInductionRange(immediate, width);
    }

    private static bool IsImmediateInIntegerInductionRange(Immediate immediate, int width) =>
        width == 64 || immediate.Value is >= int.MinValue and <= int.MaxValue;

    private static bool IsReplaceableFinalInductionCarrier(
        TypeAnalysisContext? type,
        TypeAnalysisContext targetType,
        TypeAnalysisContext booleanType) =>
        type == null
        || GenericCallRebinder.TypesEquivalent(type, targetType)
        || GenericCallRebinder.TypesEquivalent(type, booleanType)
        || type.FullName == "System.Object";

    internal static bool BindFinalIntegerControlStateCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext int32Type,
        TypeAnalysisContext booleanType) =>
        BindFinalIntegerControlStateCarrierTypes(
            instructions,
            int32Type,
            booleanType,
            out _);

    /// <summary>
    /// 一次建立整数控制状态的定义、读取与闭合值域目录，并把同一份结果交给后续控制流恢复。
    /// 测试入口仍可只读取布尔变化结果；生产流水线通过输出计划避免再次扫描整张方法图。
    /// </summary>
    internal static bool BindFinalIntegerControlStateCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext int32Type,
        TypeAnalysisContext booleanType,
        out IntegerControlStatePlan plan)
    {
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();
        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is LocalVariable destination)
            {
                if (!definitions.TryGetValue(destination, out var localDefinitions))
                    definitions[destination] = localDefinitions = [];
                localDefinitions.Add(instruction);
            }

            // 中文注释：同一指令多次读取同一局部只登记一次，既保留完整消费者集合，
            // 又避免后续闭合检查为重复操作数支付重复成本。
            var instructionUses = instruction.Operands
                .Skip(instruction.Destination is LocalVariable ? 1 : 0)
                .OfType<LocalVariable>()
                .Distinct();
            foreach (var source in instructionUses)
            {
                if (!uses.TryGetValue(source, out var localUses))
                    uses[source] = localUses = [];
                localUses.Add(instruction);
            }
        }

        var domains = new Dictionary<LocalVariable, HashSet<long>>();
        var changed = false;

        foreach (var pair in definitions)
        {
            var local = pair.Key;
            if (!IsReplaceableFinalIntegerCarrier(local.Type, int32Type, booleanType)
                || !TryCollectClosedInt32ControlStateValues(
                    pair.Value,
                    booleanType,
                    out var stateValues)
                || !stateValues.Any(value => value is < 0 or > 1))
                continue;

            if (!uses.TryGetValue(local, out var localUses)
                || localUses.Count == 0
                || localUses.Any(use => !IsClosedInt32ControlStateComparison(
                    use,
                    local,
                    stateValues)))
                continue;

            local.Type = int32Type;
            domains[local] = stateValues;
            changed = true;
        }

        plan = new IntegerControlStatePlan(domains);
        return changed;
    }

    private static bool TryCollectClosedInt32ControlStateValues(
        IReadOnlyList<Instruction> definitions,
        TypeAnalysisContext booleanType,
        out HashSet<long> stateValues)
    {
        var collectedStateValues = new HashSet<long>();
        foreach (var definition in definitions)
        {
            IEnumerable<IOperand> values = definition switch
            {
                { OpCode: OpCode.Move, Operands.Count: 2 } => definition.Operands.Skip(1),
                { OpCode: OpCode.ConditionalSelect, Operands.Count: 4 } => definition.Operands.Skip(2),
                _ => [],
            };
            var materializedValues = values.ToArray();
            if (materializedValues.Length == 0
                || materializedValues.Any(value => !IsClosedInt32ControlStateValue(
                    value,
                    booleanType,
                    collectedStateValues)))
            {
                stateValues = [];
                return false;
            }
        }

        stateValues = collectedStateValues;
        return true;
    }

    private static bool IsClosedInt32ControlStateValue(
        IOperand value,
        TypeAnalysisContext booleanType,
        ISet<long> stateValues)
    {
        if (value is Immediate { Value: >= int.MinValue and <= int.MaxValue } immediate)
        {
            stateValues.Add(immediate.Value);
            return true;
        }

        if (value is not LocalVariable source
            || !GenericCallRebinder.TypesEquivalent(source.Type, booleanType))
            return false;

        // 中文注释：Boolean 来源的完整托管值域就是 0/1；把它写入计划后，控制流分析
        // 无需重新检查来源类型，也不会把未知布尔值误当成某一个固定分支。
        stateValues.Add(0);
        stateValues.Add(1);
        return true;
    }

    private static bool IsClosedInt32ControlStateComparison(
        Instruction instruction,
        LocalVariable local,
        ISet<long> stateValues)
    {
        if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned
            || instruction.Operands.Count != 3)
            return false;

        var comparedValues = instruction.Operands
            .Skip(1)
            .Where(operand => !ReferenceEquals(operand, local))
            .ToArray();
        return comparedValues.Length == 1
            && comparedValues[0] is Immediate { Value: var value }
            && (value == 0 || stateValues.Contains(value));
    }

    private static bool IsReplaceableFinalIntegerCarrier(
        TypeAnalysisContext? type,
        TypeAnalysisContext int32Type,
        TypeAnalysisContext booleanType) =>
        type == null
        || GenericCallRebinder.TypesEquivalent(type, int32Type)
        || GenericCallRebinder.TypesEquivalent(type, booleanType)
        || type.FullName == "System.Object";

    internal static bool BindFinalBooleanBranchCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext booleanType)
    {
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var changed = false;

        foreach (var pair in definitions)
        {
            var local = pair.Key;
            if (!IsReplaceableFinalBooleanCarrier(local.Type, booleanType))
                continue;

            var immediateValues = new HashSet<long>();
            var hasAuthoritativeBooleanSource = false;
            var definitionsAreClosed = true;
            var incomingValueCount = 0;
            foreach (var definition in pair.Value)
            {
                IEnumerable<IOperand> incomingValues = definition switch
                {
                    { OpCode: OpCode.Move, Operands.Count: 2 } => definition.Operands.Skip(1),
                    { OpCode: OpCode.ConditionalSelect, Operands.Count: 4 } => definition.Operands.Skip(2),
                    _ => [],
                };
                var materializedValues = incomingValues.ToArray();
                if (materializedValues.Length == 0)
                {
                    definitionsAreClosed = false;
                    break;
                }

                incomingValueCount += materializedValues.Length;
                foreach (var incomingValue in materializedValues)
                {
                    if (incomingValue is Immediate { Value: 0 or 1 } immediate)
                    {
                        immediateValues.Add(immediate.Value);
                        continue;
                    }

                    if (incomingValue is LocalVariable source
                        && !ReferenceEquals(source, local)
                        && IsAuthoritativeBooleanSource(source, definitions, booleanType))
                    {
                        hasAuthoritativeBooleanSource = true;
                        continue;
                    }

                    definitionsAreClosed = false;
                    break;
                }

                if (!definitionsAreClosed)
                    break;
            }

            // 单侧 0/1 边复制只有在存在权威 Boolean 来源时才构成闭合证据；纯字面量仍要求 0、1 两侧齐全。
            if (!definitionsAreClosed
                || incomingValueCount < 2
                || (!hasAuthoritativeBooleanSource && immediateValues.Count != 2))
                continue;

            var uses = instructions
                .Where(instruction => instruction.Operands
                    .Skip(instruction.Destination is LocalVariable ? 1 : 0)
                    .Any(operand => ReferenceEquals(operand, local)))
                .ToArray();
            if (uses.Length == 0
                || uses.Any(use => !IsClosedBooleanBranchUse(use, local)))
                continue;

            local.Type = booleanType;
            changed = true;
        }

        return changed;
    }

    private static bool IsAuthoritativeBooleanSource(
        LocalVariable source,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        TypeAnalysisContext booleanType)
    {
        if (GenericCallRebinder.TypesEquivalent(source.Type, booleanType))
            return true;

        // 比较目标的托管结果恒为 Boolean；其局部类型可能要到 IL 生成时才被最终物化。
        return IsReplaceableFinalBooleanCarrier(source.Type, booleanType)
            && definitions.TryGetValue(source, out var sourceDefinitions)
            && sourceDefinitions.Length > 0
            && sourceDefinitions.All(definition =>
                definition.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned);
    }

    private static bool IsClosedBooleanBranchUse(Instruction instruction, LocalVariable local)
    {
        if (instruction.OpCode == OpCode.ConditionalJump)
            return instruction.Operands.Count == 2
                && ReferenceEquals(instruction.Operands[1], local);

        if (instruction.OpCode == OpCode.ConditionalSelect)
            return instruction.Operands.Count == 4
                && ReferenceEquals(instruction.Operands[1], local);

        return instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned
            && instruction.Operands.Count == 3
            && instruction.Operands.Skip(1).OfType<Immediate>().Any(value => value.Value is 0 or 1);
    }

    internal static bool BindFinalBooleanBitwiseComponentTypes(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext booleanType)
    {
        var adjacency = new Dictionary<LocalVariable, HashSet<LocalVariable>>();
        var bitwiseNodes = new HashSet<LocalVariable>();
        var bitwiseDestinations = new HashSet<LocalVariable>();
        var acceptedBitwise = new HashSet<Instruction>();

        static void Connect(
            IDictionary<LocalVariable, HashSet<LocalVariable>> graph,
            LocalVariable left,
            LocalVariable right)
        {
            if (!graph.TryGetValue(left, out var leftEdges))
                graph[left] = leftEdges = [];
            if (!graph.TryGetValue(right, out var rightEdges))
                graph[right] = rightEdges = [];
            leftEdges.Add(right);
            rightEdges.Add(left);
        }

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor
                && instruction.Operands.Count == 3
                && instruction.Operands[0] is LocalVariable destination
                && instruction.Operands.OfType<Immediate>().All(immediate => immediate.Value is 0 or 1))
            {
                var locals = instruction.Operands.OfType<LocalVariable>().Distinct().ToArray();
                if (locals.Length < 2)
                    continue;
                acceptedBitwise.Add(instruction);
                bitwiseDestinations.Add(destination);
                foreach (var local in locals)
                    bitwiseNodes.Add(local);
                for (var index = 1; index < locals.Length; index++)
                    Connect(adjacency, locals[0], locals[index]);
                continue;
            }

            if (instruction is
                {
                    OpCode: OpCode.Move,
                    Operands: [LocalVariable moveDestination, LocalVariable moveSource],
                })
                Connect(adjacency, moveDestination, moveSource);
        }

        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var visited = new HashSet<LocalVariable>();
        var changed = false;
        foreach (var root in bitwiseNodes)
        {
            if (!visited.Add(root))
                continue;

            var component = new HashSet<LocalVariable> { root };
            var pending = new Queue<LocalVariable>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!adjacency.TryGetValue(current, out var neighbors))
                    continue;
                foreach (var neighbor in neighbors)
                    if (component.Add(neighbor))
                    {
                        visited.Add(neighbor);
                        pending.Enqueue(neighbor);
                    }
            }

            if (!component.Overlaps(bitwiseNodes)
                || component.Any(local => !IsReplaceableFinalBooleanCarrier(local.Type, booleanType))
                || !component.Any(local =>
                    GenericCallRebinder.TypesEquivalent(local.Type, booleanType)
                    && !bitwiseDestinations.Contains(local))
                || component.Any(local =>
                    !GenericCallRebinder.TypesEquivalent(local.Type, booleanType)
                    && !HasOnlyBooleanComponentDefinitions(
                        local,
                        component,
                        definitions,
                        acceptedBitwise)))
                continue;

            foreach (var local in component)
                if (!GenericCallRebinder.TypesEquivalent(local.Type, booleanType))
                {
                    local.Type = booleanType;
                    changed = true;
                }
        }

        return changed;
    }

    private static bool HasOnlyBooleanComponentDefinitions(
        LocalVariable local,
        IReadOnlyCollection<LocalVariable> component,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        IReadOnlyCollection<Instruction> acceptedBitwise)
    {
        if (!definitions.TryGetValue(local, out var localDefinitions) || localDefinitions.Length == 0)
            return false;

        return localDefinitions.All(instruction =>
            acceptedBitwise.Contains(instruction)
            || instruction is { OpCode: OpCode.Move, Operands: [_, Immediate { Value: 0 or 1 }] }
            || instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] }
            && component.Contains(source));
    }

    private static bool IsReplaceableFinalBooleanCarrier(
        TypeAnalysisContext? type,
        TypeAnalysisContext booleanType) =>
        type == null
        || GenericCallRebinder.TypesEquivalent(type, booleanType)
        || type.FullName == "System.Object";

    private static bool IsReplaceableFinalCopyCarrier(
        TypeAnalysisContext? type,
        TypeAnalysisContext consensusType) =>
        type == null
        || GenericCallRebinder.TypesEquivalent(type, consensusType)
        || type.FullName is "System.Object" or "System.Boolean";

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
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor)
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable destination)
            return false;

        var nativeAddressType = NativeAddressArithmeticType(instruction, appContext);
        var exactOperandType = ExactIntegerOperationTypeFromOperands(instruction, appContext);
        var targetType = nativeAddressType ?? instruction.IntegerWidthBits switch
        {
            32 => appContext.SystemTypes.SystemInt32Type,
            64 => appContext.SystemTypes.SystemInt64Type,
            _ => exactOperandType ?? ExactIntegerDestinationType(instruction, destination, appContext),
        };
        if (targetType == null)
            return false;
        var locals = instruction.Operands.OfType<LocalVariable>().Distinct().ToArray();
        if (locals.Any(local => !IsReplaceableFinalIntegerCarrier(local.Type, targetType, appContext)))
            return false;

        var hasExactIntegerEvidence = nativeAddressType != null
            || exactOperandType != null
            || locals.Any(local => GenericCallRebinder.TypesEquivalent(local.Type, targetType));
        var hasNonBooleanMask = instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor
            && instruction.Operands.OfType<Immediate>().Any(immediate => immediate.Value is < 0 or > 1);
        if (!hasExactIntegerEvidence && !hasNonBooleanMask)
            return false;

        var promotesBooleanArithmetic = exactOperandType != null
            && instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
            && !locals.Any(local => GenericCallRebinder.TypesEquivalent(local.Type, targetType))
            && locals.Any(local => GenericCallRebinder.TypesEquivalent(
                local.Type,
                appContext.SystemTypes.SystemBooleanType));
        var changed = false;
        foreach (var local in locals)
        {
            // 中文注释：Boolean 调用结果在 CIL 栈上本来就是 I4，只需把算术结果定型为 Int32；
            // 保留源局部的 Boolean 声明，ILSpy 才能生成零/一投影而不是非法的 bool→int 赋值。
            if (promotesBooleanArithmetic
                && !ReferenceEquals(local, destination)
                && GenericCallRebinder.TypesEquivalent(
                    local.Type,
                    appContext.SystemTypes.SystemBooleanType))
                continue;
            changed |= SetExactType(local, targetType);
        }
        return changed;
    }

    /// <summary>
    /// CFG 重写可能丢失 ARM64 W/X 位宽，但已经精确定型的 Int32/Int64 输入仍能裁决普通整数
    /// 算术的结果类型。Boolean 输入参与加减乘除时按原生零/一 ABI 提升为 Int32；没有精确输入、
    /// 同时出现 Int32/Int64、越界字面量或引用输入时保持开放。移位只由被移位值裁决结果，
    /// Int32 的移位计数不参与结果宽度共识。
    /// </summary>
    private static TypeAnalysisContext? ExactIntegerOperationTypeFromOperands(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.OpCode is not (
                OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned))
            return null;

        var valueOperands = instruction.OpCode is
            OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
            ? instruction.Operands.Skip(1).Take(1).ToArray()
            : instruction.Operands.Skip(1).ToArray();
        var localValues = valueOperands.OfType<LocalVariable>().ToArray();
        if (localValues.Length == 0
            || localValues.Any(local => local.Type?.FullName is not (
                null or "System.Object" or "System.Boolean" or "System.Int32" or "System.Int64")))
            return null;

        var exactTypes = localValues
            .Select(local => local.Type)
            .Where(type => type?.FullName is "System.Int32" or "System.Int64")
            .Cast<TypeAnalysisContext>()
            .GroupBy(type => type.FullName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (exactTypes.Length > 1)
            return null;

        var targetType = exactTypes.Length == 1
            ? exactTypes[0]
            : localValues.Any(local => GenericCallRebinder.TypesEquivalent(
                local.Type,
                appContext.SystemTypes.SystemBooleanType))
                ? appContext.SystemTypes.SystemInt32Type
                : null;
        if (targetType == null)
            return null;

        return valueOperands
            .OfType<Immediate>()
            .All(immediate => IsCompatibleFinalCallScalarLiteral(immediate, targetType))
            ? targetType
            : null;
    }

    /// <summary>
    /// IL2CPP 运行时元数据结构的字段读取仍是原生地址域的一部分。ARM64 X 寄存器上的移位与加减
    /// 必须保持为 nint；若退化成 Int64，后续与类指针合并时仍会生成非法的整数/对象算术。
    /// </summary>
    private static TypeAnalysisContext? NativeAddressArithmeticType(
        Instruction instruction,
        ApplicationAnalysisContext appContext)
    {
        if (instruction.IntegerWidthBits != appContext.Binary.PointerSizeBytes * 8)
            return null;

        return instruction.Operands.Skip(1).Any(IsNativeMetadataMemoryOperand)
            ? appContext.SystemTypes.SystemIntPtrType
            : null;
    }

    /// <summary>
    /// 只接受无索引的 IL2CPP 元数据布局读取；托管对象字段、数组元素和带索引的数据访问不参与此推导。
    /// </summary>
    private static bool IsNativeMetadataMemoryOperand(IOperand operand)
    {
        if (operand is not MemoryOperand
            {
                Base: LocalVariable { Type: { } baseType },
                Index: null,
                Scale: 0,
            })
            return false;

        return baseType is RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext
            or StaticFieldStorageTypeAnalysisContext
            or RgctxTableTypeAnalysisContext
            or PointerTypeAnalysisContext;
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

        if (destination.Type != null
            && !fieldType.IsAssignableTo(destination.Type)
            && !GenericCallRebinder.IsSharedObjectPlaceholder(destination.Type, fieldType))
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
        var semanticallyDefinedLocals = new HashSet<LocalVariable>(instructions
            .Where(instruction => instruction.OpCode != OpCode.Move
                && instruction.Destination is LocalVariable)
            .Select(instruction => (LocalVariable)instruction.Destination!));

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
                changed |= BindResolvedCallReturnType(
                    returnValue,
                    calledMethod.Name is ".ctor" or ".cctor"
                        ? calledMethod.DeclaringType
                        : calledMethod.ReturnType,
                    method.ReturnType);
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
            var paramOffset = CallArgumentTrimmer.FirstParameterOperandIndex(instruction, calledMethod);

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

    /// <summary>
    /// 已解析直接调用的声明返回类型是该调用目标局部量的权威类型。ARM64 的 X0 会同时承担
    /// 当前方法返回槽和中间调用返回槽；入口阶段会先按当前方法签名给最终 Return 局部量定型，
    /// 若同一 SSA 局部随后被证明是一个直接调用的唯一目标，就必须用被调用方法的返回类型纠正。
    /// 其他具体类型证据保持不变，避免跨字段、Phi 或地址载体作无依据覆盖。
    /// </summary>
    internal static bool BindResolvedCallReturnType(
        LocalVariable returnValue,
        TypeAnalysisContext? calledReturnType,
        TypeAnalysisContext? ownerReturnType)
    {
        if (calledReturnType == null
            || GenericCallRebinder.TypesEquivalent(returnValue.Type, calledReturnType))
            return false;

        if (returnValue.Type == null)
        {
            returnValue.Type = calledReturnType;
            return true;
        }

        // 中文注释：只替换由当前方法 Return 预播种出的同型污染；若局部量已有其他具体类型，
        // 说明字段、参数或复制链提供了独立证据，本调用不能覆盖它。
        if (ownerReturnType == null
            || !GenericCallRebinder.TypesEquivalent(returnValue.Type, ownerReturnType))
            return false;

        returnValue.Type = calledReturnType;
        return true;
    }

    /// <summary>
    /// 退 SSA 合并后，ARM64 X0 可能同时表示“引用调用/字段读取结果”和空引用分支上的标量零返回。
    /// 仅当权威引用生产者是局部量唯一生产者、同一局部确实作为后续间接调用接收者、且当前方法
    /// 返回布尔或整数时，把生产结果恢复为声明引用类型，并把对该引用的直接 Return 改写为零。
    /// </summary>
    internal static int ResolveLateVirtualScalarReturnSlotConflicts(
        MethodAnalysisContext method,
        List<string>? diagnostics = null)
    {
        if (method.ControlFlowGraph is null)
        {
            diagnostics?.Add("CONTROL_FLOW_GRAPH_MISSING");
            return 0;
        }
        if (!IsIntegralScalarReturnType(method.ReturnType))
        {
            diagnostics?.Add($"OWNER_RETURN_NOT_INTEGRAL:{method.ReturnType.FullName}");
            return 0;
        }

        var instructions = method.ControlFlowGraph.Instructions;
        var definitionCounts = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.Count());
        var recovered = 0;

        foreach (var producer in instructions.ToArray())
        {
            LocalVariable destination;
            TypeAnalysisContext referenceReturnType;
            string producerKind;
            if (producer.OpCode == OpCode.Call
                && producer.Operands.Count >= 2
                && producer.Operands[0] is MethodAnalysisContext target
                && producer.Operands[1] is LocalVariable callDestination)
            {
                destination = callDestination;
                referenceReturnType = target.ReturnType;
                producerKind = "CALL";
            }
            else if (producer.OpCode == OpCode.Move
                     && producer.Operands.Count >= 2
                     && producer.Operands[0] is LocalVariable fieldDestination
                     && producer.Operands[1] is FieldReference fieldReference)
            {
                destination = fieldDestination;
                referenceReturnType = fieldReference.Field.FieldType;
                producerKind = "FIELD";
            }
            else
            {
                continue;
            }

            if (referenceReturnType.IsValueType
                || referenceReturnType is PointerTypeAnalysisContext
                || referenceReturnType is ByRefTypeAnalysisContext)
            {
                diagnostics?.Add($"PRODUCER_NOT_REFERENCE:{producerKind}:{producer.Index}:{referenceReturnType.FullName}");
                continue;
            }
            if (!definitionCounts.TryGetValue(destination, out var definitionCount)
                || definitionCount != 1)
            {
                diagnostics?.Add($"PRODUCER_DEFINITION_COUNT:{producerKind}:{producer.Index}:{definitionCount}");
                continue;
            }
            if (!GenericCallRebinder.TypesEquivalent(destination.Type, method.ReturnType))
            {
                diagnostics?.Add($"PRODUCER_NOT_OWNER_RETURN:{producerKind}:{producer.Index}:{destination.Type?.FullName}");
                continue;
            }

            var feedsIndirectReceiver = instructions.Any(instruction =>
                instruction.OpCode is OpCode.IndirectCall or OpCode.IndirectJump
                && instruction.Operands.Skip(2).Any(operand => SameLogicalLocal(operand, destination)));
            if (!feedsIndirectReceiver)
            {
                diagnostics?.Add($"INDIRECT_RECEIVER_MISSING:{producerKind}:{producer.Index}");
                continue;
            }

            var scalarReturns = instructions
                .Where(instruction => instruction.OpCode == OpCode.Return
                                      && instruction.Operands.Count == 1
                                      && SameLogicalLocal(instruction.Operands[0], destination))
                .ToArray();
            if (scalarReturns.Length == 0)
            {
                diagnostics?.Add($"SCALAR_RETURN_MISSING:{producerKind}:{producer.Index}");
                continue;
            }

            // 中文注释：退 SSA 复制合并可能保留多个对象实例表示同一个名称/寄存器局部；
            // 调用定义、间接接收者和字段基址必须同步定型，不能只修改其中一个对象引用。
            foreach (var logicalAlias in instructions
                         .SelectMany(instruction => instruction.Operands)
                         .OfType<LocalVariable>()
                         .Where(local => SameLogicalLocal(local, destination)))
                logicalAlias.Type = referenceReturnType;

            // 中文注释：标量返回类型曾把对象头零偏移读取误解析成 Boolean/Int32.m_value。
            // 在引用生产者、虚调用接收者和零返回三项证据已同时成立后，恢复原始对象头读取，
            // 并把其目标精确定型为该引用的运行时类指针，供晚期虚表槽解析直接消费。
            var restoredObjectHeaders = 0;
            foreach (var headerLoad in instructions.Where(instruction =>
                         instruction.OpCode == OpCode.Move
                         && instruction.Operands.Count >= 2
                         && instruction.Operands[0] is LocalVariable
                         && instruction.Operands[1] is FieldReference))
            {
                var klass = (LocalVariable)headerLoad.Operands[0];
                var pollutedField = (FieldReference)headerLoad.Operands[1];
                if (pollutedField.Offset != 0
                    || !SameLogicalLocal(pollutedField.Local, destination)
                    || !GenericCallRebinder.TypesEquivalent(pollutedField.Field.FieldType, method.ReturnType))
                    continue;

                headerLoad.SetOperand(1, new MemoryOperand(pollutedField.Local));
                klass.Type = new RuntimeClassTypeAnalysisContext(
                    referenceReturnType,
                    referenceReturnType.DeclaringAssembly);
                restoredObjectHeaders++;
            }
            foreach (var scalarReturn in scalarReturns)
                scalarReturn.SetOperand(0, new Immediate(0));
            diagnostics?.Add(
                $"RECOVERED:{producerKind}:{producer.Index}:{referenceReturnType.FullName}:HEADERS={restoredObjectHeaders}");
            recovered++;
        }

        return recovered;
    }

    /// <summary>比较退 SSA 后由复制合并保留下来的同名、同寄存器逻辑局部。</summary>
    private static bool SameLogicalLocal(IOperand operand, LocalVariable expected)
        => operand is LocalVariable local
           && (ReferenceEquals(local, expected)
               || local.Name == expected.Name && local.Register == expected.Register);

    /// <summary>零立即数能无损表示的托管布尔与整数返回类型。</summary>
    private static bool IsIntegralScalarReturnType(TypeAnalysisContext? type)
        => type?.FullName is "System.Boolean"
            or "System.SByte" or "System.Byte"
            or "System.Int16" or "System.UInt16"
            or "System.Int32" or "System.UInt32"
            or "System.Int64" or "System.UInt64"
            or "System.Char";

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

        // 中文注释：System.Object 是共享泛型调用约定的最弱载体，不能覆盖字段、Phi 或方法
        // 签名已经证明的开放泛型类型。否则下一轮字段解析会恢复 K/T/结构化泛型，调用传播
        // 又写回 Object，形成非单调振荡。String、具体集合等真正封闭的强类型参数仍可晋级。
        if (parameterType.FullName == "System.Object"
            && ContainsUninstantiatedGenericParameter(local.Type))
            return false;

        // 中文注释：List<object> 等共享泛型调用形参只是 ABI 宽占位；字段或 Phi 已证明
        // List<WeightedEntry<T>> 等结构化开放类型时，禁止把强类型降回 object，否则字段传播
        // 与调用传播会在同一不动点中来回覆盖而永不收敛。
        if (GenericCallRebinder.IsSharedObjectPlaceholder(parameterType, local.Type))
            return false;

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

        // 中文注释：共享泛型实例调用会把 List<T> 的接收者按 List<object> ABI 目标解析。
        // 字段或 Phi 已证明 List<WeightedEntry<T>> 等结构化开放类型后，该目标只代表调用约定，
        // 不得把接收者降级回 object，否则会与字段类型传播在不动点迭代中反复振荡。
        if (GenericCallRebinder.IsSharedObjectPlaceholder(declaringType, receiver.Type))
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
