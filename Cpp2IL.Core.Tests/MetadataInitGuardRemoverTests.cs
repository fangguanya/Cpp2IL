using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using System.Reflection;

namespace Cpp2IL.Core.Tests;

public class MetadataInitGuardRemoverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 泛型方法Rgctx初始化保护区被完整删除()
    {
        var fixture = CreateMethodRgctxGuard(useSavedCarrier: false, ordinaryRecursiveCall: false);

        var removed = MetadataInitGuardRemover.RemoveMethodRgctxInitGuards(
            fixture.Method,
            fixture.Graph,
            0x38);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.True);
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.Init));
            Assert.That(fixture.Guard.Successors, Is.EqualTo(new[] { fixture.Merge }));
            Assert.That(fixture.Guard.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
        }
    }

    [Test]
    [Category("边界值")]
    public void 保存寄存器上的唯一Move载体仍可删除Rgctx保护区()
    {
        var fixture = CreateMethodRgctxGuard(useSavedCarrier: true, ordinaryRecursiveCall: false);

        var removed = MetadataInitGuardRemover.RemoveMethodRgctxInitGuards(
            fixture.Method,
            fixture.Graph,
            0x38);

        Assert.That(removed, Is.True);
        Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.Init));
    }

    [Test]
    [Category("异常输入")]
    public void 普通递归调用不得冒充Rgctx初始化分支()
    {
        var fixture = CreateMethodRgctxGuard(useSavedCarrier: false, ordinaryRecursiveCall: true);

        var removed = MetadataInitGuardRemover.RemoveMethodRgctxInitGuards(
            fixture.Method,
            fixture.Graph,
            0x38);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.False);
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.Init));
            Assert.That(fixture.Guard.Instructions[^1].OpCode, Is.EqualTo(OpCode.ConditionalJump));
        }
    }

    [Test]
    [Category("基本功能")]
    public void ProvenMetadataGuardDropsConstantFlagTest()
    {
        var test = CreateFlagTest(0x05E7411F, 1);
        var guard = new Block { Instructions = [test] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(test.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(test.Operands, Is.Empty);
        }
    }

    [Test]
    [Category("基本功能")]
    public void FlagTestInUniqueGuardPredecessorIsDropped()
    {
        var test = CreateFlagTest(0x05E7411F, 1);
        var prefix = new Block { Instructions = [test] };
        var guard = new Block();
        prefix.Successors.Add(guard);
        guard.Predecessors.Add(prefix);

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTestsFromGuardPrefix(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(test.OpCode, Is.EqualTo(OpCode.Nop));
        }
    }

    [Test]
    [Category("基本功能")]
    public void RecoveredFlagConditionChainIsDroppedTogether()
    {
        var loadedFlag = new LocalVariable("loadedFlag", new Register(null, "X8"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var condition = new LocalVariable("condition", new Register(null, "TEST_BIT_CONDITION"));
        var load = new Instruction(0, OpCode.Move, loadedFlag, new MemoryOperand(addend: 0x05E7411F));
        var and = new Instruction(1, OpCode.And, testedBit, loadedFlag, new Immediate(1));
        var comparison = new Instruction(2, OpCode.CheckNotEqual, condition, testedBit, new Immediate(0));
        var branch = new Instruction(3, OpCode.ConditionalJump, new Block(), condition);
        var guard = new Block { Instructions = [load, and, comparison, branch] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(3));
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(and.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(comparison.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        }
    }

    [Test]
    [Category("边界值")]
    public void ZeroAddressStillHasConstantMemoryShape()
    {
        Assert.That(
            MetadataInitGuardRemover.IsConstantMetadataFlagTest(CreateFlagTest(0, 1)),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void NonUnitMaskIsPreserved()
    {
        var test = CreateFlagTest(0x05E7411F, 2);
        var guard = new Block { Instructions = [test] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.Zero);
            Assert.That(test.OpCode, Is.EqualTo(OpCode.And));
        }
    }

    [Test]
    [Category("基本功能")]
    public void AddressAddThenLoadRecognizesInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x135));
        var test = new Instruction(
            1,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: flagAddress),
            new Immediate(1));
        var guard = new Block { Instructions = [address, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void LoadedFlagThenBitTestRecognizesInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var loadedFlag = new LocalVariable("loadedFlag", new Register(null, "X10"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x135));
        var load = new Instruction(1, OpCode.Move, loadedFlag, new MemoryOperand(baseRegister: flagAddress));
        var test = new Instruction(2, OpCode.And, testedBit, loadedFlag, new Immediate(1));
        var guard = new Block { Instructions = [address, load, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void DirectInitializedFlagOffsetRemainsRecognized()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var test = new Instruction(
            0,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: classAddress, addend: 0x135),
            new Immediate(1));
        var guard = new Block { Instructions = [test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void DifferentAddressAddIsNotClassifiedAsInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x134));
        var test = new Instruction(
            1,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: flagAddress),
            new Immediate(1));
        var guard = new Block { Instructions = [address, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.False);
    }

    private static Instruction CreateFlagTest(long address, long mask) =>
        new(
            0,
            OpCode.And,
            new LocalVariable("flag", new Register(null, "TEST_BIT_VALUE")),
            new MemoryOperand(addend: address),
            new Immediate(mask));

    private static MethodRgctxGuardFixture CreateMethodRgctxGuard(
        bool useSavedCarrier,
        bool ordinaryRecursiveCall)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var objectType = app.SystemTypes.SystemObjectType;
        var booleanType = app.SystemTypes.SystemBooleanType;
        var method = new InjectedMethodAnalysisContext(
            objectType,
            "GenericOwner",
            booleanType,
            MethodAttributes.Public,
            [objectType]);
        var methodInfoType = new RuntimeMethodInfoAnalysisContext(method, objectType.DeclaringAssembly);
        var concreteCallTarget = new ConcreteGenericMethodAnalysisContext(method, [], []);
        var methodInfo = new LocalVariable("methodInfo", new Register(null, "X2"), methodInfoType);
        var savedMethodInfo = new LocalVariable(
            "savedMethodInfo",
            new Register(null, "X21"),
            app.SystemTypes.SystemIntPtrType);
        var carrier = useSavedCarrier ? savedMethodInfo : methodInfo;
        var callCarrier = useSavedCarrier
            ? new LocalVariable("callMethodInfo", new Register(null, "X0", 2), app.SystemTypes.SystemIntPtrType)
            : carrier;
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), objectType);
        var condition = new LocalVariable("condition", new Register(null, "COND"), booleanType);
        var callResult = new LocalVariable("initResult", new Register(null, "X0", 1), booleanType);

        var graph = new ISILControlFlowGraph([new Instruction(100, OpCode.Return)]);
        var guard = new Block { ID = 2 };
        var init = new Block { ID = 3 };
        var merge = new Block { ID = 4 };

        if (useSavedCarrier)
            graph.EntryBlock.Instructions.Add(new Instruction(0, OpCode.Move, savedMethodInfo, methodInfo));
        guard.Instructions.Add(new Instruction(
            1,
            OpCode.CheckNotEqual,
            condition,
            new MemoryOperand(baseRegister: carrier, addend: 0x38),
            new Immediate(0)));
        guard.Instructions.Add(new Instruction(2, OpCode.ConditionalJump, merge, condition));
        if (useSavedCarrier)
            init.Instructions.Add(new Instruction(3, OpCode.Move, callCarrier, carrier));
        init.Instructions.Add(new Instruction(
            4,
            OpCode.Call,
            concreteCallTarget,
            callResult,
            ordinaryRecursiveCall ? receiver : callCarrier));
        init.Instructions.Add(new Instruction(5, OpCode.Jump, merge));
        merge.Instructions.Add(new Instruction(6, OpCode.Return, new Immediate(1)));

        Connect(graph.EntryBlock, guard);
        Connect(guard, init);
        Connect(guard, merge);
        Connect(init, merge);
        Connect(merge, graph.ExitBlock);
        guard.CalculateBlockType();
        init.CalculateBlockType();
        merge.CalculateBlockType();
        graph.Blocks = [graph.EntryBlock, graph.ExitBlock, guard, init, merge];
        method.ControlFlowGraph = graph;

        return new MethodRgctxGuardFixture(method, graph, guard, init, merge);
    }

    private static void Connect(Block source, Block destination)
    {
        source.Successors.Add(destination);
        destination.Predecessors.Add(source);
    }

    private sealed record MethodRgctxGuardFixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        Block Guard,
        Block Init,
        Block Merge);
}
