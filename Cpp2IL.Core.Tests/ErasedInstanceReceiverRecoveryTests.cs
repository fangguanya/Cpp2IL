using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ErasedInstanceReceiverRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 同类型方法的错误返回寄存器恢复为入口This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var wrongReceiver = Local("addResult", app.SystemTypes.SystemInt32Type, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var call = new Instruction(0, OpCode.Call, target, result, wrongReceiver);
        Prepare(caller, call, wrongReceiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
            Assert.That(caller.ParameterLocals.Single().IsThis, Is.True);
            Assert.That(caller.ParameterLocals.Single().Type, Is.SameAs(owner));
        });
    }

    [Test]
    [Category("边界值")]
    public void 派生类型调用基类方法时恢复派生This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var baseType = Type("BaseOwner", app.SystemTypes.SystemObjectType);
        var derivedType = Type("DerivedOwner", baseType);
        var target = Method(baseType, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(derivedType, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var wrongReceiver = Local("collection", app.SystemTypes.SystemObjectType, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var call = new Instruction(0, OpCode.Call, target, result, wrongReceiver);
        Prepare(caller, call, wrongReceiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
            Assert.That(caller.ParameterLocals.Single().Type, Is.SameAs(derivedType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 已相容接收者保持原身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("receiver", owner, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 无关调用者类型保持错误证据而不改写()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var unrelated = Type("Unrelated", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(unrelated, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("wrong", app.SystemTypes.SystemInt32Type, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 静态调用者不生成This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: true);
        var receiver = Local("wrong", app.SystemTypes.SystemInt32Type, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    private static InjectedTypeAnalysisContext Type(string name, TypeAnalysisContext baseType)
        => new(
            Cpp2IlApi.CurrentAppContext!.Assemblies[0],
            "Cpp2IL.Core.Tests",
            name,
            baseType,
            TypeAttributes.Public | TypeAttributes.Class);

    private static InjectedMethodAnalysisContext Method(
        TypeAnalysisContext owner,
        string name,
        TypeAnalysisContext returnType,
        bool isStatic)
        => new(
            owner,
            name,
            returnType,
            MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0),
            []);

    private static LocalVariable Local(string name, TypeAnalysisContext type, int version)
        => new(name, new Register(null, "X0", version), type);

    private static void Prepare(
        MethodAnalysisContext method,
        Instruction call,
        params LocalVariable[] locals)
    {
        method.ParameterOperands = [new Register(null, "X0")];
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [.. locals];
        method.ParameterLocals = [];
    }
}
