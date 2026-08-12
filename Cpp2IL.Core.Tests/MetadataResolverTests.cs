using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
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

    [Test]
    [Category("基本功能")]
    public void 泛型参数虚表槽映射到对象声明()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericParameter = listDefinition.GenericParameters.Single();
        var objectEquals = app.SystemTypes.SystemObjectType.Methods.Single(method =>
            method.Name == "Equals" && method.Parameters.Count == 1);

        var resolved = MetadataResolver.ResolveVTableSlot(app, genericParameter, objectEquals.Definition!.slot);

        Assert.That(resolved, Is.SameAs(objectEquals));
    }

    [Test]
    [Category("边界值")]
    public void 泛型参数对象虚表末槽仍可解析()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericParameter = listDefinition.GenericParameters.Single();
        var slot = app.SystemTypes.SystemObjectType.Definition!.VtableCount - 1;

        Assert.That(MetadataResolver.ResolveVTableSlot(app, genericParameter, slot), Is.Not.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 泛型参数负虚表槽不返回伪方法()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;

        Assert.That(
            MetadataResolver.ResolveVTableSlot(app, listDefinition.GenericParameters.Single(), -1),
            Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 泛型抽象虚表槽从开放声明恢复具体方法()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var comparerDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.EqualityComparer`1")!;
        var equals = comparerDefinition.Methods.Single(method =>
            method.Name == "Equals" && method.Parameters.Count == 2);
        var receiver = comparerDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);

        var resolved = MetadataResolver.ResolveVTableSlot(app, receiver, equals.Definition!.slot);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.TypeOf<ConcreteGenericMethodAnalysisContext>());
            Assert.That(resolved!.Name, Is.EqualTo("Equals"));
            Assert.That(resolved.Parameters.Select(parameter => parameter.ParameterType),
                Is.All.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 泛型抽象虚表解析保留接收者类型实参()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var comparerDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.EqualityComparer`1")!;
        var hashCode = comparerDefinition.Methods.Single(method =>
            method.Name == "GetHashCode" && method.Parameters.Count == 1);
        var receiver = comparerDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);

        var resolved = MetadataResolver.ResolveVTableSlot(app, receiver, hashCode.Definition!.slot);

        Assert.That(resolved!.Parameters.Single().ParameterType, Is.SameAs(app.SystemTypes.SystemObjectType));
    }

    [Test]
    [Category("异常输入")]
    public void 泛型虚表越界槽不返回伪方法()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var comparerDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.EqualityComparer`1")!;
        var receiver = comparerDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);

        Assert.That(
            MetadataResolver.ResolveVTableSlot(app, receiver, int.MaxValue),
            Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 开放泛型虚表方法按封闭接收者恢复返回类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getItem = listDefinition.Methods.Single(method =>
            method.Name == "get_Item" && method.Parameters.Count == 1);
        var receiver = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);

        var specialized = MetadataResolver.SpecializeVTableMethodForReceiver(receiver, getItem);

        var concrete = (ConcreteGenericMethodAnalysisContext)specialized;
        Assert.Multiple(() =>
        {
            Assert.That(specialized, Is.TypeOf<ConcreteGenericMethodAnalysisContext>());
            Assert.That(concrete.TypeGenericParameters, Is.EqualTo(new[] { app.SystemTypes.SystemStringType }));
            Assert.That(specialized.DeclaringType!.FullName, Is.EqualTo(receiver.FullName));
            Assert.That(specialized.ReturnType, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 显式泛型接口虚表槽恢复为封闭接口声明()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var dictionaryDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.Dictionary`2")!;
        var interfaceEntry = dictionaryDefinition.Definition!.InterfaceOffsets
            .Select(entry => (entry, type: app.ResolveIl2CppType(entry.Type)))
            .First(pair => pair.type is GenericInstanceTypeAnalysisContext generic
                && generic.GenericType.Methods.Any(method => method.Definition?.slot != ushort.MaxValue));
        var openInterface = (GenericInstanceTypeAnalysisContext)interfaceEntry.type;
        var interfaceMethod = openInterface.GenericType.Methods.First(method =>
            method.Definition?.slot != ushort.MaxValue
            && (method.ReturnType is GenericParameterTypeAnalysisContext
                || method.Parameters.Any(parameter => parameter.ParameterType is GenericParameterTypeAnalysisContext)));
        var receiver = dictionaryDefinition.MakeGenericInstanceType([
            app.SystemTypes.SystemStringType,
            app.SystemTypes.SystemObjectType
        ]);

        var resolved = MetadataResolver.ResolveVTableSlot(
            app,
            receiver,
            interfaceEntry.entry.offset + interfaceMethod.Definition!.slot);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.TypeOf<ConcreteGenericMethodAnalysisContext>());
            Assert.That(resolved!.Name, Is.EqualTo(interfaceMethod.Name));
            Assert.That(resolved.DeclaringType!.FullName, Does.Not.Contain("!"));
            Assert.That(resolved.DeclaringType.FullName, Does.Contain("System.String").Or.Contain("System.Object"));
            Assert.That(
                new[] { resolved.ReturnType }.Concat(resolved.Parameters.Select(parameter => parameter.ParameterType))
                    .All(type => !type.FullName.Contains('!')),
                Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 已具体化虚表方法保持原身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getItem = listDefinition.Methods.Single(method =>
            method.Name == "get_Item" && method.Parameters.Count == 1);
        var receiver = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var existing = new ConcreteGenericMethodAnalysisContext(
            getItem,
            [app.SystemTypes.SystemStringType],
            []);

        Assert.That(
            MetadataResolver.SpecializeVTableMethodForReceiver(receiver, existing),
            Is.SameAs(existing));
    }

    [Test]
    [Category("异常输入")]
    public void 非泛型接收者不具体化泛型虚表方法()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getItem = listDefinition.Methods.Single(method =>
            method.Name == "get_Item" && method.Parameters.Count == 1);

        Assert.That(
            MetadataResolver.SpecializeVTableMethodForReceiver(app.SystemTypes.SystemObjectType, getItem),
            Is.SameAs(getItem));
    }
}
