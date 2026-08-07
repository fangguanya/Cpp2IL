using System.Collections.Generic;
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

    private static GenericInstanceTypeAnalysisContext CreateEnumerator(TypeAnalysisContext elementType)
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        return definition.MakeGenericInstanceType([elementType]);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type, int number)
        => new(name, new Register(number, name), type);
}
