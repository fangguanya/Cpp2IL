using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using System.Linq;
using System.Reflection;

namespace Cpp2IL.Core.Tests;

public class RgctxResolverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 相同普通类型的运行时类包装器必须视为等价()
    {
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var first = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);
        var second = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 独立创建但结构相同的泛型上下文必须收敛()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var firstOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var secondOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var first = new RgctxTableTypeAnalysisContext(firstOwner, firstOwner.DeclaringAssembly);
        var second = new RgctxTableTypeAnalysisContext(secondOwner, secondOwner.DeclaringAssembly);

        Assert.Multiple(() =>
        {
            Assert.That(firstOwner, Is.Not.SameAs(secondOwner));
            Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 泛型实参不同的运行时类包装器不得合并()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var integerOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var booleanOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemBooleanType]);
        var first = new RuntimeClassTypeAnalysisContext(integerOwner, integerOwner.DeclaringAssembly);
        var second = new RuntimeClassTypeAnalysisContext(booleanOwner, booleanOwner.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 相同具体方法的MethodInfo包装器必须收敛()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var list = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var add = list.Methods.First(method => method.Name == "Add" && method.Parameters.Count == 1);
        var firstMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemByteType], []);
        var secondMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemByteType], []);
        var first = new RuntimeMethodInfoAnalysisContext(firstMethod, add.DeclaringType!.DeclaringAssembly);
        var second = new RuntimeMethodInfoAnalysisContext(secondMethod, add.DeclaringType!.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 不同具体方法实参的MethodInfo包装器不得合并()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var list = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var add = list.Methods.First(method => method.Name == "Add" && method.Parameters.Count == 1);
        var byteMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemByteType], []);
        var stringMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemStringType], []);
        var first = new RuntimeMethodInfoAnalysisContext(byteMethod, add.DeclaringType!.DeclaringAssembly);
        var second = new RuntimeMethodInfoAnalysisContext(stringMethod, add.DeclaringType!.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 不同具体方法实参的MethodRgctx表不得合并()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var list = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var add = list.Methods.First(method => method.Name == "Add" && method.Parameters.Count == 1);
        var byteMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemByteType], []);
        var stringMethod = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemStringType], []);
        var first = new MethodRgctxTableTypeAnalysisContext(byteMethod, add.DeclaringType!.DeclaringAssembly);
        var second = new MethodRgctxTableTypeAnalysisContext(stringMethod, add.DeclaringType!.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 类型RGCTXData的METHOD项恢复为精确MethodInfo()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var candidate = (from type in app.Assemblies.SelectMany(assembly => assembly.Types)
                         where type.Definition != null
                         from indexed in type.Definition!.RgctXs.Select((entry, index) => (entry, index))
                         where indexed.entry.type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
                         select new { Owner = type, indexed.index }).FirstOrDefault();

        Assert.That(candidate, Is.Not.Null, "测试元数据应包含类型RGCTXData的METHOD项");
        var resolved = RgctxResolver.ResolveEntry(candidate!.Owner, candidate.index);

        Assert.That(resolved, Is.TypeOf<RuntimeMethodInfoAnalysisContext>());
    }

    [Test]
    [Category("边界值")]
    public void 类型RGCTXData负索引不生成伪上下文()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");

        Assert.That(RgctxResolver.ResolveEntry(nullableDefinition, -1), Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 无类型定义的泛型参数不生成伪RGCTXData()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");

        Assert.That(RgctxResolver.ResolveEntry(nullableDefinition.GenericParameters.Single(), 0), Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 保存寄存器沿唯一Move恢复MethodInfo身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var target = app.SystemTypes.SystemObjectType.Methods.First();
        var methodInfoType = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var methodInfo = new LocalVariable("methodInfo", new Register(null, "X2"), methodInfoType);
        var saved = new LocalVariable("saved", new Register(null, "X21"), app.SystemTypes.SystemIntPtrType);
        var move = new Instruction(0, OpCode.Move, saved, methodInfo);
        var definitions = new[] { move }.ToLookup(instruction => (LocalVariable)instruction.Destination!);

        var resolved = RgctxResolver.ResolveForwardedRuntimeMetadataType(saved, definitions);

        Assert.That(resolved, Is.SameAs(methodInfoType));
    }

    [Test]
    [Category("边界值")]
    public void 两级唯一Move链恢复同一MethodInfo身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var target = app.SystemTypes.SystemObjectType.Methods.First();
        var methodInfoType = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var methodInfo = new LocalVariable("methodInfo", new Register(null, "X3"), methodInfoType);
        var firstSaved = new LocalVariable("firstSaved", new Register(null, "X21"), app.SystemTypes.SystemIntPtrType);
        var secondSaved = new LocalVariable("secondSaved", new Register(null, "X22"), app.SystemTypes.SystemIntPtrType);
        var firstMove = new Instruction(0, OpCode.Move, firstSaved, methodInfo);
        var secondMove = new Instruction(1, OpCode.Move, secondSaved, firstSaved);
        var definitions = new[] { firstMove, secondMove }
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);

        var resolved = RgctxResolver.ResolveForwardedRuntimeMetadataType(secondSaved, definitions);

        Assert.That(resolved, Is.SameAs(methodInfoType));
    }

    [Test]
    [Category("异常输入")]
    public void 多定义保存寄存器不恢复运行时元数据身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var target = app.SystemTypes.SystemObjectType.Methods.First();
        var methodInfoType = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var methodInfo = new LocalVariable("methodInfo", new Register(null, "X2"), methodInfoType);
        var other = new LocalVariable("other", new Register(null, "X3"), app.SystemTypes.SystemIntPtrType);
        var saved = new LocalVariable("saved", new Register(null, "X21"), app.SystemTypes.SystemIntPtrType);
        var firstMove = new Instruction(0, OpCode.Move, saved, methodInfo);
        var secondMove = new Instruction(1, OpCode.Move, saved, other);
        var definitions = new[] { firstMove, secondMove }
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);

        var resolved = RgctxResolver.ResolveForwardedRuntimeMetadataType(saved, definitions);

        Assert.That(resolved, Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 当前方法Rgctx的METHOD表项证明精确调用身份载体()
    {
        var fixture = CreateMethodRgctxCarrierFixture(useSavedMethodInfo: false);

        Assert.That(
            RgctxResolver.IsMethodRgctxEntryCarrier(fixture.Method, fixture.EntryCarrier),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 保存寄存器转发的当前MethodInfo仍证明METHOD表项载体()
    {
        var fixture = CreateMethodRgctxCarrierFixture(useSavedMethodInfo: true);

        Assert.That(
            RgctxResolver.IsMethodRgctxEntryCarrier(fixture.Method, fixture.EntryCarrier),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 初始化前后同源Rgctx读取的Phi仍证明METHOD表项载体()
    {
        var fixture = CreateMethodRgctxCarrierFixture(useSavedMethodInfo: false, usePhi: true);

        Assert.That(
            RgctxResolver.IsMethodRgctxEntryCarrier(fixture.Method, fixture.EntryCarrier),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 非对齐Rgctx偏移不得证明METHOD表项载体()
    {
        var fixture = CreateMethodRgctxCarrierFixture(useSavedMethodInfo: false, addendDelta: 1);

        Assert.That(
            RgctxResolver.IsMethodRgctxEntryCarrier(fixture.Method, fixture.EntryCarrier),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 方法Rgctx目标的外层方法参数进入声明类型实参()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var list = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var add = list.Methods.First(method => method.Name == "Add" && method.Parameters.Count == 1);
        var foreignOwner = CreateGenericMethodOwner("ForeignOwner");
        var currentOwner = CreateGenericMethodOwner("CurrentOwner");
        var foreignParameter = foreignOwner.GenericParameters.Single();
        var currentParameter = currentOwner.GenericParameters.Single();
        var represented = new ConcreteGenericMethodAnalysisContext(add, [foreignParameter], []);

        var instantiated = (ConcreteGenericMethodAnalysisContext)RgctxResolver.InstantiateMethodEntryTarget(
            represented,
            [],
            [currentParameter]);

        Assert.Multiple(() =>
        {
            Assert.That(instantiated, Is.Not.SameAs(represented));
            Assert.That(instantiated.TypeGenericParameters.Single(), Is.SameAs(currentParameter));
            Assert.That(instantiated.Parameters.Single().ParameterType, Is.SameAs(currentParameter));
        });
    }

    [Test]
    [Category("边界值")]
    public void 方法Rgctx目标的具体实参保持原身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var list = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var add = list.Methods.First(method => method.Name == "Add" && method.Parameters.Count == 1);
        var represented = new ConcreteGenericMethodAnalysisContext(add, [app.SystemTypes.SystemByteType], []);

        var instantiated = RgctxResolver.InstantiateMethodEntryTarget(represented, [], []);

        Assert.That(instantiated, Is.SameAs(represented));
    }

    [Test]
    [Category("异常输入")]
    public void 非泛型方法Rgctx目标不生成伪具体实例()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var represented = app.SystemTypes.SystemObjectType.Methods.First(method => method.Name == "ToString");

        var instantiated = RgctxResolver.InstantiateMethodEntryTarget(
            represented,
            [app.SystemTypes.SystemStringType],
            [app.SystemTypes.SystemInt32Type]);

        Assert.That(instantiated, Is.SameAs(represented));
    }

    private static InjectedMethodAnalysisContext CreateGenericMethodOwner(string name)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            name,
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        owner.GenericParameters.Add(new GenericParameterTypeAnalysisContext(
            "T",
            0,
            Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
            GenericParameterAttributes.None,
            owner));
        return owner;
    }

    private static (MethodAnalysisContext Method, LocalVariable EntryCarrier) CreateMethodRgctxCarrierFixture(
        bool useSavedMethodInfo,
        int addendDelta = 0,
        bool usePhi = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var candidate = (from type in app.Assemblies.SelectMany(assembly => assembly.Types)
                         from candidateMethod in type.Methods
                         where candidateMethod.Definition != null
                         from indexed in candidateMethod.Definition!.RgctXs.Select((entry, index) => (entry, index))
                         where indexed.entry.type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
                         select new { Method = candidateMethod, indexed.index }).FirstOrDefault();
        Assert.That(candidate, Is.Not.Null, "测试元数据应包含方法 RGCTX 的 METHOD 表项");

        var method = candidate!.Method;
        var pointerSize = app.Binary.is32Bit ? 4 : 8;
        var methodRgctxOffset = app.Binary.is32Bit ? 0x1C : 0x38;
        var methodInfoType = new RuntimeMethodInfoAnalysisContext(method, method.DeclaringType!.DeclaringAssembly);
        var methodInfo = new LocalVariable("methodInfo", new Register(null, "X3"), methodInfoType);
        var saved = new LocalVariable("saved", new Register(null, "X22"), app.SystemTypes.SystemIntPtrType);
        var table = new LocalVariable("rgctx", new Register(null, "X8"), app.SystemTypes.SystemIntPtrType);
        var entry = new LocalVariable("entry", new Register(null, "X3", 1), app.SystemTypes.SystemIntPtrType);
        var instructions = new System.Collections.Generic.List<Instruction>();
        var tableSource = methodInfo;
        if (useSavedMethodInfo)
        {
            instructions.Add(new Instruction(0, OpCode.Move, saved, methodInfo));
            tableSource = saved;
        }
        instructions.Add(new Instruction(1, OpCode.Move, table, new MemoryOperand(tableSource, addend: methodRgctxOffset)));
        var effectiveTable = table;
        if (usePhi)
        {
            var secondSaved = new LocalVariable("secondSaved", new Register(null, "X23"), app.SystemTypes.SystemIntPtrType);
            var secondTable = new LocalVariable("secondRgctx", new Register(null, "X8", 2), app.SystemTypes.SystemIntPtrType);
            var mergedTable = new LocalVariable("mergedRgctx", new Register(null, "X8", 3), app.SystemTypes.SystemIntPtrType);
            instructions.Add(new Instruction(2, OpCode.Move, secondSaved, methodInfo));
            instructions.Add(new Instruction(3, OpCode.Move, secondTable, new MemoryOperand(secondSaved, addend: methodRgctxOffset)));
            instructions.Add(new Instruction(4, OpCode.Phi, mergedTable, table, secondTable));
            effectiveTable = mergedTable;
        }
        instructions.Add(new Instruction(
            5,
            OpCode.Move,
            entry,
            new MemoryOperand(effectiveTable, addend: candidate.index * pointerSize + addendDelta)));
        instructions.Add(new Instruction(6, OpCode.Return));
        method.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        return (method, entry);
    }
}
