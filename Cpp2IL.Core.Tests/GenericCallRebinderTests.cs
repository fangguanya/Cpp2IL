using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

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
    public void 当前方法泛型参数可作为Newobj接收者的精确实参()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var containingMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Convert",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        var fixture = CreateOpenListConstructorCall(
            containingMethod,
            Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
            "TValue");

        var changed = GenericCallRebinder.TryRebind(fixture.Call, containingMethod);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            var rebound = (ConcreteGenericMethodAnalysisContext)fixture.Call.Operands[0];
            Assert.That(rebound.TypeGenericParameters.Single(), Is.SameAs(fixture.Parameter));
        });
    }

    [Test]
    [Category("边界值")]
    public void 当前声明类型泛型参数可作为Newobj接收者的精确实参()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var containingType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "GenericOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var containingMethod = new InjectedMethodAnalysisContext(
            containingType,
            "Create",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        var fixture = CreateOpenListConstructorCall(
            containingType,
            Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            "TItem");

        var changed = GenericCallRebinder.TryRebind(fixture.Call, containingMethod);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            var rebound = (ConcreteGenericMethodAnalysisContext)fixture.Call.Operands[0];
            Assert.That(rebound.TypeGenericParameters.Single(), Is.SameAs(fixture.Parameter));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 其他方法拥有的开放参数不得重绑定Newobj接收者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var containingMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Create",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        var unrelatedMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Other",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        var fixture = CreateOpenListConstructorCall(
            unrelatedMethod,
            Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
            "TOther");

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(fixture.Call, containingMethod), Is.False);
            Assert.That(fixture.Call.Operands[0], Is.SameAs(fixture.OriginalTarget));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 晚期共享EnumeratorDispose按具体取址接收者重绑定()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var dispose = enumeratorDefinition.Methods.Single(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var target = new ConcreteGenericMethodAnalysisContext(dispose, [objectType], []);
        var receiver = new LocalVariable(
            "enumerator",
            new Register(null, "stack_-80"),
            enumeratorDefinition.MakeGenericInstanceType([stringType]));
        var call = new Instruction(0, OpCode.CallVoid, target, new AddressOf(receiver));
        var caller = CreateCaller(call);

        var changed = GenericCallRebinder.RunLateSharedReceiverTargets(caller);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(call.Operands[1], Is.TypeOf<AddressOf>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 晚期共享EnumeratorMoveNext保留布尔返回槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var moveNext = enumeratorDefinition.Methods.Single(method =>
            method.Name == "MoveNext" && method.Parameters.Count == 0);
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var target = new ConcreteGenericMethodAnalysisContext(moveNext, [objectType], []);
        var receiver = new LocalVariable(
            "enumerator",
            new Register(null, "stack_-80"),
            enumeratorDefinition.MakeGenericInstanceType([stringType]));
        var result = new LocalVariable(
            "moved",
            new Register(null, "W0"),
            app.SystemTypes.SystemBooleanType);
        var call = new Instruction(0, OpCode.Call, target, result, new AddressOf(receiver));
        var caller = CreateCaller(call);

        var changed = GenericCallRebinder.RunLateSharedReceiverTargets(caller);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { stringType }));
            Assert.That(result.Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 晚期不同具体Enumerator目标与接收者保持冲突红门()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var dispose = enumeratorDefinition.Methods.Single(method =>
            method.Name == "Dispose" && method.Parameters.Count == 0);
        var stringType = app.SystemTypes.SystemStringType;
        var target = new ConcreteGenericMethodAnalysisContext(dispose, [stringType], []);
        var receiver = new LocalVariable(
            "enumerator",
            new Register(null, "stack_-80"),
            enumeratorDefinition.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]));
        var call = new Instruction(0, OpCode.CallVoid, target, new AddressOf(receiver));
        var caller = CreateCaller(call);

        var changed = GenericCallRebinder.RunLateSharedReceiverTargets(caller);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
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

    [Test]
    [Category("基本功能")]
    public void Int32Enum共享链由Contains结果槽反向闭合ToList()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var enumerable = app.GetAssemblyByName("System.Core")!
            .GetTypeByFullName("System.Linq.Enumerable")!;
        var listDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var enumerableDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.IEnumerable`1")!;
        var int32Enum = mscorlib.GetTypeByFullName("System.Int32Enum")!;
        var concreteEnum = mscorlib.GetTypeByFullName("System.AttributeTargets")!;
        var toList = enumerable.Methods.Single(method =>
            method.Name == "ToList"
            && method.Parameters.Count == 1
            && method.GenericParameters.Count == 1);
        var contains = listDefinition.Methods.Single(method =>
            method.Name == "Contains" && method.Parameters.Count == 1);

        var toListTarget = new ConcreteGenericMethodAnalysisContext(toList, [], [int32Enum]);
        var containsTarget = new ConcreteGenericMethodAnalysisContext(contains, [int32Enum], []);
        var selected = new LocalVariable(
            "selected",
            new Register(null, "X0"),
            enumerableDefinition.MakeGenericInstanceType([int32Enum]));
        var list = new LocalVariable(
            "list",
            new Register(null, "X0", 2),
            listDefinition.MakeGenericInstanceType([int32Enum]));
        var found = new LocalVariable(
            "found",
            new Register(null, "W0"),
            app.SystemTypes.SystemBooleanType);
        var enumValue = new LocalVariable("value", new Register(null, "W1"), concreteEnum);
        var toListCall = new Instruction(0, OpCode.Call, toListTarget, list, selected);
        var containsCall = new Instruction(1, OpCode.Call, containsTarget, found, list, enumValue);
        var caller = CreateCaller(toListCall, containsCall);

        var changedPasses = GenericCallRebinder.RunLateSharedEnumClosure(caller);

        var reboundToList = (ConcreteGenericMethodAnalysisContext)toListCall.Operands[0];
        var reboundContains = (ConcreteGenericMethodAnalysisContext)containsCall.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(changedPasses, Is.GreaterThanOrEqualTo(1));
            Assert.That(reboundToList.MethodGenericParameters, Is.EqualTo(new[] { concreteEnum }));
            Assert.That(reboundContains.TypeGenericParameters, Is.EqualTo(new[] { concreteEnum }));
            Assert.That(selected.Type!.FullName, Does.Contain(concreteEnum.FullName));
            Assert.That(list.Type!.FullName, Does.Contain(concreteEnum.FullName));
        });
    }

    [Test]
    [Category("边界值")]
    public void Int32Enum共享接收者不得被普通Int32实参具体化()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var listDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var int32Enum = mscorlib.GetTypeByFullName("System.Int32Enum")!;
        var contains = listDefinition.Methods.Single(method =>
            method.Name == "Contains" && method.Parameters.Count == 1);
        var target = new ConcreteGenericMethodAnalysisContext(contains, [int32Enum], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            listDefinition.MakeGenericInstanceType([int32Enum]));
        var value = new LocalVariable(
            "value",
            new Register(null, "W1"),
            app.SystemTypes.SystemInt32Type);
        var result = new LocalVariable(
            "result",
            new Register(null, "W0"),
            app.SystemTypes.SystemBooleanType);
        var call = new Instruction(0, OpCode.Call, target, result, receiver, value);

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
            Assert.That(call.Operands[0], Is.SameAs(target));
            Assert.That(receiver.Type!.FullName, Does.Contain("System.Int32Enum"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Int32Enum共享目标遇到两个不同具体枚举时保持冲突红门()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var listDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var int32Enum = mscorlib.GetTypeByFullName("System.Int32Enum")!;
        var firstEnum = mscorlib.GetTypeByFullName("System.AttributeTargets")!;
        var secondEnum = mscorlib.GetTypeByFullName("System.DayOfWeek")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var pair = new InjectedMethodAnalysisContext(
            listDefinition,
            "Pair",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement, genericElement]);
        var target = new ConcreteGenericMethodAnalysisContext(pair, [int32Enum], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            listDefinition.MakeGenericInstanceType([int32Enum]));
        var first = new LocalVariable("first", new Register(null, "W1"), firstEnum);
        var second = new LocalVariable("second", new Register(null, "W2"), secondEnum);
        var call = new Instruction(0, OpCode.CallVoid, target, receiver, first, second);

        Assert.Multiple(() =>
        {
            Assert.That(GenericCallRebinder.TryRebind(call), Is.False);
            Assert.That(call.Operands[0], Is.SameAs(target));
            Assert.That(receiver.Type!.FullName, Does.Contain("System.Int32Enum"));
        });
    }

    private static MethodAnalysisContext CreateCaller(params Instruction[] calls)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "LateGenericCallOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var caller = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            .. calls,
            new Instruction(calls.Length, OpCode.Return),
        ]);
        return caller;
    }

    private static (
        Instruction Call,
        ConcreteGenericMethodAnalysisContext OriginalTarget,
        GenericParameterTypeAnalysisContext Parameter) CreateOpenListConstructorCall(
            HasGenericParameters parameterOwner,
            Il2CppTypeEnum parameterKind,
            string parameterName)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var constructor = listDefinition.Methods.Single(method =>
            method.Name == ".ctor" && method.Parameters.Count == 0);
        var originalTarget = new ConcreteGenericMethodAnalysisContext(
            constructor,
            [app.SystemTypes.SystemObjectType],
            []);
        var parameter = new GenericParameterTypeAnalysisContext(
            parameterName,
            0,
            parameterKind,
            GenericParameterAttributes.None,
            parameterOwner);
        parameterOwner.GenericParameters.Add(parameter);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            listDefinition.MakeGenericInstanceType([parameter]));
        var call = new Instruction(0, OpCode.CallVoid, originalTarget, receiver);
        return (call, originalTarget, parameter);
    }
}
