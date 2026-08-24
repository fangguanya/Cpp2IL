using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using StableNameDotNet.Providers;

namespace Cpp2IL.Core.Model.Contexts;

/// <summary>
/// Represents one method within the application. Can be analyzed to attempt to reconstruct the function body.
/// </summary>
public class MethodAnalysisContext : HasGenericParameters, IMethodInfoProvider, ISIL.IOperand
{
    /// <summary>
    /// The underlying metadata for the method.
    ///
    /// Nullable iff this is a subclass.
    /// </summary>
    public readonly Il2CppMethodDefinition? Definition;

    /// <summary>
    /// The analysis context for the declaring type of this method.
    /// </summary>
    public readonly TypeAnalysisContext? DeclaringType;

    /// <summary>
    /// The address of this method as defined in the underlying metadata.
    /// </summary>
    public virtual ulong UnderlyingPointer => Definition?.MethodPointer ?? throw new("Subclasses of MethodAnalysisContext should override UnderlyingPointer");

    public ulong Rva => UnderlyingPointer == 0 ? 0 : AppContext.Binary.GetRva(UnderlyingPointer);

    /// <summary>
    /// The raw method body as machine code in the active instruction set.
    /// </summary>
    public BinarySlice RawBytes = BinarySlice.Empty;

    /// <summary>
    /// The first-stage-analyzed Instruction-Set-Independent Language Instructions.
    /// </summary>
    public List<Instruction>? ConvertedIsil;

    /// <summary>
    /// All ISIL local variables.
    /// </summary>
    public List<LocalVariable> Locals = [];

    /// <summary>
    /// Operands used as parameters.
    /// </summary>
    public List<ISIL.IOperand> ParameterOperands = [];

    /// <summary>
    /// The control flow graph for this method, if one is built.
    /// </summary>
    public ISILControlFlowGraph? ControlFlowGraph;

    /// <summary>
    /// Dominance info for the control flow graph.
    /// </summary>
    public DominatorInfo? DominatorInfo;

    public List<string> AnalysisWarnings = [];

    public List<ParameterAnalysisContext> Parameters = [];

    public List<LocalVariable> ParameterLocals = [];

    /// <summary>
    /// SSA 阶段冻结的 ARM64 被调用方保存寄存器直接复制证据。
    /// </summary>
    /// <remarks>
    /// 异常清理 Phi 在退 SSA 后可能删除真实的 <c>X19-X29 &lt;- X0</c> 复制；这里只保存
    /// 已版本化局部的身份，不推断类型，也不改变控制流。终态恢复器必须再次验证来源确为
    /// 托管调用或分配结果、类型相容且证据唯一后才可消费。
    /// </remarks>
    internal List<(LocalVariable Destination, LocalVariable Source, int Index)> CalleeSavedSsaCopyEvidence = [];

    /// <summary>
    /// Does this method return void?
    /// </summary>
    public bool IsVoid => ReturnType == AppContext.SystemTypes.SystemVoidType;

    public bool IsStatic => (Attributes & MethodAttributes.Static) != 0;

    public bool IsVirtual => (Attributes & MethodAttributes.Virtual) != 0;

    public bool IsAbstract => (Attributes & MethodAttributes.Abstract) != 0;

    public bool IsNewSlot => (Attributes & MethodAttributes.NewSlot) != 0;

    public bool IsFinal => (Attributes & MethodAttributes.Final) != 0;

    protected override int CustomAttributeIndex => Definition?.customAttributeIndex ?? throw new("Subclasses of MethodAnalysisContext should override CustomAttributeIndex if they have custom attributes");

    public override AssemblyAnalysisContext CustomAttributeAssembly => DeclaringType?.DeclaringAssembly ?? throw new("Subclasses of MethodAnalysisContext should override CustomAttributeAssembly if they have custom attributes");

    public override string DefaultName => Definition?.Name ?? throw new("Subclasses of MethodAnalysisContext should override DefaultName");

    public string FullName => DeclaringType == null ? Name : $"{DeclaringType.FullName}::{Name}";

    public string FullNameWithSignature => $"{ReturnType.FullName} {FullName}({string.Join(", ", Parameters.Select(p => p.HumanReadableSignature))})";

    public virtual MethodAttributes DefaultAttributes => Definition?.Attributes ?? throw new($"Subclasses of MethodAnalysisContext should override {nameof(DefaultAttributes)}");

    public virtual MethodAttributes? OverrideAttributes { get; set; }

    public MethodAttributes Attributes
    {
        get => OverrideAttributes ?? DefaultAttributes;
        set => OverrideAttributes = value;
    }

    public virtual MethodImplAttributes DefaultImplAttributes => Definition?.MethodImplAttributes ?? throw new($"Subclasses of MethodAnalysisContext should override {nameof(DefaultImplAttributes)}");

    public virtual MethodImplAttributes? OverrideImplAttributes { get; set; }

    public MethodImplAttributes ImplAttributes
    {
        get => OverrideImplAttributes ?? DefaultImplAttributes;
        set => OverrideImplAttributes = value;
    }

    public MethodAttributes Visibility
    {
        get
        {
            return Attributes & MethodAttributes.MemberAccessMask;
        }
        set
        {
            Attributes = (Attributes & ~MethodAttributes.MemberAccessMask) | (value & MethodAttributes.MemberAccessMask);
        }
    }

    private List<GenericParameterTypeAnalysisContext>? _genericParameters;
    public override List<GenericParameterTypeAnalysisContext> GenericParameters
    {
        get
        {
            // Lazy load the generic parameters
            _genericParameters ??= Definition?.GenericContainer?.GenericParameters.Select(p => new GenericParameterTypeAnalysisContext(p, this)).ToList() ?? [];
            return _genericParameters;
        }
    }

    private ushort Slot => Definition?.slot ?? ushort.MaxValue;

    public virtual TypeAnalysisContext DefaultReturnType => AppContext.ResolveIl2CppType(Definition?.RawReturnType) ?? throw new($"Subclasses of MethodAnalysisContext should override {nameof(DefaultReturnType)}");

    public TypeAnalysisContext? OverrideReturnType { get; set; }

    //TODO Support custom attributes on return types (v31 feature)
    public TypeAnalysisContext ReturnType
    {
        get => OverrideReturnType ?? DefaultReturnType;
        set => OverrideReturnType = value;
    }

    public MethodAnalysisContext? BaseMethod
    {
        get
        {
            if (Definition == null)
                return null;

            var vtable = DeclaringType?.Definition?.VTable;
            if (vtable == null)
                return null;

            for (var i = 0; i < vtable.Length; ++i)
            {
                var vtableEntry = vtable[i];
                if (vtableEntry is null or { Type: not MetadataUsageType.MethodDef } || vtableEntry.AsMethod() != Definition)
                    continue;

                if (IsInterfaceSlot(this, i))
                {
                    continue;
                }

                var baseType = DeclaringType?.DefaultBaseType;
                while (baseType is not null)
                {
                    if (TryGetMethodForSlot(baseType, i, out var method))
                    {
                        return method;
                    }
                    baseType = baseType.DefaultBaseType;
                }
            }
            return null;
        }
    }

    private List<MethodAnalysisContext>? _overrides;

    /// <summary>
    /// The set of interface methods which this method explicitly overrides.
    /// </summary>
    public List<MethodAnalysisContext> Overrides
    {
        get
        {
            // Lazy load the overrides
            return _overrides ??= GetOverrides().ToList();
        }
    }

    private IEnumerable<MethodAnalysisContext> GetOverrides()
    {
        if (Definition == null)
            return [];

        var declaringTypeDefinition = DeclaringType?.Definition;
        if (declaringTypeDefinition == null)
            return [];

        var vtable = declaringTypeDefinition.VTable;
        if (vtable == null)
            return [];

        return GetOverriddenMethods(declaringTypeDefinition, vtable);

        IEnumerable<MethodAnalysisContext> GetOverriddenMethods(Il2CppTypeDefinition declaringTypeDefinition, MetadataUsage?[] vtable)
        {
            for (var i = 0; i < vtable.Length; ++i)
            {
                var vtableEntry = vtable[i];
                if (vtableEntry is null or { Type: not MetadataUsageType.MethodDef })
                    continue;

                if (vtableEntry.AsMethod() != Definition)
                    continue;

                // Interface inheritance
                foreach (var interfaceOffset in declaringTypeDefinition.InterfaceOffsets)
                {
                    if (i >= interfaceOffset.offset)
                    {
                        var interfaceTypeContext = AppContext.ResolveIl2CppType(interfaceOffset.Type);
                        var slot = i - interfaceOffset.offset;
                        if (TryGetMethodForSlot(interfaceTypeContext, slot, out var method) && !IsInterfaceSlot(method, slot))
                        {
                            yield return method;
                        }
                    }
                }
            }
        }
    }

    private static bool IsInterfaceSlot(MethodAnalysisContext method, int slot)
    {
        var declaringTypeDefinition = method.DeclaringType?.Definition;
        if (declaringTypeDefinition == null)
            return false;

        foreach (var interfaceOffset in declaringTypeDefinition.InterfaceOffsets)
        {
            if (slot >= interfaceOffset.offset)
            {
                var interfaceTypeContext = method.AppContext.ResolveIl2CppType(interfaceOffset.Type);
                if (HasMethodForSlot(interfaceTypeContext, slot - interfaceOffset.offset))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasMethodForSlot(TypeAnalysisContext declaringType, int slot)
    {
        if (declaringType is GenericInstanceTypeAnalysisContext genericInstanceType)
        {
            return genericInstanceType.GenericType.Methods.Any(m => m.Slot == slot);
        }
        else
        {
            return declaringType.Methods.Any(m => m.Slot == slot);
        }
    }

    private static bool TryGetMethodForSlot(TypeAnalysisContext declaringType, int slot, [NotNullWhen(true)] out MethodAnalysisContext? method)
    {
        if (declaringType is GenericInstanceTypeAnalysisContext genericInstanceType)
        {
            var genericMethod = genericInstanceType.GenericType.Methods.FirstOrDefault(m => m.Slot == slot);
            if (genericMethod is not null)
            {
                method = new ConcreteGenericMethodAnalysisContext(genericMethod, genericInstanceType.GenericArguments, []);
                return true;
            }
        }
        else
        {
            var baseMethod = declaringType.Methods.FirstOrDefault(m => m.Slot == slot);
            if (baseMethod is not null)
            {
                method = baseMethod;
                return true;
            }
        }

        method = null;
        return false;
    }

    public MethodAnalysisContext(Il2CppMethodDefinition? definition, TypeAnalysisContext parent) : base(definition?.token ?? 0, parent.AppContext)
    {
        DeclaringType = parent;
        Definition = definition;

        if (Definition != null)
        {
            InitCustomAttributeData();

            for (var i = 0; i < Definition.InternalParameterData!.Length; i++)
            {
                var parameterDefinition = Definition.InternalParameterData![i];
                Parameters.Add(new(parameterDefinition, i, this));
            }
        }
    }

    public void EnsureRawBytes()
    {
        //Some abstract methods (on interfaces, no less) apparently have a body? Unity doesn't support default interface methods so idk what's going on here.
        //E.g. UnityEngine.Purchasing.AppleCore.dll: UnityEngine.Purchasing.INativeAppleStore::SetUnityPurchasingCallback on among us (itch.io build)
        if (UnderlyingPointer != 0 && !DefaultAttributes.HasFlag(MethodAttributes.Abstract))
        {
            RawBytes = AppContext.InstructionSet.GetRawBytesForMethod(this, this is AttributeGeneratorMethodAnalysisContext);

            if (RawBytes.Length == 0)
            {
                Logger.VerboseNewline("\t\t\tUnexpectedly got 0-byte method body for " + this + $". Pointer was 0x{UnderlyingPointer:X}", "MAC");
            }
        }
    }

    protected MethodAnalysisContext(ApplicationAnalysisContext context) : base(0, context)
    { }

    [MemberNotNull(nameof(ConvertedIsil))]
    public void Analyze()
    {
        var maximumMethodSizeBytes = Cpp2IlApi.RuntimeOptions?.MaximumMethodSizeBytes ?? MethodAnalysisSizePolicy.DefaultMaximumBytes;
        if (MethodAnalysisSizePolicy.ExceedsMaximum(RawBytes.Length, maximumMethodSizeBytes))
        {
            Logger.WarnNewline($"Method {FullName} is too big ({RawBytes.Length} bytes), skipping analysis.");
            ConvertedIsil = [];
            return;
        }

        if (ConvertedIsil != null)
            return;

        if (UnderlyingPointer == 0)
        {
            ConvertedIsil = [];
            return;
        }

        ConvertedIsil = AppContext.InstructionSet.GetIsilFromMethod(this);
        ParameterOperands = AppContext.InstructionSet.GetParameterOperandsFromMethod(this);

        if (ConvertedIsil.Count == 0)
            return; //Nothing to do, empty function

        ControlFlowGraph = new ISILControlFlowGraph(ConvertedIsil);

        // Indirect jumps/calls should probably be resolved here before stack analysis

        StackAnalyzer.Analyze(this);

        // Dominator info must be computed after stack analysis, which removes unreachable/empty
        // blocks and would otherwise leave the dominator tree out of sync with the graph SSA sees.
        DominatorInfo = new DominatorInfo(ControlFlowGraph);

        // Create locals
        SsaForm.Build(this);
        LocalVariables.CreateAll(this);
        // 中文注释：此刻物理寄存器复制仍保留完整 SSA 版本；后续 Phi 简化可能把真实复制
        // 删除为 Nop，因此必须在任何复制传播和死码删除前冻结其身份证据。
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(this);
        // 中文注释：任何元数据操作数改写前冻结全部“绝对槽 - 零偏移二次读取”候选链；
        // 元数据调用解析完成后再用初始化槽目录过滤，兼顾完整证据与精确边界。
        var runtimeClassSlotCandidates = RuntimeClassSlotIdentityRecovery.CaptureCandidates(this);

        // Fold the explicit per-comparison flag arithmetic back into single relational comparisons,
        // then eliminate the now-dead flag computations. Both run in SSA form, where each
        // flag/temporary has a single, version-stable definition.
        FlagConditionRecovery.Run(this);
        DeadCodeEliminator.Run(this);

        // Resolve call targets, strings and getters, then run the combined type-propagation and
        // field-resolution fixpoint - all while still in SSA form, so every local is
        // single-assignment and a type, once known, is stable for that value.
        MetadataResolver.ResolveAll(this);

        // ARM64大值类型通过X8指向调用方返回缓冲区；目标身份解析后才能把返回值绑定到真实栈局部。
        HiddenReturnBufferRecovery.Run(this);

        // Resolve KeyFunctionAddress calls, then collect what removing the write barriers left dead.
        KeyFunctionRecovery.RewriteAllocationsAndBarriers(this);
        DeadCodeEliminator.Run(this);

        // 初始化保护区删除后不再能从CFG枚举其 MethodInfo 槽；因此先冻结槽地址目录，
        // 后续只把该不可变证据用于清理同一方法内的隐藏元数据读取。
        var initializedRuntimeMetadataSlots = RuntimeMetadataSlotResolver.CaptureInitializedSlotAddresses(this);
        // 中文注释：部分ARM64调用在提升时已丢弃隐藏MethodInfo实参，但初始化目录仍保存具体
        // MethodRef；只在同一泛型基方法得到唯一具体实例时收紧共享object目标。
        RuntimeMetadataSlotResolver.RecoverInitializedGenericCallTargets(
            this,
            initializedRuntimeMetadataSlots);
        InjectedCheckRemover.Run(this);
        // 中文注释：类初始化分支内部常带编译器注入的空引用检查；必须先删除注入异常边，
        // 再一次性裁除元数据与类初始化保护区，避免对同一 CFG 做重复保护区扫描。
        MetadataInitGuardRemover.Run(this);

        // 中文注释：post-27 第一层绝对槽保存元数据表基址，第二层读取才是 Il2CppClass*；
        // 必须在字段不动点前只改写第二层，才能继续闭合 static_fields 与单例字段链。
        MetadataResolver.ResolvePost27TypeLoads(this, initializedRuntimeMetadataSlots);
        LocalVariables.ResolveTypesAndFields(this);
        // 初始化保护和注入异常边已经裁除，字符串目标类型也已收敛；此时闭合post-27
        // 二层字符串槽，既保留第一层地址载体证据，也避免把其他元数据指针误写为字符串。
        MetadataResolver.ResolveTypedPost27StringLoads(this);
        // 类型仍处于SSA局部时修复与托管签名冲突的实例接收者，避免后续简化把它折成地址操作数。
        ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(this);
        // 值类型通过主不动点到达取址栈槽后，再把Object::Box唯一恢复为托管box。
        KeyFunctionRecovery.RewriteBoxing(this);
        // 新版 ARM64 运行时把接口安全转换集中到 Object::IsInst 尾跳板；在接口分派前恢复，
        // 使成功值携带接口类型进入后续 vtable/BLR 识别。
        KeyFunctionRecovery.RewriteTypeTests(this, initializedRuntimeMetadataSlots);
        // IL2CPP把castclass展开成类深度与继承表比较；在字段解析前折回托管转换，
        // 让成功路径以派生类型进入同一个类型不动点，同时保留InvalidCastException语义；
        // post-27类型槽复用本阶段已经建立的SSA唯一定义索引，禁止重复计算整张方法图。
        InlineTypeCheckRecovery.Run(this, initializedRuntimeMetadataSlots);
        AggregateStackCopyRecovery.RewriteResolvedCopies(this);
        // 中文注释：聚合返回槽已经绑定到最终 Enumerator 栈槽；在 SSA 常量传播把重叠的
        // current 子槽折成序言零值前，以循环和字段布局证据恢复该子槽的真实字段身份。
        ListEnumeratorCurrentRecovery.Run(this);

        // 接口与委托分派均依赖统一类型不动点；两者直接给重写后的返回局部变量写入精确类型。
        MetadataResolver.ResolveMethodRgctxCalls(this);
        InterfaceDispatchRecovery.Run(this);
        DelegateInvokeRecovery.Run(this);
        // 间接调用刚刚获得真实签名；立即把接收者、ref/out栈槽及其地址载体收敛为可生成CIL的精确类型。
        LocalVariables.ResolveLateCallTypesAndAddressCarriers(this);
        // 第一次接口分派会给原生取址载体补出 T&。仅在这一新证据出现后，才能恢复
        // 共享泛型地址上的 Object::IsInst；恢复出的接口类型再驱动同一链的最终BLR绑定。
        // 这是固定的第二阶段闭包，不进行次数不定的重算。
        KeyFunctionRecovery.RewriteTypeTests(this, initializedRuntimeMetadataSlots);
        InterfaceDispatchRecovery.Run(this);
        LocalVariables.ResolveLateCallTypesAndAddressCarriers(this);
        // Object::Unbox 的返回值仍是原生地址；此时接口与普通调用形参均已精确定型，
        // 可把唯一零偏移读取闭合为 unbox.any，而无需重复运行早期装箱分析。
        KeyFunctionRecovery.RewriteUnboxing(this);
        RuntimeMetadataSlotResolver.Run(this, initializedRuntimeMetadataSlots);
        // 运行时元数据槽到此才从原生地址闭合为直接 typeof(T)；紧接着折叠其与零的比较，
        // 防止 IL 生成器把类型元数据误投影成托管构造器调用。
        MetadataInitGuardRemover.RunRuntimeClassNullComparisons(this);
        // 中文注释：运行时类型槽到此才全部闭合；按最终 Newobj 类型一次性校准分配结果及其直接泛型调用。
        LocalVariables.RefreshResolvedNewobjTypesAndCalls(this);
        // 泛型方法的 rgctx 初始化函数可能与托管方法共享地址；只有在
        // 真实 rgctx 尾调用已绑定后，才能从同一 CFG 排除该伪递归初始化分支。
        MetadataInitGuardRemover.RunMethodRgctxInitGuards(this);

        // 所有可解析目标此时已经绑定。在SSA单一定义仍有效时裁掉原生猜测出的多余隐参，
        // 随后的死码删除才能精确移除只为MethodInfo隐参服务的全局加载；若等物理寄存器
        // 合并后再做，同一寄存器的后续重定义会让旧加载被保守误判为仍有用途。
        CallArgumentTrimmer.Run(this);
        BooleanFlagSimplifier.Run(this);

        // Copy/constant propagation belongs in SSA, where one definition dominates all uses and phis
        // make joins explicit, so forwarding a value is an unconditional global substitution.
        SsaSimplifier.Run(this);
        DeadCodeEliminator.Run(this);

        SsaForm.Remove(this);

        // Phi removal leaves a copy per merged version, most of which can share one local
        CopyCoalescer.Run(this);

        // 将无副作用的共享Move/Return尾链改写为直接返回，使大型字符串分派反编译为
        // 扁平早返回序列，避免数百层else触发Roslyn表达式复杂度上限。
        TailReturnRecovery.Run(ControlFlowGraph);

        // Now out of SSA: clean up the per-edge copies that phi removal introduced (a local can have
        // several definitions merging at a join here, so this pass propagates conservatively), then
        // drop dead locals.
        Simplifier.Simplify(this);
        // 中文注释：退 SSA 的保守简化已物化最终绝对槽读取；使用早期冻结的类型种子与当前
        // 唯一槽链配对，只重放 static_fields、字段和泛型调用闭包。
        var finalRuntimeClassSlotGroups = RuntimeClassSlotIdentityRecovery.BuildCurrentPlan(
            runtimeClassSlotCandidates,
            this,
            initializedRuntimeMetadataSlots);
        LocalVariables.ResolveRuntimeClassSlotFieldClosure(this, finalRuntimeClassSlotGroups);
        // SSA拆除后的保守复制传播才把局部承载的方法目标物化为最终MethodAnalysisContext。
        // 这里仅执行一次二级TypeInfo静态字段闭合，并从调用形参、定型局部和字段写入读取预期类型；
        // 链上每个局部仍必须只有一个定义。
        LocalVariables.ResolveExpectedSelfTypedStaticFields(this);
        // 中文注释：必须先完成 RuntimeClass 与字段闭包，再把剩余的零偏移槽读取改写为
        // 字符串、类型或方法操作数；否则类型元数据会提前吃掉 static_fields 的类载体证据。
        MetadataResolver.ResolveInitializedInlineMetadataOperands(this, initializedRuntimeMetadataSlots);

        // Fix float literals
        FloatLiteralRecovery.Run(this);

        // 中文注释：异常清理路径先把引用取址折回直接访问，再一次性确定所有取址槽位类型；
        // out 数组只有在此阶段才具备精确 SzArray 身份，必须先于数组布局与集合内联恢复。
        ManagedReferenceAddressRecovery.Run(this);
        LocalVariables.TypeAddressedLocals(this);

        // 浮点槽位类型到此已稳定；把只读 ELF 段的绝对标量加载恢复为源码字面量，
        // 可写静态存储仍保留为内存访问，不冻结其运行时状态。
        ReadOnlyScalarLiteralRecovery.Run(this);

        // 中文注释：字段、字面量和退 SSA 复制已提供最终浮点证据；只重放二元浮点算术类型绑定，
        // 防止晚期结果局部仍以 object 接收 Single/Double 栈值。
        LocalVariables.ResolveFinalFloatingArithmeticCarrierTypes(this);

        // 退SSA后的比较边与原生整数位宽此时同时可见；据此恢复循环计数器和状态掩码，
        // 避免同一物理寄存器的布尔返回值把32位整数载体污染成object。
        LocalVariables.ResolveFinalScalarCarrierTypes(this);

        // 中文注释：退 SSA 后索引、无符号上界、RELA 表基址和默认字符串槽同时可见；
        // 只有四项证据完整闭合时才把原生指针表恢复为托管 switch。
        RuntimeMetadataStringTableRecovery.Run(this);

        // 原生优化可省略从未被被调方法观察的this实参；退SSA边复制与终态标量类型现在同时
        // 可见；该阶段只处理类型已相容的标量合流，与早期类型冲突规则互斥。
        ErasedInstanceReceiverRecovery.RunScalarValueReceivers(this);

        // 中文注释：真实调用结果已有定义时，异常清理 Phi 中无参数身份、无生产者的 X0
        // 旧值不属于托管数据流；先删除该伪复制，避免它污染布尔值或引用返回值。
        CopyCoalescer.PruneUndefinedSourcePhiCopies(this);

        // 中文注释：退 SSA 的引用 Phi 此时已表现为“零或具体引用”的边复制。先把零恢复为
        // null 并定型目标，再运行字段偏移解析，才能识别上一 Scenario 的链式字段写入。
        CopyCoalescer.ResolveNullReferencePhiCopyTypes(ControlFlowGraph);
        // 中文注释：数组与集合布局恢复之前，以全部定义的一致托管复制证据覆盖 X19-X29 上的
        // IntPtr/Object ABI 占位；不同类型或真实指针定义会使该局部保持原样。
        CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(this);
        MetadataResolver.ResolveFieldOffsets(this);
        InlineConstructorRecovery.Run(this);

        // 中文注释：终态字段类型已稳定；按 TypeInfo→static_fields→自类型单例→实例字段
        // 的完整布局证据恢复管理器接收者，任何字段歧义或静态表根冲突都保持红门。
        StaticSingletonReceiverRecovery.Run(this);

        // 中文注释：异常清理 Phi 可能仅留下未定义的 X19-X28 接收者；以此前唯一相容的
        // 托管生产值恢复 List<T> 与 IEnumerator<T> 的跨调用保存身份，并同步泛型签名。
        CalleeSavedManagedReceiverRecovery.Run(this);

        // 中文注释：共享接收者 Phi 到此才获得最终具体引用类型。只用现成的接收者、
        // methodPtr 与 MethodInfo 双半槽恢复早期未闭合的普通虚调用，不重跑 SSA 前驱分析。
        LateVirtualCallRecovery.Run(this);

        // 中文注释：集合快慢边比较前先删除终态已证明跨值域的 Phi 复制；否则布尔返回槽
        // 会分别承载元素与集合引用，阻断原本完全等价的 List<T>.Add 容量菱形。
        CopyCoalescer.PruneIncompatiblePhiCopies(ControlFlowGraph);

        // 中文注释：跨类型私有字段先恢复为公开属性；getter 声明返回类型可把此前被 LINQ
        // 参数拓宽的结果槽收紧为 List<T>，后续布局恢复才能识别同一实例的 Count。
        PropertyBackingFieldRecovery.Run(this);
        // 中文注释：属性恢复可能刚把原生字段读取物化为公开 getter；此处是调用目标完整后的
        // 唯一结果收紧点，把 IEnumerable 等上界恢复为 List<T>，不重复早期调用传播。
        ManagedCallResultTypeRecovery.Run(this);
        // 中文注释：数值和取址槽位均已终态定型后执行布局恢复链；数组元素类型先闭合，
        // 随后的 List 快速路径可用同一具体 T 生成公开 Add/Clear/Count 调用。
        ArrayRecovery.Run(this);
        ListAddRecovery.Run(this);
        ListClearRecovery.Run(this);
        ListCountRecovery.Run(this);
        StringLengthRecovery.Run(this);
        // 中文注释：长度专用操作数刚在上方生成；此处只把长度的 Int32 事实回填到循环归纳变量，
        // 不重复早期字段比较和原生位宽推导。
        LocalVariables.ResolveRecoveredLengthComparisonCarrierTypes(this);
        EmptyArrayRecovery.Run(this);
        // 中文注释：公开getter在上方刚替换跨类型私有字段；单次消费新getter的标量结果，
        // 为比较归纳变量和普通整数算术结果补回精确类型，避免重复扫描属性、数组和集合恢复链。
        LocalVariables.ResolveRecoveredPropertyScalarCarrierTypes(this);
        // 中文注释：集合长度与公开属性已提供最后一批精确标量种子；沿退 SSA Move 连通分量
        // 一次性回填其上游 Not/Phi 载体，避免在 IL 生成阶段把 Int32 仍声明成 Object。
        LocalVariables.ResolveFinalScalarCopyCarrierTypes(this);
        // 中文注释：条件选择与退 SSA 边复制会用 Boolean 叶和非布尔状态码构造异常/短路控制状态；
        // 仅在定义与比较消费者完全闭合时恢复 Int32，引用和普通业务数值仍保持原类型。
        LocalVariables.ResolveFinalIntegerControlStateCarrierTypes(this);
        // 中文注释：退 SSA 的布尔边复制在此已稳定；只闭合由权威 Boolean 叶、0/1 复制和
        // 小位逻辑组成的分量，引用类型或非布尔掩码会使整个分量失败关闭。
        LocalVariables.ResolveFinalBooleanBitwiseCarrierTypes(this);
        // 中文注释：没有位逻辑叶的 0/1 分支状态在退 SSA 后表现为两个立即数定义；只有其全部
        // 消费者都是零/一比较时才恢复 Boolean，算术、调用和字段消费者继续保持红门。
        LocalVariables.ResolveFinalBooleanBranchCarrierTypes(this);

        // 中文注释：字段、集合和保存接收者全部恢复后，固定异常状态码的比较已经成为纯常量；
        // 此时裁掉其不可达返回/抛出边，避免异常 ABI 状态值进入托管返回类型。
        ConstantControlFlowRecovery.Run(ControlFlowGraph);

        // 中文注释：固定异常边裁除后，正常返回路径重新成为唯一链；把同一 X0 上紧邻的
        // 托管调用结果接回 Return，避免正确 ToArray 结果被无定义返回局部替换成 default。
        ManagedReturnValueRecovery.Run(this);

        // 中文注释：直接返回已闭合后，再处理 foreach 搜索命中边。原生被调用方保存寄存器、
        // 默认构造值和 CFG 的命中/耗尽双出口必须同时唯一，才把命中对象写回最终返回局部。
        ManagedReturnPhiRecovery.Run(this);

        // 中文注释：搜索/加权选择成功边可能返回“对象 + 字段偏移”，默认边返回静态槽地址；
        // 两个地址只有在共同尾声才解引用。引用 Phi 与字段类型到此均已稳定，现统一恢复字段值返回。
        ManagedFieldReturnRecovery.Run(this);

        // Near-last, as it depends on the final block layout
        EqualityBranchInverter.Run(this);

        // 中文注释：公开托管调用已完成目标和参数绑定后，删除紧邻调用的原生虚表
        // MethodInfo 半槽装载；随后统一活跃性分析会级联删除失去用途的类指针装载。
        ResolvedManagedCallScaffoldingRecovery.Run(this);

        // 中文注释：此处已经退出 SSA，同一物理局部可有多个定义；按跨块活跃性删除后期重写
        // 留下的具体死写，禁止再用 SSA 全局定义标记保守保留最后一次读取之后的元数据槽。
        PostSsaDeadStoreEliminator.Run(ControlFlowGraph);
        // 中文注释：末次死码删除刚把部分 List<T>.Add 快路的缩放和地址合成消为 Nop；
        // 此处只恢复 0/0 直接数组证据，不重复早期原生内存与 1/1 数组路径。
        ListAddRecovery.RunCompactArrayAccess(this);
        DeadAddressCarrierRecovery.Run(this);

        // 所有退SSA常量、运行时类型、异常栈槽取址与保存寄存器接收者到此均已物化；
        // 这是三类直接辅助ABI唯一执行点，避免早期不完整操作数造成局部恢复和重复扫描。
        DirectRuntimeHelperRecovery.Run(this);

        // 中文注释：字段、聚合接收者和直接辅助调用现已全部稳定；只重绑仍以 object 共享实例为
        // 目标、但接收者已具体化的调用，修正晚期 Enumerator<T>.MoveNext/Dispose，不重放泛型推断。
        GenericCallRebinder.RunLateSharedReceiverTargets(this);

        // 中文注释：业务枚举实参在退 SSA、集合和直接辅助调用恢复后才成为最终证据；
        // 仅对仍含 Int32Enum 的调用链执行单调闭包，使 Contains 的具体枚举向前推进到
        // Any/ToList/Select 的结果槽与委托签名，不重放字段解析或其它泛型推断。
        GenericCallRebinder.RunLateSharedEnumClosure(this);

        // 中文注释：枚举泛型链闭合后，委托调用形参已获得最终 Func 类型；只在静态缓存
        // 空值门、唯一构造与唯一写回形成完整菱形时，把退 SSA 丢失的调用载体接回缓存字段。
        CachedDelegateCarrierRecovery.Run(this);

        // 中文注释：这是布局、集合、调用和末次死码之后的唯一归纳变量门；只消费保留一致 W32/X64
        // 自更新的闭合初始化/比较分量，避免把零初始化在 CIL 中投影成引用 null。
        LocalVariables.ResolveFinalIntegerInductionCarrierTypes(this);

        LocalVariables.RemoveUnused(this);
    }

    public void AddWarning(string warning) => AnalysisWarnings.Add(warning);

    public void ReleaseAnalysisData()
    {
        ConvertedIsil = null;
        ControlFlowGraph = null;
        DominatorInfo = null;
        CalleeSavedSsaCopyEvidence.Clear();
    }

    public ConcreteGenericMethodAnalysisContext MakeGenericInstanceMethod(params IEnumerable<TypeAnalysisContext> methodGenericParameters)
    {
        if (this is ConcreteGenericMethodAnalysisContext methodOnGenericInstanceType)
        {
            return new ConcreteGenericMethodAnalysisContext(methodOnGenericInstanceType.BaseMethodContext, methodOnGenericInstanceType.TypeGenericParameters, methodGenericParameters);
        }
        else
        {
            return new ConcreteGenericMethodAnalysisContext(this, [], methodGenericParameters);
        }
    }

    public ConcreteGenericMethodAnalysisContext MakeConcreteGenericMethod(IEnumerable<TypeAnalysisContext> typeGenericParameters, IEnumerable<TypeAnalysisContext> methodGenericParameters)
    {
        if (this is ConcreteGenericMethodAnalysisContext)
        {
            throw new InvalidOperationException($"Attempted to make a {nameof(ConcreteGenericMethodAnalysisContext)} concrete: {this}");
        }
        else
        {
            return new ConcreteGenericMethodAnalysisContext(this, typeGenericParameters, methodGenericParameters);
        }
    }

    public override string ToString() => $"Method: {FullName}";

    #region StableNameDot implementation

    ITypeInfoProvider IMethodInfoProvider.ReturnType =>
        Definition!.RawReturnType!.ThisOrElementIsGenericParam()
            ? new GenericParameterTypeInfoProviderWrapper(Definition.RawReturnType!.GetGenericParamName())
            : TypeAnalysisContext.GetSndnProviderForType(AppContext, Definition!.RawReturnType);

    IEnumerable<IParameterInfoProvider> IMethodInfoProvider.ParameterInfoProviders => Parameters;

    string IMethodInfoProvider.MethodName => Name;

    MethodAttributes IMethodInfoProvider.MethodAttributes => Attributes;

    MethodSemantics IMethodInfoProvider.MethodSemantics
    {
        get
        {
            if (DeclaringType != null)
            {
                //This one is a bit trickier, as il2cpp doesn't use semantics.
                foreach (var prop in DeclaringType.Properties)
                {
                    if (prop.Getter == this)
                        return MethodSemantics.Getter;
                    if (prop.Setter == this)
                        return MethodSemantics.Setter;
                }

                foreach (var evt in DeclaringType.Events)
                {
                    if (evt.Adder == this)
                        return MethodSemantics.AddOn;
                    if (evt.Remover == this)
                        return MethodSemantics.RemoveOn;
                    if (evt.Invoker == this)
                        return MethodSemantics.Fire;
                }
            }

            return 0;
        }
    }

    #endregion
}
