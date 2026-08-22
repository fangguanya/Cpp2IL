using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class LateVirtualCallRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 最终具体接收者与双半槽恢复普通虚调用()
    {
        var fixture = CreateFixture(useInterfaceKlass: false, includeMethodInfo: true);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(fixture.Transfer.Operands[0], Is.TypeOf<MethodAnalysisContext>());
            Assert.That(((MethodAnalysisContext)fixture.Transfer.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(
                ((LocalVariable)fixture.Transfer.Destination!).Type,
                Is.SameAs(fixture.App.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 接口类指针与具体实现接收者共同恢复类虚表槽()
    {
        var fixture = CreateFixture(useInterfaceKlass: true, includeMethodInfo: true);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(((MethodAnalysisContext)fixture.Transfer.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(fixture.Transfer.Operands, Does.Contain(fixture.Receiver));
        });
    }

    [Test]
    [Category("边界值")]
    public void 接口接收者的全部最终定义来自同一具体类型时恢复虚表槽()
    {
        var fixture = CreateFixture(useInterfaceKlass: true, includeMethodInfo: true);
        var arrayList = fixture.Receiver.Type!;
        var firstSource = new LocalVariable("firstSource", new Register(null, "X20"), arrayList);
        var secondSource = new LocalVariable("secondSource", new Register(null, "X21"), arrayList);
        fixture.Receiver.Type = fixture.App.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ICollection")!;
        var prefix = new[]
        {
            new Instruction(-2, OpCode.Move, fixture.Receiver, firstSource),
            new Instruction(-1, OpCode.Move, fixture.Receiver, secondSource),
        };
        fixture.Method.ControlFlowGraph = new ISILControlFlowGraph([
            .. prefix,
            fixture.Transfer,
            new Instruction(3, OpCode.Return),
        ]);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(((MethodAnalysisContext)fixture.Transfer.Operands[0]).Name, Is.EqualTo("Add"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 唯一局部承载的目标与MethodInfo载荷仍可恢复()
    {
        var fixture = CreateFixture(useInterfaceKlass: true, includeMethodInfo: true, foldLoads: false);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.That(recovered, Is.EqualTo(1));
        Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.Call));
    }

    [Test]
    [Category("边界值")]
    public void 未定型类指针由具体接收者与双半槽共同闭合()
    {
        var fixture = CreateFixture(useInterfaceKlass: false, includeMethodInfo: true);
        fixture.Klass.Type = null;

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(((MethodAnalysisContext)fixture.Transfer.Operands[0]).Name, Is.EqualTo("Add"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 缺少MethodInfo半槽时保持间接调用()
    {
        var fixture = CreateFixture(useInterfaceKlass: false, includeMethodInfo: false);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.IndirectCall));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 接收者与类指针类型不相容时保持间接调用()
    {
        var fixture = CreateFixture(useInterfaceKlass: false, includeMethodInfo: true);
        fixture.Klass.Type = new RuntimeClassTypeAnalysisContext(
            fixture.App.SystemTypes.SystemStringType,
            fixture.App.SystemTypes.SystemStringType.DeclaringAssembly);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.IndirectCall));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非类元数据类型的目标基址保持间接调用()
    {
        var fixture = CreateFixture(useInterfaceKlass: false, includeMethodInfo: true);
        fixture.Klass.Type = fixture.App.SystemTypes.SystemIntPtrType;

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.IndirectCall));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 接口接收者的最终定义类型冲突时保持间接调用()
    {
        var fixture = CreateFixture(useInterfaceKlass: true, includeMethodInfo: true);
        var assembly = fixture.App.GetAssemblyByName("mscorlib")!;
        fixture.Receiver.Type = assembly.GetTypeByFullName("System.Collections.ICollection")!;
        var arrayListSource = new LocalVariable(
            "arrayListSource",
            new Register(null, "X20"),
            assembly.GetTypeByFullName("System.Collections.ArrayList")!);
        var stringSource = new LocalVariable(
            "stringSource",
            new Register(null, "X21"),
            fixture.App.SystemTypes.SystemStringType);
        fixture.Method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(-2, OpCode.Move, fixture.Receiver, arrayListSource),
            new Instruction(-1, OpCode.Move, fixture.Receiver, stringSource),
            fixture.Transfer,
            new Instruction(3, OpCode.Return),
        ]);

        var recovered = LateVirtualCallRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Transfer.OpCode, Is.EqualTo(OpCode.IndirectCall));
        });
    }

    private static Fixture CreateFixture(
        bool useInterfaceKlass,
        bool includeMethodInfo,
        bool foldLoads = true)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var arrayList = assembly.GetTypeByFullName("System.Collections.ArrayList")!;
        var collection = assembly.GetTypeByFullName("System.Collections.ICollection")!;
        var add = arrayList.Methods.First(method =>
            method.Name == "Add" && method.Parameters.Count == 1 && method.Definition?.slot != ushort.MaxValue);
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "LateVirtualCallOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var method = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 1), arrayList);
        var result = new LocalVariable("result", new Register(null, "X0", 2));
        var argument = new LocalVariable("argument", new Register(null, "X1"), app.SystemTypes.SystemStringType);
        var klassType = useInterfaceKlass ? collection : arrayList;
        var klass = new LocalVariable(
            "klass",
            new Register(null, "X8"),
            new RuntimeClassTypeAnalysisContext(klassType, assembly));
        var pointerSize = app.Binary.PointerSizeBytes;
        var targetOffset = Il2CppClassUsefulOffsets.GetVirtualInvokeDataVtableOffset(app.Binary.is32Bit)
                           + add.Definition!.slot * pointerSize * 2L;
        var instructions = new List<Instruction>();
        IOperand target = new MemoryOperand(klass, addend: targetOffset);
        IOperand methodInfo = new MemoryOperand(klass, addend: targetOffset + pointerSize);

        if (!foldLoads)
        {
            var targetLocal = new LocalVariable("target", new Register(null, "X9"));
            var methodInfoLocal = new LocalVariable("methodInfo", new Register(null, "X2"));
            instructions.Add(new Instruction(0, OpCode.Move, targetLocal, target));
            instructions.Add(new Instruction(1, OpCode.Move, methodInfoLocal, methodInfo));
            target = targetLocal;
            methodInfo = methodInfoLocal;
        }

        var operands = new List<IOperand> { target, result, receiver, argument };
        if (includeMethodInfo)
            operands.Add(methodInfo);
        var transfer = new Instruction(2, OpCode.IndirectCall, operands);
        instructions.Add(transfer);
        instructions.Add(new Instruction(3, OpCode.Return));
        method.ControlFlowGraph = new ISILControlFlowGraph(instructions);

        return new Fixture(app, method, transfer, receiver, klass);
    }

    private sealed record Fixture(
        ApplicationAnalysisContext App,
        MethodAnalysisContext Method,
        Instruction Transfer,
        LocalVariable Receiver,
        LocalVariable Klass);
}
