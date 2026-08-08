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

    private static MethodAnalysisContext CreateMethod(params Instruction[] instructions)
    {
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            .. instructions,
            new Instruction(instructions.Length, OpCode.Return),
        ]);
        return method;
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type)
        => new(name, new Register(null, name), type);
}
