using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ResolvedManagedCallScaffoldingRecoveryTests
{
    [SetUp]
    public void 准备测试应用模型()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 已解析调用前的虚表MethodInfo半槽装载被删除()
    {
        var method = 创建测试方法();
        var klass = 创建运行时类局部();
        var hiddenMethodInfo = new LocalVariable("hiddenMethodInfo", new Register(null, "X2"));
        var load = new Instruction(
            0,
            OpCode.Move,
            hiddenMethodInfo,
            new MemoryOperand(klass, null, 0x310, 0));
        var call = new Instruction(1, OpCode.CallVoid, 创建已解析消费者());
        method.ControlFlowGraph = new ISILControlFlowGraph([
            load,
            call,
            new Instruction(2, OpCode.Return),
        ]);

        var removed = ResolvedManagedCallScaffoldingRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid));
        });
    }

    [Test]
    [Category("边界值")]
    public void Nop间隔与零号虚表MethodInfo半槽仍被精确识别()
    {
        var method = 创建测试方法();
        var klass = 创建运行时类局部();
        var hiddenMethodInfo = new LocalVariable("hiddenMethodInfo", new Register(null, "X1"));
        var firstMethodInfoOffset = Il2CppClassUsefulOffsets.GetVirtualInvokeDataVtableOffset(
            method.AppContext.Binary.is32Bit) + method.AppContext.Binary.PointerSizeBytes;
        var load = new Instruction(
            0,
            OpCode.Move,
            hiddenMethodInfo,
            new MemoryOperand(klass, null, firstMethodInfoOffset, 0));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            load,
            new Instruction(1, OpCode.Nop),
            new Instruction(2, OpCode.CallVoid, 创建已解析消费者()),
            new Instruction(3, OpCode.Return),
        ]);

        var removed = ResolvedManagedCallScaffoldingRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 方法地址半槽与普通类内偏移保持原指令()
    {
        var method = 创建测试方法();
        var klass = 创建运行时类局部();
        var value = new LocalVariable("value", new Register(null, "X1"));
        var vtableOffset = Il2CppClassUsefulOffsets.GetVirtualInvokeDataVtableOffset(
            method.AppContext.Binary.is32Bit);
        var methodPointerLoad = new Instruction(
            0,
            OpCode.Move,
            value,
            new MemoryOperand(klass, null, vtableOffset, 0));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            methodPointerLoad,
            new Instruction(1, OpCode.CallVoid, 创建已解析消费者()),
            new Instruction(2, OpCode.Return),
        ]);

        var removed = ResolvedManagedCallScaffoldingRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(methodPointerLoad.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未解析与间接调用继续保留虚表MethodInfo装载()
    {
        var method = 创建测试方法();
        var klass = 创建运行时类局部();
        var hiddenMethodInfo = new LocalVariable("hiddenMethodInfo", new Register(null, "X2"));
        var load = new Instruction(
            0,
            OpCode.Move,
            hiddenMethodInfo,
            new MemoryOperand(klass, null, 0x310, 0));
        var indirect = new Instruction(
            1,
            OpCode.IndirectCall,
            new MemoryOperand(klass, null, 0x308, 0),
            new LocalVariable("result", new Register(null, "X0")),
            hiddenMethodInfo);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            load,
            indirect,
            new Instruction(2, OpCode.Return),
        ]);

        var removed = ResolvedManagedCallScaffoldingRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(indirect.OpCode, Is.EqualTo(OpCode.IndirectCall));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 已解析调用真实读取该局部时保持装载()
    {
        var method = 创建测试方法();
        var klass = 创建运行时类局部();
        var value = new LocalVariable("value", new Register(null, "X1"));
        var load = new Instruction(
            0,
            OpCode.Move,
            value,
            new MemoryOperand(klass, null, 0x310, 0));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            load,
            new Instruction(1, OpCode.CallVoid, 创建已解析消费者(), value),
            new Instruction(2, OpCode.Return),
        ]);

        var removed = ResolvedManagedCallScaffoldingRecovery.Run(method);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    private static InjectedMethodAnalysisContext 创建测试方法()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Object")!;
        return new InjectedMethodAnalysisContext(
            owner,
            "TestMethod",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
    }

    private static InjectedMethodAnalysisContext 创建已解析消费者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Object")!;
        return new InjectedMethodAnalysisContext(
            owner,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [app.SystemTypes.SystemObjectType]);
    }

    private static LocalVariable 创建运行时类局部()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var representedType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        return new LocalVariable(
            "klass",
            new Register(null, "X8"),
            new RuntimeClassTypeAnalysisContext(representedType, representedType.DeclaringAssembly));
    }
}
