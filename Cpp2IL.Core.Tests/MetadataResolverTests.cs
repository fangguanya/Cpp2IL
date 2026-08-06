using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class MetadataResolverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void Void目标删除伪返回槽并转为CallVoid()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var argument = new LocalVariable("argument", new Register(null, "X0"), appContext.SystemTypes.SystemInt32Type);
        var fakeReturn = new LocalVariable("fakeReturn", new Register(null, "X0"));
        var target = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "Target",
            appContext.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [appContext.SystemTypes.SystemInt32Type]);
        var call = new Instruction(0, OpCode.Call, Imm(0x1234), fakeReturn, argument);

        MetadataResolver.BindCallTarget(call, target);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(call.Operands, Is.EqualTo(new IOperand[] { target, argument }));
            Assert.That(call.Destination, Is.Null);
        });
    }

    [Test]
    [Category("边界值")]
    public void 非Void目标保持Call及真实返回槽()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var argument = new LocalVariable("argument", new Register(null, "X0"), appContext.SystemTypes.SystemInt32Type);
        var returnValue = new LocalVariable("returnValue", new Register(null, "X0"));
        var target = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "Target",
            appContext.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [appContext.SystemTypes.SystemInt32Type]);
        var call = new Instruction(0, OpCode.Call, Imm(0x1234), returnValue, argument);

        MetadataResolver.BindCallTarget(call, target);

        Assert.Multiple(() =>
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(call.Operands, Is.EqualTo(new IOperand[] { target, returnValue, argument }));
            Assert.That(call.Destination, Is.SameAs(returnValue));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非调用指令拒绝绑定方法目标()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var target = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "Target",
            appContext.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        var move = new Instruction(0, OpCode.Move, new LocalVariable("x", new Register(null, "X0")), Imm(1));

        Assert.That(
            () => MetadataResolver.BindCallTarget(move, target),
            Throws.TypeOf<InvalidOperationException>());
    }
}
