using System.Reflection;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ManagedReturnPhiRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 搜索命中边把X21当前对象写回默认返回局部()
    {
        var fixture = CreateFixture();

        var recovered = ManagedReturnPhiRecovery.Run(
            fixture.Method,
            new ManagedReturnPhiRecovery.NativeReturnPhiEvidence("X21", "X19"));

        var assignment = fixture.MatchExit.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands.Count: 2 }
            && ReferenceEquals(instruction.Operands[0], fixture.DefaultResult));
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(assignment.Operands[1], Is.SameAs(fixture.Candidate));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.DefaultResult));
            Assert.That(fixture.NoMatchExit.Instructions, Has.None.Matches<Instruction>(instruction =>
                instruction.OpCode == OpCode.Move
                && instruction.Operands.Contains(fixture.Candidate)));
        });
    }

    [Test]
    [Category("边界值")]
    public void 空集合直接返回默认对象时不生成命中赋值()
    {
        var fixture = CreateFixture(includeCandidate: false);

        var recovered = ManagedReturnPhiRecovery.Run(
            fixture.Method,
            new ManagedReturnPhiRecovery.NativeReturnPhiEvidence("X21", "X19"));

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.MatchExit.Instructions, Has.None.Matches<Instruction>(instruction =>
                instruction.OpCode == OpCode.Move));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.DefaultResult));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一返回寄存器存在两个候选类型转换时拒绝猜测命中对象()
    {
        var fixture = CreateFixture(duplicateCandidate: true);

        var recovered = ManagedReturnPhiRecovery.Run(
            fixture.Method,
            new ManagedReturnPhiRecovery.NativeReturnPhiEvidence("X21", "X19"));

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.MatchExit.Instructions, Has.None.Matches<Instruction>(instruction =>
                instruction.OpCode == OpCode.Move
                && instruction.Operands.Contains(fixture.Candidate)));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.DefaultResult));
        });
    }

    private static Fixture CreateFixture(bool includeCandidate = true, bool duplicateCandidate = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ReturnPhiOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "Find",
            app.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var defaultResult = new LocalVariable(
            "defaultResult",
            new Register(null, "X0", 1),
            app.SystemTypes.SystemStringType);
        var source = new LocalVariable(
            "current",
            new Register(null, "X0", 2),
            app.SystemTypes.SystemObjectType);
        var candidate = new LocalVariable(
            "candidate",
            new Register(null, "CAST_X21_String", 3),
            app.SystemTypes.SystemStringType);
        var duplicate = new LocalVariable(
            "duplicate",
            new Register(null, "CAST_X21_String", 4),
            app.SystemTypes.SystemStringType);
        var condition = new LocalVariable(
            "condition",
            new Register(null, "W0", 5),
            app.SystemTypes.SystemBooleanType);

        var entry = NewBlock(0, new Instruction(0, OpCode.Newobj, defaultResult, app.SystemTypes.SystemStringType));
        var loopGate = NewBlock(1, new Instruction(1, OpCode.ConditionalJump, condition));
        var candidateBlock = NewBlock(2);
        if (includeCandidate)
            candidateBlock.Instructions.Add(new Instruction(2, OpCode.CastClass, candidate, source, app.SystemTypes.SystemStringType));
        if (duplicateCandidate)
            candidateBlock.Instructions.Add(new Instruction(3, OpCode.CastClass, duplicate, source, app.SystemTypes.SystemStringType));
        var predicate = NewBlock(
            3,
            new Instruction(4, OpCode.CheckEqual, condition, candidate, candidate),
            new Instruction(5, OpCode.ConditionalJump, condition));
        var matchExit = NewBlock(4, new Instruction(6, OpCode.Nop));
        var noMatchExit = NewBlock(5, new Instruction(7, OpCode.Nop));
        var cleanup = NewBlock(6, new Instruction(8, OpCode.Nop));
        var returnInstruction = new Instruction(9, OpCode.Return, defaultResult);
        var returnBlock = NewBlock(7, returnInstruction);
        var exit = NewBlock(8);

        Connect(entry, loopGate);
        if (includeCandidate)
        {
            Connect(loopGate, noMatchExit);
            Connect(loopGate, candidateBlock);
            Connect(candidateBlock, predicate);
            Connect(predicate, matchExit);
            Connect(predicate, loopGate);
        }
        else
        {
            Connect(loopGate, noMatchExit);
        }
        Connect(matchExit, cleanup);
        Connect(noMatchExit, cleanup);
        Connect(cleanup, returnBlock);
        Connect(returnBlock, exit);

        var graph = new ISILControlFlowGraph([])
        {
            EntryBlock = entry,
            ExitBlock = exit,
            Blocks = [entry, loopGate, candidateBlock, predicate, matchExit, noMatchExit, cleanup, returnBlock, exit]
        };
        method.ControlFlowGraph = graph;
        method.Locals = [defaultResult, source, candidate, duplicate, condition];
        method.ParameterLocals = [];

        return new Fixture(method, matchExit, noMatchExit, returnInstruction, defaultResult, candidate);
    }

    private static Block NewBlock(int id, params Instruction[] instructions)
        => new()
        {
            ID = id,
            Instructions = [.. instructions]
        };

    private static void Connect(Block from, Block to)
    {
        from.Successors.Add(to);
        to.Predecessors.Add(from);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        Block MatchExit,
        Block NoMatchExit,
        Instruction Return,
        LocalVariable DefaultResult,
        LocalVariable Candidate);
}
