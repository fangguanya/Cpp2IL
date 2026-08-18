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

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

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

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

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

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已定型同类接收者的非零常量定义恢复为入口This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("typedScalar", owner, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var scalarDefinition = new Instruction(0, OpCode.Move, receiver, new Immediate(2));
        var call = new Instruction(1, OpCode.Call, target, result, receiver);
        Prepare(caller, scalarDefinition, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
            Assert.That(caller.ParameterLocals.Single().IsThis, Is.True);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 同类型实例调用的未定义X0恢复为入口This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("erasedX0", owner, 408);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 409);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同类型实例调用的未定义X1保持红门()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = new LocalVariable("erasedX1", new Register(null, "X1", 408), owner);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 409);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 异类型调用者的未定义X0禁止恢复为This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var unrelated = Type("Unrelated", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(unrelated, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("erasedX0", owner, 408);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 409);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);
        Prepare(caller, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已定型同类接收者的零和非零Phi恢复为入口This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("typedScalarPhi", owner, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var phi = new Instruction(-1, OpCode.Phi, receiver, new Immediate(2), new Immediate(0));
        var call = new Instruction(1, OpCode.Call, target, result, receiver);
        Prepare(caller, phi, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
        });
    }

    [Test]
    [Category("边界值")]
    public void 退SSA后的零和非零多定义接收者恢复为入口This()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("loweredScalarPhi", owner, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var nonZeroEdge = new Instruction(-1, OpCode.Move, receiver, new Immediate(2));
        var zeroEdge = new Instruction(-1, OpCode.Move, receiver, new Immediate(0));
        var call = new Instruction(1, OpCode.Call, target, result, receiver);
        caller.ParameterOperands = [new Register(null, "X0")];
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            nonZeroEdge,
            zeroEdge,
            call,
            new Instruction(2, OpCode.Return),
        ]);
        caller.Locals = [receiver, result];
        caller.ParameterLocals = [];

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(caller.ParameterLocals.Single()));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 已定型同类接收者仅有空常量时保持原身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "Read", app.SystemTypes.SystemBooleanType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("explicitNull", owner, 4);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 5);
        var nullDefinition = new Instruction(0, OpCode.Move, receiver, new Immediate(0));
        var call = new Instruction(1, OpCode.Call, target, result, receiver);
        Prepare(caller, nullDefinition, call, receiver, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunScalarValueReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 开放泛型接收者调用Object方法时保持原身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericReceiverType = owner.GenericParameters.Single();
        var target = Method(
            app.SystemTypes.SystemObjectType,
            "Equals",
            app.SystemTypes.SystemBooleanType,
            isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local("value", genericReceiverType, 4);
        var argument = Local("other", app.SystemTypes.SystemObjectType, 5);
        var result = Local("result", app.SystemTypes.SystemBooleanType, 6);
        var call = new Instruction(0, OpCode.Call, target, result, receiver, argument);
        Prepare(caller, call, receiver, argument, result);

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[2], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 隐藏MethodInfo伪递归调用保持元数据接收者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "GenericTarget", app.SystemTypes.SystemVoidType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var receiver = Local(
            "methodInfo",
            new RuntimeMethodInfoAnalysisContext(target, owner.DeclaringAssembly),
            4);
        var call = new Instruction(0, OpCode.CallVoid, target, receiver);
        Prepare(caller, call, receiver);

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[1], Is.SameAs(receiver));
            Assert.That(caller.ParameterLocals, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void MethodInfo经两级Move进入调用时仍保持原载体()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Type("Owner", app.SystemTypes.SystemObjectType);
        var target = Method(owner, "GenericTarget", app.SystemTypes.SystemVoidType, isStatic: false);
        var caller = Method(owner, "Caller", app.SystemTypes.SystemVoidType, isStatic: false);
        var methodInfo = Local(
            "methodInfo",
            new RuntimeMethodInfoAnalysisContext(target, owner.DeclaringAssembly),
            1);
        var saved = Local("savedMethodInfo", app.SystemTypes.SystemIntPtrType, 2);
        var receiver = Local("callReceiver", app.SystemTypes.SystemIntPtrType, 3);
        var save = new Instruction(0, OpCode.Move, saved, methodInfo);
        var copy = new Instruction(1, OpCode.Move, receiver, saved);
        var call = new Instruction(2, OpCode.CallVoid, target, receiver);
        caller.ParameterOperands = [new Register(null, "X0")];
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            save,
            copy,
            call,
            new Instruction(3, OpCode.Return),
        ]);
        caller.Locals = [methodInfo, saved, receiver];
        caller.ParameterLocals = [];

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.Operands[1], Is.SameAs(receiver));
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

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

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

        var rewritten = ErasedInstanceReceiverRecovery.RunIncompatibleTypeReceivers(caller);

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

    private static void Prepare(
        MethodAnalysisContext method,
        Instruction definition,
        Instruction call,
        params LocalVariable[] locals)
    {
        method.ParameterOperands = [new Register(null, "X0")];
        method.ControlFlowGraph = new ISILControlFlowGraph([
            definition,
            call,
            new Instruction(call.Index + 1, OpCode.Return),
        ]);
        method.Locals = [.. locals];
        method.ParameterLocals = [];
    }
}
