using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
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

    [Test]
    [Category("基本功能")]
    public void 对象寄存器复用时从最近字段赋值恢复虚表接收者类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var arrayListType = assembly.GetTypeByFullName("System.Collections.ArrayList")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "VirtualReceiverOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext(
            "GiftArray",
            arrayListType,
            System.Reflection.FieldAttributes.Private);
        field.OverrideOffset = 0x178;
        var method = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public);
        var thisLocal = new LocalVariable("this", new Register(null, "X0"), owner) { IsThis = true };
        var savedThis = new LocalVariable(
            "savedThis",
            new Register(null, "X19", 1),
            app.SystemTypes.SystemObjectType);
        method.CalleeSavedSsaCopyEvidence.Add((savedThis, thisLocal, 0));
        var reusedReceiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 281),
            app.SystemTypes.SystemObjectType);
        var assignedReceiver = new LocalVariable(
            "assignedReceiver",
            reusedReceiver.Register,
            app.SystemTypes.SystemObjectType);
        var klass = new LocalVariable("klass", new Register(null, "X8"));
        var predecessor = new Block
        {
            Instructions =
            {
                new Instruction(0, OpCode.Move, assignedReceiver, new MemoryOperand(savedThis, addend: 0x178)),
            },
        };
        var dispatchBlock = new Block
        {
            Instructions =
            {
                new Instruction(1, OpCode.Move, klass, new MemoryOperand(reusedReceiver)),
                new Instruction(2, OpCode.IndirectCall, new MemoryOperand(klass, addend: 0x388)),
            },
            Predecessors = { predecessor },
        };
        predecessor.Successors.Add(dispatchBlock);

        var resolved = MetadataResolver.ResolveLinearPredecessorVTableReceiverType(
            method,
            dispatchBlock,
            1,
            klass,
            out _);

        Assert.That(resolved, Is.SameAs(arrayListType));
    }

    [Test]
    [Category("基本功能")]
    public void 多前驱中的基类局部沿唯一SSA定义恢复为同一具体字段类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var arrayListType = assembly.GetTypeByFullName("System.Collections.ArrayList")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "VirtualReceiverConsensusOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext(
            "GiftArray",
            arrayListType,
            System.Reflection.FieldAttributes.Private);
        field.OverrideOffset = 0x178;
        var method = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public);
        var thisLocal = new LocalVariable("this", new Register(null, "X0"), owner) { IsThis = true };
        var savedThis = new LocalVariable(
            "savedThis",
            new Register(null, "X19", 1),
            app.SystemTypes.SystemObjectType);
        method.CalleeSavedSsaCopyEvidence.Add((savedThis, thisLocal, 0));

        var baseTypedOrigin = new LocalVariable(
            "baseTypedOrigin",
            new Register(null, "X0", 19),
            app.SystemTypes.SystemObjectType);
        var firstPhiInput = new LocalVariable(
            "firstPhiInput",
            new Register(null, "X0", 207),
            app.SystemTypes.SystemObjectType);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 281),
            app.SystemTypes.SystemObjectType);
        var klass = new LocalVariable("klass", new Register(null, "X8"));
        var originBlock = new Block
        {
            Instructions =
            {
                new Instruction(0, OpCode.Move, baseTypedOrigin, new MemoryOperand(savedThis, addend: 0x178)),
            },
        };
        var firstPredecessor = new Block
        {
            Instructions =
            {
                new Instruction(1, OpCode.Move, firstPhiInput, new MemoryOperand(savedThis, addend: 0x178)),
            },
            Predecessors = { originBlock },
        };
        var secondPredecessor = new Block
        {
            Instructions = { new Instruction(2, OpCode.Nop) },
            Predecessors = { originBlock },
        };
        var dispatchBlock = new Block
        {
            Instructions =
            {
                new Instruction(3, OpCode.Phi, receiver, firstPhiInput, baseTypedOrigin),
                new Instruction(4, OpCode.Move, klass, new MemoryOperand(receiver)),
                new Instruction(5, OpCode.IndirectCall, new MemoryOperand(klass, addend: 0x388)),
            },
            Predecessors = { firstPredecessor, secondPredecessor },
        };
        originBlock.Successors.Add(firstPredecessor);
        originBlock.Successors.Add(secondPredecessor);
        firstPredecessor.Successors.Add(dispatchBlock);
        secondPredecessor.Successors.Add(dispatchBlock);

        var resolved = MetadataResolver.ResolveLinearPredecessorVTableReceiverType(
            method,
            dispatchBlock,
            2,
            klass,
            out _);

        Assert.That(resolved, Is.SameAs(arrayListType));
    }

    [Test]
    [Category("边界值")]
    public void Klass读取之后再次复用接收者不改变已捕获虚表类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "VirtualReceiverBoundaryOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var method = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public);
        var receiverType = app.SystemTypes.SystemStringType;
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var later = new LocalVariable("later", new Register(null, "X1"), app.SystemTypes.SystemObjectType);
        var typedSource = new LocalVariable("typedSource", new Register(null, "X20"), receiverType);
        var klass = new LocalVariable("klass", new Register(null, "X8"));
        var dispatchBlock = new Block
        {
            Instructions =
            {
                new Instruction(0, OpCode.Move, receiver, typedSource),
                new Instruction(1, OpCode.Move, klass, new MemoryOperand(receiver)),
                new Instruction(2, OpCode.Move, receiver, later),
                new Instruction(3, OpCode.IndirectCall, new MemoryOperand(klass, addend: 0x138)),
            },
        };

        var resolved = MetadataResolver.ResolveLinearPredecessorVTableReceiverType(
            method,
            dispatchBlock,
            3,
            klass,
            out _);

        Assert.That(resolved, Is.SameAs(receiverType));
    }

    [Test]
    [Category("异常输入")]
    public void 多前驱歧义不推断虚表接收者类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "VirtualReceiverAmbiguousOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var method = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var firstReceiver = new LocalVariable("firstReceiver", receiver.Register, app.SystemTypes.SystemObjectType);
        var secondReceiver = new LocalVariable("secondReceiver", receiver.Register, app.SystemTypes.SystemObjectType);
        var firstSource = new LocalVariable("firstSource", new Register(null, "X20"), app.SystemTypes.SystemStringType);
        var arrayListType = assembly.GetTypeByFullName("System.Collections.ArrayList")!;
        var secondSource = new LocalVariable("secondSource", new Register(null, "X21"), arrayListType);
        var klass = new LocalVariable("klass", new Register(null, "X8"));
        var firstPredecessor = new Block
        {
            Instructions = { new Instruction(0, OpCode.Move, firstReceiver, firstSource) },
        };
        var secondPredecessor = new Block
        {
            Instructions = { new Instruction(0, OpCode.Move, secondReceiver, secondSource) },
        };
        var dispatchBlock = new Block
        {
            Instructions =
            {
                new Instruction(1, OpCode.Move, klass, new MemoryOperand(receiver)),
                new Instruction(2, OpCode.IndirectCall, new MemoryOperand(klass, addend: 0x138)),
            },
            Predecessors = { firstPredecessor, secondPredecessor },
        };

        var resolved = MetadataResolver.ResolveLinearPredecessorVTableReceiverType(
            method,
            dispatchBlock,
            1,
            klass,
            out _);

        Assert.That(resolved, Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 字符串槽地址Phi后的公共解引用恢复为字符串值Phi()
    {
        var first = new LocalVariable("first", new Register(null, "X8", 1));
        var second = new LocalVariable("second", new Register(null, "X8", 2));
        var merged = new LocalVariable("merged", new Register(null, "X8", 3));
        var result = new LocalVariable("result", new Register(null, "X0", 1));
        var phi = new Instruction(-1, OpCode.Phi, merged, first, second);
        var load = new Instruction(3, OpCode.Move, result, new MemoryOperand(merged));
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, first, new StringLiteral("alpha")),
            new(1, OpCode.Move, second, new StringLiteral("beta")),
            phi,
            load,
        };

        var changed = MetadataResolver.ResolvePhiBackedStringLoads(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(load.Operands[1], Is.SameAs(merged));
            Assert.That(phi.Operands.Skip(1).OfType<StringLiteral>().Select(item => item.Value),
                Is.EqualTo(new[] { "alpha", "beta" }));
        });
    }

    [Test]
    [Category("边界值")]
    public void 四百个字符串槽输入保持逐边映射且只改写一次公共读取()
    {
        const int inputCount = 400;
        var inputs = Enumerable.Range(0, inputCount)
            .Select(index => new LocalVariable($"input{index}", new Register(null, "X8", index + 1)))
            .ToArray();
        var merged = new LocalVariable("merged", new Register(null, "X8", inputCount + 1));
        var result = new LocalVariable("result", new Register(null, "X0", 1));
        var instructions = inputs
            .Select((input, index) => new Instruction(index, OpCode.Move, input, new StringLiteral($"value-{index}")))
            .ToList();
        var phi = new Instruction(-1, OpCode.Phi, new IOperand[] { merged }.Concat(inputs).ToList());
        var load = new Instruction(inputCount, OpCode.Move, result, new MemoryOperand(merged));
        instructions.Add(phi);
        instructions.Add(load);

        var changed = MetadataResolver.ResolvePhiBackedStringLoads(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(phi.Operands.Count, Is.EqualTo(inputCount + 1));
            Assert.That(phi.Operands.Skip(1).OfType<StringLiteral>().Select(item => item.Value).Distinct().Count(),
                Is.EqualTo(inputCount));
            Assert.That(load.Operands[1], Is.SameAs(merged));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 字符串Phi混入未解析地址时保持公共解引用原样()
    {
        var resolved = new LocalVariable("resolved", new Register(null, "X8", 1));
        var unresolved = new LocalVariable("unresolved", new Register(null, "X8", 2));
        var merged = new LocalVariable("merged", new Register(null, "X8", 3));
        var result = new LocalVariable("result", new Register(null, "X0", 1));
        var phi = new Instruction(-1, OpCode.Phi, merged, resolved, unresolved);
        var memory = new MemoryOperand(merged);
        var load = new Instruction(3, OpCode.Move, result, memory);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, resolved, new StringLiteral("alpha")),
            new(1, OpCode.Move, unresolved, new Immediate(0x1234)),
            phi,
            load,
        };

        var changed = MetadataResolver.ResolvePhiBackedStringLoads(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(load.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(phi.Operands[1], Is.SameAs(resolved));
            Assert.That(phi.Operands[2], Is.SameAs(unresolved));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Post27绝对槽地址Phi经二级读取恢复字符串且保留第一层定义()
    {
        var first = new LocalVariable("first", new Register(null, "X8", 1));
        var second = new LocalVariable("second", new Register(null, "X8", 2));
        var merged = new LocalVariable("merged", new Register(null, "X8", 3));
        var result = new LocalVariable("result", new Register(null, "X0", 1));
        var firstLoad = new Instruction(0, OpCode.Move, first, new MemoryOperand(addend: 0x1000));
        var secondLoad = new Instruction(1, OpCode.Move, second, new MemoryOperand(addend: 0x2000));
        var phi = new Instruction(-1, OpCode.Phi, merged, first, second);
        var commonLoad = new Instruction(2, OpCode.Move, result, new MemoryOperand(merged));
        var values = new Dictionary<ulong, string>
        {
            [0x1000] = "alpha",
            [0x2000] = "beta",
        };

        var changed = MetadataResolver.ResolvePhiBackedStringLoads(
            [firstLoad, secondLoad, phi, commonLoad],
            post27StringSlotResolver: address => values.TryGetValue(address, out var value)
                ? new StringLiteral(value)
                : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(firstLoad.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(secondLoad.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(phi.Operands.Skip(1).Cast<StringLiteral>().Select(value => value.Value),
                Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(commonLoad.Operands[1], Is.SameAs(merged));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 绝对槽直接解码失败后使用Post27二级表项()
    {
        var directCalls = 0;
        var tableCalls = 0;

        var resolved = MetadataResolver.ResolveAbsoluteSlotUsage<string>(
            0x5A22BB0,
            _ =>
            {
                directCalls++;
                return null;
            },
            (address, offset) =>
            {
                tableCalls++;
                return address == 0x5A22BB0 && offset == 0 ? "acLiveTo80" : null;
            });

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo("acLiveTo80"));
            Assert.That(directCalls, Is.EqualTo(1));
            Assert.That(tableCalls, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 绝对槽直接解码成功时不重复读取二级表项()
    {
        var tableCalls = 0;

        var resolved = MetadataResolver.ResolveAbsoluteSlotUsage<string>(
            ulong.MaxValue,
            address => address == ulong.MaxValue ? "direct" : null,
            (_, _) =>
            {
                tableCalls++;
                return "unexpected";
            });

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo("direct"));
            Assert.That(tableCalls, Is.Zero);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 绝对槽直接误解码为其他类型时继续读取字符串二级表项()
    {
        var tableCalls = 0;

        var resolved = MetadataResolver.ResolveAbsoluteSlotUsage(
            0x5A01398,
            _ => "method",
            (address, offset) =>
            {
                tableCalls++;
                return address == 0x5A01398 && offset == 0 ? "string" : null;
            },
            value => value == "string");

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo("string"));
            Assert.That(tableCalls, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 绝对槽两种布局均未解码时保持空结果()
    {
        var observedOffset = -1L;

        var resolved = MetadataResolver.ResolveAbsoluteSlotUsage<object>(
            0,
            _ => null,
            (_, offset) =>
            {
                observedOffset = offset;
                return null;
            });

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Null);
            Assert.That(observedOffset, Is.Zero);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 强类型字符串二层槽恢复所有共享读取并保留地址载体()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var beforeGuard = new LocalVariable("beforeGuard", new Register(null, "X21", 1));
        var afterGuard = new LocalVariable("afterGuard", new Register(null, "X21", 2));
        var tableBase = new LocalVariable("tableBase", new Register(null, "X21", 3));
        var firstResult = new LocalVariable("firstResult", new Register(null, "X0", 1), stringType);
        var secondResult = new LocalVariable("secondResult", new Register(null, "X0", 2), stringType);
        var beforeDefinition = new Instruction(0, OpCode.Move, beforeGuard, new MemoryOperand(addend: 0x5A00A98));
        var afterDefinition = new Instruction(1, OpCode.Move, afterGuard, new MemoryOperand(addend: 0x5A00A98));
        var tablePhi = new Instruction(-1, OpCode.Phi, tableBase, beforeGuard, afterGuard);
        var firstLoad = new Instruction(2, OpCode.Move, firstResult, new MemoryOperand(tableBase));
        var secondLoad = new Instruction(3, OpCode.Move, secondResult, new MemoryOperand(tableBase));
        var resolverCalls = 0;

        var changed = MetadataResolver.ResolveTypedPost27StringLoads(
            [beforeDefinition, afterDefinition, tablePhi, firstLoad, secondLoad],
            stringType,
            (address, offset) =>
            {
                resolverCalls++;
                return address == 0x5A00A98 && offset == 0 ? new StringLiteral("trophy") : null;
            });

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(2));
            Assert.That(resolverCalls, Is.EqualTo(1));
            Assert.That(beforeDefinition.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(afterDefinition.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(tablePhi.Operands.Skip(1), Is.EqualTo(new IOperand[] { beforeGuard, afterGuard }));
            Assert.That(((StringLiteral)firstLoad.Operands[1]).Value, Is.EqualTo("trophy"));
            Assert.That(((StringLiteral)secondLoad.Operands[1]).Value, Is.EqualTo("trophy"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 四百个不同偏移的强类型字符串二层槽全部恢复()
    {
        const int inputCount = 400;
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var tableBase = new LocalVariable("tableBase", new Register(null, "X21", 1));
        var tableDefinition = new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: 0x5A00000));
        var loads = Enumerable.Range(0, inputCount)
            .Select(index => new Instruction(
                index + 1,
                OpCode.Move,
                new LocalVariable($"result{index}", new Register(null, "X0", index + 1), stringType),
                new MemoryOperand(tableBase, addend: index * 8L)))
            .ToList();
        var instructions = new List<Instruction> { tableDefinition };
        instructions.AddRange(loads);

        var changed = MetadataResolver.ResolveTypedPost27StringLoads(
            instructions,
            stringType,
            (address, offset) => address == 0x5A00000
                ? new StringLiteral($"value-{offset / 8}")
                : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(inputCount));
            Assert.That(loads.Select(load => ((StringLiteral)load.Operands[1]).Value).Distinct().Count(),
                Is.EqualTo(inputCount));
            Assert.That(((StringLiteral)loads[^1].Operands[1]).Value, Is.EqualTo("value-399"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非字符串目标多定义载体和未解析表项均保持原样()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stringType = appContext.SystemTypes.SystemStringType;
        var tableBase = new LocalVariable("tableBase", new Register(null, "X21", 1));
        var ambiguousBase = new LocalVariable("ambiguousBase", new Register(null, "X22", 1));
        var differentFirst = new LocalVariable("differentFirst", new Register(null, "X23", 1));
        var differentSecond = new LocalVariable("differentSecond", new Register(null, "X23", 2));
        var differentPhi = new LocalVariable("differentPhi", new Register(null, "X23", 3));
        var integerResult = new LocalVariable(
            "integerResult",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt32Type);
        var stringResult = new LocalVariable("stringResult", new Register(null, "X0", 2), stringType);
        var unresolvedResult = new LocalVariable("unresolvedResult", new Register(null, "X0", 3), stringType);
        var differentResult = new LocalVariable("differentResult", new Register(null, "X0", 4), stringType);
        var integerMemory = new MemoryOperand(tableBase);
        var ambiguousMemory = new MemoryOperand(ambiguousBase);
        var unresolvedMemory = new MemoryOperand(tableBase, addend: 8);
        var differentMemory = new MemoryOperand(differentPhi);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, tableBase, new MemoryOperand(addend: 0x1000)),
            new(1, OpCode.Move, ambiguousBase, new MemoryOperand(addend: 0x2000)),
            new(2, OpCode.Move, ambiguousBase, new MemoryOperand(addend: 0x3000)),
            new(3, OpCode.Move, differentFirst, new MemoryOperand(addend: 0x4000)),
            new(4, OpCode.Move, differentSecond, new MemoryOperand(addend: 0x5000)),
            new(-1, OpCode.Phi, differentPhi, differentFirst, differentSecond),
            new(5, OpCode.Move, integerResult, integerMemory),
            new(6, OpCode.Move, stringResult, ambiguousMemory),
            new(7, OpCode.Move, unresolvedResult, unresolvedMemory),
            new(8, OpCode.Move, differentResult, differentMemory),
        };

        var changed = MetadataResolver.ResolveTypedPost27StringLoads(
            instructions,
            stringType,
            (_, _) => null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(instructions[6].Operands[1], Is.EqualTo(integerMemory));
            Assert.That(instructions[7].Operands[1], Is.EqualTo(ambiguousMemory));
            Assert.That(instructions[8].Operands[1], Is.EqualTo(unresolvedMemory));
            Assert.That(instructions[9].Operands[1], Is.EqualTo(differentMemory));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两个Post27字符串槽经条件选择后恢复字面量并删除返回解引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var condition = new LocalVariable(
            "condition",
            new Register(null, "Z", 1),
            appContext.SystemTypes.SystemBooleanType);
        var whenTrue = new LocalVariable("whenTrue", new Register(null, "X20", 1));
        var whenFalse = new LocalVariable("whenFalse", new Register(null, "X21", 1));
        var selected = new LocalVariable("selected", new Register(null, "X8", 1));
        var trueDefinition = new Instruction(0, OpCode.Move, whenTrue, new MemoryOperand(addend: 0x5A01398));
        var falseDefinition = new Instruction(1, OpCode.Move, whenFalse, new MemoryOperand(addend: 0x5A01388));
        var selection = new Instruction(
            2,
            OpCode.ConditionalSelect,
            selected,
            condition,
            whenTrue,
            whenFalse);
        var returnedMemory = new MemoryOperand(selected);
        var returned = new Instruction(3, OpCode.Return, returnedMemory);
        var writeTarget = new MemoryOperand(selected);
        var write = new Instruction(4, OpCode.Move, writeTarget, new Immediate(1));
        var values = new Dictionary<ulong, string>
        {
            [0x5A01398] = "silver",
            [0x5A01388] = "gold",
        };

        var changed = MetadataResolver.ResolvePost27ConditionalStringSelections(
            [trueDefinition, falseDefinition, selection, returned, write],
            appContext.SystemTypes.SystemStringType,
            address => values.TryGetValue(address, out var value) ? new StringLiteral(value) : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(selected.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
            Assert.That(((StringLiteral)selection.Operands[2]).Value, Is.EqualTo("silver"));
            Assert.That(((StringLiteral)selection.Operands[3]).Value, Is.EqualTo("gold"));
            Assert.That(trueDefinition.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(falseDefinition.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(returned.Operands[0], Is.SameAs(selected));
            Assert.That(write.Operands[0], Is.EqualTo(writeTarget));
        });
    }

    [Test]
    [Category("边界值")]
    public void 四百个同槽字符串条件选择只解析每个地址一次()
    {
        const int selectionCount = 400;
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var condition = new LocalVariable(
            "condition",
            new Register(null, "Z", 1),
            appContext.SystemTypes.SystemBooleanType);
        var instructions = new List<Instruction>();
        var selectedValues = new List<LocalVariable>();
        for (var index = 0; index < selectionCount; index++)
        {
            var selected = new LocalVariable($"selected{index}", new Register(null, "X8", index + 1));
            selectedValues.Add(selected);
            instructions.Add(new Instruction(
                index * 2,
                OpCode.ConditionalSelect,
                selected,
                condition,
                new MemoryOperand(addend: 0),
                new MemoryOperand(addend: long.MaxValue)));
            instructions.Add(new Instruction(index * 2 + 1, OpCode.Return, new MemoryOperand(selected)));
        }

        var resolverCalls = 0;
        var changed = MetadataResolver.ResolvePost27ConditionalStringSelections(
            instructions,
            appContext.SystemTypes.SystemStringType,
            address =>
            {
                resolverCalls++;
                return new StringLiteral(address == 0 ? "minimum" : "maximum");
            });

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(selectionCount));
            Assert.That(resolverCalls, Is.EqualTo(2));
            Assert.That(selectedValues.All(value => value.Type == appContext.SystemTypes.SystemStringType), Is.True);
            Assert.That(instructions.Where(instruction => instruction.OpCode == OpCode.Return)
                .All(instruction => instruction.Operands[0] is LocalVariable), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 条件选择任一槽未解析或含索引时保持原生地址数据流()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var condition = new LocalVariable(
            "condition",
            new Register(null, "Z", 1),
            appContext.SystemTypes.SystemBooleanType);
        var unresolvedResult = new LocalVariable("unresolved", new Register(null, "X8", 1));
        var indexedResult = new LocalVariable("indexed", new Register(null, "X8", 2));
        var index = new LocalVariable(
            "index",
            new Register(null, "X9", 1),
            appContext.SystemTypes.SystemInt32Type);
        var unresolved = new Instruction(
            0,
            OpCode.ConditionalSelect,
            unresolvedResult,
            condition,
            new MemoryOperand(addend: 0x1000),
            new MemoryOperand(addend: 0x2000));
        var indexed = new Instruction(
            1,
            OpCode.ConditionalSelect,
            indexedResult,
            condition,
            new MemoryOperand(indexRegister: index, addend: 0x3000),
            new MemoryOperand(addend: 0x4000));
        var unresolvedReturn = new Instruction(2, OpCode.Return, new MemoryOperand(unresolvedResult));
        var indexedReturn = new Instruction(3, OpCode.Return, new MemoryOperand(indexedResult));

        var changed = MetadataResolver.ResolvePost27ConditionalStringSelections(
            [unresolved, indexed, unresolvedReturn, indexedReturn],
            appContext.SystemTypes.SystemStringType,
            address => address == 0x1000 ? new StringLiteral("known") : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(unresolved.Operands[2], Is.TypeOf<MemoryOperand>());
            Assert.That(unresolved.Operands[3], Is.TypeOf<MemoryOperand>());
            Assert.That(indexed.Operands[2], Is.TypeOf<MemoryOperand>());
            Assert.That(unresolvedReturn.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(indexedReturn.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(unresolvedResult.Type, Is.Null);
            Assert.That(indexedResult.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 初始化保护区后的二层类型槽恢复未定型目标()
    {
        var targetType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var tableBase = new LocalVariable("tableBase", new Register(null, "X27", 1));
        var result = new LocalVariable("result", new Register(null, "X1", 1));
        var tableDefinition = new Instruction(
            0,
            OpCode.Move,
            tableBase,
            new MemoryOperand(addend: 0x59F7388));
        var load = new Instruction(1, OpCode.Move, result, new MemoryOperand(tableBase));
        ulong observedAddress = 0;
        long observedOffset = -1;

        var changed = MetadataResolver.ResolvePost27TypeLoads(
            [tableDefinition, load],
            new HashSet<ulong> { 0x59F7388 },
            (address, offset) =>
            {
                observedAddress = address;
                observedOffset = offset;
                return targetType;
            });

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(observedAddress, Is.EqualTo(0x59F7388));
            Assert.That(observedOffset, Is.Zero);
            Assert.That(load.Operands[1], Is.SameAs(targetType));
            Assert.That(result.Type, Is.TypeOf<RuntimeClassTypeAnalysisContext>());
            Assert.That(((RuntimeClassTypeAnalysisContext)result.Type!).RepresentedType, Is.SameAs(targetType));
            Assert.That(tableDefinition.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 同址Phi与最大对齐偏移的二层类型读取只解析一次()
    {
        var targetType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType;
        var first = new LocalVariable("first", new Register(null, "X27", 1));
        var second = new LocalVariable("second", new Register(null, "X27", 2));
        var tableBase = new LocalVariable("tableBase", new Register(null, "X27", 3));
        var firstResult = new LocalVariable("firstResult", new Register(null, "X1", 1));
        var secondResult = new LocalVariable("secondResult", new Register(null, "X1", 2));
        var firstDefinition = new Instruction(0, OpCode.Move, first, new MemoryOperand(addend: 0x5A00000));
        var secondDefinition = new Instruction(1, OpCode.Move, second, new MemoryOperand(addend: 0x5A00000));
        var tablePhi = new Instruction(-1, OpCode.Phi, tableBase, first, second);
        var firstLoad = new Instruction(2, OpCode.Move, firstResult, new MemoryOperand(tableBase, addend: 0x7FFF8));
        var secondLoad = new Instruction(3, OpCode.Move, secondResult, new MemoryOperand(tableBase, addend: 0x7FFF8));
        var resolverCalls = 0;

        var changed = MetadataResolver.ResolvePost27TypeLoads(
            [firstDefinition, secondDefinition, tablePhi, firstLoad, secondLoad],
            new HashSet<ulong> { 0x5A00000 },
            (address, offset) =>
            {
                resolverCalls++;
                return address == 0x5A00000 && offset == 0x7FFF8 ? targetType : null;
            });

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(2));
            Assert.That(resolverCalls, Is.EqualTo(1));
            Assert.That(firstLoad.Operands[1], Is.SameAs(targetType));
            Assert.That(secondLoad.Operands[1], Is.SameAs(targetType));
            Assert.That(
                ((RuntimeClassTypeAnalysisContext)firstResult.Type!).RepresentedType,
                Is.SameAs(targetType));
            Assert.That(
                ((RuntimeClassTypeAnalysisContext)secondResult.Type!).RepresentedType,
                Is.SameAs(targetType));
            Assert.That(tablePhi.Operands.Skip(1), Is.EqualTo(new IOperand[] { first, second }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 异址Phi多定义地址和非类型表项均不改写()
    {
        var ambiguous = new LocalVariable("ambiguous", new Register(null, "X27", 1));
        var first = new LocalVariable("first", new Register(null, "X28", 1));
        var second = new LocalVariable("second", new Register(null, "X28", 2));
        var differentPhi = new LocalVariable("differentPhi", new Register(null, "X28", 3));
        var unresolvedBase = new LocalVariable("unresolvedBase", new Register(null, "X29", 1));
        var ambiguousMemory = new MemoryOperand(ambiguous);
        var differentMemory = new MemoryOperand(differentPhi);
        var unresolvedMemory = new MemoryOperand(unresolvedBase);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, ambiguous, new MemoryOperand(addend: 0x1000)),
            new(1, OpCode.Move, ambiguous, new MemoryOperand(addend: 0x2000)),
            new(2, OpCode.Move, first, new MemoryOperand(addend: 0x3000)),
            new(3, OpCode.Move, second, new MemoryOperand(addend: 0x4000)),
            new(-1, OpCode.Phi, differentPhi, first, second),
            new(4, OpCode.Move, unresolvedBase, new MemoryOperand(addend: 0x5000)),
            new(5, OpCode.Move, new LocalVariable("a", new Register(null, "X0", 1)), ambiguousMemory),
            new(6, OpCode.Move, new LocalVariable("b", new Register(null, "X0", 2)), differentMemory),
            new(7, OpCode.Move, new LocalVariable("c", new Register(null, "X0", 3)), unresolvedMemory),
        };

        var changed = MetadataResolver.ResolvePost27TypeLoads(
            instructions,
            new HashSet<ulong> { 0x1000, 0x3000, 0x4000, 0x5000 },
            (_, _) => null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(instructions[6].Operands[1], Is.EqualTo(ambiguousMemory));
            Assert.That(instructions[7].Operands[1], Is.EqualTo(differentMemory));
            Assert.That(instructions[8].Operands[1], Is.EqualTo(unresolvedMemory));
        });
    }

    [Test]
    [Category("边界值")]
    public void 空初始化槽目录不扫描二层类型读取()
    {
        var tableBase = new LocalVariable("tableBase", new Register(null, "X27", 1));
        var result = new LocalVariable("result", new Register(null, "X1", 1));
        var memory = new MemoryOperand(tableBase);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, tableBase, new MemoryOperand(addend: 0x59F7388)),
            new(1, OpCode.Move, result, memory),
        };

        var changed = MetadataResolver.ResolvePost27TypeLoads(
            instructions,
            new HashSet<ulong>(),
            (_, _) => throw new AssertionException("空目录不得调用元数据解析器"));

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(instructions[1].Operands[1], Is.EqualTo(memory));
            Assert.That(result.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 循环复制链只判定一次并保持二层读取原样()
    {
        var first = new LocalVariable("first", new Register(null, "X27", 1));
        var second = new LocalVariable("second", new Register(null, "X27", 2));
        var result = new LocalVariable("result", new Register(null, "X1", 1));
        var memory = new MemoryOperand(first);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, first, second),
            new(1, OpCode.Move, second, first),
            new(2, OpCode.Move, result, memory),
        };

        var changed = MetadataResolver.ResolvePost27TypeLoads(
            instructions,
            new HashSet<ulong> { 0x59EF8C0 },
            (_, _) => throw new AssertionException("循环复制链不得查询元数据表项"));

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(instructions[2].Operands[1], Is.EqualTo(memory));
            Assert.That(result.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 静态偏移跳过常量并解析真实存储字段()
    {
        var fixture = CreateStaticFieldOffsetFixture(includeLiteral: true, runtimeFieldCount: 1);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(fixture.Access.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Access.Operands[1]).Field, Is.SameAs(fixture.RuntimeFields.Single()));
        });
    }

    [Test]
    [Category("边界值")]
    public void 静态偏移仅有一个真实字段时保持精确解析()
    {
        var fixture = CreateStaticFieldOffsetFixture(includeLiteral: false, runtimeFieldCount: 1);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(((FieldReference)fixture.Access.Operands[1]).Field, Is.SameAs(fixture.RuntimeFields.Single()));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同偏移多个真实静态字段保持未解析()
    {
        var fixture = CreateStaticFieldOffsetFixture(includeLiteral: true, runtimeFieldCount: 2);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(fixture.Access.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 静态偏移只有常量时保持未解析()
    {
        var fixture = CreateStaticFieldOffsetFixture(includeLiteral: true, runtimeFieldCount: 0);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(fixture.Access.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("基本功能")]
    public void 托管对象加常量地址与附加偏移合并为实例字段()
    {
        var fixture = CreateIndirectInstanceFieldFixture(
            addressOffsets: [0x20],
            useIndexedMemory: false);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(fixture.Access.Operands[0], Is.TypeOf<FieldReference>());
            var reference = (FieldReference)fixture.Access.Operands[0];
            Assert.That(reference.Field, Is.SameAs(fixture.Field));
            Assert.That(reference.Local, Is.SameAs(fixture.Receiver));
            Assert.That(reference.Offset, Is.EqualTo(0x25));
        });
    }

    [Test]
    [Category("边界值")]
    public void 带索引的托管地址访问保持内存语义()
    {
        var fixture = CreateIndirectInstanceFieldFixture(
            addressOffsets: [0x20],
            useIndexedMemory: true);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(fixture.Access.Operands[0], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("基本功能")]
    public void 分支上等价的托管字段地址解析为同一字段()
    {
        var fixture = CreateIndirectInstanceFieldFixture(
            addressOffsets: [0x20, 0x20],
            useIndexedMemory: false);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(fixture.Access.Operands[0], Is.TypeOf<FieldReference>());
            var reference = (FieldReference)fixture.Access.Operands[0];
            Assert.That(reference.Field, Is.SameAs(fixture.Field));
            Assert.That(reference.Local, Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("边界值")]
    public void 四百条等价地址定义全部核对后仍解析字段()
    {
        var fixture = CreateIndirectInstanceFieldFixture(
            addressOffsets: Enumerable.Repeat(0x20L, 400).ToArray(),
            useIndexedMemory: false);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(fixture.Access.Operands[0], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Access.Operands[0]).Field, Is.SameAs(fixture.Field));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 任一分支字段地址偏移漂移时保持未解析()
    {
        var fixture = CreateIndirectInstanceFieldFixture(
            addressOffsets: [0x20, 0x20, 0x28],
            useIndexedMemory: false);

        var changed = MetadataResolver.ResolveFieldOffsets(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(fixture.Access.Operands[0], Is.TypeOf<MemoryOperand>());
        });
    }

    private static IndirectInstanceFieldFixture CreateIndirectInstanceFieldFixture(
        IReadOnlyList<long> addressOffsets,
        bool useIndexedMemory)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "IndirectInstanceFieldOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext(
            "PackedFlag",
            app.SystemTypes.SystemBooleanType,
            System.Reflection.FieldAttributes.Private);
        field.OverrideOffset = 0x25;
        var method = owner.InjectMethodContext(
            "Write",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public);
        var receiver = new LocalVariable("this", new Register(null, "X0"), owner);
        var address = new LocalVariable("address", new Register(null, "X8"));
        var index = new LocalVariable(
            "index",
            new Register(null, "X9"),
            app.SystemTypes.SystemInt32Type);
        var instructions = addressOffsets
            .Select((offset, index) =>
                new Instruction(index, OpCode.Add, address, receiver, new Immediate(offset)))
            .ToList();
        var memory = useIndexedMemory
            ? new MemoryOperand(address, index, addend: 5, scale: 1)
            : new MemoryOperand(address, addend: 5);
        var access = new Instruction(addressOffsets.Count, OpCode.Move, memory, new Immediate(0));
        instructions.Add(access);
        instructions.Add(new Instruction(addressOffsets.Count + 1, OpCode.Return));
        method.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        return new IndirectInstanceFieldFixture(method, receiver, field, access);
    }

    private sealed record IndirectInstanceFieldFixture(
        MethodAnalysisContext Method,
        LocalVariable Receiver,
        InjectedFieldAnalysisContext Field,
        Instruction Access);

    private static StaticFieldOffsetFixture CreateStaticFieldOffsetFixture(
        bool includeLiteral,
        int runtimeFieldCount)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "StaticFieldOwner",
            app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);

        if (includeLiteral)
        {
            var literal = owner.InjectFieldContext(
                "MetadataOnlyConstant",
                app.SystemTypes.SystemStringType,
                System.Reflection.FieldAttributes.Public
                | System.Reflection.FieldAttributes.Static
                | System.Reflection.FieldAttributes.Literal
                | System.Reflection.FieldAttributes.HasDefault);
            literal.OverrideOffset = 0;
            literal.UseOverrideConstantValue = true;
            literal.OverrideConstantValue = "fixture";
        }

        var runtimeFields = Enumerable.Range(0, runtimeFieldCount)
            .Select(index =>
            {
                var field = owner.InjectFieldContext(
                    $"RuntimeField{index}",
                    app.SystemTypes.SystemObjectType,
                    System.Reflection.FieldAttributes.Private | System.Reflection.FieldAttributes.Static);
                field.OverrideOffset = 0;
                return field;
            })
            .ToArray();
        var method = owner.InjectMethodContext(
            "Read",
            app.SystemTypes.SystemObjectType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static);
        var storage = new LocalVariable(
            "staticStorage",
            new Register(null, "X8"),
            new StaticFieldStorageTypeAnalysisContext(owner, assembly));
        var result = new LocalVariable(
            "result",
            new Register(null, "X0"),
            app.SystemTypes.SystemObjectType);
        var access = new Instruction(0, OpCode.Move, result, new MemoryOperand(storage));
        method.ControlFlowGraph = new ISILControlFlowGraph([
            access,
            new Instruction(1, OpCode.Return, result),
        ]);

        return new StaticFieldOffsetFixture(method, access, runtimeFields);
    }

    private sealed record StaticFieldOffsetFixture(
        MethodAnalysisContext Method,
        Instruction Access,
        InjectedFieldAnalysisContext[] RuntimeFields);
}
