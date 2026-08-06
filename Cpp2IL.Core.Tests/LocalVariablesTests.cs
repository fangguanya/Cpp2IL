using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class LocalVariablesTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 一位掩码恢复位测试两端的布尔类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 非一位掩码保持原始数值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemInt32Type);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通按位与不得冒充ARM64位测试()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "X0", 7));
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.Null);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 可空低字节掩码恢复存在标志类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var nullableInteger = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [appContext.SystemTypes.SystemInt32Type]);
        var source = new LocalVariable("source", new Register(null, "X0", 7), nullableInteger);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(source.Type, Is.SameAs(nullableInteger));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 可空一位掩码不冒充低字节存在测试()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var nullableInteger = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [appContext.SystemTypes.SystemInt32Type]);
        var source = new LocalVariable("source", new Register(null, "X0", 7), nullableInteger);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(nullableInteger));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通结构低字节掩码保持未解析状态()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(destination.Type, Is.Null);
        });
    }
}
