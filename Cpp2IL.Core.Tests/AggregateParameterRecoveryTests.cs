using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class AggregateParameterRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void KeyValuePair的X1与X2读取恢复为键和值字段()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.Dictionary`2")!;
        var pairDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = pairDefinition.MakeGenericInstanceType(owner.GenericParameters);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "Contains",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public,
            [pair],
            ["kvp"]);
        var thisLocal = Local("this", "X0", owner, isThis: true);
        var pairLocal = Local("kvp", "X1", pair);
        var valueLocal = Local("valueComponent", "X2", null);
        var keyUse = new Instruction(0, OpCode.Move, Local("key", "X3", null), pairLocal);
        var valueUse = new Instruction(1, OpCode.Move, Local("value", "X4", null), valueLocal);
        Prepare(method, [keyUse, valueUse], thisLocal, pairLocal, valueLocal);

        var rewritten = AggregateParameterRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(2));
            Assert.That(keyUse.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)keyUse.Operands[1]).Field.Name, Is.EqualTo("key"));
            Assert.That(valueUse.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)valueUse.Operands[1]).Field.Name, Is.EqualTo("value"));
            Assert.That(((FieldReference)valueUse.Operands[1]).Local, Is.SameAs(pairLocal));
        });
    }

    [Test]
    [Category("边界值")]
    public void 静态方法的首个聚合参数从X0开始投影()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var pairDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = pairDefinition.MakeGenericInstanceType(pairDefinition.GenericParameters);
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Read",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [pair],
            ["pair"]);
        var pairLocal = Local("pair", "X0", pair);
        var secondComponent = Local("secondComponent", "X1", null);
        var use = new Instruction(0, OpCode.Move, Local("result", "X3", null), secondComponent);
        Prepare(method, [use], pairLocal, secondComponent);

        AggregateParameterRecovery.Run(method);

        Assert.That(((FieldReference)use.Operands[1]).Field.Name, Is.EqualTo("value"));
    }

    [Test]
    [Category("异常输入")]
    public void 普通引用参数保持物理局部身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Read",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [app.SystemTypes.SystemStringType],
            ["text"]);
        var text = Local("text", "X0", app.SystemTypes.SystemStringType);
        var use = new Instruction(0, OpCode.Move, Local("result", "X2", null), text);
        Prepare(method, [use], text);

        var rewritten = AggregateParameterRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(use.Operands[1], Is.SameAs(text));
        });
    }

    private static LocalVariable Local(
        string name,
        string register,
        TypeAnalysisContext? type,
        bool isThis = false)
        => new(name, new Register(null, register), type)
        {
            IsThis = isThis,
        };

    private static void Prepare(
        MethodAnalysisContext method,
        Instruction[] instructions,
        params LocalVariable[] locals)
    {
        method.ControlFlowGraph = new ISILControlFlowGraph([
            .. instructions,
            new Instruction(instructions.Length, OpCode.Return),
        ]);
        method.Locals = locals.Concat(instructions
                .Select(instruction => instruction.Destination)
                .OfType<LocalVariable>())
            .Distinct()
            .ToList();
        method.ParameterLocals = locals
            .Where(local => local.IsThis || local.Name is "kvp" or "pair" or "text")
            .ToList();
    }
}
