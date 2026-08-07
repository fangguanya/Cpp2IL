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
    public void ARM64标准序言恢复栈帧基址的原生指针类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stackPointer = new LocalVariable(
            "stackPointer",
            new Register(null, "X31", -1),
            appContext.SystemTypes.SystemObjectType);
        var frameBase = new LocalVariable(
            "frameBase",
            new Register(null, "X31", 1),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            frameBase,
            stackPointer,
            new Immediate(0x60));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(stackPointer.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
            Assert.That(frameBase.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零帧长不得冒充ARM64栈帧序言()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stackPointer = new LocalVariable("stackPointer", new Register(null, "X31", -1));
        var frameBase = new LocalVariable("frameBase", new Register(null, "X31", 1));
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            frameBase,
            stackPointer,
            new Immediate(0));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(stackPointer.Type, Is.Null);
            Assert.That(frameBase.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通寄存器减法不得改变为栈帧指针()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "X0", 1));
        var destination = new LocalVariable("destination", new Register(null, "X0", 2));
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            destination,
            source,
            new Immediate(0x60));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.Null);
            Assert.That(destination.Type, Is.Null);
        });
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

    [Test]
    [Category("基本功能")]
    public void 布尔逻辑非覆盖寄存器复用留下的对象占位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemBooleanType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(0, OpCode.Not, destination, source);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 整数按位非不得被强制解释为布尔逻辑非()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt32Type);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(0, OpCode.Not, destination, source);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 缺少源操作数的逻辑非不得修改目标类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(0, OpCode.Not, destination);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 地址载体从解引用写入恢复引用槽位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable("slot", new Register(null, "stack_-40", 1));
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X22", 4),
            appContext.SystemTypes.SystemObjectType);
        var storedValue = new LocalVariable(
            "storedValue",
            new Register(null, "X0", 19),
            appContext.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, new MemoryOperand(carrier), storedValue),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(slot.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(
                ((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 值类型槽位通过地址别名保持精确元素类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-16", 1),
            appContext.SystemTypes.SystemInt32Type);
        var carrier = new LocalVariable("carrier", new Register(null, "X20", 2));
        var alias = new LocalVariable("alias", new Register(null, "X21", 3));
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "W0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, alias, carrier),
            new(2, OpCode.Move, loadedValue, new MemoryOperand(alias)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(((ByRefTypeAnalysisContext)alias.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(loadedValue.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通局部量复制不得建立地址载体关系()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable("destination", new Register(null, "X1", 1));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, destination, source),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未解引用的地址不得污染槽位与载体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable("slot", new Register(null, "stack_-24", 1));
        var staleElementType = appContext.SystemTypes.SystemObjectType;
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X1", 8),
            staleElementType.MakeByReferenceType());
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(slot.Type, Is.Null);
            Assert.That(((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(staleElementType));
        });
    }
}
