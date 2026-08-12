using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

public class InterfaceDispatchRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void EntryOffsetPlusSlotIsRecovered()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(2)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slots, Is.EquivalentTo(new[] { 2 }));
        });
    }

    [Test]
    [Category("边界值")]
    public void MaximumUnsignedSlotIsAccepted()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(ushort.MaxValue)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slots, Is.EquivalentTo(new[] { (int)ushort.MaxValue }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void NegativeSlotIsRejectedWithoutMutation()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(-1)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.False);
            Assert.That(slots, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 共享地址污染后的零偏移字段仍识别为对象类指针()
    {
        var receiver = Local("receiver");
        var klass = Local("klass");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            // 字段实例仅承载零偏移形状；接口恢复不读取字段元数据本身。
            [klass] = new Instruction(0, OpCode.Move, klass, new FieldReference(null!, receiver, 0)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(InterfaceDispatchRecovery.IsObjectHeaderClassLoad(new FieldReference(null!, receiver, 0)), Is.True);
            Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, klass, []), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 双分支对象类指针Phi要求每路均为零偏移读取()
    {
        var receiverA = Local("receiverA");
        var receiverB = Local("receiverB");
        var klassA = Local("klassA");
        var klassB = Local("klassB");
        var merged = Local("merged");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [klassA] = new Instruction(0, OpCode.Move, klassA, new MemoryOperand(receiverA)),
            [klassB] = new Instruction(1, OpCode.Move, klassB, new FieldReference(null!, receiverB, 0)),
            [merged] = new Instruction(2, OpCode.Phi, merged, klassA, klassB),
        };

        Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, merged, []), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 非零偏移字段不冒充对象类指针()
    {
        var receiver = Local("receiver");
        var klass = Local("klass");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [klass] = new Instruction(0, OpCode.Move, klass, new FieldReference(null!, receiver, 1)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(InterfaceDispatchRecovery.IsObjectHeaderClassLoad(new FieldReference(null!, receiver, 1)), Is.False);
            Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, klass, []), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 共享地址慢路径的类型信息与槽位实参可被识别()
    {
        var interfaceDefinition = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            interfaceDefinition,
            interfaceDefinition.DeclaringAssembly);
        var interfaceTypeInfo = new LocalVariable("interfaceTypeInfo", new Register(null, "X1"), runtimeClassType);
        var slot = Local("slot");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [slot] = new Instruction(0, OpCode.Move, slot, new Immediate(1)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(InterfaceDispatchRecovery.IsRuntimeClassOperand(definitions, interfaceTypeInfo), Is.True);
            Assert.That(InterfaceDispatchRecovery.ResolveConstant(definitions, slot), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零号接口槽位保持有效()
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();

        Assert.That(
            InterfaceDispatchRecovery.ResolveConstant(definitions, new Immediate(0)),
            Is.Zero);
    }

    [Test]
    [Category("异常输入")]
    public void 普通业务对象不冒充接口类型信息()
    {
        var ordinary = Local("ordinary");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [ordinary] = new Instruction(0, OpCode.Move, ordinary, new Immediate(7)),
        };

        Assert.That(InterfaceDispatchRecovery.IsRuntimeClassOperand(definitions, ordinary), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 值类型枚举器地址可恢复为接口接收者()
    {
        var enumerator = Local("enumerator");

        var matched = InterfaceDispatchRecovery.TryResolveReceiverOperand(
            new AddressOf(enumerator),
            out var receiver);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(receiver, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零偏移地址解引用可恢复为接口接收者()
    {
        var enumeratorAddress = Local("enumeratorAddress");

        var matched = InterfaceDispatchRecovery.TryResolveReceiverOperand(
            new MemoryOperand(enumeratorAddress),
            out var receiver);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(receiver, Is.SameAs(enumeratorAddress));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 带偏移的原生地址不冒充接口接收者()
    {
        var nativeAddress = Local("nativeAddress");

        var matched = InterfaceDispatchRecovery.TryResolveReceiverOperand(
            new MemoryOperand(nativeAddress, addend: 8),
            out _);

        Assert.That(matched, Is.False);
    }

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
