using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

public static class MetadataResolver
{
    public static void ResolveAll(MethodAnalysisContext method)
    {
        ResolveCalls(method);
        ResolveGetter(method);
        ResolveMetadataUsages(method);
    }

    /// <summary>
    /// Resolves <c>Move local, [absoluteAddress]</c> loads of IL2CPP metadata-usage globals into a
    /// strongly-typed operand: a string literal, a <see cref="TypeAnalysisContext"/> (an Il2CppType*/
    /// Il2CppClass* usage) or, for a MethodInfo* usage, a <see cref="RuntimeMethodInfoAnalysisContext"/>
    /// naming the method it refers to (also used to type the local - see <see cref="LocalVariables"/>).
    /// </summary>
    private static void ResolveMetadataUsages(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;
        var definitions = BuildUniqueDefinitions(method.ControlFlowGraph!.Instructions);

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move)
                continue;

            if (instruction.Operands[0] is not LocalVariable
                || instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0 } memory)
                continue;

            MetadataUsage? tableUsage = null;
            if (memory.Base is LocalVariable tableBase
                && definitions.TryGetValue(tableBase, out var tableDefinition)
                && tableDefinition.Operands[1] is MemoryOperand { Base: null, Index: null, Scale: 0 } tableGlobal
                && tableGlobal.Addend >= 0)
            {
                tableUsage = libContext.CheckForPost27GlobalTableEntryAt((ulong)tableGlobal.Addend, memory.Addend);
            }

            if (tableUsage != null)
            {
                if (ResolveMetadataUsageOperand(method, tableUsage) is { } resolvedTableOperand)
                    instruction.SetOperand(1, resolvedTableOperand);

                continue;
            }

            // 旧布局和部分新布局仍会直接从绝对地址读取元数据使用值。
            if (memory.Base != null || memory.Addend < 0)
                continue;

            var address = (ulong)memory.Addend;
            // 绝对槽在旧布局中直接保存编码值；post-27二级布局的第一层保存的是
            // 编码项地址，必须留给后续[tableBase+offset]或Phi专用规则执行第二次读取。
            var absoluteUsage = libContext.GetAnyGlobalByAddress(address);
            if (absoluteUsage != null
                && ResolveMetadataUsageOperand(method, absoluteUsage) is { } resolvedAbsoluteOperand)
                instruction.SetOperand(1, resolvedAbsoluteOperand);
        }

        var stringPhiDetails = new List<string>();
        var stringPhiRecoveryCount = ResolvePhiBackedStringLoads(
            method.ControlFlowGraph.Instructions,
            stringPhiDetails,
            address =>
            {
                var usage = libContext.CheckForPost27GlobalTableEntryAt(address, 0);
                return usage?.Type == MetadataUsageType.StringLiteral
                    ? new StringLiteral(usage.AsLiteral())
                    : null;
            });
        foreach (var detail in stringPhiDetails)
            Logger.VerboseNewline(
                $"字符串元数据Phi：method={method.Name}，recovered={stringPhiRecoveryCount}，{detail}",
                "MetadataResolver");
    }

    /// <summary>
    /// 统一解析绝对元数据槽：先识别槽内直接编码值，再识别“槽内保存编码项地址”的
    /// post-27 二级布局。二级布局必须使用零字节偏移，避免把任意地址误判为元数据表。
    /// </summary>
    internal static TUsage? ResolveAbsoluteSlotUsage<TUsage>(
        ulong address,
        Func<ulong, TUsage?> directResolver,
        Func<ulong, long, TUsage?> tableEntryResolver)
        where TUsage : class
        => directResolver(address) ?? tableEntryResolver(address, 0);

    /// <summary>
    /// 初始化保护区裁除和首次类型传播完成后，恢复“绝对槽保存编码项地址，强类型字符串局部
    /// 再从该地址读取”的post-27二层布局。第一层地址载体保持原样，只有唯一Move定义、
    /// 无索引内存读取、System.String目标和StringLiteral元数据四项证据同时成立时才改写。
    /// </summary>
    public static int ResolveTypedPost27StringLoads(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;
        var changed = ResolveTypedPost27StringLoads(
            method.ControlFlowGraph!.Instructions,
            method.AppContext.SystemTypes.SystemStringType,
            (address, offset) =>
            {
                var usage = libContext.CheckForPost27GlobalTableEntryAt(address, offset);
                return usage?.Type == MetadataUsageType.StringLiteral
                    ? new StringLiteral(usage.AsLiteral())
                    : null;
            });

        if (changed > 0)
            Logger.VerboseNewline(
                $"字符串元数据二层槽：method={method.Name}，recovered={changed}",
                "MetadataResolver");

        return changed;
    }

    /// <summary>
    /// 对已完成类型传播的指令执行可测试的二层字符串槽恢复。相同地址与偏移只解析一次，
    /// 避免多个返回分支共享默认字符串槽时重复读取二进制和元数据。
    /// </summary>
    internal static int ResolveTypedPost27StringLoads(
        IReadOnlyList<Instruction> instructions,
        TypeAnalysisContext stringType,
        Func<ulong, long, StringLiteral?> post27StringEntryResolver)
    {
        var definitions = BuildUniqueDefinitions(instructions);
        var resolvedEntries = new Dictionary<(ulong Address, long Offset), StringLiteral?>();
        var changed = 0;

        foreach (var load in instructions)
        {
            if (load is not
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable destination,
                        MemoryOperand
                        {
                            Base: LocalVariable tableBase,
                            Index: null,
                            Scale: 0,
                            Addend: >= 0
                        } entryMemory
                    ]
                }
                || destination.Type != stringType
                || ResolveAbsoluteSlotAddress(tableBase, definitions, []) is not { } tableGlobalAddress)
                continue;

            var key = (tableGlobalAddress, entryMemory.Addend);
            if (!resolvedEntries.TryGetValue(key, out var literal))
            {
                literal = post27StringEntryResolver(key.Item1, key.Item2);
                resolvedEntries[key] = literal;
            }

            if (literal == null)
                continue;

            load.SetOperand(1, literal);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// 沿SSA单一定义链解析绝对槽地址。Move复制继续追踪；Phi只有在全部输入都能证明为
    /// 同一绝对地址时才收敛，异址、缺失定义和循环链均保持未解析。
    /// </summary>
    private static ulong? ResolveAbsoluteSlotAddress(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited)
    {
        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition))
            return null;

        if (definition is
            {
                OpCode: OpCode.Move,
                Operands:
                [
                    LocalVariable,
                    MemoryOperand
                    {
                        Base: null,
                        Index: null,
                        Scale: 0,
                        Addend: >= 0
                    } absoluteSlot
                ]
            })
            return (ulong)absoluteSlot.Addend;

        if (definition is { OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable source] })
            return ResolveAbsoluteSlotAddress(source, definitions, visited);

        if (definition.OpCode != OpCode.Phi || definition.Operands.Count < 2)
            return null;

        ulong? resolvedAddress = null;
        for (var index = 1; index < definition.Operands.Count; index++)
        {
            var inputAddress = ResolveAbsoluteSlotAddress(
                definition.Operands[index],
                definitions,
                new HashSet<LocalVariable>(visited));
            if (inputAddress == null || resolvedAddress is { } existing && existing != inputAddress.Value)
                return null;

            resolvedAddress = inputAddress;
        }

        return resolvedAddress;
    }

    /// <summary>
    /// 把“多个字符串元数据槽地址经Phi汇合，再统一解引用”的原生形态恢复为字符串值Phi。
    /// MetadataUsage解析已经把每条输入边的绝对槽加载证明为StringLiteral；此时继续保留
    /// 公共<c>[phi]</c>会把托管字符串再次当作地址读取，并让退SSA后的定义链丢失。
    /// 只有全部输入都沿唯一Move链解析为字符串时才提交重写，任何混合输入均保持原样。
    /// </summary>
    internal static int ResolvePhiBackedStringLoads(
        IReadOnlyList<Instruction> instructions,
        List<string>? details = null,
        Func<ulong, StringLiteral?>? post27StringSlotResolver = null)
    {
        var definitions = BuildUniqueDefinitions(instructions);
        var changed = 0;

        foreach (var load in instructions)
        {
            if (load is
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable { IsReturn: true },
                        MemoryOperand
                        {
                            Base: LocalVariable returnBase,
                            Index: null,
                            Scale: 0,
                            Addend: 0
                        }
                    ]
                })
            {
                details?.Add(
                    $"返回读取基址={returnBase}，定义="
                    + (definitions.TryGetValue(returnBase, out var returnBaseDefinition)
                        ? returnBaseDefinition.ToString()
                        : "<非唯一或缺失>"));
            }

            if (load is not
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable,
                        MemoryOperand
                        {
                            Base: LocalVariable phiValue,
                            Index: null,
                            Scale: 0,
                            Addend: 0
                        }
                    ]
                }
                || !definitions.TryGetValue(phiValue, out var phi)
                || phi.OpCode != OpCode.Phi
                || phi.Operands.Count < 3)
                continue;

            var resolvedInputs = new List<StringLiteral>(phi.Operands.Count - 1);
            var allInputsResolved = true;
            string? firstUnresolved = null;
            for (var index = 1; index < phi.Operands.Count; index++)
            {
                if (ResolveStringLiteral(
                        phi.Operands[index],
                        definitions,
                        [],
                        post27StringSlotResolver) is not { } literal)
                {
                    allInputsResolved = false;
                    var unresolved = phi.Operands[index];
                    firstUnresolved = unresolved is LocalVariable local
                        && definitions.TryGetValue(local, out var unresolvedDefinition)
                            ? $"{local} <- {unresolvedDefinition}"
                            : unresolved.ToString();
                    break;
                }

                resolvedInputs.Add(literal);
            }

            details?.Add(
                $"候选Phi={phiValue}，输入={phi.Operands.Count - 1}，"
                + $"已解析={resolvedInputs.Count}，首个未解析={firstUnresolved ?? "<无>"}");
            if (!allInputsResolved)
                continue;

            for (var index = 0; index < resolvedInputs.Count; index++)
                phi.SetOperand(index + 1, resolvedInputs[index]);

            // Phi现在直接保存托管字符串值，公共读取退化为普通复制；后续类型传播会从
            // 方法返回值反向绑定Phi，退SSA则在每条原始命中边写入对应字符串常量。
            load.SetOperand(1, phiValue);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// 建立单一定义索引；同一局部出现多个定义时排除该局部，避免跨控制流边猜测来源。
    /// </summary>
    private static Dictionary<LocalVariable, Instruction> BuildUniqueDefinitions(
        IReadOnlyList<Instruction> instructions)
        => instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

    private static StringLiteral? ResolveStringLiteral(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited,
        Func<ulong, StringLiteral?>? post27StringSlotResolver)
    {
        if (operand is StringLiteral literal)
            return literal;

        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition)
            || definition is not { OpCode: OpCode.Move, Operands.Count: >= 2 })
            return null;

        if (definition.Operands[1] is MemoryOperand
            {
                Base: null,
                Index: null,
                Scale: 0,
                Addend: >= 0
            } absoluteSlot
            && post27StringSlotResolver != null)
            return post27StringSlotResolver((ulong)absoluteSlot.Addend);

        return ResolveStringLiteral(
            definition.Operands[1],
            definitions,
            visited,
            post27StringSlotResolver);
    }

    /// <summary>
    /// 将已验证的元数据使用项转换为 ISIL 强类型操作数。
    /// 字段元数据需要独立的字段句柄模型，当前保持原始内存读取，避免错误改写。
    /// </summary>
    private static IOperand? ResolveMetadataUsageOperand(MethodAnalysisContext method, MetadataUsage usage)
    {
        switch (usage.Type)
        {
            case MetadataUsageType.StringLiteral:
                return new StringLiteral(usage.AsLiteral());
            case MetadataUsageType.Type:
            case MetadataUsageType.TypeInfo:
                return method.DeclaringType?.AppContext.ResolveIl2CppType(usage.AsType());
            case MetadataUsageType.MethodDef:
            case MetadataUsageType.MethodRef:
                if (method.AppContext.ResolveContextForMethod(usage) is { DeclaringType: { } declaringType } methodContext)
                    return new RuntimeMethodInfoAnalysisContext(methodContext, declaringType.DeclaringAssembly);

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Replaces every <c>[base + addend]</c> memory operand whose base is a typed local with a
    /// <see cref="FieldReference"/> to the field at that offset. Returns whether any operand was
    /// resolved this pass, so the type/field fixpoint can detect convergence: as more bases become
    /// typed (a field load types its result, which is the base of the next load), more offsets
    /// resolve, so this is re-run until it stops finding new fields.
    /// </summary>
    public static bool ResolveFieldOffsets(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is not MemoryOperand memory)
                    continue;

                // Has to be [base (local) + addend (field offset)]
                if (memory.Index != null || memory.Scale != 0)
                    continue;

                if (memory.Base is not LocalVariable local || local?.Type == null)
                    continue;

                // check if static field access
                var staticOwner = (local.Type as StaticFieldStorageTypeAnalysisContext)?.OwnerType;
                var owner = staticOwner ?? local.Type;
                // 泛型声明的字段元数据偏移均可能为零；必须先以声明自身的 T 参数构造开放布局实例，
                // 否则委托字段无法获得 Func<T>/Comparison<T> 等精确类型，后续 BR 尾调用也无法绑定 Invoke。
                var genericOwner = GenericInstanceFieldLayout.CreateLayoutOwner(owner);

                FieldAnalysisContext? field;
                if (genericOwner != null && staticOwner == null)
                {
                    // 泛型定义的字段元数据偏移不可信，统一按具体或开放实例重新计算布局。
                    field = GenericInstanceFieldLayout.FindFieldAtOffset(genericOwner, memory.Addend);
                }
                else
                {
                    field = FindUniqueRuntimeFieldAtOffset(
                        genericOwner?.GenericType ?? owner,
                        staticOwner != null,
                        memory.Addend);
                }

                if (field == null) // TODO: Support nested fields (Field1.Field2.Field3)
                    continue;

                // make sure we have a full GIT for field access. open type is bad.
                if (genericOwner != null
                    && field is not ConcreteGenericFieldAnalysisContext)
                    field = new ConcreteGenericFieldAnalysisContext(field, genericOwner);

                instruction.SetOperand(i, new FieldReference(field, local, (int)memory.Addend));
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 沿声明类型及其基类查找给定偏移上的唯一运行时字段。
    /// 常量字段只存在于元数据，不占用实例或静态存储；同一声明类型仍有多个候选时保持未解析，避免猜测字段身份。
    /// </summary>
    private static FieldAnalysisContext? FindUniqueRuntimeFieldAtOffset(
        TypeAnalysisContext owner,
        bool isStatic,
        long offset)
    {
        for (var candidateOwner = owner; candidateOwner != null; candidateOwner = candidateOwner.BaseType)
        {
            var candidates = candidateOwner.Fields
                .Where(field =>
                    field.IsStatic == isStatic
                    && field.Offset == offset
                    && (field.Attributes & System.Reflection.FieldAttributes.Literal) == 0)
                .Take(2)
                .ToArray();

            if (candidates.Length == 1)
                return candidates[0];

            if (candidates.Length > 1)
                return null;
        }

        return null;
    }

    private static void ResolveCalls(MethodAnalysisContext method)
    {
        var resolvedThrow = false;
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            if (block.BlockType != BlockType.Call && block.BlockType != BlockType.TailCall)
                continue;

            var callInstruction = block.Instructions[^1];
            if (callInstruction.Operands[0] is not Immediate dest)
                continue;

            var target = dest.UnsignedValue;

            var keyFunctionAddresses = method.AppContext.GetOrCreateKeyFunctionAddresses();

            if (keyFunctionAddresses.IsKeyFunctionAddress(target))
            {
                HandleKeyFunction(method.AppContext, callInstruction, target, keyFunctionAddresses);
                continue;
            }

            //Non-key function call. Try to find a single match
            if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var targetMethods))
            {
                // Not a managed method at all. It may be one of the runtime helpers that exist purely to
                // throw, in which case restore the throw itself
                if (ThrowHelperRecovery.GetThrownException(method.AppContext, target) is { } thrown)
                {
                    callInstruction.OpCode = OpCode.Throw;
                    callInstruction.SetOperands(thrown);
                    method.ControlFlowGraph.TerminateAtThrow(block);
                    resolvedThrow = true;
                }

                continue;
            }

            // Duplicated/Shared method bodies are resolved later in ResolveCallsViaMethodInfo/ResolveAmbiguousCalls.
            if (targetMethods is not [{ } singleTargetMethod])
                continue;

            // IL2CPP 的接口慢查表助手可能与 List<T>.AddWithResize 共用同一原生地址。
            // 当原始 X0 返回值已经进入 VirtualInvokeData 的双路 Phi 时，先绑定 void 方法会删除
            // 这个真实返回槽，令后续接口分派只能看到快速路径。此处只延迟具有完整消费者形状的
            // 共享地址调用；接口、槽位和 vtable 证据仍由 InterfaceDispatchRecovery 统一验收。
            TryBindCallTarget(method, callInstruction, singleTargetMethod);
        }

        if (resolvedThrow)
            method.ControlFlowGraph.RemoveUnreachableBlocks();
        method.ControlFlowGraph.MergeCallBlocks();
    }

    /// <summary>
    /// 把已解析的方法身份绑定到调用，并同步修正返回槽形状。
    /// 原生提升阶段在目标未知时会按有返回值的Call保留X0；若目标随后解析为void，
    /// 该X0只是伪返回槽，必须删除并改成CallVoid，否则IL生成会在call void后写入局部变量。
    /// </summary>
    internal static void BindCallTarget(Instruction call, MethodAnalysisContext target)
    {
        if (!call.IsCall)
            throw new InvalidOperationException($"目标只能绑定到调用指令：{call.OpCode}");

        call.SetOperand(0, target);

        // 原始寄存器布局仍以Call的返回槽为基准，必须先完成参数重排，再删除伪返回槽。
        CallingConventionResolver.RemapRawArguments(call, target);
        if (call.OpCode != OpCode.Call || !target.IsVoid)
            return;

        var operands = call.Operands.ToList();
        if (operands.Count > 1)
            operands.RemoveAt(1);

        call.OpCode = OpCode.CallVoid;
        call.SetOperands(operands);
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by matching the receiver's known
    /// type against the candidates' declaring types. Runs inside the type/field fixpoint and so
    /// re-fires as receivers become typed - a resolved call types its return value, which can type
    /// the receiver of a further call. Returns whether any call was resolved this pass.
    ///
    /// Conservative by design: it commits only when exactly one non-static candidate's declaring
    /// type matches the receiver's type. Anything still untyped or ambiguous is left for a later
    /// pass, or left unresolved - it never guesses.
    /// </summary>
    public static bool ResolveAmbiguousCalls(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            // A resolved call's target is a method/key-function name; only unresolved ones are still numeric.
            if (instruction.Operands[0] is not Immediate target)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates) || candidates.Count < 2)
                continue;

            // e.g. string.Equals and string.op_Equality, identical params, instance type, and bodies are shared
            // we can't differentiate which is being called but it doesn't matter
            if (AreInterchangeable(candidates))
            {
                var preferred = PreferredOf(candidates);
                changed |= TryBindCallTarget(method, instruction, preferred);
                continue;
            }

            if (GetReceiver(instruction) is not { Type: { } receiverType } receiver)
                continue;

            // Prefer picking base ctor if we are a ctor
            var callerIsCtor = method.Name == ".ctor" && receiver.IsThis;

            // Handle methods with shared bodies
            var match = default(MethodAnalysisContext);

            for (var type = receiverType; type != null && match == null; type = type.BaseType)
            {
                var matches = candidates.Where(c => !c.IsStatic && ReferenceEquals(c.DeclaringType, type)).ToList();

                if (matches.Count > 1 && callerIsCtor)
                    matches = matches.Where(c => c.Name == ".ctor").ToList();

                if (matches.Count > 1)
                    break;

                match = matches.SingleOrDefault();
            }

            if (match == null)
                continue;

            changed |= TryBindCallTarget(method, instruction, match);
        }

        return changed;
    }

    private static bool AreInterchangeable(List<MethodAnalysisContext> candidates)
    {
        var first = candidates[0];

        return candidates.All(c => c.IsStatic == first.IsStatic
            && ReferenceEquals(c.DeclaringType, first.DeclaringType)
            && ReferenceEquals(c.ReturnType, first.ReturnType)
            && c.Parameters.Count == first.Parameters.Count
            && SameParameterTypes(c, first));
    }

    private static bool SameParameterTypes(MethodAnalysisContext a, MethodAnalysisContext b)
    {
        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!ReferenceEquals(a.Parameters[i].ParameterType, b.Parameters[i].ParameterType))
                return false;
        }

        return true;
    }

    // Prefer operators if possible
    private static MethodAnalysisContext PreferredOf(List<MethodAnalysisContext> candidates) =>
        candidates.FirstOrDefault(c => c.Name.StartsWith("op_")) ?? candidates[0];

    // The receiver ('this') of a call is the first integer-slot argument: operand 1 for CallVoid
    // (after the target), operand 2 for Call (after the target and the return value).
    private static LocalVariable? GetReceiver(Instruction call)
    {
        var index = call.OpCode == OpCode.CallVoid ? 1 : 2;
        return index < call.Operands.Count ? call.Operands[index] as LocalVariable : null;
    }

    /// <summary>
    /// Resolves any Call (theoretically should always be a CallVoid) target directly after a Newobj to a constructor call.
    /// </summary>
    public static bool ResolveConstructorCalls(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable definition)
                definitions[definition] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands[0] is not Immediate callTarget)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(callTarget.UnsignedValue, out var candidates))
                continue;

            if (GetReceiver(instruction) is not { } receiver || AllocatedType(receiver, definitions) is not { } allocatedType)
                continue;

            var constructor = candidates.FirstOrDefault(c => !c.IsStatic && c.Name == ".ctor" && ReferenceEquals(c.DeclaringType, allocatedType));
            if (constructor == null)
                continue;

            BindCallTarget(instruction, constructor);
            changed = true;
        }

        return changed;
    }

    // Follow SSA copies from a local back to the Newobj that produced the value
    private static TypeAnalysisContext? AllocatedType(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();

        while (visited.Add(local) && definitions.TryGetValue(local, out var definition))
        {
            switch (definition.OpCode)
            {
                case OpCode.Newobj:
                    return (definition.Operands[0] as LocalVariable)?.Type;
                case OpCode.Move when definition.Operands[1] is LocalVariable source:
                    local = source;
                    continue;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by reading the runtime
    /// <c>MethodInfo*</c> the caller passes in, if there is one.
    /// </summary>
    public static bool ResolveCallsViaMethodInfo(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is not Immediate target)
                //Already resolved
                continue;

            if (GetMethodInfoArgument(instruction) is not { RepresentedMethod: { } representedMethod })
                //No MethodInfo to work with
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates))
            {
                // Some shared generic bodies aren't in the address map at all (todo investigate?).
                // Il2cpp still passes the concrete MethodInfo as the hidden final parameter, so we can use a methodof there if we have one.
                var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
                var hiddenParamIndex = firstArg
                    + (CallingConventionResolver.ReturnsViaHiddenBuffer(representedMethod) ? 1 : 0)
                    + (representedMethod.IsStatic ? 0 : 1) + representedMethod.Parameters.Count;

                if (hiddenParamIndex >= instruction.Operands.Count
                    || AsMethodInfo(instruction.Operands[hiddenParamIndex]) == null)
                    continue;

                changed |= TryBindCallTarget(method, instruction, representedMethod);
                continue;
            }

            if (candidates.Count < 2)
                continue;

            //Try to actually match on the method name so we don't just replace a call with something else.
            var representedBase = BaseMethodOf(representedMethod);
            if (!candidates.Any(candidate => ReferenceEquals(BaseMethodOf(candidate), representedBase)))
                continue;

            changed |= TryBindCallTarget(method, instruction, representedMethod);
        }

        return changed;
    }

    /// <summary>
    /// 所有托管调用身份绑定共用同一共享地址保护门，防止直接地址、接收者推导和 MethodInfo
    /// 三条解析路径出现不同语义。返回值精确表示本次是否完成绑定，供不动点统计使用。
    /// </summary>
    private static bool TryBindCallTarget(
        MethodAnalysisContext method,
        Instruction instruction,
        MethodAnalysisContext target)
    {
        if (InterfaceDispatchRecovery.ShouldDeferSharedAddWithResizeBinding(
                instruction,
                target,
                method.ControlFlowGraph!.Instructions))
            return false;

        BindCallTarget(instruction, target);
        return true;
    }

    // Offset of Il2CppClass::vtable, VirtualInvokeData entries of {methodPtr, MethodInfo*}.
    // TODO this is almost certainly not correct on every version
    private const long VTableOffset64 = 0x138;
    private const long VTableOffset32 = 0xC0;
    
    // 在类局部量的实际类型已知时，通过
    // <c>[klass + vtableOffset + slot * sizeof(VirtualInvokeData)]</c> 恢复普通虚调用与尾虚调用。
    public static bool ResolveVirtualCalls(MethodAnalysisContext method)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var vtableOffset = pointerSize == 8 ? VTableOffset64 : VTableOffset32;
        var invokeDataSize = 2L * pointerSize;
        var changed = false;

        var loads = new Dictionary<LocalVariable, MemoryOperand>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is MemoryOperand { Index: null, Scale: 0 } load)
                loads[destination] = load;
        }

        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            // 尾调用重写会在块末追加Return，使用快照避免枚举期间修改集合。
            foreach (var instruction in block.Instructions.ToArray())
            {
                if (instruction.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump))
                    continue;

                if (SlotLoad(instruction.Operands[0]) is not { } target
                    || target.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } receiverType } } klassLocal)
                    continue;

                var offset = target.Addend - vtableOffset;
                if (offset < 0 || offset % invokeDataSize != 0)
                    continue;

                var slot = (int)(offset / invokeDataSize);
                if (ResolveVTableSlot(method.AppContext, receiverType, slot) is not { } resolved)
                    continue;

                var assembly = resolved.DeclaringType?.DeclaringAssembly ?? method.DeclaringType?.DeclaringAssembly;

                // MethodInfo字段与目标方法属于同一VirtualInvokeData；先替换原始寄存器快照，
                // 再交给统一间接转移重写器裁剪参数并恢复尾返回。
                for (var i = 1; i < instruction.Operands.Count && assembly != null; i++)
                {
                    if (SlotLoad(instruction.Operands[i]) is { } methodInfoLoad
                        && ReferenceEquals(methodInfoLoad.Base, klassLocal)
                        && methodInfoLoad.Addend == target.Addend + pointerSize)
                        instruction.SetOperand(i, new RuntimeMethodInfoAnalysisContext(resolved, assembly));
                }

                IndirectTransferCallRewriter.Rewrite(method, instruction, block, resolved);
                changed = true;
            }
        }

        return changed;

        MemoryOperand? SlotLoad(IOperand operand) => operand switch
        {
            MemoryOperand { Index: null, Scale: 0 } inlined => inlined,
            LocalVariable local when loads.TryGetValue(local, out var load) => load,
            _ => null
        };
    }

    /// <summary>
    /// 方法RGCTXData的METHOD项已经携带精确托管方法身份。IL2CPP共享泛型代码常从该项的
    /// MethodInfo中读取虚调用入口并以BR尾调；目标地址本身无需再次猜测。
    /// </summary>
    public static bool ResolveMethodRgctxCalls(MethodAnalysisContext method)
    {
        var changed = false;
        var definitions = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);

        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            foreach (var instruction in block.Instructions.ToArray())
            {
                if (instruction.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump)
                    || ResolveTargetMethodInfo(instruction.Operands[0], definitions) is not
                    {
                        RepresentedMethod: { } resolved
                    })
                    continue;

                IndirectTransferCallRewriter.Rewrite(method, instruction, block, resolved);
                changed = true;
            }
        }

        return changed;
    }

    private static RuntimeMethodInfoAnalysisContext? ResolveTargetMethodInfo(
        IOperand operand,
        ILookup<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (true)
        {
            switch (operand)
            {
                case MemoryOperand
                {
                    Base: LocalVariable methodInfoLocal,
                    Index: null,
                    Scale: 0,
                    Addend: 0
                }:
                    return ResolveTargetMethodInfo(methodInfoLocal, definitions);
                case LocalVariable { Type: RuntimeMethodInfoAnalysisContext typed }:
                    return typed;
                case LocalVariable local when visited.Add(local)
                                                  && definitions[local].Take(2).ToArray() is [var definition]
                                                  && definition.OpCode == OpCode.Move
                                                  && definition.Operands.Count >= 2:
                    operand = definition.Operands[1];
                    continue;
                default:
                    return null;
            }
        }
    }

    internal static MethodAnalysisContext? ResolveVTableSlot(ApplicationAnalysisContext appContext, TypeAnalysisContext type, int slot)
    {
        // 未约束泛型参数的共享代码仍通过实际运行时类型的对象虚表分派；槽身份来自 System.Object，
        // CIL 生成阶段再使用 constrained. T 保留引用类型和值类型各自的重写语义。
        var lookupType = type is GenericParameterTypeAnalysisContext
            ? appContext.SystemTypes.SystemObjectType
            : type;
        var definition = (lookupType as GenericInstanceTypeAnalysisContext)?.GenericType.Definition ?? lookupType.Definition;

        if (definition == null || slot < 0 || slot >= definition.VtableCount)
            return null;

        // 接口槽在原生虚表中指向类实现，但托管CIL必须引用已经按接收者具体化的接口声明。
        // 直接引用显式实现会把方法名中的TKey/TValue原样泄漏到反编译源码。
        foreach (var interfaceOffset in definition.InterfaceOffsets)
        {
            if (slot < interfaceOffset.offset)
                continue;

            var declaringInterface = appContext.ResolveIl2CppType(interfaceOffset.Type);
            if (lookupType is GenericInstanceTypeAnalysisContext receiver)
                declaringInterface = GenericInstantiation.Instantiate(
                    declaringInterface,
                    receiver.GenericArguments,
                    []);

            var interfaceSlot = slot - interfaceOffset.offset;
            var interfaceMethod = declaringInterface is GenericInstanceTypeAnalysisContext genericInterface
                ? genericInterface.GenericType.Methods.FirstOrDefault(method => method.Definition?.slot == interfaceSlot)
                : declaringInterface.Methods.FirstOrDefault(method => method.Definition?.slot == interfaceSlot);
            if (interfaceMethod != null)
                return SpecializeVTableMethodForReceiver(declaringInterface, interfaceMethod);
        }

        if (appContext.ResolveContextForMethod(definition.VTable[slot]) is { } implementation)
            return SpecializeVTableMethodForReceiver(lookupType, implementation);

        // an abstract method has no implementation, try to resolve it
        for (var declarer = lookupType; declarer != null; declarer = declarer.BaseType)
        {
            // 泛型实例包装器自身没有方法列表；声明仍位于开放 GenericType 上，
            // 找到后再按实际接收者具体化返回值与参数。
            var methods = declarer is GenericInstanceTypeAnalysisContext genericDeclarer
                ? genericDeclarer.GenericType.Methods
                : declarer.Methods;
            if (methods.FirstOrDefault(m => m.Definition?.slot == slot) is { } declaration)
                return SpecializeVTableMethodForReceiver(lookupType, declaration);
        }

        return null;
    }

    /// <summary>
    /// 虚表保存的是开放泛型方法定义；接收者已经是封闭泛型实例时，必须同步具体化声明类型、参数与返回值。
    /// 否则CIL会泄漏TKey/TValue等开放占位符，反编译源码也会产生无法编译的!0。
    /// </summary>
    internal static MethodAnalysisContext SpecializeVTableMethodForReceiver(
        TypeAnalysisContext receiverType,
        MethodAnalysisContext method)
    {
        if (method is ConcreteGenericMethodAnalysisContext
            || receiverType is not GenericInstanceTypeAnalysisContext receiver
            || method.DeclaringType == null
            || !SameTypeDefinition(receiver.GenericType, method.DeclaringType)
            || method.DeclaringType.GenericParameters.Count != receiver.GenericArguments.Count)
            return method;

        return new ConcreteGenericMethodAnalysisContext(method, receiver.GenericArguments, []);

        static bool SameTypeDefinition(TypeAnalysisContext left, TypeAnalysisContext right) =>
            ReferenceEquals(left, right)
            || (left.Definition != null && ReferenceEquals(left.Definition, right.Definition));
    }

    private static MethodAnalysisContext BaseMethodOf(MethodAnalysisContext method) =>
        method is ConcreteGenericMethodAnalysisContext { BaseMethodContext: { } baseMethod } ? baseMethod : method;

    private static RuntimeMethodInfoAnalysisContext? GetMethodInfoArgument(Instruction call)
    {
        var firstArg = call.OpCode == OpCode.CallVoid ? 1 : 2;

        for (var i = call.Operands.Count - 1; i >= firstArg; i--)
        {
            if (AsMethodInfo(call.Operands[i]) is { } methodInfo)
                return methodInfo;
        }

        return null;
    }

    private static RuntimeMethodInfoAnalysisContext? AsMethodInfo(IOperand operand) =>
        operand switch
        {
            RuntimeMethodInfoAnalysisContext methodInfo => methodInfo,
            LocalVariable { Type: RuntimeMethodInfoAnalysisContext methodInfoLocal } => methodInfoLocal,
            _ => null
        };

    private static void HandleKeyFunction(ApplicationAnalysisContext appContext, Instruction instruction, ulong target, BaseKeyFunctionAddresses kFA)
    {
        var method = "";
        if (target == kFA.il2cpp_codegen_initialize_method || target == kFA.il2cpp_codegen_initialize_runtime_metadata)
        {
            if (appContext.MetadataVersion < 27)
            {
                method = nameof(kFA.il2cpp_codegen_initialize_method);
            }
            else
            {
                method = nameof(kFA.il2cpp_codegen_initialize_runtime_metadata);
            }
        }
        else
        {
            var pairs = kFA.Pairs.ToList();
            var key = pairs.FirstOrDefault(pair => pair.Value == target).Key;
            if (key == null)
                return;
            method = key;
        }

        if (method != "")
        {
            instruction.SetOperand(0, new StringLiteral(method));
        }
    }

    // Because of il2cpp fields (like cctor_finished_or_no_cctor) [local @ reg+offset] sometimes can't be resolved, but this works for now
    private static void ResolveGetter(MethodAnalysisContext method)
    {
        if (!method.Name.StartsWith("get_"))
            return;

        // Default get: Return [this @ reg+offset]
        var instructions = method.ControlFlowGraph!.Instructions;
        if (instructions.Count == 1)
        {
            var instr = instructions[0];

            if (instr.OpCode != OpCode.Return
                || instr.Operands.Count < 1
                || instr.Operands[0] is not MemoryOperand memory
                || memory.Index != null || memory.Scale != 0
                || memory.Base is not LocalVariable local)
                return;

            var fieldName = $"<{method.Name[4..]}>k__BackingField";

            var field = method.DeclaringType!.Fields.Find(f => f.Name == fieldName);
            if (field == null)
                return;

            instr.SetOperand(0, new FieldReference(field, local, (int)memory.Addend));
        }
    }
}
