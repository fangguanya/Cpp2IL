using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using System.Linq;

namespace Cpp2IL.Core.Tests;

public class AggregateStackCopyRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 类型收敛后完整复制还原为聚合赋值和目标字段引用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concreteEnumerator = CreateEnumerator(app.SystemTypes.SystemStringType);
        var sourceBase = Local("stack_-D8", concreteEnumerator);
        var vector = Local("V0", concreteEnumerator);
        var destinationBase = Local("stack_-80", concreteEnumerator);
        var sourceTail = Local("stack_-C8", app.SystemTypes.SystemStringType);
        var scalar = Local("X8", app.SystemTypes.SystemStringType);
        var destinationTail = Local("stack_-70", app.SystemTypes.SystemStringType);
        var observed = Local("observed", app.SystemTypes.SystemStringType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, sourceBase),
            new(1, OpCode.Move, scalar, sourceTail),
            new(2, OpCode.Move, destinationBase, vector),
            new(3, OpCode.Move, destinationTail, scalar),
            new(4, OpCode.Move, observed, destinationTail),
        };

        var changed = AggregateStackCopyRecovery.RewriteResolvedBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(instructions[2].Operands[1], Is.SameAs(sourceBase));
            Assert.That(instructions[1].OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(instructions[3].OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(instructions[4].Operands[1], Is.TypeOf<FieldReference>());
            var field = (FieldReference)instructions[4].Operands[1];
            Assert.That(field.Local, Is.SameAs(destinationBase));
            Assert.That(field.Field.Name, Is.EqualTo("current"));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 隐藏返回缓冲区完整复制时调用结果直接绑定最终Enumerator槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getEnumerator = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var target = new ConcreteGenericMethodAnalysisContext(getEnumerator, [stringType], []);
        var sourceBase = Local("stack_-A8", target.ReturnType);
        var vector = Local("V0", target.ReturnType);
        var destinationBase = Local("stack_-90", target.ReturnType);
        var sourceTail = Local("stack_-98", stringType);
        var scalar = Local("X8", stringType);
        var destinationTail = Local("stack_-80", stringType);
        var receiver = Local("X0", listDefinition.MakeGenericInstanceType([stringType]));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Call, target, sourceBase, receiver),
            new(1, OpCode.Move, vector, sourceBase),
            new(2, OpCode.Move, scalar, sourceTail),
            new(3, OpCode.Move, destinationBase, vector),
            new(4, OpCode.Move, destinationTail, scalar),
        };

        var changed = AggregateStackCopyRecovery.RewriteResolvedBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(instructions[0].Destination, Is.SameAs(destinationBase));
            Assert.That(instructions.Skip(1).All(instruction => instruction.OpCode == OpCode.Nop), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 临时返回槽另有读取时保留显式聚合赋值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getEnumerator = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var target = new ConcreteGenericMethodAnalysisContext(getEnumerator, [stringType], []);
        var sourceBase = Local("stack_-A8", target.ReturnType);
        var vector = Local("V0", target.ReturnType);
        var destinationBase = Local("stack_-90", target.ReturnType);
        var sourceTail = Local("stack_-98", stringType);
        var scalar = Local("X8", stringType);
        var destinationTail = Local("stack_-80", stringType);
        var receiver = Local("X0", listDefinition.MakeGenericInstanceType([stringType]));
        var observed = Local("X20", target.ReturnType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Call, target, sourceBase, receiver),
            new(1, OpCode.Move, vector, sourceBase),
            new(2, OpCode.Move, scalar, sourceTail),
            new(3, OpCode.Move, destinationBase, vector),
            new(4, OpCode.Move, destinationTail, scalar),
            new(5, OpCode.Move, observed, sourceBase),
        };

        var changed = AggregateStackCopyRecovery.RewriteResolvedBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(instructions[0].Destination, Is.SameAs(sourceBase));
            Assert.That(instructions[3].Operands[1], Is.SameAs(sourceBase));
            Assert.That(instructions[5].Operands[1], Is.SameAs(sourceBase));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 聚合尾字段覆盖错误地址类型并恢复精确元素类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var disposable = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var concreteEnumerator = CreateEnumerator(stringType);
        var sourceBase = Local("stack_-D8", concreteEnumerator);
        var vector = Local("V0", concreteEnumerator);
        var destinationBase = Local("stack_-80", concreteEnumerator);
        var sourceTail = Local("stack_-C8", stringType);
        var scalar = Local("X8", stringType);
        var destinationTail = Local("stack_-70", stringType);
        var observed = Local("X9", disposable.MakeByReferenceType());
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, sourceBase),
            new(1, OpCode.Move, scalar, sourceTail),
            new(2, OpCode.Move, destinationBase, vector),
            new(3, OpCode.Move, destinationTail, scalar),
            new(4, OpCode.Move, observed, destinationTail),
        };

        var changed = AggregateStackCopyRecovery.RewriteResolvedBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(observed.Type, Is.SameAs(stringType));
            Assert.That(instructions[4].Operands[1], Is.TypeOf<FieldReference>());
        });
    }

    [Test]
    [Category("基本功能")]
    public void 向量主体加标量尾块恢复具体Enumerator及当前元素类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var concreteEnumerator = CreateEnumerator(stringType);
        var objectEnumerator = CreateEnumerator(app.SystemTypes.SystemObjectType);
        var sourceBase = Local("stack_-D8", concreteEnumerator);
        var vector = Local("V0", objectEnumerator);
        var destinationBase = Local("stack_-80", objectEnumerator);
        var sourceTail = Local("stack_-C8", app.SystemTypes.SystemObjectType);
        var scalar = Local("X8", app.SystemTypes.SystemObjectType);
        var destinationTail = Local("stack_-70", app.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, sourceBase),
            new(1, OpCode.Move, scalar, sourceTail),
            new(2, OpCode.Move, destinationBase, vector),
            new(3, OpCode.Move, destinationTail, scalar),
        };

        var changed = AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(vector.Type, concreteEnumerator), Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(destinationBase.Type, concreteEnumerator), Is.True);
            Assert.That(sourceTail.Type, Is.SameAs(stringType));
            Assert.That(scalar.Type, Is.SameAs(stringType));
            Assert.That(destinationTail.Type, Is.SameAs(stringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 标量尾字段先读取时仍恢复完整Enumerator复制()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concreteEnumerator = CreateEnumerator(app.SystemTypes.SystemStringType);
        var sourceBase = Local("stack_-98", concreteEnumerator);
        var sourceTail = Local("stack_-88", app.SystemTypes.SystemStringType);
        var scalar = Local("X8", app.SystemTypes.SystemObjectType);
        var vector = Local("V0", null);
        var destinationBase = Local("stack_-80", null);
        var destinationTail = Local("stack_-70", app.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, scalar, sourceTail),
            new(1, OpCode.Move, vector, sourceBase),
            new(2, OpCode.Move, destinationBase, vector),
            new(3, OpCode.Move, destinationTail, scalar),
        };

        var changed = AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(destinationBase.Type, concreteEnumerator), Is.True);
            Assert.That(scalar.Type, Is.SameAs(app.SystemTypes.SystemStringType));
            Assert.That(destinationTail.Type, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 标量尾字段先写入时仍恢复互不重叠的完整复制()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concreteEnumerator = CreateEnumerator(app.SystemTypes.SystemStringType);
        var sourceBase = Local("stack_-98", concreteEnumerator);
        var sourceTail = Local("stack_-88", app.SystemTypes.SystemStringType);
        var scalar = Local("X8", null);
        var vector = Local("V0", null);
        var destinationBase = Local("stack_-80", null);
        var destinationTail = Local("stack_-70", null);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, sourceBase),
            new(1, OpCode.Move, scalar, sourceTail),
            new(2, OpCode.Move, destinationTail, scalar),
            new(3, OpCode.Move, destinationBase, vector),
        };

        var changed = AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(destinationBase.Type, concreteEnumerator), Is.True);
            Assert.That(destinationTail.Type, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 任一目标写入早于第二次读取时拒绝聚合复制()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concreteEnumerator = CreateEnumerator(app.SystemTypes.SystemStringType);
        var sourceBase = Local("stack_-98", concreteEnumerator);
        var sourceTail = Local("stack_-88", app.SystemTypes.SystemStringType);
        var scalar = Local("X8", app.SystemTypes.SystemObjectType);
        var vector = Local("V0", app.SystemTypes.SystemObjectType);
        var destinationBase = Local("stack_-80", app.SystemTypes.SystemObjectType);
        var destinationTail = Local("stack_-70", app.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, scalar, sourceTail),
            new(1, OpCode.Move, destinationTail, scalar),
            new(2, OpCode.Move, vector, sourceBase),
            new(3, OpCode.Move, destinationBase, vector),
        };

        var changed = AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destinationBase.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(destinationTail.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    [TestCase("stack_-10", -0x10)]
    [TestCase("stack_0", 0)]
    [TestCase("stack_2A", 0x2A)]
    [Category("边界值")]
    public void 栈槽十六进制边界名称精确解析(string name, int expected)
    {
        var local = Local(name, null);

        Assert.Multiple(() =>
        {
            Assert.That(AggregateStackCopyRecovery.TryGetStackOffset(local, out var actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 相对偏移不一致时不得传播尾字段类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concreteEnumerator = CreateEnumerator(app.SystemTypes.SystemStringType);
        var sourceBase = Local("stack_-D8", concreteEnumerator);
        var vector = Local("V0", null);
        var destinationBase = Local("stack_-80", null);
        var sourceTail = Local("stack_-C8", app.SystemTypes.SystemObjectType);
        var scalar = Local("X8", app.SystemTypes.SystemObjectType);
        var destinationTail = Local("stack_-78", app.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, sourceBase),
            new(1, OpCode.Move, scalar, sourceTail),
            new(2, OpCode.Move, destinationBase, vector),
            new(3, OpCode.Move, destinationTail, scalar),
        };

        AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(sourceTail.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(scalar.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(destinationTail.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非法栈槽名称不得被当作零偏移()
    {
        var invalid = Local("stack_NOT_HEX", null);

        Assert.That(AggregateStackCopyRecovery.TryGetStackOffset(invalid, out _), Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 已有不同具体Enumerator时拒绝冲突覆盖()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var sourceType = CreateEnumerator(app.SystemTypes.SystemStringType);
        var existingType = CreateEnumerator(app.SystemTypes.SystemInt32Type);
        var source = Local("stack_-D8", sourceType);
        var vector = Local("V0", existingType);
        var destination = Local("stack_-80", existingType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, vector, source),
            new(1, OpCode.Move, destination, vector),
        };

        var changed = AggregateStackCopyRecovery.RecoverBlock(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(GenericCallRebinder.TypesEquivalent(vector.Type, existingType), Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(destination.Type, existingType), Is.True);
        });
    }

    private static GenericInstanceTypeAnalysisContext CreateEnumerator(TypeAnalysisContext elementType)
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        return definition.MakeGenericInstanceType([elementType]);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type)
        => new(name, new Register(null, name), type);
}
