using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests.Analysis;

public class DirectRuntimeHelperRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 属性结果已脱离X0时仍按机器码恢复CastClass()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("propertyValue", "PROPERTY_VALUE_1", app.SystemTypes.SystemObjectType);
        var nativeResult = Local("nativeResult", "X0", app.SystemTypes.SystemObjectType);
        var use = Local("use", "X20", app.SystemTypes.SystemObjectType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x1F5559C),
            nativeResult,
            source,
            app.SystemTypes.SystemStringType);
        var consume = new Instruction(1, OpCode.Move, use, nativeResult);
        var context = CreateContext(call, consume, new Instruction(2, OpCode.Return));
        context.Locals = [source, nativeResult, use];

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ => DirectRuntimeHelperRecovery.HelperKind.CastClass);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.OpCode, Is.EqualTo(OpCode.CastClass));
            Assert.That(call.Operands[1], Is.SameAs(source));
            Assert.That(call.Operands[2], Is.SameAs(app.SystemTypes.SystemStringType));
            Assert.That(call.Operands[0], Is.TypeOf<LocalVariable>().And.Not.SameAs(nativeResult));
            Assert.That(((LocalVariable)call.Operands[0]).Type, Is.SameAs(app.SystemTypes.SystemStringType));
            Assert.That(consume.Operands[1], Is.SameAs(call.Operands[0]));
        }
    }

    [Test]
    [Category("边界值")]
    public void 同地址字段输入CastClass逐调用恢复且机器码只分类一次()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = Local("owner", "X21", app.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "Occupation",
            app.SystemTypes.SystemObjectType,
            FieldAttributes.Public,
            app.SystemTypes.SystemObjectType);
        var firstSource = new FieldReference(field, owner, 0x138);
        var secondSource = new FieldReference(field, owner, 0x138);
        var firstResult = Local("firstResult", "X0", app.SystemTypes.SystemObjectType);
        var secondResult = Local("secondResult", "X0", app.SystemTypes.SystemObjectType);
        var first = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x1F5559C),
            firstResult,
            firstSource,
            app.SystemTypes.SystemStringType);
        var second = new Instruction(
            1,
            OpCode.Call,
            new Immediate(0x1F5559C),
            secondResult,
            secondSource,
            app.SystemTypes.SystemStringType);
        var context = CreateContext(first, second, new Instruction(2, OpCode.Return));
        context.Locals = [owner, firstResult, secondResult];
        var classifications = 0;

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ =>
            {
                classifications++;
                return DirectRuntimeHelperRecovery.HelperKind.CastClass;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(2));
            Assert.That(classifications, Is.EqualTo(1));
            Assert.That(first.OpCode, Is.EqualTo(OpCode.CastClass));
            Assert.That(first.Operands[1], Is.SameAs(firstSource));
            Assert.That(second.OpCode, Is.EqualTo(OpCode.CastClass));
            Assert.That(second.Operands[1], Is.SameAs(secondSource));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 值类型输入或非X0返回槽均不进入CastClass候选()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var valueSource = Local("valueSource", "PROPERTY_VALUE_2", app.SystemTypes.SystemInt32Type);
        var referenceSource = Local("referenceSource", "PROPERTY_VALUE_3", app.SystemTypes.SystemObjectType);
        var firstResult = Local("firstResult", "X0", app.SystemTypes.SystemObjectType);
        var secondResult = Local("secondResult", "X1", app.SystemTypes.SystemObjectType);
        var first = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x1F5559C),
            firstResult,
            valueSource,
            app.SystemTypes.SystemStringType);
        var second = new Instruction(
            1,
            OpCode.Call,
            new Immediate(0x1F5559C),
            secondResult,
            referenceSource,
            app.SystemTypes.SystemStringType);
        var context = CreateContext(first, second, new Instruction(2, OpCode.Return));
        context.Locals = [valueSource, referenceSource, firstResult, secondResult];
        var classifications = 0;

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ =>
            {
                classifications++;
                return DirectRuntimeHelperRecovery.HelperKind.CastClass;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(classifications, Is.Zero);
            Assert.That(first.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(second.OpCode, Is.EqualTo(OpCode.Call));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 调用形状成立但机器码不是CastClass时保持原调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = Local("source", "PROPERTY_VALUE_4", app.SystemTypes.SystemObjectType);
        var result = Local("result", "X0", app.SystemTypes.SystemObjectType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x1F5559C),
            result,
            source,
            app.SystemTypes.SystemStringType);
        var context = CreateContext(call, new Instruction(1, OpCode.Return));
        context.Locals = [source, result];

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ => DirectRuntimeHelperRecovery.HelperKind.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(call.Operands[0], Is.EqualTo(new Immediate(0x1F5559C)));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 直接接口槽调用恢复MoveNext并拆分X0返回生存期()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumeratorType.Methods.Single(method => method.Name == "MoveNext");
        var slot = moveNext.Definition!.slot;
        var receiver = Local("receiver", "X2", enumeratorType);
        var nativeResult = Local("nativeResult", "X0", app.SystemTypes.SystemObjectType);
        var condition = Local("condition", "W8", app.SystemTypes.SystemBooleanType);
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x1234),
            nativeResult,
            new Immediate(slot),
            enumeratorType,
            receiver);
        var use = new Instruction(1, OpCode.And, condition, nativeResult, new Immediate(1));
        var context = CreateContext(call, use, new Instruction(2, OpCode.Return));
        context.Locals = [receiver, nativeResult, condition];

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ => DirectRuntimeHelperRecovery.HelperKind.DirectInterfaceInvoke);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(call.Operands[0], Is.SameAs(moveNext));
            Assert.That(call.Operands[1], Is.TypeOf<LocalVariable>().And.Not.SameAs(nativeResult));
            Assert.That(((LocalVariable)call.Operands[1]).Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
            Assert.That(use.Operands[1], Is.SameAs(call.Operands[1]));
        }
    }

    [Test]
    [Category("边界值")]
    public void 清理辅助函数选择最近且真实使用的枚举器而非残留X2()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var arrayListType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var getEnumerator = arrayListType.Methods.Single(method =>
            method.Name == "GetEnumerator"
            && method.Parameters.Count == 0
            && method.ReturnType.FullName == enumeratorType.FullName);
        var moveNext = enumeratorType.Methods.Single(method => method.Name == "MoveNext");
        var array = Local("array", "X0", arrayListType);
        var previous = Local("previous", "X0", enumeratorType);
        var current = Local("current", "X0", enumeratorType);
        var staleX2 = Local("staleX2", "X2", enumeratorType);
        var moveResult = Local("moveResult", "X0", app.SystemTypes.SystemBooleanType);
        var cleanupResult = Local("cleanupResult", "X0", app.SystemTypes.SystemObjectType);
        var state = Local("state", "stack_-A8", app.SystemTypes.SystemIntPtrType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Call, getEnumerator, previous, array),
            new(1, OpCode.Call, moveNext, moveResult, previous),
            new(2, OpCode.Call, getEnumerator, current, array),
            new(3, OpCode.Call, moveNext, moveResult, current),
            new(4, OpCode.Call, new Immediate(0x5678), cleanupResult, new AddressOf(state),
                enumeratorType, staleX2),
            new(5, OpCode.Return),
        };
        var context = CreateContext(instructions);
        context.Locals = [array, previous, current, staleX2, moveResult, cleanupResult, state];

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ => DirectRuntimeHelperRecovery.HelperKind.DisposeIfSupported);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(instructions[4].OpCode, Is.EqualTo(OpCode.DisposeIfSupported));
            Assert.That(instructions[4].Operands, Has.Count.EqualTo(1));
            Assert.That(instructions[4].Operands[0], Is.SameAs(current));
            Assert.That(instructions[4].Operands[0], Is.Not.SameAs(staleX2));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 没有循环读取证据的清理地址保持原调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var arrayListType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var getEnumerator = arrayListType.Methods.Single(method =>
            method.Name == "GetEnumerator"
            && method.Parameters.Count == 0
            && method.ReturnType.FullName == enumeratorType.FullName);
        var array = Local("array", "X0", arrayListType);
        var unusedEnumerator = Local("unused", "X0", enumeratorType);
        var result = Local("result", "X0", app.SystemTypes.SystemObjectType);
        var state = Local("state", "stack_-A8", app.SystemTypes.SystemIntPtrType);
        var get = new Instruction(0, OpCode.Call, getEnumerator, unusedEnumerator, array);
        var cleanup = new Instruction(
            1,
            OpCode.Call,
            new Immediate(0x5678),
            result,
            new AddressOf(state));
        var context = CreateContext(get, cleanup, new Instruction(2, OpCode.Return));
        context.Locals = [array, unusedEnumerator, result, state];

        var rewritten = DirectRuntimeHelperRecovery.Run(
            context,
            _ => DirectRuntimeHelperRecovery.HelperKind.DisposeIfSupported);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(cleanup.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(cleanup.Operands[0], Is.EqualTo(new Immediate(0x5678)));
        }
    }

    [Test]
    [Category("基本功能")]
    public void ARM64直接接口尾调用机器码被分类()
    {
        var body = Decode(
            0xFE, 0x4F, 0xBF, 0xA9, 0x48, 0x00, 0x40, 0xF9,
            0xF3, 0x03, 0x02, 0xAA, 0xE2, 0x03, 0x00, 0x2A,
            0x09, 0x5D, 0x42, 0x79, 0x29, 0x01, 0x00, 0xB4,
            0x0A, 0x59, 0x40, 0xF9, 0x4A, 0x21, 0x00, 0x91,
            0x4B, 0x81, 0x5F, 0xF8, 0x7F, 0x01, 0x01, 0xEB,
            0xE0, 0x00, 0x00, 0x54, 0x29, 0x05, 0x00, 0xF1,
            0x4A, 0x41, 0x00, 0x91, 0x61, 0xFF, 0xFF, 0x54,
            0xE0, 0x03, 0x13, 0xAA, 0x20, 0x0A, 0x09, 0x94,
            0x05, 0x00, 0x00, 0x14, 0x49, 0x01, 0x40, 0xB9,
            0x29, 0x21, 0x22, 0x0B, 0x08, 0xD1, 0x29, 0x8B,
            0x00, 0xE1, 0x04, 0x91, 0x02, 0x04, 0x40, 0xA9,
            0xE0, 0x03, 0x13, 0xAA, 0xFE, 0x4F, 0xC1, 0xA8,
            0x40, 0x00, 0x1F, 0xD6);

        var kind = DirectRuntimeHelperRecovery.ClassifyArm64Body(body, 0, _ => null);

        Assert.That(kind, Is.EqualTo(DirectRuntimeHelperRecovery.HelperKind.DirectInterfaceInvoke));
    }

    [Test]
    [Category("边界值")]
    public void ARM64空值成功分支与InvalidCast失败分支共同分类CastClass()
    {
        var body = Decode(
            0x60, 0x01, 0x00, 0xB4, 0x08, 0x00, 0x40, 0xF9,
            0x29, 0xC0, 0x44, 0x39, 0x0A, 0xC1, 0x44, 0x39,
            0x5F, 0x01, 0x09, 0x6B, 0xE3, 0x00, 0x00, 0x54,
            0x08, 0x65, 0x40, 0xF9, 0x08, 0x0D, 0x09, 0x8B,
            0x08, 0x81, 0x5F, 0xF8, 0x1F, 0x01, 0x01, 0xEB,
            0x41, 0x00, 0x00, 0x54, 0xC0, 0x03, 0x5F, 0xD6,
            0xFE, 0x0F, 0x1F, 0xF8, 0xFA, 0x34, 0x08, 0x94);

        var kind = DirectRuntimeHelperRecovery.ClassifyArm64Body(
            body,
            0,
            _ => "System.InvalidCastException");

        Assert.That(kind, Is.EqualTo(DirectRuntimeHelperRecovery.HelperKind.CastClass));
    }

    [Test]
    [Category("异常输入")]
    public void ARM64相同前缀但失败分支不是InvalidCast时拒绝分类()
    {
        var body = Decode(
            0x60, 0x01, 0x00, 0xB4, 0x08, 0x00, 0x40, 0xF9,
            0x29, 0xC0, 0x44, 0x39, 0x0A, 0xC1, 0x44, 0x39,
            0x5F, 0x01, 0x09, 0x6B, 0xE3, 0x00, 0x00, 0x54,
            0x08, 0x65, 0x40, 0xF9, 0x08, 0x0D, 0x09, 0x8B,
            0x08, 0x81, 0x5F, 0xF8, 0x1F, 0x01, 0x01, 0xEB,
            0x41, 0x00, 0x00, 0x54, 0xC0, 0x03, 0x5F, 0xD6,
            0xFE, 0x0F, 0x1F, 0xF8, 0xFA, 0x34, 0x08, 0x94);

        var kind = DirectRuntimeHelperRecovery.ClassifyArm64Body(
            body,
            0,
            _ => "System.ArgumentException");

        Assert.That(kind, Is.EqualTo(DirectRuntimeHelperRecovery.HelperKind.None));
    }

    private static MethodAnalysisContext CreateContext(params Instruction[] instructions)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "RecoverDirectRuntimeHelpers",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions.ToList());
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];
        return context;
    }

    private static LocalVariable Local(string name, string register, TypeAnalysisContext type)
        => new(name, new Register(null, register), type);

    private static Arm64Instruction[] Decode(params byte[] bytes)
    {
        var decoded = new List<Arm64Instruction>();
        foreach (var instruction in Disassembler.Disassemble(
                     bytes,
                     0x1000,
                     new Disassembler.Options(true, true, false)))
            decoded.Add(instruction);
        return decoded.ToArray();
    }
}
