using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
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
    [Category("基本功能")]
    public void 方法布尔返回值经过普通寄存器一位掩码后保持布尔类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemBooleanType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X8", 3));
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
    [Category("异常输入")]
    public void 一位掩码不得覆盖静态字段存储载体且第二次传播稳定()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var owner = appContext.SystemTypes.SystemObjectType;
        var storageType = new StaticFieldStorageTypeAnalysisContext(owner, owner.DeclaringAssembly);
        var source = new LocalVariable("source", new Register(null, "X8", 7), storageType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var firstChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);
        var secondChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(firstChanged, Is.True);
            Assert.That(secondChanged, Is.False);
            Assert.That(source.Type, Is.SameAs(storageType));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
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
    [Category("边界值")]
    public void 强制转换语义定义必须截断旧地址别名()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var castType = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "RecoveredCastTarget",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-D0", 1),
            appContext.SystemTypes.SystemObjectType);
        var carrier = new LocalVariable("carrier", new Register(null, "X22", 2));
        var castLocal = new LocalVariable(
            "castLocal",
            new Register(null, "CAST_X22", 3),
            castType);
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "X0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, castLocal, carrier),
            new(2, OpCode.CastClass, castLocal, castLocal, castType),
            new(3, OpCode.Move, loadedValue, new MemoryOperand(carrier)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(castLocal.Type, Is.SameAs(castType));
            Assert.That(loadedValue.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 算术语义定义不得继承旧地址别名()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-20", 1),
            appContext.SystemTypes.SystemInt32Type);
        var carrier = new LocalVariable("carrier", new Register(null, "X20", 2));
        var redefined = new LocalVariable(
            "redefined",
            new Register(null, "X21", 3),
            appContext.SystemTypes.SystemInt64Type);
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "W0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, redefined, carrier),
            new(2, OpCode.Add, redefined, redefined, new Immediate(8)),
            new(3, OpCode.Move, loadedValue, new MemoryOperand(carrier)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(redefined.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
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

    [Test]
    [Category("基本功能")]
    public void 后置调用返回类型必须立即解析后续字段读取()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateFieldOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var field = new InjectedFieldAnalysisContext(
            "Text",
            appContext.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            owner,
            0x10);
        owner.Fields.Add(field);
        var getter = new InjectedMethodAnalysisContext(
            owner,
            "GetOwner",
            owner,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var loaded = new LocalVariable("loaded", new Register(null, "X1", 1));
        var call = new Instruction(0, OpCode.Call, getter, returned);
        var load = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(returned, addend: 0x10));
        var method = new InjectedMethodAnalysisContext(
            owner,
            "ReadLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([call, load, new Instruction(2, OpCode.Return)]);
        method.Locals = [returned, loaded];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(returned.Type, Is.SameAs(owner));
            Assert.That(load.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)load.Operands[1]).Field, Is.SameAs(field));
            Assert.That(loaded.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后置字段链必须跨多轮收敛到末端值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var inner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateInner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var valueField = new InjectedFieldAnalysisContext(
            "Value",
            appContext.SystemTypes.SystemInt32Type,
            FieldAttributes.Public,
            inner,
            0x18);
        inner.Fields.Add(valueField);
        var outer = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateOuter",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var innerField = new InjectedFieldAnalysisContext(
            "Inner",
            inner,
            FieldAttributes.Public,
            outer,
            0x10);
        outer.Fields.Add(innerField);
        var getter = new InjectedMethodAnalysisContext(
            outer,
            "GetOuter",
            outer,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var innerValue = new LocalVariable("innerValue", new Register(null, "X1", 1));
        var value = new LocalVariable("value", new Register(null, "W2", 1));
        var firstLoad = new Instruction(1, OpCode.Move, innerValue, new MemoryOperand(returned, addend: 0x10));
        var secondLoad = new Instruction(2, OpCode.Move, value, new MemoryOperand(innerValue, addend: 0x18));
        var method = new InjectedMethodAnalysisContext(
            outer,
            "ReadNestedLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, getter, returned),
            firstLoad,
            secondLoad,
            new Instruction(3, OpCode.Return),
        ]);
        method.Locals = [returned, innerValue, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(firstLoad.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(secondLoad.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(innerValue.Type, Is.SameAs(inner));
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 后置调用后的未知字段偏移必须保持未解析()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateUnknownOffsetOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        owner.Fields.Add(new InjectedFieldAnalysisContext(
            "Known",
            appContext.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            owner,
            0x10));
        var getter = new InjectedMethodAnalysisContext(
            owner,
            "GetOwner",
            owner,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var loaded = new LocalVariable("loaded", new Register(null, "X1", 1));
        var unresolved = new MemoryOperand(returned, addend: 0x28);
        var load = new Instruction(1, OpCode.Move, loaded, unresolved);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "ReadUnknownLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, getter, returned),
            load,
            new Instruction(2, OpCode.Return),
        ]);
        method.Locals = [returned, loaded];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(load.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(loaded.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析接口调用覆盖不兼容的共享泛型接收者类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var pollutedDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var pollutedType = pollutedDefinition.MakeGenericInstanceType(pollutedDefinition.GenericParameters);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X20", 14),
            pollutedType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(receiver.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析接口接收者类型沿唯一复制链传播到长期局部()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var pollutedDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var pollutedType = pollutedDefinition.MakeGenericInstanceType(pollutedDefinition.GenericParameters);
        var longLived = new LocalVariable(
            "longLived",
            new Register(null, "X20", 14),
            pollutedType);
        var callReceiver = new LocalVariable(
            "callReceiver",
            new Register(null, "X0", 47),
            pollutedType);
        var copy = new Instruction(0, OpCode.Move, callReceiver, longLived);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [callReceiver] = copy,
        };

        var changed = LocalVariables.BindResolvedInstanceReceiverCopySources(
            callReceiver,
            moveNext,
            definitions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(callReceiver.Type, Is.SameAs(enumerator));
            Assert.That(longLived.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已闭合List调用目标闭合同定义开放接收者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var stringType = appContext.SystemTypes.SystemStringType;
        var closedTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [stringType],
            []);
        var openReceiverType = listDefinition.MakeGenericInstanceType([genericElement]);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openReceiverType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, closedTarget);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, closedTarget.DeclaringType),
                Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 开放接口接收者跨继承接口调用时保持原声明身份()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listInterfaceDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.IList`1")!;
        var collectionInterfaceDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.ICollection`1")!;
        var openListInterface = listInterfaceDefinition.MakeGenericInstanceType(
            listInterfaceDefinition.GenericParameters);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openListInterface);
        var countGetter = collectionInterfaceDefinition.Methods.First(method => method.Name == "get_Count");

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, countGetter);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(openListInterface));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已是目标接口的接收者保持精确类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X20", 14),
            enumerator);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非接口调用不得覆盖已有接收者类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var toString = systemObject.Methods.First(method => method.Name == "ToString");
        var existingType = appContext.SystemTypes.SystemStringType;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            existingType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, toString);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(existingType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不兼容但封闭的接口类型不得被覆盖()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var existingType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IConvertible")!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            existingType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(existingType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 后置类型收敛必须按具体实参闭合开放List调用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [genericElement],
            []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openTarget.DeclaringType);
        var value = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, value);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "CloseLateListCall",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(
                rebound.TypeGenericParameters,
                Is.EqualTo(new[] { appContext.SystemTypes.SystemStringType }));
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, rebound.DeclaringType),
                Is.True);
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后置类型收敛必须优先保持封闭List接收者实参()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [genericElement],
            []);
        var objectType = appContext.SystemTypes.SystemObjectType;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            listDefinition.MakeGenericInstanceType([objectType]));
        var value = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, value);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "PreserveClosedListReceiver",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { objectType }));
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, rebound.DeclaringType),
                Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 后置类型收敛遇到冲突实参必须保持开放调用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var pair = new InjectedMethodAnalysisContext(
            listDefinition,
            "Pair",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement, genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(pair, [genericElement], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openTarget.DeclaringType);
        var first = new LocalVariable(
            "first",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var second = new LocalVariable(
            "second",
            new Register(null, "X2", 1),
            appContext.SystemTypes.SystemInt32Type);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, first, second);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "RejectConflictingLateArguments",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, first, second];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.Operands[0], Is.SameAs(openTarget));
            Assert.That(first.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
            Assert.That(second.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 封闭调用参数必须替换局部的开放泛型占位符()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            listDefinition.GenericParameters.Single());

        var changed = LocalVariables.SetTypeFromClosedCallParameter(
            local,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(local.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 封闭调用参数不得覆盖局部已有的具体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemObjectType);

        var changed = LocalVariables.SetTypeFromClosedCallParameter(
            local,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(local.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 开放调用参数不得改写开放局部类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            genericElement);

        var changed = LocalVariables.SetTypeFromClosedCallParameter(local, genericElement);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(local.Type, Is.SameAs(genericElement));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 构造器接收者必须覆盖分配结果的错误值类型猜测()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt32Type);
        var allocation = new Instruction(0, OpCode.Newobj, receiver, listType);
        var call = new Instruction(1, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(listType, "RecoverConstructedReceiver", [allocation, call], [receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(listType));
    }

    [Test]
    [Category("边界值")]
    public void 构造器接收者沿唯一Move闭合到分配结果()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var allocated = new LocalVariable("allocated", new Register(null, "X0", 1), appContext.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 2), appContext.SystemTypes.SystemInt32Type);
        var allocation = new Instruction(0, OpCode.Newobj, allocated, listType);
        var copy = new Instruction(1, OpCode.Move, receiver, allocated);
        var call = new Instruction(2, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(
            listType,
            "RecoverCopiedConstructedReceiver",
            [allocation, copy, call],
            [allocated, receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(receiver.Type, Is.SameAs(listType));
            Assert.That(allocated.Type, Is.SameAs(listType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通实例调用不得覆盖接收者已有具体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var toString = objectType.Methods.First(method => method.Name == "ToString" && method.Parameters.Count == 0);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 1), appContext.SystemTypes.SystemStringType);
        var result = new LocalVariable("result", new Register(null, "X0", 2));
        var call = new Instruction(0, OpCode.Call, toString, result, receiver);
        var method = CreateConstructorFixture(objectType, "PreserveNormalReceiver", [call], [receiver, result]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
    }

    private static InjectedMethodAnalysisContext CreateConstructorFixture(
        TypeAnalysisContext declaringType,
        string name,
        IReadOnlyList<Instruction> body,
        IReadOnlyList<LocalVariable> locals)
    {
        var method = new InjectedMethodAnalysisContext(
            declaringType,
            name,
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            .. body,
            new Instruction(body.Count, OpCode.Return),
        ]);
        method.Locals = [.. locals];
        return method;
    }
}
