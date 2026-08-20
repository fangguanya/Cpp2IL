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
    [Category("基本功能")]
    public void Enumerable序列实参覆盖共享委托中的Object占位并同步参数()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var enumerableType = mscorlib.GetTypeByFullName("System.Collections.Generic.IEnumerable`1")!;
        var funcType = mscorlib.GetTypeByFullName("System.Func`2")!;
        var enumerable = app.GetAssemblyByName("System.Core")!
            .GetTypeByFullName("System.Linq.Enumerable")!;
        var orderBy = enumerable.Methods.Single(method =>
            method.Name == "OrderBy"
            && method.Parameters.Count == 2
            && method.GenericParameters.Count == 2);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var intType = app.SystemTypes.SystemInt32Type;
        var oldTarget = new ConcreteGenericMethodAnalysisContext(orderBy, [], [objectType, intType]);
        var source = new LocalVariable(
            "source",
            new Register(null, "X0"),
            enumerableType.MakeGenericInstanceType([stringType]));
        var selector = new LocalVariable(
            "selector",
            new Register(null, "X1"),
            funcType.MakeGenericInstanceType([objectType, intType]));
        var result = new LocalVariable("result", new Register(null, "X0", 2), oldTarget.ReturnType);
        var call = new Instruction(0, OpCode.Call, oldTarget, result, source, selector);

        var changed = GenericCallRebinder.TryRebind(call);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(rebound.MethodGenericParameters, Is.EqualTo(new[] { stringType, intType }));
            Assert.That(rebound.ReturnType.FullName, Does.Contain("System.String"));
            Assert.That(selector.Type!.FullName, Does.Contain("System.String"));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Enumerable字段选择器从字符串数组闭合并推进ToList()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var enumerable = app.GetAssemblyByName("System.Core")!
            .GetTypeByFullName("System.Linq.Enumerable")!;
        var orderBy = enumerable.Methods.Single(method =>
            method.Name == "OrderBy"
            && method.Parameters.Count == 2
            && method.GenericParameters.Count == 2);
        var toList = enumerable.Methods.Single(method =>
            method.Name == "ToList"
            && method.Parameters.Count == 1
            && method.GenericParameters.Count == 1);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var funcType = mscorlib.GetTypeByFullName("System.Func`2")!;
        var selectorType = funcType.MakeGenericInstanceType([stringType, stringType]);
        var orderByTarget = new ConcreteGenericMethodAnalysisContext(
            orderBy,
            [],
            [objectType, objectType]);
        var toListTarget = new ConcreteGenericMethodAnalysisContext(toList, [], [objectType]);
        var source = new LocalVariable(
            "source",
            new Register(null, "X0"),
            new SzArrayTypeAnalysisContext(stringType));
        var owner = new LocalVariable("owner", new Register(null, "X9"), objectType);
        var selectorField = new InjectedFieldAnalysisContext(
            "selector",
            selectorType,
            FieldAttributes.Static,
            objectType);
        var selector = new FieldReference(selectorField, owner, 0x8A8);
        var ordered = new LocalVariable(
            "ordered",
            new Register(null, "X0", 2),
            orderByTarget.ReturnType);
        var list = new LocalVariable(
            "list",
            new Register(null, "X0", 3),
            toListTarget.ReturnType);
        var orderByCall = new Instruction(0, OpCode.Call, orderByTarget, ordered, source, selector);
        var toListCall = new Instruction(1, OpCode.Call, toListTarget, list, ordered);

        var orderByChanged = GenericCallRebinder.TryRebind(orderByCall);
        var toListChanged = GenericCallRebinder.TryRebind(toListCall);
        var reboundOrderBy = (ConcreteGenericMethodAnalysisContext)orderByCall.Operands[0];
        var reboundToList = (ConcreteGenericMethodAnalysisContext)toListCall.Operands[0];

        Assert.Multiple(() =>
        {
            Assert.That(orderByChanged, Is.True);
            Assert.That(toListChanged, Is.True);
            Assert.That(reboundOrderBy.MethodGenericParameters, Is.EqualTo(new[] { stringType, stringType }));
            Assert.That(reboundToList.MethodGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(ordered.Type!.FullName, Does.Contain("System.String"));
            Assert.That(list.Type!.FullName, Does.Contain("System.String"));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 开放List成员从具体值参数闭合声明类型实参()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [genericElement],
            []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            openTarget.DeclaringType);
        var value = new LocalVariable(
            "value",
            new Register(null, "X1"),
            app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, value);

        var changed = GenericCallRebinder.TryRebind(call);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { app.SystemTypes.SystemStringType }));
            Assert.That(rebound.DeclaringType!.FullName, Does.Contain("System.String"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 封闭ListObject接收者不得被String参数收窄()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var objectType = app.SystemTypes.SystemObjectType;
        var target = new ConcreteGenericMethodAnalysisContext(addWithResize, [objectType], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            listDefinition.MakeGenericInstanceType([objectType]));
        var value = new LocalVariable(
            "value",
            new Register(null, "X1"),
            app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, target, receiver, value);

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
            Assert.That(call.Operands[0], Is.SameAs(target));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一声明类型参数收到冲突实参时保持开放目标()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var pair = new InjectedMethodAnalysisContext(
            listDefinition,
            "Pair",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement, genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(pair, [genericElement], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            openTarget.DeclaringType);
        var first = new LocalVariable(
            "first",
            new Register(null, "X1"),
            app.SystemTypes.SystemStringType);
        var second = new LocalVariable(
            "second",
            new Register(null, "X2"),
            app.SystemTypes.SystemInt32Type);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, first, second);

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
            Assert.That(call.Operands[0], Is.SameAs(openTarget));
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
