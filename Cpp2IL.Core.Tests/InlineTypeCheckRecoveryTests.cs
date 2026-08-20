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
    [Category("基本功能")]
    public void 两个独立纯Jump失败入口汇合时仍恢复CastClass()
    {
        var fixture = CreateFixture(failureTrampolineDepth: 1);

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.CastClass), Is.EqualTo(1));
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 源对象类型尚未传播时从类指针身份恢复CastClass()
    {
        var fixture = CreateFixture(failureTrampolineDepth: 1);
        fixture.Source.Type = null;

        InlineTypeCheckRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(
                fixture.Method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.CastClass),
                Is.EqualTo(1));
            Assert.That(fixture.Source.Type, Is.SameAs(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 多级纯Jump失败入口汇合时仍恢复CastClass()
    {
        var fixture = CreateFixture(failureTrampolineDepth: 2);

        InlineTypeCheckRecovery.Run(fixture.Method);

        Assert.That(
            fixture.Method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.CastClass),
            Is.EqualTo(1));
    }

    [Test]
    [Category("基本功能")]
    public void 相等条件跳成功且落空纯Jump失败时恢复CastClass()
    {
        var fixture = CreateFixture(secondCheckBranchesToSuccess: true);

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.CastClass), Is.EqualTo(1));
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 相等条件跳成功且落空多级纯Jump失败时仍恢复CastClass()
    {
        var fixture = CreateFixture(
            failureTrampolineDepth: 2,
            secondCheckBranchesToSuccess: true);

        InlineTypeCheckRecovery.Run(fixture.Method);

        Assert.That(
            fixture.Method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.CastClass),
            Is.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 相等条件跳成功但落空失败入口含业务计算时保持原图()
    {
        var fixture = CreateFixture(
            failureTrampolineDepth: 1,
            failureTrampolineHasComputation: true,
            secondCheckBranchesToSuccess: true);

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
    public void 失败跳板含业务计算时保持原始控制流()
    {
        var fixture = CreateFixture(failureTrampolineDepth: 1, failureTrampolineHasComputation: true);

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
    [Category("边界值")]
    public void 环形成功区不得改写既有强制转换的定义目标()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemStringType);
        var replacement = Local("replacement", app.SystemTypes.SystemObjectType);
        var originalInput = Local("originalInput", app.SystemTypes.SystemObjectType);
        var returned = Local("returned", app.SystemTypes.SystemObjectType);
        var definition = new Instruction(
            0,
            OpCode.CastClass,
            source,
            originalInput,
            app.SystemTypes.SystemStringType);
        var read = new Instruction(1, OpCode.Move, returned, source);
        var successReturn = new Instruction(2, OpCode.Return, returned);
        var failureReturn = new Instruction(3, OpCode.Return);
        var graph = new ISILControlFlowGraph([
            definition,
            read,
            successReturn,
            failureReturn,
        ]);
        var start = graph.FindBlockByInstruction(definition)!;
        var failure = graph.FindBlockByInstruction(failureReturn)!;

        InlineTypeCheckRecovery.ReplaceSuccessRegionUses(
            start,
            failure,
            source,
            replacement);

        Assert.Multiple(() =>
        {
            Assert.That(definition.Destination, Is.SameAs(source));
            Assert.That(definition.Operands[1], Is.SameAs(originalInput));
            Assert.That(read.Operands[1], Is.SameAs(replacement));
        });
    }

    [Test]
    [Category("边界值")]
    public void 成功区回到新CastClass定义时不得把源对象改成自引用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var replacement = Local("replacement", app.SystemTypes.SystemStringType);
        var returned = Local("returned", app.SystemTypes.SystemObjectType);
        var cast = new Instruction(
            0,
            OpCode.CastClass,
            replacement,
            source,
            app.SystemTypes.SystemStringType);
        var read = new Instruction(1, OpCode.Move, returned, source);
        var loop = new Instruction(2, OpCode.Jump, cast);
        var failureReturn = new Instruction(3, OpCode.Return);
        var graph = new ISILControlFlowGraph([cast, read, loop, failureReturn]);

        InlineTypeCheckRecovery.ReplaceSuccessRegionUses(
            graph.FindBlockByInstruction(cast)!,
            graph.FindBlockByInstruction(failureReturn)!,
            source,
            replacement);

        Assert.Multiple(() =>
        {
            Assert.That(cast.Operands[1], Is.SameAs(source));
            Assert.That(read.Operands[1], Is.SameAs(replacement));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 只有定义没有读取时不得产生替换()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemStringType);
        var replacement = Local("replacement", app.SystemTypes.SystemObjectType);
        var originalInput = Local("originalInput", app.SystemTypes.SystemObjectType);
        var definition = new Instruction(
            0,
            OpCode.CastClass,
            source,
            originalInput,
            app.SystemTypes.SystemStringType);
        var successReturn = new Instruction(1, OpCode.Return);
        var failureReturn = new Instruction(2, OpCode.Return);
        var graph = new ISILControlFlowGraph([
            definition,
            successReturn,
            failureReturn,
        ]);

        InlineTypeCheckRecovery.ReplaceSuccessRegionUses(
            graph.FindBlockByInstruction(definition)!,
            graph.FindBlockByInstruction(failureReturn)!,
            source,
            replacement);

        Assert.Multiple(() =>
        {
            Assert.That(definition.Destination, Is.SameAs(source));
            Assert.That(definition.Operands[1], Is.SameAs(originalInput));
            Assert.That(graph.Instructions.Any(instruction =>
                instruction.Operands.Any(operand => ReferenceEquals(operand, replacement))), Is.False);
        });
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
    [Category("基本功能")]
    public void 空引用失败且成功块立即目标调用时恢复IsInst()
    {
        var fixture = CreateFixture(
            exceptionFullName: "System.NullReferenceException",
            dereferenceTargetOnSuccess: true);

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        var isInst = instructions.Single(instruction => instruction.OpCode == OpCode.IsInst);
        Assert.Multiple(() =>
        {
            Assert.That(isInst.Operands[1], Is.SameAs(fixture.Source));
            Assert.That(isInst.Operands[2], Is.SameAs(fixture.TargetType));
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.CastClass), Is.False);
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
            Assert.That(
                instructions.Single(instruction => ReferenceEquals(instruction.Destination, fixture.Returned)).Operands[2],
                Is.SameAs(isInst.Destination));
        });
    }

    [Test]
    [Category("边界值")]
    public void 空引用失败块仍有真实空值守卫前驱时只移除类型检查分支()
    {
        var fixture = CreateFixture(
            exceptionFullName: "System.NullReferenceException",
            dereferenceTargetOnSuccess: true,
            sharedNullGuardPredecessor: true);

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.IsInst), Is.EqualTo(1));
            Assert.That(instructions.Count(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.EqualTo(1));
            Assert.That(
                instructions.Any(instruction => instruction is
                {
                    OpCode: OpCode.Throw,
                    Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }],
                }),
                Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空引用失败但成功块未立即解引用目标时保持原始控制流()
    {
        var fixture = CreateFixture(exceptionFullName: "System.NullReferenceException");

        InlineTypeCheckRecovery.Run(fixture.Method);

        var instructions = fixture.Method.ControlFlowGraph!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.CastClass), Is.False);
            Assert.That(instructions.Any(instruction => instruction.OpCode == OpCode.IsInst), Is.False);
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
        string exceptionFullName = "System.InvalidCastException",
        int failureTrampolineDepth = 0,
        bool failureTrampolineHasComputation = false,
        bool dereferenceTargetOnSuccess = false,
        bool sharedNullGuardPredecessor = false,
        bool secondCheckBranchesToSuccess = false)
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
        var secondJump = new Instruction(
            8,
            OpCode.ConditionalJump,
            new Immediate(-1),
            secondCheckBranchesToSuccess ? equal : notEqual);
        var secondFailureJump = new Instruction(-1, OpCode.Jump, new Immediate(-1));
        var useSource = dereferenceTargetOnSuccess
            ? new Instruction(
                9,
                OpCode.Call,
                targetType.Methods.Single(method =>
                    method.Name == "ToString"
                    && !method.IsStatic
                    && method.Parameters.Count == 0),
                returned,
                source)
            : new Instruction(9, OpCode.Move, returned, source);
        var successReturn = new Instruction(10, OpCode.Return, returned);
        var failureThrow = new Instruction(11, OpCode.Throw, exceptionType);
        var failureReturn = new Instruction(12, OpCode.Return);
        var nullCondition = Local("nullCondition", app.SystemTypes.SystemBooleanType);
        var nullCheck = new Instruction(
            -1,
            OpCode.CheckEqual,
            nullCondition,
            source,
            new Immediate(0));
        var nullJump = new Instruction(-1, OpCode.ConditionalJump, failureThrow, nullCondition);
        var instructions = new List<Instruction>
        {
            sourceClassLoad,
            targetClassLoad,
            depthCheck,
            firstJump,
            depthScale,
            hierarchyAddressLoad,
            hierarchyCheck,
            invertCheck,
            secondJump,
        };
        if (secondCheckBranchesToSuccess)
            instructions.Add(secondFailureJump);
        instructions.Add(useSource);
        instructions.Add(successReturn);

        if (sharedNullGuardPredecessor)
        {
            instructions.Insert(0, nullJump);
            instructions.Insert(0, nullCheck);
        }

        if (failureTrampolineDepth == 0)
        {
            firstJump.SetOperand(0, failureThrow);
            if (secondCheckBranchesToSuccess)
            {
                secondJump.SetOperand(0, useSource);
                secondFailureJump.SetOperand(0, failureThrow);
            }
            else
            {
                secondJump.SetOperand(0, failureThrow);
            }
        }
        else
        {
            var firstFailureEntry = AddFailureTrampolineChain(
                instructions,
                failureThrow,
                failureTrampolineDepth,
                "first",
                failureTrampolineHasComputation,
                app.SystemTypes.SystemInt32Type);
            var secondFailureEntry = AddFailureTrampolineChain(
                instructions,
                failureThrow,
                failureTrampolineDepth,
                "second",
                failureTrampolineHasComputation,
                app.SystemTypes.SystemInt32Type);
            firstJump.SetOperand(0, firstFailureEntry);
            if (secondCheckBranchesToSuccess)
            {
                secondJump.SetOperand(0, useSource);
                secondFailureJump.SetOperand(0, secondFailureEntry);
            }
            else
            {
                secondJump.SetOperand(0, secondFailureEntry);
            }
        }

        instructions.Add(failureThrow);
        instructions.Add(failureReturn);
        var graph = new ISILControlFlowGraph(instructions);
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

    private static Instruction AddFailureTrampolineChain(
        ICollection<Instruction> instructions,
        Instruction failureThrow,
        int depth,
        string name,
        bool hasComputation,
        TypeAnalysisContext intType)
    {
        Instruction? entry = null;
        Instruction? previousJump = null;
        for (var index = 0; index < depth; index++)
        {
            if (hasComputation && index == 0)
            {
                var calculation = new Instruction(
                    -1,
                    OpCode.Add,
                    Local($"{name}FailureValue", intType),
                    new Immediate(1),
                    new Immediate(2));
                instructions.Add(calculation);
                entry = calculation;
            }

            var jump = new Instruction(-1, OpCode.Jump, new Immediate(-1));
            instructions.Add(jump);
            entry ??= jump;
            previousJump?.SetOperand(0, jump);
            previousJump = jump;
        }

        previousJump!.SetOperand(0, failureThrow);
        return entry!;
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
