using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
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
    [Category("基本功能")]
    public void 未写入W2时MethodInfo载体保留到快速路径交叉验证()
    {
        var carrier = new LocalVariable("methodInfoCarrier", new Register(null, "X2"))
        {
            IsMethodInfo = true,
        };

        var matched = InterfaceDispatchRecovery.TryResolveSlowPathSlotOperand([], carrier, out var slot);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slot, Is.SameAs(carrier));
        });
    }

    [Test]
    [Category("基本功能")]
    public void MethodInfo载体与唯一非零快速槽位闭合为同一接口方法()
    {
        var carrier = new LocalVariable("methodInfoCarrier", new Register(null, "X2"))
        {
            IsMethodInfo = true,
        };

        var matched = InterfaceDispatchRecovery.TryResolveConsistentDirectSlot(
            [],
            carrier,
            new HashSet<int> { 1 },
            out var slot);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slot, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 显式零号慢槽位只接受零号快速路径()
    {
        var matched = InterfaceDispatchRecovery.TryResolveConsistentDirectSlot(
            [],
            new Immediate(0),
            new HashSet<int> { 0 },
            out var slot);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slot, Is.Zero);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 显式慢槽位与快速路径冲突时保持未解析()
    {
        Assert.That(
            InterfaceDispatchRecovery.TryResolveConsistentDirectSlot(
                [],
                new Immediate(0),
                new HashSet<int> { 1 },
                out _),
            Is.False);
    }

    [Test]
    [Category("边界值")]
    public void 接口慢查表接受显式最大槽位()
    {
        var matched = InterfaceDispatchRecovery.TryResolveSlowPathSlotOperand(
            [],
            new Immediate(ushort.MaxValue),
            out var slot);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(((Immediate)slot).Value, Is.EqualTo(ushort.MaxValue));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未类型化第三寄存器不冒充省略的零号槽位()
    {
        Assert.That(
            InterfaceDispatchRecovery.TryResolveSlowPathSlotOperand([], Local("untypedX2"), out _),
            Is.False);
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
    public void 同一零偏移槽的直接内存实参恢复接口类型信息()
    {
        var interfaceDefinition = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            interfaceDefinition,
            interfaceDefinition.DeclaringAssembly);
        var slot = Local("slot");
        var loadedType = new LocalVariable("loadedType", new Register(null, "X1"), runtimeClassType);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [loadedType] = new Instruction(0, OpCode.Move, loadedType, new MemoryOperand(slot)),
        };

        Assert.That(
            InterfaceDispatchRecovery.IsRuntimeClassOperand(definitions, new MemoryOperand(slot)),
            Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void 同源未类型化实参局部恢复接口类型信息()
    {
        var interfaceDefinition = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            interfaceDefinition,
            interfaceDefinition.DeclaringAssembly);
        var slot = Local("slot");
        var typedLoad = new LocalVariable("typedLoad", new Register(null, "X1"), runtimeClassType);
        var argumentLoad = Local("argumentLoad");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [typedLoad] = new Instruction(0, OpCode.Move, typedLoad, new MemoryOperand(slot)),
            [argumentLoad] = new Instruction(1, OpCode.Move, argumentLoad, new MemoryOperand(slot)),
        };

        Assert.That(
            InterfaceDispatchRecovery.IsRuntimeClassOperand(definitions, argumentLoad),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 同一最大正偏移槽仍恢复接口类型信息()
    {
        var interfaceDefinition = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            interfaceDefinition,
            interfaceDefinition.DeclaringAssembly);
        var slot = Local("slot");
        var loadedType = new LocalVariable("loadedType", new Register(null, "X1"), runtimeClassType);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [loadedType] = new Instruction(0, OpCode.Move, loadedType, new MemoryOperand(slot, addend: int.MaxValue)),
        };

        Assert.That(
            InterfaceDispatchRecovery.IsRuntimeClassOperand(
                definitions,
                new MemoryOperand(slot, addend: int.MaxValue)),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 不同偏移内存槽不复用接口类型身份()
    {
        var interfaceDefinition = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            interfaceDefinition,
            interfaceDefinition.DeclaringAssembly);
        var slot = Local("slot");
        var loadedType = new LocalVariable("loadedType", new Register(null, "X1"), runtimeClassType);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [loadedType] = new Instruction(0, OpCode.Move, loadedType, new MemoryOperand(slot)),
        };

        Assert.That(
            InterfaceDispatchRecovery.IsRuntimeClassOperand(definitions, new MemoryOperand(slot, addend: 8)),
            Is.False);
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
    [Category("基本功能")]
    public void 慢查表接收者经纯Move仍回溯到接口类型()
    {
        var disposable = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var source = new LocalVariable("source", new Register(null, "X0"), disposable);
        var copied = Local("copied");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [copied] = new Instruction(0, OpCode.Move, copied, source),
        };

        var matched = InterfaceDispatchRecovery.TryResolveTypedInterfaceReceiver(
            definitions,
            copied,
            out var resolved);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(resolved, Is.SameAs(source));
        });
    }

    [Test]
    [Category("边界值")]
    public void 慢查表接收者多层纯Move保持接口类型()
    {
        var disposable = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var source = new LocalVariable("source", new Register(null, "X0"), disposable);
        var first = Local("first");
        var second = Local("second");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [first] = new Instruction(0, OpCode.Move, first, source),
            [second] = new Instruction(1, OpCode.Move, second, first),
        };

        Assert.That(
            InterfaceDispatchRecovery.TryResolveTypedInterfaceReceiver(definitions, second, out var resolved)
            && ReferenceEquals(resolved, source),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 内存读取接收者不跨边界推断接口类型()
    {
        var source = Local("source");
        var loaded = Local("loaded");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [loaded] = new Instruction(0, OpCode.Move, loaded, new MemoryOperand(source)),
        };

        Assert.That(
            InterfaceDispatchRecovery.TryResolveTypedInterfaceReceiver(definitions, loaded, out _),
            Is.False);
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

    [Test]
    [Category("基本功能")]
    public void 共享AddWithResize地址的接口慢查表返回槽延迟绑定()
    {
        var callResult = Local("callResult");
        var fastResult = Local("fastResult");
        var invokeData = Local("invokeData");
        var methodPointer = Local("methodPointer");
        var methodInfo = Local("methodInfo");
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x219B070),
            callResult,
            Local("receiver"),
            Local("interfaceType"),
            new Immediate(0));
        var phi = new Instruction(1, OpCode.Phi, invokeData, callResult, fastResult);
        var load = new Instruction(2, OpCode.Move, methodPointer, new MemoryOperand(invokeData));
        var loadMethodInfo = new Instruction(3, OpCode.Move, methodInfo, new MemoryOperand(invokeData, addend: 8));
        var dispatch = new Instruction(4, OpCode.IndirectCall, new MemoryOperand(invokeData), Local("result"));

        var deferred = InterfaceDispatchRecovery.ShouldDeferSharedAddWithResizeBinding(
            call,
            CreateAddWithResizeCandidate(),
            [call, phi, load, loadMethodInfo, dispatch]);

        Assert.Multiple(() =>
        {
            Assert.That(deferred, Is.True);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(call.Destination, Is.SameAs(callResult));
        });
    }

    [Test]
    [Category("边界值")]
    public void 多层边复制与零偏移字段仍保留接口慢查表返回槽()
    {
        var callResult = Local("callResult");
        var aliasA = Local("aliasA");
        var aliasB = Local("aliasB");
        var fastResult = Local("fastResult");
        var invokeData = Local("invokeData");
        var methodPointer = Local("methodPointer");
        var methodInfo = Local("methodInfo");
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x219B070),
            callResult,
            Local("receiver"),
            Local("interfaceType"),
            new Immediate(ushort.MaxValue));
        var copyA = new Instruction(1, OpCode.Move, aliasA, callResult);
        var copyB = new Instruction(2, OpCode.Move, aliasB, aliasA);
        var phi = new Instruction(3, OpCode.Phi, invokeData, aliasB, fastResult);
        var load = new Instruction(4, OpCode.Move, methodPointer, new FieldReference(null!, invokeData, 0));
        var loadMethodInfo = new Instruction(5, OpCode.Move, methodInfo, new MemoryOperand(invokeData, addend: 8));
        var dispatch = new Instruction(6, OpCode.IndirectJump, new MemoryOperand(invokeData), Local("result"));

        Assert.That(
            InterfaceDispatchRecovery.ShouldDeferSharedAddWithResizeBinding(
                call,
                CreateAddWithResizeCandidate(),
                [call, copyA, copyB, phi, load, loadMethodInfo, dispatch]),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 非零偏移消费者不延迟真实AddWithResize绑定()
    {
        var callResult = Local("callResult");
        var fastResult = Local("fastResult");
        var invokeData = Local("invokeData");
        var loaded = Local("loaded");
        var methodInfo = Local("methodInfo");
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x219B070),
            callResult,
            Local("receiver"),
            Local("interfaceType"),
            new Immediate(0));
        var phi = new Instruction(1, OpCode.Phi, invokeData, callResult, fastResult);
        var load = new Instruction(2, OpCode.Move, loaded, new MemoryOperand(invokeData, addend: 8));
        var loadMethodInfo = new Instruction(3, OpCode.Move, methodInfo, new MemoryOperand(invokeData, addend: 8));
        var dispatch = new Instruction(4, OpCode.IndirectCall, new MemoryOperand(invokeData), Local("result"));

        Assert.That(
            InterfaceDispatchRecovery.ShouldDeferSharedAddWithResizeBinding(
                call,
                CreateAddWithResizeCandidate(),
                [call, phi, load, loadMethodInfo, dispatch]),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 共享慢查表与快速地址复用同一返回槽时解析Dispose()
    {
        var shape = CreateSharedInvokeDataShape(includeMethodInfo: true, slowSlot: 0);

        var resolved = InterfaceDispatchRecovery.ResolveSharedInvokeDataTarget(
            shape.Dispatch,
            shape.Instructions);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Name, Is.EqualTo("Dispose"));
            Assert.That(resolved.DeclaringType!.FullName, Is.EqualTo("System.IDisposable"));
        });
    }

    [Test]
    [Category("边界值")]
    public void MethodInfo经局部加载仍可闭合共享无Phi接口查表()
    {
        var shape = CreateSharedInvokeDataShape(
            includeMethodInfo: true,
            slowSlot: 0,
            indirectMethodInfo: true,
            separateConsumerBase: true);

        var resolved = InterfaceDispatchRecovery.ResolveSharedInvokeDataTarget(
            shape.Dispatch,
            shape.Instructions);

        Assert.That(resolved?.Name, Is.EqualTo("Dispose"));
    }

    [Test]
    [Category("异常输入")]
    public void 缺失MethodInfo消费者或槽位冲突时拒绝共享无Phi接口查表()
    {
        var missingMethodInfo = CreateSharedInvokeDataShape(includeMethodInfo: false, slowSlot: 0);
        var conflictingSlot = CreateSharedInvokeDataShape(includeMethodInfo: true, slowSlot: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                InterfaceDispatchRecovery.ResolveSharedInvokeDataTarget(
                    missingMethodInfo.Dispatch,
                    missingMethodInfo.Instructions),
                Is.Null);
            Assert.That(
                InterfaceDispatchRecovery.ResolveSharedInvokeDataTarget(
                    conflictingSlot.Dispatch,
                    conflictingSlot.Instructions),
                Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已消费的MethodInfo局部载入被精确删除()
    {
        var invokeData = Local("invokeData");
        var methodInfo = Local("methodInfo");
        var load = new Instruction(0, OpCode.Move, methodInfo, new MemoryOperand(invokeData, addend: 8));
        var definitions = new Dictionary<LocalVariable, Instruction> { [methodInfo] = load };

        var changed = InterfaceDispatchRecovery.SuppressConsumedMethodInfoLoad(
            methodInfo,
            definitions,
            invokeData);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(load.Operands, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 同寄存器不同局部对象的MethodInfo载入仍被删除()
    {
        var producerBase = new LocalVariable("producerBase", new Register(null, "X8"));
        var consumerBase = new LocalVariable("consumerBase", new Register(null, "X8"));
        var methodInfo = Local("methodInfo");
        var load = new Instruction(0, OpCode.Move, methodInfo, new MemoryOperand(producerBase, addend: 8));
        var definitions = new Dictionary<LocalVariable, Instruction> { [methodInfo] = load };

        var changed = InterfaceDispatchRecovery.SuppressConsumedMethodInfoLoad(
            methodInfo,
            definitions,
            consumerBase);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非MethodInfo载入保持原指令不变()
    {
        var invokeData = Local("invokeData");
        var unrelatedBase = Local("unrelatedBase");
        var value = Local("value");
        var wrongBaseLoad = new Instruction(0, OpCode.Move, value, new MemoryOperand(unrelatedBase, addend: 8));
        var definitions = new Dictionary<LocalVariable, Instruction> { [value] = wrongBaseLoad };

        var changed = InterfaceDispatchRecovery.SuppressConsumedMethodInfoLoad(
            value,
            definitions,
            invokeData);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(wrongBaseLoad.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(wrongBaseLoad.Operands, Has.Count.EqualTo(2));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已恢复接口直调会删除纯原生查表区域并保留入口寄存器状态()
    {
        var fixture = CreateLookupExcisionFixture(includeBusinessBackedge: false, includeSideEffect: false);

        var changed = InterfaceDispatchRecovery.TryExciseLookupRegion(
            fixture.Graph,
            fixture.Head,
            fixture.Merge,
            fixture.Slow,
            fixture.SlowCall,
            fixture.Definitions,
            fixture.HomeBlocks,
            out var removedBlockCount,
            out var rejection);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True, rejection);
            Assert.That(removedBlockCount, Is.EqualTo(2));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.Fast));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.Slow));
            Assert.That(fixture.Head.Successors, Is.EqualTo(new[] { fixture.Merge }));
            Assert.That(fixture.Merge.Predecessors, Is.EqualTo(new[] { fixture.Head }));
            Assert.That(fixture.StatePhi.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.StatePhi.Operands, Is.EqualTo(new IOperand[] { fixture.StatePhiDestination, fixture.HeadState }));
            Assert.That(fixture.LookupPhi.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("边界值")]
    public void 业务循环回边重入查表入口时仍只裁剪查表并保留回边()
    {
        var fixture = CreateLookupExcisionFixture(includeBusinessBackedge: true, includeSideEffect: false);

        var changed = InterfaceDispatchRecovery.TryExciseLookupRegion(
            fixture.Graph,
            fixture.Head,
            fixture.Merge,
            fixture.Slow,
            fixture.SlowCall,
            fixture.Definitions,
            fixture.HomeBlocks,
            out var removedBlockCount,
            out var rejection);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True, rejection);
            Assert.That(removedBlockCount, Is.EqualTo(2));
            Assert.That(fixture.Backedge, Is.Not.Null);
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.Backedge!));
            Assert.That(fixture.Backedge!.Successors, Is.EqualTo(new[] { fixture.Head }));
            Assert.That(fixture.Head.Predecessors, Does.Contain(fixture.Backedge));
            Assert.That(fixture.Head.Successors, Is.EqualTo(new[] { fixture.Merge }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 查表区域混入业务调用时原子拒绝且控制流保持不变()
    {
        var fixture = CreateLookupExcisionFixture(includeBusinessBackedge: true, includeSideEffect: true);
        var originalBlocks = fixture.Graph.Blocks.ToList();
        var originalHeadSuccessors = fixture.Head.Successors.ToList();
        var originalMergePredecessors = fixture.Merge.Predecessors.ToList();
        var originalStatePhiOperands = fixture.StatePhi.Operands.ToList();

        var changed = InterfaceDispatchRecovery.TryExciseLookupRegion(
            fixture.Graph,
            fixture.Head,
            fixture.Merge,
            fixture.Slow,
            fixture.SlowCall,
            fixture.Definitions,
            fixture.HomeBlocks,
            out var removedBlockCount,
            out var rejection);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(removedBlockCount, Is.Zero);
            Assert.That(rejection, Does.Contain("副作用"));
            Assert.That(fixture.Graph.Blocks, Is.EqualTo(originalBlocks));
            Assert.That(fixture.Head.Successors, Is.EqualTo(originalHeadSuccessors));
            Assert.That(fixture.Merge.Predecessors, Is.EqualTo(originalMergePredecessors));
            Assert.That(fixture.StatePhi.OpCode, Is.EqualTo(OpCode.Phi));
            Assert.That(fixture.StatePhi.Operands, Is.EqualTo(originalStatePhiOperands));
        });
    }

    private sealed record LookupExcisionFixture(
        ISILControlFlowGraph Graph,
        Block Head,
        Block Fast,
        Block Slow,
        Block Merge,
        Block? Backedge,
        Instruction SlowCall,
        Instruction StatePhi,
        Instruction LookupPhi,
        LocalVariable StatePhiDestination,
        LocalVariable HeadState,
        Dictionary<LocalVariable, Instruction> Definitions,
        Dictionary<Instruction, Block> HomeBlocks);

    /// <summary>构造与 ARM64 接口快慢查表同构的最小闭合控制流。</summary>
    private static LookupExcisionFixture CreateLookupExcisionFixture(
        bool includeBusinessBackedge,
        bool includeSideEffect)
    {
        var graph = new ISILControlFlowGraph([new Instruction(999, OpCode.Return)]);
        var entry = new Block { ID = 0, BlockType = BlockType.Entry };
        var exit = new Block { ID = 1, BlockType = BlockType.Exit };
        var initial = new Block { ID = 2 };
        var head = new Block { ID = 3 };
        var fast = new Block { ID = 4 };
        var slow = new Block { ID = 5 };
        var merge = new Block { ID = 6 };
        var continuation = new Block { ID = 7 };
        var backedge = includeBusinessBackedge ? new Block { ID = 8 } : null;

        var initialState = Local("initialState");
        var loopState = Local("loopState");
        var headState = Local("headState");
        var klass = Local("klass");
        var receiver = Local("receiver");
        var fastInvokeData = Local("fastInvokeData");
        var slowInvokeData = Local("slowInvokeData");
        var slowState = Local("slowState");
        var stateAfterLookup = Local("stateAfterLookup");
        var invokeDataAfterLookup = Local("invokeDataAfterLookup");
        var condition = Local("condition");

        initial.Instructions.Add(new Instruction(0, OpCode.Move, initialState, new Immediate(7)));
        initial.Instructions.Add(new Instruction(1, OpCode.Jump, head));

        var headStateDefinition = includeBusinessBackedge
            ? new Instruction(2, OpCode.Phi, headState, initialState, loopState)
            : new Instruction(2, OpCode.Move, headState, initialState);
        var klassDefinition = new Instruction(3, OpCode.Move, klass, new MemoryOperand(receiver));
        head.Instructions.Add(headStateDefinition);
        head.Instructions.Add(klassDefinition);
        head.Instructions.Add(new Instruction(4, OpCode.ConditionalJump, slow, condition));

        fast.Instructions.Add(new Instruction(5, OpCode.Move, fastInvokeData, new Immediate(0x138)));
        fast.Instructions.Add(new Instruction(6, OpCode.Jump, merge));

        var slowCall = new Instruction(7, OpCode.Call, new Immediate(0x219B070), slowInvokeData);
        slow.Instructions.Add(slowCall);
        slow.Instructions.Add(new Instruction(8, OpCode.Move, slowState, new Immediate(0)));
        if (includeSideEffect)
            slow.Instructions.Add(new Instruction(9, OpCode.CallVoid, new Immediate(0x123456)));
        slow.Instructions.Add(new Instruction(10, OpCode.Jump, merge));

        var statePhi = new Instruction(11, OpCode.Phi, stateAfterLookup, headState, slowState);
        var lookupPhi = new Instruction(12, OpCode.Phi, invokeDataAfterLookup, fastInvokeData, slowInvokeData);
        merge.Instructions.Add(statePhi);
        merge.Instructions.Add(lookupPhi);
        merge.Instructions.Add(new Instruction(13, OpCode.Jump, continuation));
        continuation.Instructions.Add(new Instruction(14, OpCode.Return, stateAfterLookup));

        if (backedge != null)
        {
            backedge.Instructions.Add(new Instruction(15, OpCode.Move, loopState, stateAfterLookup));
            backedge.Instructions.Add(new Instruction(16, OpCode.Jump, head));
        }

        Connect(entry, initial);
        Connect(initial, head);
        Connect(head, fast);
        Connect(head, slow);
        Connect(fast, merge);
        Connect(slow, merge);
        Connect(merge, continuation);
        if (backedge == null)
            Connect(continuation, exit);
        else
        {
            Connect(continuation, exit);
            Connect(continuation, backedge);
            Connect(backedge, head);
        }

        graph.EntryBlock = entry;
        graph.ExitBlock = exit;
        graph.Blocks = [entry, exit, initial, head, fast, slow, merge, continuation];
        if (backedge != null)
            graph.Blocks.Add(backedge);

        foreach (var block in graph.Blocks)
            block.CalculateBlockType();

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var homeBlocks = new Dictionary<Instruction, Block>();
        foreach (var block in graph.Blocks)
        foreach (var instruction in block.Instructions)
        {
            homeBlocks[instruction] = block;
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;
        }

        return new LookupExcisionFixture(
            graph,
            head,
            fast,
            slow,
            merge,
            backedge,
            slowCall,
            statePhi,
            lookupPhi,
            stateAfterLookup,
            headState,
            definitions,
            homeBlocks);
    }

    private static void Connect(Block from, Block to)
    {
        from.Successors.Add(to);
        to.Predecessors.Add(from);
    }

    private static (Instruction Dispatch, IReadOnlyList<Instruction> Instructions) CreateSharedInvokeDataShape(
        bool includeMethodInfo,
        long slowSlot,
        bool indirectMethodInfo = false,
        bool separateConsumerBase = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var disposable = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            disposable);
        // 生产形状中的TypeInfo仍是共享内存槽，接口身份来自同链isinst定型的接收者。
        var interfaceTypeInfo = new MemoryOperand(Local("sharedTypeInfoSlot"));
        var invokeDataRegister = new Register(0, "X0", 404);
        var slowInvokeData = new LocalVariable("slowInvokeData", invokeDataRegister);
        var invokeData = new LocalVariable("invokeData", invokeDataRegister);
        // 真实转换结果会为两个内存消费者各建局部对象，但保留完全相同的SSA寄存器身份。
        var consumerBase = separateConsumerBase
            ? new LocalVariable("consumerInvokeData", invokeDataRegister)
            : invokeData;
        var klass = Local("klass");
        var entryOffset = Local("entryOffset");
        var shifted = Local("shifted");
        var beforeVTable = Local("beforeVTable");
        var methodInfo = Local("methodInfo");

        var instructions = new List<Instruction>
        {
            // 两条原生路径具有同一SSA寄存器身份，但解析阶段可能保留为不同局部对象。
            new(0, OpCode.Call, new Immediate(0x219B070), slowInvokeData, receiver, interfaceTypeInfo, new Immediate(slowSlot)),
            new(1, OpCode.Move, klass, new MemoryOperand(receiver)),
            new(2, OpCode.Move, entryOffset, new MemoryOperand(Local("interfaceOffsets"))),
            new(3, OpCode.ShiftLeft, shifted, entryOffset, new Immediate(4)),
            new(4, OpCode.Add, beforeVTable, shifted, klass),
            new(5, OpCode.Add, invokeData, beforeVTable, new Immediate(0x138)),
        };

        IOperand methodInfoOperand = new MemoryOperand(consumerBase, addend: 8);
        if (includeMethodInfo && indirectMethodInfo)
        {
            instructions.Add(new Instruction(6, OpCode.Move, methodInfo, methodInfoOperand));
            methodInfoOperand = methodInfo;
        }

        var operands = includeMethodInfo
            ? new IOperand[]
            {
                new MemoryOperand(consumerBase),
                Local("result"),
                receiver,
                methodInfoOperand,
            }
            : new IOperand[]
            {
                new MemoryOperand(consumerBase),
                Local("result"),
                receiver,
            };
        var dispatch = new Instruction(7, OpCode.IndirectCall, operands.ToList());
        instructions.Add(dispatch);
        return (dispatch, instructions);
    }

    private static MethodAnalysisContext CreateAddWithResizeCandidate()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Private,
            [listDefinition.GenericParameters.Single()]);
        return new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [app.SystemTypes.SystemStringType],
            []);
    }

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
