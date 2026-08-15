using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class KeyFunctionRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase("il2cpp_value_box")]
    [TestCase("il2cpp_vm_object_box")]
    [TestCase("il2cpp_codegen_object_box")]
    [Category("基本功能")]
    public void 三层装箱入口恢复为同一托管Box指令(string functionName)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var value = Local("value", app.SystemTypes.SystemBooleanType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var runtimeClass = Local("runtimeClass", app.SystemTypes.SystemIntPtrType);
        var staleArgument = Local("staleArgument", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral(functionName),
            result,
            runtimeClass,
            new AddressOf(value),
            staleArgument);
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(call.Operands, Has.Count.EqualTo(3));
            Assert.That(call.Operands[0], Is.SameAs(result));
            Assert.That(call.Operands[1], Is.SameAs(value));
            Assert.That(call.Operands[2], Is.SameAs(app.SystemTypes.SystemBooleanType));
            Assert.That(call.Operands, Does.Not.Contain(staleArgument));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 代码生成拆箱入口与唯一托管形参恢复为Unbox指令()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var resultAddress = Local("resultAddress", app.SystemTypes.SystemIntPtrType);
        var unbox = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_unbox"),
            resultAddress,
            source);
        var consumer = new Instruction(
            1,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt32Type),
            new MemoryOperand(resultAddress));
        var method = CreateMethod(unbox, consumer);

        KeyFunctionRecovery.RewriteUnboxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(unbox.OpCode, Is.EqualTo(OpCode.Unbox));
            Assert.That(unbox.Operands, Has.Count.EqualTo(3));
            Assert.That(unbox.Operands[0], Is.SameAs(resultAddress));
            Assert.That(unbox.Operands[1], Is.SameAs(source));
            Assert.That(unbox.Operands[2], Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(resultAddress.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(consumer.Operands[1], Is.SameAs(resultAddress));
        });
    }

    [Test]
    [Category("边界值")]
    public void 两个同型零偏移消费者共享同一次拆箱值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var resultAddress = Local("resultAddress", app.SystemTypes.SystemIntPtrType);
        var unbox = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_unbox"),
            resultAddress,
            source);
        var firstConsumer = new Instruction(
            1,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt32Type),
            new MemoryOperand(resultAddress));
        // 转换器会为同一 SSA 寄存器的第二个内存操作数创建独立局部对象。
        var aliasedResultAddress = new LocalVariable(
            "aliasedResultAddress",
            resultAddress.Register,
            app.SystemTypes.SystemIntPtrType);
        var secondConsumer = new Instruction(
            2,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt32Type),
            new MemoryOperand(aliasedResultAddress));
        var method = CreateMethod(unbox, firstConsumer, secondConsumer);

        KeyFunctionRecovery.RewriteUnboxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(unbox.OpCode, Is.EqualTo(OpCode.Unbox));
            Assert.That(firstConsumer.Operands[1], Is.SameAs(resultAddress));
            Assert.That(secondConsumer.Operands[1], Is.SameAs(resultAddress));
        });
    }

    [Test]
    [Category("边界值")]
    public void 拆箱地址经唯一Move载体后恢复为同一值类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var resultAddress = Local("resultAddress", app.SystemTypes.SystemIntPtrType);
        var savedAddress = Local("savedAddress", app.SystemTypes.SystemIntPtrType);
        var unbox = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_unbox"),
            resultAddress,
            source);
        var saveAddress = new Instruction(1, OpCode.Move, savedAddress, resultAddress);
        var consumer = new Instruction(
            2,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt32Type),
            new MemoryOperand(savedAddress));
        var method = CreateMethod(unbox, saveAddress, consumer);

        KeyFunctionRecovery.RewriteUnboxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(unbox.OpCode, Is.EqualTo(OpCode.Unbox));
            Assert.That(resultAddress.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(savedAddress.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(consumer.Operands[1], Is.SameAs(savedAddress));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 冲突值类型消费者保持原生拆箱调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var resultAddress = Local("resultAddress", app.SystemTypes.SystemIntPtrType);
        var unbox = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_object_unbox"),
            resultAddress,
            source);
        var firstConsumer = new Instruction(
            1,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt32Type),
            new MemoryOperand(resultAddress));
        var conflictingConsumer = new Instruction(
            2,
            OpCode.CallVoid,
            CreateStaticValueConsumer(app.SystemTypes.SystemInt64Type),
            new MemoryOperand(resultAddress));
        var method = CreateMethod(unbox, firstConsumer, conflictingConsumer);

        KeyFunctionRecovery.RewriteUnboxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(unbox.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(firstConsumer.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(conflictingConsumer.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 整数值类型地址保留精确装箱类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var value = Local("value", app.SystemTypes.SystemInt32Type);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemInt32Type,
            new AddressOf(value));
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(call.Operands[1], Is.SameAs(value));
            Assert.That(call.Operands[2], Is.SameAs(app.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非值类型地址不推测为装箱()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var value = Local("value", app.SystemTypes.SystemObjectType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemObjectType,
            new AddressOf(value));
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("基本功能")]
    public void SSA地址载体沿唯一Move定义恢复装箱值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var value = Local("value", app.SystemTypes.SystemBooleanType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var defineCarrier = new Instruction(0, OpCode.Move, carrier, new AddressOf(value));
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var method = CreateMethod(defineCarrier, call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(call.Operands[1], Is.SameAs(value));
            Assert.That(call.Operands[2], Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 循环地址载体定义保持原始调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var defineCarrier = new Instruction(0, OpCode.Move, carrier, carrier);
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var method = CreateMethod(defineCarrier, call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("异常输入")]
    public void 多定义地址载体保持原始调用且不选择任一版本()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var firstValue = Local("firstValue", app.SystemTypes.SystemBooleanType);
        var secondValue = Local("secondValue", app.SystemTypes.SystemInt32Type);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var firstDefinition = new Instruction(0, OpCode.Move, carrier, new AddressOf(firstValue));
        var secondDefinition = new Instruction(1, OpCode.Move, carrier, new AddressOf(secondValue));
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var method = CreateMethod(firstDefinition, secondDefinition, call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("边界值")]
    public void 无装箱方法允许同一局部存在多个定义()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var local = Local("local", app.SystemTypes.SystemInt32Type);
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, local, new Immediate(1)),
            new Instruction(1, OpCode.Move, local, new Immediate(2)));

        Assert.DoesNotThrow(() => KeyFunctionRecovery.RewriteBoxing(method));
    }

    [Test]
    [Category("基本功能")]
    public void ObjectIsInst键函数恢复为独立托管类型测试()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", app.SystemTypes.SystemObjectType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var testedType = app.SystemTypes.SystemStringType;
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            source,
            testedType,
            new Immediate(0xBAD));
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteTypeTests(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
            Assert.That(call.Operands, Is.EqualTo(new IOperand[] { result, source, testedType }));
            Assert.That(result.Type, Is.SameAs(testedType));
        }
    }

    [Test]
    [Category("边界值")]
    public void 运行时类局部可恢复其表示的接口类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var testedType = app.SystemTypes.SystemStringType;
        var typeHandle = Local(
            "typeHandle",
            new RuntimeClassTypeAnalysisContext(testedType, testedType.DeclaringAssembly));
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            typeHandle);
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(call.Operands[2], Is.SameAs(testedType));
    }

    [Test]
    [Category("边界值")]
    public void 取址解引用接收者保持为IsInst源操作数()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var addressed = Local("addressed", app.SystemTypes.SystemObjectType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var source = new MemoryOperand(carrier);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var testedType = app.SystemTypes.SystemStringType;
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            source,
            testedType);
        var method = CreateMethod(new Instruction(0, OpCode.Move, carrier, new AddressOf(addressed)), call);

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(call.Operands[1], Is.EqualTo(source));
    }

    [Test]
    [Category("异常输入")]
    public void 值类型目标不得恢复为引用类型IsInst()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            app.SystemTypes.SystemInt32Type);
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("异常输入")]
    public void 普通引用局部不得冒充运行时类句柄()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var call = new Instruction(
            0,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            Local("ordinary", app.SystemTypes.SystemStringType));
        var method = CreateMethod(call);

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("基本功能")]
    public void 未解析类型句柄可由IsInst结果的强类型栈槽恢复()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var testedType = app.SystemTypes.SystemStringType;
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var slot = Local("slot", testedType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            Local("source", app.SystemTypes.SystemObjectType),
            Local("unresolvedTypeHandle", null));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, carrier, new AddressOf(slot)),
            call,
            new Instruction(2, OpCode.Move, new MemoryOperand(carrier), result));

        KeyFunctionRecovery.RewriteTypeTests(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
            Assert.That(call.Operands[2], Is.SameAs(testedType));
            Assert.That(result.Type, Is.SameAs(testedType));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 多定义地址载体可由自身ByRef类型恢复IsInst目标()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var testedType = app.SystemTypes.SystemStringType;
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var carrier = Local("carrier", new ByRefTypeAnalysisContext(testedType));
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            Local("source", app.SystemTypes.SystemObjectType),
            Local("unresolvedTypeHandle", null));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, carrier,
                new AddressOf(Local("firstSlot", testedType))),
            new Instruction(1, OpCode.Move, carrier,
                new AddressOf(Local("secondSlot", testedType))),
            call,
            new Instruction(3, OpCode.Move, new MemoryOperand(carrier), result));

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(call.Operands[2], Is.SameAs(testedType));
    }

    [Test]
    [Category("边界值")]
    public void IsInst结果写入多个等价强类型栈槽仍可恢复()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var testedType = app.SystemTypes.SystemStringType;
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var firstCarrier = Local("firstCarrier", app.SystemTypes.SystemIntPtrType);
        var secondCarrier = Local("secondCarrier", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            Local("source", app.SystemTypes.SystemObjectType),
            Local("unresolvedTypeHandle", null));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, firstCarrier, new AddressOf(Local("firstSlot", testedType))),
            new Instruction(1, OpCode.Move, secondCarrier, new AddressOf(Local("secondSlot", testedType))),
            call,
            new Instruction(3, OpCode.Move, new MemoryOperand(firstCarrier), result),
            new Instruction(4, OpCode.Move, new MemoryOperand(secondCarrier), result));

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(call.Operands[2], Is.SameAs(testedType));
    }

    [Test]
    [Category("异常输入")]
    public void IsInst结果写入冲突类型栈槽时保持原始调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var firstCarrier = Local("firstCarrier", app.SystemTypes.SystemIntPtrType);
        var secondCarrier = Local("secondCarrier", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            Local("source", app.SystemTypes.SystemObjectType),
            Local("unresolvedTypeHandle", null));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, firstCarrier,
                new AddressOf(Local("firstSlot", app.SystemTypes.SystemStringType))),
            new Instruction(1, OpCode.Move, secondCarrier,
                new AddressOf(Local("secondSlot", app.SystemTypes.SystemObjectType))),
            call,
            new Instruction(3, OpCode.Move, new MemoryOperand(firstCarrier), result),
            new Instruction(4, OpCode.Move, new MemoryOperand(secondCarrier), result));

        KeyFunctionRecovery.RewriteTypeTests(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("基本功能")]
    public void 已初始化Post27类型槽恢复为托管IsInst()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong slotAddress = 0x59EE370;
        var tableBase = Local("tableBase", app.SystemTypes.SystemIntPtrType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var testedType = app.SystemTypes.SystemStringType;
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            result,
            Local("source", app.SystemTypes.SystemObjectType),
            new MemoryOperand(tableBase));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: (long)slotAddress)),
            call);

        KeyFunctionRecovery.RewriteTypeTests(
            method,
            [slotAddress],
            address => address == slotAddress ? testedType : null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
            Assert.That(call.Operands[2], Is.SameAs(testedType));
            Assert.That(result.Type, Is.SameAs(testedType));
        }
    }

    [Test]
    [Category("边界值")]
    public void 直接槽为非类型时继续解析Post27类型表项()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong slotAddress = 0x59EE370;
        var directCalls = 0;
        var tableCalls = 0;

        var resolved = KeyFunctionRecovery.ResolveMetadataTypeSlot(
            slotAddress,
            address =>
            {
                directCalls++;
                Assert.That(address, Is.EqualTo(slotAddress));
                // 直接usage已被调用方判定为非Type/TypeInfo，因此返回空类型结果。
                return null;
            },
            (address, offset) =>
            {
                tableCalls++;
                Assert.That(address, Is.EqualTo(slotAddress));
                Assert.That(offset, Is.Zero);
                return app.SystemTypes.SystemStringType;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.SameAs(app.SystemTypes.SystemStringType));
            Assert.That(directCalls, Is.EqualTo(1));
            Assert.That(tableCalls, Is.EqualTo(1));
        }
    }

    [Test]
    [Category("边界值")]
    public void Post27类型槽等价Phi合流恢复为托管IsInst()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong slotAddress = 0x59EE370;
        var firstBase = Local("firstBase", app.SystemTypes.SystemIntPtrType);
        var secondBase = Local("secondBase", app.SystemTypes.SystemIntPtrType);
        var mergedBase = Local("mergedBase", app.SystemTypes.SystemIntPtrType);
        var testedType = app.SystemTypes.SystemStringType;
        var call = new Instruction(
            3,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            new MemoryOperand(mergedBase));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, firstBase, new MemoryOperand(addend: (long)slotAddress)),
            new Instruction(1, OpCode.Move, secondBase, new MemoryOperand(addend: (long)slotAddress)),
            new Instruction(2, OpCode.Phi, mergedBase, firstBase, secondBase),
            call);

        KeyFunctionRecovery.RewriteTypeTests(
            method,
            [slotAddress],
            address => address == slotAddress ? testedType : null);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(call.Operands[2], Is.SameAs(testedType));
    }

    [Test]
    [Category("异常输入")]
    public void Post27类型槽冲突Phi合流保持原生调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong firstSlotAddress = 0x59EE370;
        const ulong secondSlotAddress = 0x59EE388;
        var firstBase = Local("firstBase", app.SystemTypes.SystemIntPtrType);
        var secondBase = Local("secondBase", app.SystemTypes.SystemIntPtrType);
        var mergedBase = Local("mergedBase", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            3,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            new MemoryOperand(mergedBase));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, firstBase, new MemoryOperand(addend: (long)firstSlotAddress)),
            new Instruction(1, OpCode.Move, secondBase, new MemoryOperand(addend: (long)secondSlotAddress)),
            new Instruction(2, OpCode.Phi, mergedBase, firstBase, secondBase),
            call);
        var resolverCalls = 0;

        KeyFunctionRecovery.RewriteTypeTests(
            method,
            [firstSlotAddress, secondSlotAddress],
            _ =>
            {
                resolverCalls++;
                return app.SystemTypes.SystemStringType;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(resolverCalls, Is.Zero);
        }
    }

    [Test]
    [Category("边界值")]
    public void Post27类型槽非零解引用偏移保持原生调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong slotAddress = 0x59EE370;
        var tableBase = Local("tableBase", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            new MemoryOperand(tableBase, addend: 8));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: (long)slotAddress)),
            call);
        var resolverCalls = 0;

        KeyFunctionRecovery.RewriteTypeTests(
            method,
            [slotAddress],
            _ =>
            {
                resolverCalls++;
                return app.SystemTypes.SystemStringType;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(resolverCalls, Is.Zero);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 未初始化Post27类型槽不得恢复为托管IsInst()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const ulong slotAddress = 0x59EE370;
        var tableBase = Local("tableBase", app.SystemTypes.SystemIntPtrType);
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_vm_object_is_inst"),
            Local("result", app.SystemTypes.SystemObjectType),
            Local("source", app.SystemTypes.SystemObjectType),
            new MemoryOperand(tableBase));
        var method = CreateMethod(
            new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: (long)slotAddress)),
            call);
        var resolverCalls = 0;

        KeyFunctionRecovery.RewriteTypeTests(
            method,
            [0x1000],
            _ =>
            {
                resolverCalls++;
                return app.SystemTypes.SystemStringType;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(resolverCalls, Is.Zero);
        }
    }

    [Test]
    [Category("基本功能")]
    public void 死代码清理保留Box数据槽的调用前写入链()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const int stackNumber = 240;
        var oldValue = new LocalVariable("oldValue", new Register(stackNumber, "stack_-24", 1));
        var writtenValue = new LocalVariable(
            "writtenValue",
            new Register(stackNumber, "stack_-24", 2),
            app.SystemTypes.SystemBooleanType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var source = Local("source", app.SystemTypes.SystemBooleanType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var takeAddress = new Instruction(0, OpCode.Move, carrier, new AddressOf(oldValue));
        var writeValue = new Instruction(1, OpCode.Move, writtenValue, source);
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var method = CreateMethod(takeAddress, writeValue, call);

        DeadCodeEliminator.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(takeAddress.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(writeValue.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 先取地址后写栈槽时选择调用前最近值版本()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const int stackNumber = 240;
        var oldValue = new LocalVariable(
            "oldValue",
            new Register(stackNumber, "stack_-24", 1));
        var writtenValue = new LocalVariable(
            "writtenValue",
            new Register(stackNumber, "stack_-24", 2),
            app.SystemTypes.SystemBooleanType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var source = Local("source", app.SystemTypes.SystemBooleanType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var defineCarrier = new Instruction(0, OpCode.Move, carrier, new AddressOf(oldValue));
        var writeValue = new Instruction(1, OpCode.Move, writtenValue, source);
        var call = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var method = CreateMethod(defineCarrier, writeValue, call);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(call.Operands[1], Is.SameAs(writtenValue));
            Assert.That(call.Operands[2], Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 装箱调用后的栈槽写入不得倒流到调用点()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        const int stackNumber = 240;
        var oldValue = new LocalVariable(
            "oldValue",
            new Register(stackNumber, "stack_-24", 1));
        var futureValue = new LocalVariable(
            "futureValue",
            new Register(stackNumber, "stack_-24", 2),
            app.SystemTypes.SystemBooleanType);
        var carrier = Local("carrier", app.SystemTypes.SystemIntPtrType);
        var source = Local("source", app.SystemTypes.SystemBooleanType);
        var result = Local("result", app.SystemTypes.SystemObjectType);
        var defineCarrier = new Instruction(0, OpCode.Move, carrier, new AddressOf(oldValue));
        var call = new Instruction(
            1,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            result,
            app.SystemTypes.SystemBooleanType,
            carrier);
        var futureWrite = new Instruction(2, OpCode.Move, futureValue, source);
        var method = CreateMethod(defineCarrier, call, futureWrite);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("基本功能")]
    public void 同一类型句柄把已证明值类型传播到未知装箱槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var firstTypeClass = Local("firstTypeClass", app.SystemTypes.SystemIntPtrType);
        var secondTypeClass = Local("secondTypeClass", app.SystemTypes.SystemIntPtrType);
        const int knownStackNumber = 240;
        var oldKnownSlot = new LocalVariable("oldKnownSlot", new Register(knownStackNumber, "stack_-24", 1));
        var knownValue = new LocalVariable(
            "knownValue",
            new Register(knownStackNumber, "stack_-24", 2),
            app.SystemTypes.SystemBooleanType);
        var knownSource = Local("knownSource", app.SystemTypes.SystemBooleanType);
        var unknownValue = Local("unknownValue", null);
        var firstResult = Local("firstResult", app.SystemTypes.SystemObjectType);
        var secondResult = Local("secondResult", app.SystemTypes.SystemObjectType);
        var firstTypeLoad = new Instruction(0, OpCode.Move, firstTypeClass, new MemoryOperand(addend: 0x1000));
        var secondTypeLoad = new Instruction(1, OpCode.Move, secondTypeClass, new MemoryOperand(addend: 0x1000));
        var knownWrite = new Instruction(2, OpCode.Move, knownValue, knownSource);
        var firstCall = new Instruction(
            3,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            firstResult,
            new MemoryOperand(firstTypeClass, addend: 0x28),
            new AddressOf(oldKnownSlot));
        var secondCall = new Instruction(
            4,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            secondResult,
            new MemoryOperand(secondTypeClass, addend: 0x28),
            new AddressOf(unknownValue));
        var method = CreateMethod(firstTypeLoad, secondTypeLoad, knownWrite, firstCall, secondCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(firstCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(secondCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(unknownValue.Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
            Assert.That(secondCall.Operands[2], Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一类型句柄出现互斥证明时未知装箱槽保持原始调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var typeClass = Local("typeClass", app.SystemTypes.SystemIntPtrType);
        var booleanValue = Local("booleanValue", app.SystemTypes.SystemBooleanType);
        var integerValue = Local("integerValue", app.SystemTypes.SystemInt32Type);
        var unknownValue = Local("unknownValue", null);
        var typeLoad = new Instruction(0, OpCode.Move, typeClass, new MemoryOperand(addend: 0x2000));
        var booleanCall = BoxCall(1, "booleanResult", typeClass, booleanValue, app);
        var integerCall = BoxCall(2, "integerResult", typeClass, integerValue, app);
        var unknownCall = BoxCall(3, "unknownResult", typeClass, unknownValue, app);
        var method = CreateMethod(typeLoad, booleanCall, integerCall, unknownCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(booleanCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(integerCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(unknownCall.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(unknownValue.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不同类型句柄不得向未知装箱槽串播类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var provenTypeClass = Local("provenTypeClass", app.SystemTypes.SystemIntPtrType);
        var unknownTypeClass = Local("unknownTypeClass", app.SystemTypes.SystemIntPtrType);
        var knownValue = Local("knownValue", app.SystemTypes.SystemBooleanType);
        var unknownValue = Local("unknownValue", null);
        var firstTypeLoad = new Instruction(0, OpCode.Move, provenTypeClass, new MemoryOperand(addend: 0x3000));
        var secondTypeLoad = new Instruction(1, OpCode.Move, unknownTypeClass, new MemoryOperand(addend: 0x4000));
        var knownCall = BoxCall(2, "knownResult", provenTypeClass, knownValue, app);
        var unknownCall = BoxCall(3, "unknownResult", unknownTypeClass, unknownValue, app);
        var method = CreateMethod(firstTypeLoad, secondTypeLoad, knownCall, unknownCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(knownCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(unknownCall.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(unknownValue.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void ARM64取址定义清理后从紧邻X1栈写恢复装箱值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var typeClass = Local("typeClass", app.SystemTypes.SystemIntPtrType);
        var knownValue = Local("knownValue", app.SystemTypes.SystemBooleanType);
        var opaqueX1 = new LocalVariable("opaqueX1", new Register(1, "X1"));
        var stackValue = new LocalVariable("stackValue", new Register(240, "stack_-3C", 1));
        var typeLoad = new Instruction(0, OpCode.Move, typeClass, new MemoryOperand(addend: 0x5000));
        var knownCall = BoxCall(1, "knownResult", typeClass, knownValue, app);
        var stackWrite = new Instruction(2, OpCode.Move, stackValue, new Immediate(0));
        var removedAddressDefinition = new Instruction(3, OpCode.Nop);
        var finalCall = new Instruction(
            4,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            Local("finalResult", app.SystemTypes.SystemObjectType),
            new MemoryOperand(typeClass, addend: 0x28),
            opaqueX1);
        var method = CreateMethod(typeLoad, knownCall, stackWrite, removedAddressDefinition, finalCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.Multiple(() =>
        {
            Assert.That(finalCall.OpCode, Is.EqualTo(OpCode.Box));
            Assert.That(finalCall.Operands[1], Is.SameAs(stackValue));
            Assert.That(stackValue.Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void X1栈写与装箱之间存在调用时禁止跨调用取值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var typeClass = Local("typeClass", app.SystemTypes.SystemIntPtrType);
        var knownValue = Local("knownValue", app.SystemTypes.SystemBooleanType);
        var opaqueX1 = new LocalVariable("opaqueX1", new Register(1, "X1"));
        var stackValue = new LocalVariable("stackValue", new Register(240, "stack_-40", 1));
        var typeLoad = new Instruction(0, OpCode.Move, typeClass, new MemoryOperand(addend: 0x6000));
        var knownCall = BoxCall(1, "knownResult", typeClass, knownValue, app);
        var stackWrite = new Instruction(2, OpCode.Move, stackValue, new Immediate(0));
        var interveningCall = new Instruction(3, OpCode.CallVoid, new StringLiteral("intervening"));
        var finalCall = new Instruction(
            4,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            Local("finalResult", app.SystemTypes.SystemObjectType),
            new MemoryOperand(typeClass, addend: 0x28),
            opaqueX1);
        var method = CreateMethod(typeLoad, knownCall, stackWrite, interveningCall, finalCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(finalCall.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("异常输入")]
    public void 非X1未知载体不得绑定临近栈写()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var typeClass = Local("typeClass", app.SystemTypes.SystemIntPtrType);
        var knownValue = Local("knownValue", app.SystemTypes.SystemBooleanType);
        var opaqueX2 = new LocalVariable("opaqueX2", new Register(2, "X2"));
        var stackValue = new LocalVariable("stackValue", new Register(240, "stack_-44", 1));
        var typeLoad = new Instruction(0, OpCode.Move, typeClass, new MemoryOperand(addend: 0x7000));
        var knownCall = BoxCall(1, "knownResult", typeClass, knownValue, app);
        var stackWrite = new Instruction(2, OpCode.Move, stackValue, new Immediate(0));
        var finalCall = new Instruction(
            3,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            Local("finalResult", app.SystemTypes.SystemObjectType),
            new MemoryOperand(typeClass, addend: 0x28),
            opaqueX2);
        var method = CreateMethod(typeLoad, knownCall, stackWrite, finalCall);

        KeyFunctionRecovery.RewriteBoxing(method);

        Assert.That(finalCall.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("基本功能")]
    public void 两前驱地址Phi转换为值Phi并由SSA销毁为边复制()
    {
        var fixture = CreateAddressPhiFixture(AddressPhiFixtureMode.Valid);

        KeyFunctionRecovery.RewriteBoxing(fixture.Method);

        Assert.That(fixture.FinalCall.OpCode, Is.EqualTo(OpCode.Box));
        var mergedValue = (LocalVariable)fixture.FinalCall.Operands[1];
        var valuePhi = fixture.Join.Instructions.Single(instruction =>
            instruction.OpCode == OpCode.Phi
            && ReferenceEquals(instruction.Destination, mergedValue));
        Assert.That(valuePhi.Operands.Skip(1), Is.EqualTo(new[] { fixture.FirstStackValue, fixture.SecondStackValue }));

        SsaForm.Remove(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Method.ControlFlowGraph!.Instructions.Any(instruction => instruction.OpCode == OpCode.Phi), Is.False);
            Assert.That(fixture.FirstPredecessor.Instructions.Any(instruction =>
                instruction.OpCode == OpCode.Move
                && ReferenceEquals(instruction.Destination, mergedValue)
                && ReferenceEquals(instruction.Operands[1], fixture.FirstStackValue)), Is.True);
            Assert.That(fixture.SecondPredecessor.Instructions.Any(instruction =>
                instruction.OpCode == OpCode.Move
                && ReferenceEquals(instruction.Destination, mergedValue)
                && ReferenceEquals(instruction.Operands[1], fixture.SecondStackValue)), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 地址Phi输入数与前驱数不一致时保持原始调用()
    {
        var fixture = CreateAddressPhiFixture(AddressPhiFixtureMode.MissingInput);

        KeyFunctionRecovery.RewriteBoxing(fixture.Method);

        Assert.That(fixture.FinalCall.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("异常输入")]
    public void 地址Phi任一来源缺少取址证明时保持原始调用()
    {
        var fixture = CreateAddressPhiFixture(AddressPhiFixtureMode.UnresolvedInput);

        KeyFunctionRecovery.RewriteBoxing(fixture.Method);

        Assert.That(fixture.FinalCall.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("异常输入")]
    public void 类型句柄Phi来源不一致时保持原始调用()
    {
        var fixture = CreateAddressPhiFixture(AddressPhiFixtureMode.ConflictingTypeHandle);

        KeyFunctionRecovery.RewriteBoxing(fixture.Method);

        Assert.That(fixture.FinalCall.OpCode, Is.EqualTo(OpCode.Call));
    }

    private static AddressPhiFixture CreateAddressPhiFixture(AddressPhiFixtureMode mode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var booleanType = app.SystemTypes.SystemBooleanType;
        var knownValue = Local("knownValue", booleanType);
        var knownResult = Local("knownResult", app.SystemTypes.SystemObjectType);
        var firstTypeClass = Local("firstTypeClass", app.SystemTypes.SystemIntPtrType);
        var secondTypeClass = Local("secondTypeClass", app.SystemTypes.SystemIntPtrType);
        var mergedTypeClass = Local("mergedTypeClass", app.SystemTypes.SystemIntPtrType);
        var firstSource = Local("firstSource", booleanType);
        var firstStackValue = new LocalVariable("firstStackValue", new Register(240, "stack_-50", 1));
        var secondStackValue = new LocalVariable("secondStackValue", new Register(241, "stack_-54", 1));
        var firstCarrier = new LocalVariable("firstCarrier", new Register(1, "X1", 1));
        var secondCarrier = new LocalVariable("secondCarrier", new Register(1, "X1", 2));
        var mergedCarrier = new LocalVariable("mergedCarrier", new Register(1, "X1", 3));
        var unresolvedCarrier = new LocalVariable("unresolvedCarrier", new Register(1, "X1", 4));

        var firstTypeLoad = new Instruction(0, OpCode.Move, firstTypeClass, new MemoryOperand(addend: 0x8000));
        var secondTypeLoad = new Instruction(
            1,
            OpCode.Move,
            secondTypeClass,
            new MemoryOperand(addend: mode == AddressPhiFixtureMode.ConflictingTypeHandle ? 0x9000 : 0x8000));
        var knownCall = new Instruction(
            2,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            knownResult,
            firstTypeClass,
            new AddressOf(knownValue));
        var firstStackWrite = new Instruction(3, OpCode.Move, firstStackValue, firstSource);
        var firstAddress = new Instruction(4, OpCode.Move, firstCarrier, new AddressOf(firstStackValue));
        var secondStackWrite = new Instruction(5, OpCode.Move, secondStackValue, new Immediate(0));
        var secondAddress = mode == AddressPhiFixtureMode.UnresolvedInput
            ? new Instruction(6, OpCode.Move, secondCarrier, unresolvedCarrier)
            : new Instruction(6, OpCode.Move, secondCarrier, new AddressOf(secondStackValue));
        var phiOperands = mode == AddressPhiFixtureMode.MissingInput
            ? new List<IOperand> { mergedCarrier, firstCarrier }
            : new List<IOperand> { mergedCarrier, firstCarrier, secondCarrier };
        var addressPhi = new Instruction(7, OpCode.Phi, phiOperands);
        var typePhi = new Instruction(8, OpCode.Phi, mergedTypeClass, firstTypeClass, secondTypeClass);
        var finalCall = new Instruction(
            9,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            Local("finalResult", app.SystemTypes.SystemObjectType),
            mergedTypeClass,
            mergedCarrier);
        var returnInstruction = new Instruction(10, OpCode.Return);

        var graph = new ISILControlFlowGraph([new Instruction(100, OpCode.Return)]);
        foreach (var block in graph.Blocks)
        {
            block.Predecessors.Clear();
            block.Successors.Clear();
            block.Instructions.Clear();
        }

        var firstPredecessor = new Block { ID = 2, Instructions = [firstTypeLoad, knownCall, firstStackWrite, firstAddress] };
        var secondPredecessor = new Block { ID = 3, Instructions = [secondTypeLoad, secondStackWrite, secondAddress] };
        var join = new Block { ID = 4, Instructions = [addressPhi, typePhi, finalCall, returnInstruction] };
        graph.EntryBlock.Successors.AddRange([firstPredecessor, secondPredecessor]);
        firstPredecessor.Predecessors.Add(graph.EntryBlock);
        secondPredecessor.Predecessors.Add(graph.EntryBlock);
        firstPredecessor.Successors.Add(join);
        secondPredecessor.Successors.Add(join);
        join.Predecessors.AddRange([firstPredecessor, secondPredecessor]);
        join.Successors.Add(graph.ExitBlock);
        graph.ExitBlock.Predecessors.Add(join);
        graph.Blocks = [graph.EntryBlock, firstPredecessor, secondPredecessor, join, graph.ExitBlock];

        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new AddressPhiFixture(
            method,
            finalCall,
            firstPredecessor,
            secondPredecessor,
            join,
            firstStackValue,
            secondStackValue);
    }

    private enum AddressPhiFixtureMode
    {
        Valid,
        MissingInput,
        UnresolvedInput,
        ConflictingTypeHandle,
    }

    private sealed record AddressPhiFixture(
        MethodAnalysisContext Method,
        Instruction FinalCall,
        Block FirstPredecessor,
        Block SecondPredecessor,
        Block Join,
        LocalVariable FirstStackValue,
        LocalVariable SecondStackValue);

    private static Instruction BoxCall(
        int index,
        string resultName,
        LocalVariable typeClass,
        LocalVariable value,
        ApplicationAnalysisContext app)
        => new(
            index,
            OpCode.Call,
            new StringLiteral("il2cpp_codegen_object_box"),
            Local(resultName, app.SystemTypes.SystemObjectType),
            new MemoryOperand(typeClass, addend: 0x28),
            new AddressOf(value));

    private static InjectedMethodAnalysisContext CreateStaticValueConsumer(TypeAnalysisContext parameterType)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        return new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "ConsumeValue",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [parameterType]);
    }

    private static MethodAnalysisContext CreateMethod(params Instruction[] instructions)
    {
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            .. instructions,
            new Instruction(instructions.Length, OpCode.Return),
        ]);
        return method;
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type)
        => new(name, new Register(null, name), type);
}
