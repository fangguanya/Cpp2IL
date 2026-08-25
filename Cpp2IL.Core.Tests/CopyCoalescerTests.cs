using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class CopyCoalescerTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 独立构造但结构等价的具体泛型副本可以合并()
    {
        var sourceType = CreateEnumerator(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var destinationType = CreateEnumerator(Cpp2IlApi.CurrentAppContext.SystemTypes.SystemStringType);
        var source = Local("source", sourceType, 7);
        var destination = Local("destination", destinationType, 7);
        var copy = new Instruction(0, OpCode.Move, destination, source);
        var result = new Instruction(1, OpCode.Return, destination);
        var graph = new ISILControlFlowGraph(new List<Instruction> { copy, result });

        CopyCoalescer.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(result.Operands[0], Is.SameAs(destination));
        });
    }

    [Test]
    [Category("边界值")]
    public void 不同具体泛型实参的副本保持独立()
    {
        var source = Local(
            "source",
            CreateEnumerator(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType),
            7);
        var destination = Local(
            "destination",
            CreateEnumerator(Cpp2IlApi.CurrentAppContext.SystemTypes.SystemInt32Type),
            7);
        var copy = new Instruction(0, OpCode.Move, destination, source);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            copy,
            new(1, OpCode.Return, destination),
        });

        CopyCoalescer.Run(graph);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    [Category("异常输入")]
    public void 不同物理槽的赋值不得冒充退SSA副本()
    {
        var type = CreateEnumerator(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var source = new LocalVariable("source", new Register(1, "source"), type);
        var destination = new LocalVariable("destination", new Register(2, "destination"), type);
        var copy = new Instruction(0, OpCode.Move, destination, source);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            copy,
            new(1, OpCode.Return, destination),
        });

        CopyCoalescer.Run(graph);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    [Category("基本功能")]
    public void 实例参数与后续同槽数组结果保持独立()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var thisLocal = Local("this", appContext.SystemTypes.SystemObjectType, 0);
        thisLocal.IsThis = true;
        var reusedResult = new LocalVariable("arrayResult", new Register(0, "this"));
        var copy = new Instruction(-1, OpCode.Move, reusedResult, thisLocal);
        var allocation = new Instruction(
            1,
            OpCode.NewArr,
            reusedResult,
            appContext.SystemTypes.SystemStringType.MakeSzArrayType(),
            new Immediate(10));
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            copy,
            allocation,
            new(2, OpCode.Return),
        });

        CopyCoalescer.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(allocation.Operands[0], Is.SameAs(reusedResult));
            Assert.That(allocation.Operands[0], Is.Not.SameAs(thisLocal));
            Assert.That(thisLocal.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 保存到X20的托管分配结果不与后续X0生命期合并()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!
            .MakeGenericInstanceType([appContext.SystemTypes.SystemStringType]);
        var listOrigin = new LocalVariable(
            "listOrigin",
            new Register(0, "X0_v1"),
            listType);
        var laterX0 = new LocalVariable(
            "laterX0",
            new Register(0, "X0_v2"));
        var saved = new LocalVariable(
            "saved",
            new Register(20, "X20"),
            listType);
        var allocation = new Instruction(
            0,
            OpCode.Newobj,
            listOrigin,
            listType);
        var phiCopy = new Instruction(-1, OpCode.Move, laterX0, listOrigin);
        var method = Method(
            allocation,
            phiCopy,
            new Instruction(2, OpCode.Return, laterX0));
        method.CalleeSavedSsaCopyEvidence.Add((saved, listOrigin, 1));

        CopyCoalescer.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(phiCopy.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(allocation.Operands[0], Is.SameAs(listOrigin));
            Assert.That(phiCopy.Operands[0], Is.SameAs(laterX0));
            Assert.That(phiCopy.Operands[1], Is.SameAs(listOrigin));
        });
    }

    [Test]
    [Category("边界值")]
    public void 没有保存证据的同槽同型副本仍按原规则合并()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var source = new LocalVariable("source", new Register(0, "X0_v1"), stringType);
        var destination = new LocalVariable("destination", new Register(0, "X0_v2"), stringType);
        var copy = new Instruction(-1, OpCode.Move, destination, source);
        var method = Method(copy, new Instruction(1, OpCode.Return, destination));

        CopyCoalescer.Run(method);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [Test]
    [Category("异常输入")]
    public void 非托管生产值的X20复制不得建立保护来源()
    {
        var intType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type;
        var source = new LocalVariable("source", new Register(0, "X0_v1"), intType);
        var destination = new LocalVariable("destination", new Register(0, "X0_v2"), intType);
        var saved = new LocalVariable("saved", new Register(20, "X20"), intType);
        var copy = new Instruction(-1, OpCode.Move, destination, source);
        var method = Method(
            new Instruction(0, OpCode.Move, source, new Immediate(7)),
            copy,
            new Instruction(2, OpCode.Return, destination));
        method.CalleeSavedSsaCopyEvidence.Add((saved, source, 1));

        CopyCoalescer.Run(method);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [Test]
    [Category("异常输入")]
    public void 退SSA生成的Enumerator到单精度伪复制被删除()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = Local(
            "source",
            CreateEnumerator(appContext.SystemTypes.SystemStringType),
            6);
        var destination = Local("destination", appContext.SystemTypes.SystemSingleType, 7);
        var copy = new Instruction(-1, OpCode.Move, destination, source);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            copy,
            new(1, OpCode.Return, destination),
        });

        CopyCoalescer.PruneIncompatiblePhiCopies(graph);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(copy.Operands, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退SSA的零与同型引用入边恢复为可空引用类型()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var destination = new LocalVariable("destination", new Register(28, "X28"));
        var current = Local("current", stringType, 0);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(-1, OpCode.Move, destination, new Immediate(0)),
            new(-1, OpCode.Move, destination, current),
            new(1, OpCode.Return, destination),
        });

        var changed = CopyCoalescer.ResolveNullReferencePhiCopyTypes(graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(stringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 退SSA的零与不同引用类型入边不建立伪共识()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(28, "X28"));
        var first = Local("first", appContext.SystemTypes.SystemStringType, 0);
        var second = Local("second", appContext.SystemTypes.SystemObjectType, 1);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(-1, OpCode.Move, destination, new Immediate(0)),
            new(-1, OpCode.Move, destination, first),
            new(-1, OpCode.Move, destination, second),
            new(1, OpCode.Return, destination),
        });

        var changed = CopyCoalescer.ResolveNullReferencePhiCopyTypes(graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 退SSA含非零常量入边时拒绝引用推断()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var destination = new LocalVariable("destination", new Register(28, "X28"));
        var current = Local("current", stringType, 0);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(-1, OpCode.Move, destination, new Immediate(1)),
            new(-1, OpCode.Move, destination, current),
            new(1, OpCode.Return, destination),
        });

        var changed = CopyCoalescer.ResolveNullReferencePhiCopyTypes(graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 多定义目标上的无来源Phi复制被删除()
    {
        var boolType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemBooleanType;
        var destination = Local("destination", boolType, 0);
        var unresolved = new LocalVariable("unresolved", new Register(null, "X0", 21));
        var realSource = Local("real", boolType, 1);
        var realDefinition = new Instruction(0, OpCode.Move, destination, realSource);
        var phiCopy = new Instruction(-1, OpCode.Move, destination, unresolved);
        var method = Method(realDefinition, phiCopy, new Instruction(2, OpCode.Return, destination));

        var pruned = CopyCoalescer.PruneUndefinedSourcePhiCopies(method);

        Assert.Multiple(() =>
        {
            Assert.That(pruned, Is.EqualTo(1));
            Assert.That(phiCopy.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("边界值")]
    public void 唯一定义即使源未定型也保持为恢复红门()
    {
        var boolType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemBooleanType;
        var destination = Local("destination", boolType, 0);
        var unresolved = new LocalVariable("unresolved", new Register(null, "X0", 21));
        var phiCopy = new Instruction(-1, OpCode.Move, destination, unresolved);
        var method = Method(phiCopy, new Instruction(2, OpCode.Return, destination));

        var pruned = CopyCoalescer.PruneUndefinedSourcePhiCopies(method);

        Assert.Multiple(() =>
        {
            Assert.That(pruned, Is.Zero);
            Assert.That(phiCopy.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Phi源具有真实定义时禁止删除复制()
    {
        var boolType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemBooleanType;
        var destination = Local("destination", boolType, 0);
        var source = new LocalVariable("source", new Register(null, "X0", 21));
        var realSource = Local("real", boolType, 1);
        var sourceDefinition = new Instruction(0, OpCode.Move, source, new Immediate(0));
        var realDefinition = new Instruction(1, OpCode.Move, destination, realSource);
        var phiCopy = new Instruction(-1, OpCode.Move, destination, source);
        var method = Method(
            sourceDefinition,
            realDefinition,
            phiCopy,
            new Instruction(3, OpCode.Return, destination));

        var pruned = CopyCoalescer.PruneUndefinedSourcePhiCopies(method);

        Assert.Multiple(() =>
        {
            Assert.That(pruned, Is.Zero);
            Assert.That(phiCopy.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    private static GenericInstanceTypeAnalysisContext CreateEnumerator(TypeAnalysisContext elementType)
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        return definition.MakeGenericInstanceType([elementType]);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type, int number)
        => new(name, new Register(number, name), type);

    private static MethodAnalysisContext Method(params Instruction[] instructions)
    {
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = new ISILControlFlowGraph([.. instructions]);
        method.ParameterLocals = [];
        // 中文注释：该夹具绕过构造器，必须显式建立生产路径依赖的冻结证据集合。
        method.CalleeSavedSsaCopyEvidence = [];
        return method;
    }
}
