using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class InlineTypeCheckRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 两段IL2CPP类层级检查恢复为单个CastClass()
    {
        var fixture = CreateFixture();

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        var cast = instructions.Single(instruction => instruction.OpCode == OpCode.CastClass);
        Assert.Multiple(() =>
        {
            Assert.That(cast.Operands[1], Is.SameAs(fixture.Source));
            Assert.That(cast.Operands[2], Is.SameAs(fixture.TargetType));
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
            Assert.That(
                instructions.Single(instruction => ReferenceEquals(instruction.Destination, fixture.Returned)).Operands[1],
                Is.SameAs(cast.Destination));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 比较源经过唯一SSA复制仍恢复CastClass()
    {
        var fixture = CreateFixture();
        WrapComparisonSources(fixture.Method);

        InlineTypeCheckRecovery.Run(fixture.Method);

        Assert.That(
            fixture.Method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.CastClass),
            Is.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 类层级表偏移不精确时保持原始控制流()
    {
        var fixture = CreateFixture(hierarchyOffset: 0xC0);

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.CastClass), Is.False);
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.EqualTo(2));
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 失败块并非InvalidCast时不吞掉异常语义()
    {
        var fixture = CreateFixture(exceptionFullName: "System.NullReferenceException");

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.CastClass), Is.False);
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.EqualTo(2));
            Assert.That(
                instructions.Any(instruction => instruction is
                {
                    OpCode: OpCode.Throw,
                    Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }],
                }),
                Is.True);
        });
    }

    private static Fixture CreateFixture(
        long hierarchyOffset = 0xC8,
        string exceptionFullName = "System.InvalidCastException")
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var targetType = app.SystemTypes.SystemStringType;
        var exceptionType = mscorlib.GetTypeByFullName(exceptionFullName)!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var sourceClass = Local(
            "sourceClass",
            new RuntimeClassTypeAnalysisContext(app.SystemTypes.SystemObjectType, mscorlib));
        var targetClass = Local(
            "targetClass",
            new RuntimeClassTypeAnalysisContext(targetType, mscorlib));
        var firstCondition = Local("firstCondition", app.SystemTypes.SystemBooleanType);
        var scaledDepth = Local("scaledDepth", app.SystemTypes.SystemIntPtrType);
        var hierarchyAddress = Local("hierarchyAddress", app.SystemTypes.SystemIntPtrType);
        var equal = Local("equal", app.SystemTypes.SystemBooleanType);
        var notEqual = Local("notEqual", app.SystemTypes.SystemBooleanType);
        var returned = Local("returned", app.SystemTypes.SystemObjectType);

        var sourceClassLoad = new Instruction(0, OpCode.Move, sourceClass, new MemoryOperand(source));
        var targetClassLoad = new Instruction(1, OpCode.Move, targetClass, targetType);
        var depthCheck = new Instruction(
            2,
            OpCode.CheckLessUnsigned,
            firstCondition,
            new MemoryOperand(sourceClass, addend: 0x130),
            new MemoryOperand(targetClass, addend: 0x130));
        var firstJump = new Instruction(3, OpCode.ConditionalJump, new Immediate(-1), firstCondition);
        var depthScale = new Instruction(
            4,
            OpCode.ShiftLeft,
            scaledDepth,
            new MemoryOperand(targetClass, addend: 0x130),
            new Immediate(3));
        var hierarchyAddressLoad = new Instruction(
            5,
            OpCode.Add,
            hierarchyAddress,
            new MemoryOperand(sourceClass, addend: hierarchyOffset),
            scaledDepth);
        var hierarchyCheck = new Instruction(
            6,
            OpCode.CheckEqual,
            equal,
            new MemoryOperand(hierarchyAddress, addend: -8),
            targetType);
        var invertCheck = new Instruction(7, OpCode.Not, notEqual, equal);
        var secondJump = new Instruction(8, OpCode.ConditionalJump, new Immediate(-1), notEqual);
        var useSource = new Instruction(9, OpCode.Move, returned, source);
        var successReturn = new Instruction(10, OpCode.Return, returned);
        var failureThrow = new Instruction(11, OpCode.Throw, exceptionType);
        var failureReturn = new Instruction(12, OpCode.Return);

        firstJump.SetOperand(0, failureThrow);
        secondJump.SetOperand(0, failureThrow);
        var graph = new ISILControlFlowGraph([
            sourceClassLoad,
            targetClassLoad,
            depthCheck,
            firstJump,
            depthScale,
            hierarchyAddressLoad,
            hierarchyCheck,
            invertCheck,
            secondJump,
            useSource,
            successReturn,
            failureThrow,
            failureReturn,
        ]);
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "InlineTypeCheckFixture",
            app.SystemTypes.SystemObjectType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = graph;
        method.Locals = graph.Instructions
            .SelectMany(instruction => instruction.Operands.OfType<LocalVariable>())
            .Distinct()
            .ToList();

        return new Fixture(method, source, targetType, returned);
    }

    private static void WrapComparisonSources(MethodAnalysisContext method)
    {
        var comparisons = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.OpCode is OpCode.CheckLessUnsigned or OpCode.CheckEqual)
            .ToList();
        foreach (var comparison in comparisons)
        {
            var block = method.ControlFlowGraph.FindBlockByInstruction(comparison)!;
            var comparisonIndex = block.Instructions.IndexOf(comparison);
            for (var sourceIndex = 1; sourceIndex <= 2; sourceIndex++)
            {
                var source = comparison.Operands[sourceIndex];
                var carrierType = source switch
                {
                    LocalVariable local => local.Type,
                    TypeAnalysisContext type => new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly),
                    _ => null,
                };
                var carrier = new LocalVariable(
                    $"comparisonCarrier{comparison.Index}_{sourceIndex}",
                    new Register(null, $"comparisonCarrier{comparison.Index}_{sourceIndex}"),
                    carrierType);
                block.Instructions.Insert(
                    comparisonIndex++,
                    new Instruction(-1, OpCode.Move, carrier, source));
                comparison.SetOperand(sourceIndex, carrier);
                method.Locals.Add(carrier);
            }
        }
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type) =>
        new(name, new Register(null, name), type);

    private sealed record Fixture(
        MethodAnalysisContext Method,
        LocalVariable Source,
        TypeAnalysisContext TargetType,
        LocalVariable Returned);
}
