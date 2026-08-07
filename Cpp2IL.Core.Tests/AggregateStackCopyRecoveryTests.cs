using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

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
