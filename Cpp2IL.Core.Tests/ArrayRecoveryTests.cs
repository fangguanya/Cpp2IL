using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ArrayRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 引用数组数据区基址恢复为常量元素访问()
    {
        var fixture = CreateFixture(indexed: false);

        ArrayRecovery.RecoverPointerDerivedAccesses(fixture.Graph, 8);

        Assert.That(fixture.Read.Operands[1], Is.InstanceOf<ArrayAccess>());
        var access = (ArrayAccess)fixture.Read.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(access.Array, Is.SameAs(fixture.Array));
            Assert.That(access.Index, Is.InstanceOf<Immediate>().And.Property("Value").EqualTo(0));
        });
    }

    [Test]
    [Category("边界值")]
    public void 引用数组数据区基址恢复为索引元素访问()
    {
        var fixture = CreateFixture(indexed: true);

        ArrayRecovery.RecoverPointerDerivedAccesses(fixture.Graph, 8);

        Assert.That(fixture.Read.Operands[1], Is.InstanceOf<ArrayAccess>());
        var access = (ArrayAccess)fixture.Read.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(access.Array, Is.SameAs(fixture.Array));
            Assert.That(access.Index, Is.SameAs(fixture.Index));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非数组根或错误步长保持原始内存操作()
    {
        var fixture = CreateFixture(indexed: true, wrongStride: true);

        ArrayRecovery.RecoverPointerDerivedAccesses(fixture.Graph, 8);

        Assert.That(fixture.Read.Operands[1], Is.InstanceOf<MemoryOperand>());
    }

    private static Fixture CreateFixture(bool indexed, bool wrongStride = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var arrayType = app.SystemTypes.SystemStringType.MakeSzArrayType();
        var array = new LocalVariable("array", new Register(null, "X0"), arrayType);
        var index = new LocalVariable("index", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        var data = new LocalVariable("data", new Register(null, "X2"), app.SystemTypes.SystemIntPtrType);
        var value = new LocalVariable("value", new Register(null, "X3"), app.SystemTypes.SystemStringType);

        var instructions = new List<Instruction>
        {
            new(0, OpCode.Add, data, array, new Immediate(32))
        };

        var memory = indexed
            ? new MemoryOperand(data, index, 0, wrongStride ? 4 : 8)
            : new MemoryOperand(data, null, 0, 0);
        var read = new Instruction(1, OpCode.Move, value, memory);
        instructions.Add(read);
        instructions.Add(new Instruction(2, OpCode.Return, value));

        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new Fixture(method, graph, read, array, index);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        Instruction Read,
        LocalVariable Array,
        LocalVariable Index);
}
