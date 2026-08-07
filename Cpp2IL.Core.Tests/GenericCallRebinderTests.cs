using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class GenericCallRebinderTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 实例共享体按List接收者重绑定Enumerator返回类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var listDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getEnumerator = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var oldTarget = new ConcreteGenericMethodAnalysisContext(getEnumerator, [objectType], []);
        var receiverType = listDefinition.MakeGenericInstanceType([stringType]);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), receiverType);
        var result = new LocalVariable("result", new Register(null, "stack_-20"), oldTarget.ReturnType);
        var call = new Instruction(0, OpCode.Call, oldTarget, result, receiver);

        var changed = GenericCallRebinder.TryRebind(call);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(result.Type, Is.SameAs(rebound.ReturnType));
            Assert.That(rebound.ReturnType.FullName, Does.Contain("System.String"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已具体化接收者保持同一调用目标()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getEnumerator = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var stringType = app.SystemTypes.SystemStringType;
        var target = new ConcreteGenericMethodAnalysisContext(getEnumerator, [stringType], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            listDefinition.MakeGenericInstanceType([stringType]));
        var result = new LocalVariable("result", new Register(null, "stack_-20"), target.ReturnType);
        var call = new Instruction(0, OpCode.Call, target, result, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
            Assert.That(call.Operands[0], Is.SameAs(target));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 字段定义链优先于旧共享签名污染的局部类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var listDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getEnumerator = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var oldTarget = new ConcreteGenericMethodAnalysisContext(getEnumerator, [objectType], []);
        var concreteList = listDefinition.MakeGenericInstanceType([stringType]);
        var owner = new LocalVariable("owner", new Register(null, "X20"), objectType);
        var field = new InjectedFieldAnalysisContext(
            "items",
            concreteList,
            FieldAttributes.Public,
            objectType);
        var fieldReference = new FieldReference(field, owner, 0x10);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), oldTarget.DeclaringType);
        var result = new LocalVariable("result", new Register(null, "stack_-20"), oldTarget.ReturnType);
        var call = new Instruction(0, OpCode.Call, oldTarget, result, receiver);
        var definitions = new Dictionary<LocalVariable, IOperand> { [receiver] = fieldReference };

        var changed = GenericCallRebinder.TryRebind(call, definitions);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(GenericCallRebinder.TypesEquivalent(receiver.Type, concreteList), Is.True);
            Assert.That(result.Type, Is.SameAs(rebound.ReturnType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 静态Enumerable方法从List接口投影推断方法泛型参数()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var enumerable = app.GetAssemblyByName("System.Core")!
            .GetTypeByFullName("System.Linq.Enumerable")!;
        var last = enumerable.Methods.Single(method =>
            method.Name == "Last"
            && method.Parameters.Count == 1);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var oldTarget = new ConcreteGenericMethodAnalysisContext(last, [], [objectType]);
        var listType = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!
            .MakeGenericInstanceType([stringType]);
        var argument = new LocalVariable("source", new Register(null, "X0"), listType);
        var result = new LocalVariable("result", new Register(null, "X0"), objectType);
        var call = new Instruction(0, OpCode.Call, oldTarget, result, argument);

        var changed = GenericCallRebinder.TryRebind(call);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(rebound.MethodGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(result.Type, Is.SameAs(stringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非泛型目标不得冒充共享泛型调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var target = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Identity",
            app.SystemTypes.SystemObjectType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [app.SystemTypes.SystemObjectType]);
        var value = new LocalVariable("value", new Register(null, "X0"), app.SystemTypes.SystemStringType);
        var result = new LocalVariable("result", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var call = new Instruction(0, OpCode.Call, target, result, value);

        Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
    }
}
