using System.Collections.Generic;
using System.Reflection;
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
    [Category("基本功能")]
    public void Out调用写回后的数组不得沿调用前空值折叠()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var arrayType = app.SystemTypes.SystemStringType.MakeSzArrayType();
        var array = new LocalVariable("array", new Register(null, "X0"), arrayType);
        var index = new LocalVariable("index", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        var data = new LocalVariable("data", new Register(null, "X2"), app.SystemTypes.SystemIntPtrType);
        var value = new LocalVariable("value", new Register(null, "X3"), app.SystemTypes.SystemStringType);
        var read = new Instruction(3, OpCode.Move, value, new MemoryOperand(data, index, 0, 8));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, array, new Immediate(0)),
            new Instruction(1, OpCode.CallVoid, new StringLiteral("FillArray"), new AddressOf(array)),
            new Instruction(2, OpCode.Add, data, array, new Immediate(32)),
            read,
            new Instruction(4, OpCode.Return, value),
        ]);

        ArrayRecovery.RecoverPointerDerivedAccesses(graph, 8);

        Assert.That(read.Operands[1], Is.InstanceOf<ArrayAccess>());
        var access = (ArrayAccess)read.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 折叠进基址的引用数组索引恢复为元素访问()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var array = new LocalVariable(
            "array",
            new Register(null, "X0"),
            app.SystemTypes.SystemStringType.MakeSzArrayType());
        var index = new LocalVariable("index", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        var scaledIndex = new LocalVariable("scaledIndex", new Register(null, "X2"));
        var address = new LocalVariable("address", new Register(null, "X3"), app.SystemTypes.SystemIntPtrType);
        var value = new LocalVariable("value", new Register(null, "X4"), app.SystemTypes.SystemStringType);
        var read = new Instruction(2, OpCode.Move, value, new MemoryOperand(address, addend: 32));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ShiftLeft, scaledIndex, index, new Immediate(3)),
            new Instruction(1, OpCode.Add, address, array, scaledIndex),
            read,
            new Instruction(3, OpCode.Return, value),
        ]);

        ArrayRecovery.RecoverPointerDerivedAccesses(graph, 8);

        Assert.That(read.Operands[1], Is.InstanceOf<ArrayAccess>());
        var access = (ArrayAccess)read.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
        });
    }

    [Test]
    [Category("边界值")]
    public void 字段加载的数组局部保持为折叠地址根()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var arrayType = app.SystemTypes.SystemStringType.MakeSzArrayType();
        var owner = new LocalVariable("owner", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var array = new LocalVariable("array", new Register(null, "X1"), arrayType);
        var index = new LocalVariable("index", new Register(null, "X2"), app.SystemTypes.SystemInt32Type);
        var scaledIndex = new LocalVariable("scaledIndex", new Register(null, "X3"));
        var address = new LocalVariable("address", new Register(null, "X4"), app.SystemTypes.SystemIntPtrType);
        var value = new LocalVariable("value", new Register(null, "X5"), app.SystemTypes.SystemStringType);
        var field = new InjectedFieldAnalysisContext(
            "items",
            arrayType,
            FieldAttributes.Public,
            app.SystemTypes.SystemObjectType);
        var read = new Instruction(3, OpCode.Move, value, new MemoryOperand(address, addend: 32));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, array, new FieldReference(field, owner, 0x10)),
            new Instruction(1, OpCode.ShiftLeft, scaledIndex, index, new Immediate(3)),
            new Instruction(2, OpCode.Add, address, array, scaledIndex),
            read,
            new Instruction(4, OpCode.Return, value),
        ]);

        ArrayRecovery.RecoverPointerDerivedAccesses(graph, 8);

        Assert.That(read.Operands[1], Is.InstanceOf<ArrayAccess>());
        var access = (ArrayAccess)read.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 折叠索引步长与元素大小不同时保留内存访问()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var array = new LocalVariable(
            "array",
            new Register(null, "X0"),
            app.SystemTypes.SystemStringType.MakeSzArrayType());
        var index = new LocalVariable("index", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        var scaledIndex = new LocalVariable("scaledIndex", new Register(null, "X2"));
        var address = new LocalVariable("address", new Register(null, "X3"), app.SystemTypes.SystemIntPtrType);
        var value = new LocalVariable("value", new Register(null, "X4"), app.SystemTypes.SystemStringType);
        var read = new Instruction(2, OpCode.Move, value, new MemoryOperand(address, addend: 32));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ShiftLeft, scaledIndex, index, new Immediate(2)),
            new Instruction(1, OpCode.Add, address, array, scaledIndex),
            read,
            new Instruction(3, OpCode.Return, value),
        ]);

        ArrayRecovery.RecoverPointerDerivedAccesses(graph, 8);

        Assert.That(read.Operands[1], Is.InstanceOf<MemoryOperand>());
    }

    [Test]
    [Category("异常输入")]
    public void 非数组根或错误步长保持原始内存操作()
    {
        var fixture = CreateFixture(indexed: true, wrongStride: true);

        ArrayRecovery.RecoverPointerDerivedAccesses(fixture.Graph, 8);

        Assert.That(fixture.Read.Operands[1], Is.InstanceOf<MemoryOperand>());
    }

    [Test]
    [Category("基本功能")]
    public void 原生宽度动态数组索引绑定为IntPtr()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var index = new LocalVariable("index", new Register(null, "X23"));

        ArrayRecovery.BindArrayIndexType(index, MemoryIndexExtension.None, app.SystemTypes);

        Assert.That(index.Type, Is.SameAs(app.SystemTypes.SystemIntPtrType));
    }

    [Test]
    [Category("边界值")]
    public void Uxtw数组索引绑定为UInt32()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var index = new LocalVariable("index", new Register(null, "X23"));

        ArrayRecovery.BindArrayIndexType(index, MemoryIndexExtension.ZeroExtend32, app.SystemTypes);

        Assert.That(index.Type, Is.SameAs(app.SystemTypes.SystemUInt32Type));
    }

    [Test]
    [Category("异常输入")]
    public void 已定型数组索引不被地址模式覆盖()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var index = new LocalVariable(
            "index",
            new Register(null, "X23"),
            app.SystemTypes.SystemInt64Type);

        ArrayRecovery.BindArrayIndexType(index, MemoryIndexExtension.ZeroExtend32, app.SystemTypes);

        Assert.That(index.Type, Is.SameAs(app.SystemTypes.SystemInt64Type));
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
