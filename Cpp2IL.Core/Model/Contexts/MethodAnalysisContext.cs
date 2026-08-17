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

        InjectedCheckRemover.Run(this);
        // 中文注释：类初始化分支内部常带编译器注入的空引用检查；必须先删除注入异常边，
        // 再一次性裁除元数据与类初始化保护区，避免对同一 CFG 做重复保护区扫描。
        MetadataInitGuardRemover.Run(this);

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
        // 让成功路径以派生类型进入同一个类型不动点，同时保留InvalidCastException语义。
        InlineTypeCheckRecovery.Run(this);
        AggregateStackCopyRecovery.RewriteResolvedCopies(this);

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
        // SSA拆除后的保守复制传播才把局部承载的方法目标物化为最终MethodAnalysisContext。
        // 这里仅执行一次二级TypeInfo静态字段闭合，并从调用形参、定型局部和字段写入读取预期类型；
        // 链上每个局部仍必须只有一个定义。
        LocalVariables.ResolveExpectedSelfTypedStaticFields(this);

        // Fix float literals
        FloatLiteralRecovery.Run(this);

        // 中文注释：异常清理路径先把引用取址折回直接访问，再一次性确定所有取址槽位类型；
        // out 数组只有在此阶段才具备精确 SzArray 身份，必须先于数组布局与集合内联恢复。
        ManagedReferenceAddressRecovery.Run(this);
        LocalVariables.TypeAddressedLocals(this);

        // 浮点槽位类型到此已稳定；把只读 ELF 段的绝对标量加载恢复为源码字面量，
        // 可写静态存储仍保留为内存访问，不冻结其运行时状态。
        ReadOnlyScalarLiteralRecovery.Run(this);

        // 退SSA后的比较边与原生整数位宽此时同时可见；据此恢复循环计数器和状态掩码，
        // 避免同一物理寄存器的布尔返回值把32位整数载体污染成object。
        LocalVariables.ResolveFinalScalarCarrierTypes(this);

        // 原生优化可省略从未被被调方法观察的this实参；退SSA边复制与终态标量类型现在同时
        // 可见；该阶段只处理类型已相容的标量合流，与早期类型冲突规则互斥。
        ErasedInstanceReceiverRecovery.RunScalarValueReceivers(this);

        // 中文注释：真实调用结果已有定义时，异常清理 Phi 中无参数身份、无生产者的 X0
        // 旧值不属于托管数据流；先删除该伪复制，避免它污染布尔值或引用返回值。
        CopyCoalescer.PruneUndefinedSourcePhiCopies(this);

        // 中文注释：退 SSA 的引用 Phi 此时已表现为“零或具体引用”的边复制。先把零恢复为
        // null 并定型目标，再运行字段偏移解析，才能识别上一 Scenario 的链式字段写入。
        CopyCoalescer.ResolveNullReferencePhiCopyTypes(ControlFlowGraph);
        MetadataResolver.ResolveFieldOffsets(this);
        InlineConstructorRecovery.Run(this);

        // 中文注释：异常清理 Phi 可能仅留下未定义的 X19-X28 接收者；以此前唯一相容的
        // 托管生产值恢复 List<T> 与 IEnumerator<T> 的跨调用保存身份，并同步泛型签名。
        CalleeSavedManagedReceiverRecovery.Run(this);

        // 中文注释：集合快慢边比较前先删除终态已证明跨值域的 Phi 复制；否则布尔返回槽
        // 会分别承载元素与集合引用，阻断原本完全等价的 List<T>.Add 容量菱形。
        CopyCoalescer.PruneIncompatiblePhiCopies(ControlFlowGraph);

        // 中文注释：数值和取址槽位均已终态定型后再执行一次布局恢复链；数组元素类型先闭合，
        // 随后的 List 快速路径才可用同一具体 T 生成公开 Add/Clear/Count 调用。
        ArrayRecovery.Run(this);
        ListAddRecovery.Run(this);
        ListClearRecovery.Run(this);
        ListCountRecovery.Run(this);
        StringLengthRecovery.Run(this);
        // 中文注释：长度专用操作数刚在上方生成；此处只把长度的 Int32 事实回填到循环归纳变量，
        // 不重复早期字段比较和原生位宽推导。
        LocalVariables.ResolveRecoveredLengthComparisonCarrierTypes(this);
        EmptyArrayRecovery.Run(this);
        PropertyBackingFieldRecovery.Run(this);

        // 中文注释：字段、集合和保存接收者全部恢复后，固定异常状态码的比较已经成为纯常量；
        // 此时裁掉其不可达返回/抛出边，避免异常 ABI 状态值进入托管返回类型。
        ConstantControlFlowRecovery.Run(ControlFlowGraph);

        // 中文注释：固定异常边裁除后，正常返回路径重新成为唯一链；把同一 X0 上紧邻的
        // 托管调用结果接回 Return，避免正确 ToArray 结果被无定义返回局部替换成 default。
        ManagedReturnValueRecovery.Run(this);

        // Near-last, as it depends on the final block layout
        EqualityBranchInverter.Run(this);

        // 清理数组与地址局部等后期重写留下的无用定义。
        DeadCodeEliminator.Run(this);
        DeadAddressCarrierRecovery.Run(this);

        LocalVariables.RemoveUnused(this);
    }

    public void AddWarning(string warning) => AnalysisWarnings.Add(warning);

    public void ReleaseAnalysisData()
    {
        ConvertedIsil = null;
        ControlFlowGraph = null;
        DominatorInfo = null;
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
