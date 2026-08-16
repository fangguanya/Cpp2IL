using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class EmptyArrayRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 返回中的空数组单例恢复为公开ArrayEmpty调用()
    {
        var fixture = CreateFixture("Value", staticField: true);
        var ret = new Instruction(1, OpCode.Return, fixture.Reference);
        fixture.Method.ControlFlowGraph = new ISILControlFlowGraph([ret]);

        var recovered = EmptyArrayRecovery.Run(fixture.Method);
        var instructions = fixture.Method.ControlFlowGraph.Instructions;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(instructions, Has.Count.EqualTo(2));
            Assert.That(instructions[0].OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(((MethodAnalysisContext)instructions[0].Operands[0]).Name, Is.EqualTo("Empty"));
            Assert.That(ret.Operands[0], Is.SameAs(instructions[0].Operands[1]));
            Assert.That(
                GenericCallRebinder.TypesEquivalent(
                    ((LocalVariable)instructions[0].Operands[1]).Type,
                    fixture.ArrayType),
                Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 直接空数组赋值恢复为公开调用且不增加临时局部()
    {
        var fixture = CreateFixture("Value", staticField: true);
        var destination = new LocalVariable("value", new Register(null, "X0"), fixture.ArrayType);
        var move = new Instruction(1, OpCode.Move, destination, fixture.Reference);
        fixture.Method.ControlFlowGraph = new ISILControlFlowGraph([move, new Instruction(2, OpCode.Return)]);

        var recovered = EmptyArrayRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(move.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(move.Operands[1], Is.SameAs(destination));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非静态或非Value字段保持原读取()
    {
        var fixture = CreateFixture("Other", staticField: false);
        var ret = new Instruction(1, OpCode.Return, fixture.Reference);
        fixture.Method.ControlFlowGraph = new ISILControlFlowGraph([ret]);

        var recovered = EmptyArrayRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Method.ControlFlowGraph.Instructions, Has.Count.EqualTo(1));
            Assert.That(ret.Operands[0], Is.SameAs(fixture.Reference));
        });
    }

    private static Fixture CreateFixture(string fieldName, bool staticField)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var elementType = app.SystemTypes.SystemStringType;
        var arrayType = elementType.MakeSzArrayType();
        var emptyDefinition = assembly.GetTypeByFullName("System.EmptyArray`1")!;
        var baseField = emptyDefinition.Fields.Single(field => field.Name == "Value");
        if (!staticField)
        {
            baseField.OverrideName = fieldName;
            baseField.Attributes = FieldAttributes.Public;
        }
        var field = baseField.MakeConcreteGenericField(elementType);
        var emptyOwner = (GenericInstanceTypeAnalysisContext)field.DeclaringType;
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "EmptyArrayCaller",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var method = callerType.InjectMethodContext(
            "Run",
            arrayType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("empty", new Register(null, "X8"), emptyOwner);
        return new Fixture(method, arrayType, new FieldReference(field, receiver, 0));
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        SzArrayTypeAnalysisContext ArrayType,
        FieldReference Reference);
}
