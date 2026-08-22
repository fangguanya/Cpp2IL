using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class LocalVariablesTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 有符号整数转单精度结果覆盖先到的Enumerator占位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var enumeratorType = enumeratorDefinition.MakeGenericInstanceType(
            [appContext.SystemTypes.SystemObjectType]);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "V0", 4),
            enumeratorType);
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemInt32Type);
        var conversion = new Instruction(
            0,
            OpCode.ConvertSignedIntegerToFloat,
            destination,
            source,
            new Immediate(32),
            new Immediate(32));

        var changed = LocalVariables.BindNumericConversionTypes(conversion, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Smaddl有符号拓宽把W源定型为Int32并把临时结果定型为Int64()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "SMADDL_LEFT_SIGNED64_271BCA8", 0));
        var source = new LocalVariable("source", new Register(null, "X10", 3));
        var conversion = new Instruction(
            0,
            OpCode.ConvertSignedIntegerWidth,
            destination,
            source,
            new Immediate(64),
            new Immediate(32));

        var changed = LocalVariables.BindNumericConversionTypes(conversion, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Ubfm逻辑右移按X目标位宽传播Int64()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(null, "X9", 1));
        var source = new LocalVariable(
            "source",
            new Register(null, "X8", 3),
            appContext.SystemTypes.SystemInt64Type);
        var shift = new Instruction(
            0,
            OpCode.ShiftRightUnsigned,
            destination,
            source,
            new Immediate(63))
        {
            IntegerWidthBits = 64,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(shift, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void Phi全部局部入边类型一致时才建立目标类型共识()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var singleType = appContext.SystemTypes.SystemSingleType;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var first = new LocalVariable("first", new Register(null, "V0", 5), singleType);
        var second = new LocalVariable("second", new Register(null, "V0", 6), singleType);
        var phi = new Instruction(-1, OpCode.Phi, destination, first, second);

        var changed = LocalVariables.PropagatePhi(phi);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(singleType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Phi混合Enumerator与单精度入边时拒绝跨语义类型传播()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var enumeratorType = enumeratorDefinition.MakeGenericInstanceType(
            [appContext.SystemTypes.SystemObjectType]);
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var enumerator = new LocalVariable("enumerator", new Register(null, "V0", 4), enumeratorType);
        var weight = new LocalVariable(
            "weight",
            new Register(null, "V0", 5),
            appContext.SystemTypes.SystemSingleType);
        var phi = new Instruction(-1, OpCode.Phi, destination, enumerator, weight);

        var changed = LocalVariables.PropagatePhi(phi);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
            Assert.That(enumerator.Type, Is.SameAs(enumeratorType));
            Assert.That(weight.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Phi存在未定型局部入边时等待完整证据()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var known = new LocalVariable(
            "known",
            new Register(null, "V0", 5),
            appContext.SystemTypes.SystemSingleType);
        var unknown = new LocalVariable("unknown", new Register(null, "V0", 6));
        var phi = new Instruction(-1, OpCode.Phi, destination, known, unknown);

        var changed = LocalVariables.PropagatePhi(phi);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
            Assert.That(unknown.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void Phi具体泛型入边共识覆盖object共享占位()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listObject = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemObjectType]);
        var listString = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemStringType]);
        var destination = new LocalVariable("destination", new Register(null, "X19", 8), listObject);
        var first = new LocalVariable("first", new Register(null, "X0", 5), listString);
        var second = new LocalVariable("second", new Register(null, "X19", 6), listString);
        var phi = new Instruction(-1, OpCode.Phi, destination, first, second);

        var changed = LocalVariables.PropagatePhi(phi);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(GenericCallRebinder.TypesEquivalent(destination.Type, listString), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Phi不同具体泛型不得覆盖既有业务类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listString = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemStringType]);
        var listInt = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemInt32Type]);
        var destination = new LocalVariable("destination", new Register(null, "X19", 8), listString);
        var first = new LocalVariable("first", new Register(null, "X0", 5), listInt);
        var second = new LocalVariable("second", new Register(null, "X19", 6), listInt);
        var phi = new Instruction(-1, OpCode.Phi, destination, first, second);

        var changed = LocalVariables.PropagatePhi(phi);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(GenericCallRebinder.TypesEquivalent(destination.Type, listString), Is.True);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退SSA为相同单精度类型保留Phi复制()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8), singleType);
        var source = new LocalVariable("source", new Register(null, "V0", 5), singleType);

        Assert.That(SsaForm.ShouldEmitPhiCopy(destination, source), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 退SSA允许引用实例流入目标基类()
    {
        var systemTypes = Cpp2IlApi.CurrentAppContext!.SystemTypes;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 8),
            systemTypes.SystemObjectType);
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 5),
            systemTypes.SystemStringType);

        Assert.That(SsaForm.ShouldEmitPhiCopy(destination, source), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 退SSA拒绝Enumerator流入单精度目标()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumeratorDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var enumeratorType = enumeratorDefinition.MakeGenericInstanceType(
            [appContext.SystemTypes.SystemObjectType]);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "V0", 8),
            appContext.SystemTypes.SystemSingleType);
        var source = new LocalVariable("source", new Register(null, "V0", 4), enumeratorType);

        Assert.That(SsaForm.ShouldEmitPhiCopy(destination, source), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 单精度字面量驱动乘法目标与局部源定型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var source = new LocalVariable("source", new Register(null, "V1", 5));
        var multiply = new Instruction(
            0,
            OpCode.Multiply,
            destination,
            source,
            new FloatLiteral(8f));

        var changed = LocalVariables.BindFloatingArithmeticTypes(multiply, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 双精度字面量保持六十四位算术域()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var source = new LocalVariable("source", new Register(null, "V1", 5));
        var add = new Instruction(0, OpCode.Add, destination, source, new DoubleLiteral(double.MaxValue));

        var changed = LocalVariables.BindFloatingArithmeticTypes(add, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemDoubleType));
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemDoubleType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 混合单双精度源保持开放而不猜测转换方向()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("destination", new Register(null, "V0", 8));
        var add = new Instruction(
            0,
            OpCode.Add,
            destination,
            new FloatLiteral(1f),
            new DoubleLiteral(1d));

        var changed = LocalVariables.BindFloatingArithmeticTypes(add, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("边界值")]
    public void 绝对值位宽不得覆盖已经落定的另一浮点生命期()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var shared = new LocalVariable(
            "shared",
            new Register(null, "V0", 8),
            appContext.SystemTypes.SystemSingleType);
        var absolute = new Instruction(
            0,
            OpCode.AbsoluteNumber,
            shared,
            shared,
            new Immediate(64));

        var firstChanged = LocalVariables.BindScalarFloatingMathTypes(absolute, appContext);
        var secondChanged = LocalVariables.BindScalarFloatingMathTypes(absolute, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(firstChanged, Is.False);
            Assert.That(secondChanged, Is.False);
            Assert.That(shared.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 单精度算术不得覆盖已经落定的双精度目标()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "V0", 8),
            appContext.SystemTypes.SystemDoubleType);
        var multiply = new Instruction(
            0,
            OpCode.Multiply,
            destination,
            new FloatLiteral(2f),
            new FloatLiteral(8f));

        var firstChanged = LocalVariables.BindFloatingArithmeticTypes(multiply, appContext);
        var secondChanged = LocalVariables.BindFloatingArithmeticTypes(multiply, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(firstChanged, Is.False);
            Assert.That(secondChanged, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemDoubleType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析字段加载覆盖先到的Object宽类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X19", 1),
            appContext.SystemTypes.SystemObjectType);

        var changed = LocalVariables.BindResolvedFieldLoadType(
            destination,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析泛型字段覆盖共享ListObject占位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var sharedType = listType.MakeGenericInstanceType([appContext.SystemTypes.SystemObjectType]);
        var fieldType = listType.MakeGenericInstanceType([appContext.SystemTypes.SystemStringType]);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X19", 1),
            sharedType);

        var changed = LocalVariables.BindResolvedFieldLoadType(destination, fieldType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(fieldType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Post27二级表恢复StringEmpty静态字段所有者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stringType = appContext.SystemTypes.SystemStringType;
        var emptyField = stringType.Fields.Single(field =>
            field.IsStatic
            && field.Name == "Empty"
            && GenericCallRebinder.TypesEquivalent(field.FieldType, stringType));
        var tableBase = new LocalVariable("tableBase", new Register(null, "X8", 1));
        var runtimeClass = new LocalVariable("runtimeClass", new Register(null, "X8", 2));
        var staticStorage = new LocalVariable("staticStorage", new Register(null, "X8", 3));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [tableBase] = new(0, OpCode.Move, tableBase, new MemoryOperand(null, null, 0x1000)),
            [runtimeClass] = new(1, OpCode.Move, runtimeClass, new MemoryOperand(tableBase, null, 0x90)),
            [staticStorage] = new(2, OpCode.Move, staticStorage, new MemoryOperand(runtimeClass, null, 0xB8)),
        };
        var argument = new MemoryOperand(staticStorage, null, emptyField.Offset);

        var firstResolved = LocalVariables.TryResolveSelfTypedStaticFieldLoad(
            argument,
            stringType,
            definitions,
            0xB8,
            out var resolvedField);
        var secondResolved = LocalVariables.TryResolveSelfTypedStaticFieldLoad(
            resolvedField!,
            stringType,
            definitions,
            0xB8,
            out var repeatedField);

        Assert.Multiple(() =>
        {
            Assert.That(firstResolved, Is.True);
            Assert.That(secondResolved, Is.False);
            Assert.That(resolvedField, Is.Not.Null);
            Assert.That(resolvedField!.Field.Name, Is.EqualTo("Empty"));
            Assert.That(repeatedField, Is.Null);
            Assert.That(runtimeClass.Type, Is.TypeOf<RuntimeClassTypeAnalysisContext>());
            Assert.That(
                ((RuntimeClassTypeAnalysisContext)runtimeClass.Type!).RepresentedType,
                Is.SameAs(stringType));
            Assert.That(staticStorage.Type, Is.TypeOf<StaticFieldStorageTypeAnalysisContext>());
            Assert.That(
                ((StaticFieldStorageTypeAnalysisContext)staticStorage.Type!).OwnerType,
                Is.SameAs(stringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 返回签名恢复StringEmpty静态字段()
    {
        var fixture = CreateStringEmptyReturnFixture();

        var resolvedCount = LocalVariables.ResolveExpectedSelfTypedStaticFields(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(resolvedCount, Is.EqualTo(1));
            Assert.That(fixture.Return.Operands[0], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Return.Operands[0]).Field.Name, Is.EqualTo("Empty"));
            Assert.That(fixture.RuntimeClass.Type, Is.TypeOf<RuntimeClassTypeAnalysisContext>());
            Assert.That(fixture.StaticStorage.Type, Is.TypeOf<StaticFieldStorageTypeAnalysisContext>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 返回字段偏移不匹配时保持原始静态区读取()
    {
        var fixture = CreateStringEmptyReturnFixture(fieldOffsetDelta: 8);

        var resolvedCount = LocalVariables.ResolveExpectedSelfTypedStaticFields(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(resolvedCount, Is.Zero);
            Assert.That(fixture.Return.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(fixture.RuntimeClass.Type, Is.Null);
            Assert.That(fixture.StaticStorage.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 返回链已有冲突类身份时保持原始静态区读取()
    {
        var fixture = CreateStringEmptyReturnFixture(conflictingRuntimeClass: true);

        var resolvedCount = LocalVariables.ResolveExpectedSelfTypedStaticFields(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(resolvedCount, Is.Zero);
            Assert.That(fixture.Return.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(
                ((RuntimeClassTypeAnalysisContext)fixture.RuntimeClass.Type!).RepresentedType,
                Is.SameAs(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType));
            Assert.That(fixture.StaticStorage.Type, Is.Null);
        });
    }

    [Test]
    [Category("边界值")]
    public void 错位StaticFields读取不得绑定StringEmpty所有者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stringType = appContext.SystemTypes.SystemStringType;
        var emptyField = stringType.Fields.Single(field => field.IsStatic && field.Name == "Empty");
        var tableBase = new LocalVariable("tableBase", new Register(null, "X8", 1));
        var runtimeClass = new LocalVariable("runtimeClass", new Register(null, "X8", 2));
        var staticStorage = new LocalVariable("staticStorage", new Register(null, "X8", 3));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [tableBase] = new(0, OpCode.Move, tableBase, new MemoryOperand(null, null, 0x1000)),
            [runtimeClass] = new(1, OpCode.Move, runtimeClass, new MemoryOperand(tableBase, null, 0x90)),
            [staticStorage] = new(2, OpCode.Move, staticStorage, new MemoryOperand(runtimeClass, null, 0xB0)),
        };

        var resolved = LocalVariables.TryResolveSelfTypedStaticFieldLoad(
            new MemoryOperand(staticStorage, null, emptyField.Offset),
            stringType,
            definitions,
            0xB8,
            out var resolvedField);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.False);
            Assert.That(resolvedField, Is.Null);
            Assert.That(runtimeClass.Type, Is.Null);
            Assert.That(staticStorage.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 已有冲突运行时类禁止重解释为StringEmpty所有者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stringType = appContext.SystemTypes.SystemStringType;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var emptyField = stringType.Fields.Single(field => field.IsStatic && field.Name == "Empty");
        var tableBase = new LocalVariable("tableBase", new Register(null, "X8", 1));
        var conflictingType = new RuntimeClassTypeAnalysisContext(objectType, objectType.DeclaringAssembly);
        var runtimeClass = new LocalVariable("runtimeClass", new Register(null, "X8", 2), conflictingType);
        var staticStorage = new LocalVariable("staticStorage", new Register(null, "X8", 3));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [tableBase] = new(0, OpCode.Move, tableBase, new MemoryOperand(null, null, 0x1000)),
            [runtimeClass] = new(1, OpCode.Move, runtimeClass, new MemoryOperand(tableBase, null, 0x90)),
            [staticStorage] = new(2, OpCode.Move, staticStorage, new MemoryOperand(runtimeClass, null, 0xB8)),
        };

        var resolved = LocalVariables.TryResolveSelfTypedStaticFieldLoad(
            new MemoryOperand(staticStorage, null, emptyField.Offset),
            stringType,
            definitions,
            0xB8,
            out var resolvedField);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.False);
            Assert.That(resolvedField, Is.Null);
            Assert.That(runtimeClass.Type, Is.SameAs(conflictingType));
            Assert.That(staticStorage.Type, Is.Null);
        });
    }

    /// <summary>
    /// 构造与真实ARM64路径一致的“默认类型表→String类→静态区→Empty字段→Return”闭包。
    /// 三类测试只改变一个边界条件，公共图构造集中在此处，避免重复维护偏移与局部身份。
    /// </summary>
    private static (
        MethodAnalysisContext Method,
        Instruction Return,
        LocalVariable RuntimeClass,
        LocalVariable StaticStorage) CreateStringEmptyReturnFixture(
        long fieldOffsetDelta = 0,
        bool conflictingRuntimeClass = false)
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stringType = appContext.SystemTypes.SystemStringType;
        var emptyField = stringType.Fields.Single(field =>
            field.IsStatic
            && field.Name == "Empty"
            && GenericCallRebinder.TypesEquivalent(field.FieldType, stringType));
        var tableBase = new LocalVariable("tableBase", new Register(null, "X8", 1));
        var runtimeClassType = conflictingRuntimeClass
            ? new RuntimeClassTypeAnalysisContext(
                appContext.SystemTypes.SystemObjectType,
                appContext.SystemTypes.SystemObjectType.DeclaringAssembly)
            : null;
        var runtimeClass = new LocalVariable(
            "runtimeClass",
            new Register(null, "X8", 2),
            runtimeClassType);
        var staticStorage = new LocalVariable("staticStorage", new Register(null, "X8", 3));
        var returnInstruction = new Instruction(
            3,
            OpCode.Return,
            new MemoryOperand(staticStorage, null, emptyField.Offset + fieldOffsetDelta));
        var method = new InjectedMethodAnalysisContext(
            stringType,
            "ReturnStringEmpty",
            stringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(null, null, 0x1000)),
            new Instruction(1, OpCode.Move, runtimeClass, new MemoryOperand(tableBase, null, 0x90)),
            new Instruction(2, OpCode.Move, staticStorage, new MemoryOperand(runtimeClass, null, 0xB8)),
            returnInstruction,
        ]);
        method.Locals = [tableBase, runtimeClass, staticStorage];
        return (method, returnInstruction, runtimeClass, staticStorage);
    }

    [Test]
    [Category("边界值")]
    public void 已等价的字段类型不产生重复计算()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var destination = new LocalVariable("destination", new Register(null, "X19", 1), stringType);

        var changed = LocalVariables.BindResolvedFieldLoadType(destination, stringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(stringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不相容字段类型不得覆盖已有定义()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "W19", 1),
            appContext.SystemTypes.SystemInt32Type);

        var changed = LocalVariables.BindResolvedFieldLoadType(
            destination,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void ARM64标准序言恢复栈帧基址的原生指针类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stackPointer = new LocalVariable(
            "stackPointer",
            new Register(null, "X31", -1),
            appContext.SystemTypes.SystemObjectType);
        var frameBase = new LocalVariable(
            "frameBase",
            new Register(null, "X31", 1),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            frameBase,
            stackPointer,
            new Immediate(0x60));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(stackPointer.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
            Assert.That(frameBase.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零帧长不得冒充ARM64栈帧序言()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var stackPointer = new LocalVariable("stackPointer", new Register(null, "X31", -1));
        var frameBase = new LocalVariable("frameBase", new Register(null, "X31", 1));
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            frameBase,
            stackPointer,
            new Immediate(0));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(stackPointer.Type, Is.Null);
            Assert.That(frameBase.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通寄存器减法不得改变为栈帧指针()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "X0", 1));
        var destination = new LocalVariable("destination", new Register(null, "X0", 2));
        var instruction = new Instruction(
            0,
            OpCode.Subtract,
            destination,
            source,
            new Immediate(0x60));

        var changed = LocalVariables.BindStackFrameBaseTypes(
            instruction,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.Null);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 一位掩码恢复位测试两端的布尔类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 一位掩码只定义布尔目标并保留已定型整数源()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X8", 7),
            appContext.SystemTypes.SystemInt32Type);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var firstChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);
        var secondChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(firstChanged, Is.True);
            Assert.That(secondChanged, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 方法布尔返回值经过普通寄存器一位掩码后保持布尔类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemBooleanType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X8", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 非一位掩码保持原始数值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemInt32Type);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通按位与不得冒充ARM64位测试()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "X0", 7));
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.Null);
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 一位掩码不得覆盖静态字段存储载体且第二次传播稳定()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var owner = appContext.SystemTypes.SystemObjectType;
        var storageType = new StaticFieldStorageTypeAnalysisContext(owner, owner.DeclaringAssembly);
        var source = new LocalVariable("source", new Register(null, "X8", 7), storageType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "TEST_BIT_VALUE", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var firstChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);
        var secondChanged = LocalVariables.BindBooleanBitTestOperands(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(firstChanged, Is.True);
            Assert.That(secondChanged, Is.False);
            Assert.That(source.Type, Is.SameAs(storageType));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 可空低字节掩码恢复存在标志类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var nullableInteger = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [appContext.SystemTypes.SystemInt32Type]);
        var source = new LocalVariable("source", new Register(null, "X0", 7), nullableInteger);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(source.Type, Is.SameAs(nullableInteger));
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 内联可空字段的低字节掩码恢复存在标志类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var nullableBoolean = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [appContext.SystemTypes.SystemBooleanType]);
        var owner = new LocalVariable(
            "owner",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "isMarketingChecked",
            nullableBoolean,
            FieldAttributes.Public,
            appContext.SystemTypes.SystemObjectType);
        var source = new FieldReference(field, owner, 0x10);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
            Assert.That(LocalVariables.NullableOperandType(source), Is.SameAs(nullableBoolean));
        });
    }

    [Test]
    [Category("边界值")]
    public void 可空一位掩码不冒充低字节存在测试()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var nullableInteger = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [appContext.SystemTypes.SystemInt32Type]);
        var source = new LocalVariable("source", new Register(null, "X0", 7), nullableInteger);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(1));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(nullableInteger));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通结构低字节掩码保持未解析状态()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 7),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable("destination", new Register(null, "X1", 3));
        var instruction = new Instruction(0, OpCode.And, destination, source, new Immediate(0xFF));

        var changed = LocalVariables.BindNullablePresenceTestResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 布尔逻辑非覆盖寄存器复用留下的对象占位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemBooleanType);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(0, OpCode.Not, destination, source);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 整数按位非不得被强制解释为布尔逻辑非()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt32Type);
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(0, OpCode.Not, destination, source);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 缺少源操作数的逻辑非不得修改目标类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X0", 2),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(0, OpCode.Not, destination);

        var changed = LocalVariables.BindBooleanNotResult(
            instruction,
            appContext.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 地址载体从解引用写入恢复引用槽位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable("slot", new Register(null, "stack_-40", 1));
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X22", 4),
            appContext.SystemTypes.SystemObjectType);
        var storedValue = new LocalVariable(
            "storedValue",
            new Register(null, "X0", 19),
            appContext.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, new MemoryOperand(carrier), storedValue),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(slot.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(
                ((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 值类型槽位通过地址别名保持精确元素类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-16", 1),
            appContext.SystemTypes.SystemInt32Type);
        var carrier = new LocalVariable("carrier", new Register(null, "X20", 2));
        var alias = new LocalVariable("alias", new Register(null, "X21", 3));
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "W0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, alias, carrier),
            new(2, OpCode.Move, loadedValue, new MemoryOperand(alias)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(((ByRefTypeAnalysisContext)alias.Type!).ElementType,
                Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(loadedValue.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 强制转换语义定义必须截断旧地址别名()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var castType = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "RecoveredCastTarget",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-D0", 1),
            appContext.SystemTypes.SystemObjectType);
        var carrier = new LocalVariable("carrier", new Register(null, "X22", 2));
        var castLocal = new LocalVariable(
            "castLocal",
            new Register(null, "CAST_X22", 3),
            castType);
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "X0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, castLocal, carrier),
            new(2, OpCode.CastClass, castLocal, castLocal, castType),
            new(3, OpCode.Move, loadedValue, new MemoryOperand(carrier)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(castLocal.Type, Is.SameAs(castType));
            Assert.That(loadedValue.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 算术语义定义不得继承旧地址别名()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable(
            "slot",
            new Register(null, "stack_-20", 1),
            appContext.SystemTypes.SystemInt32Type);
        var carrier = new LocalVariable("carrier", new Register(null, "X20", 2));
        var redefined = new LocalVariable(
            "redefined",
            new Register(null, "X21", 3),
            appContext.SystemTypes.SystemInt64Type);
        var loadedValue = new LocalVariable("loadedValue", new Register(null, "W0", 4));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
            new(1, OpCode.Move, redefined, carrier),
            new(2, OpCode.Add, redefined, redefined, new Immediate(8)),
            new(3, OpCode.Move, loadedValue, new MemoryOperand(carrier)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(carrier.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            Assert.That(redefined.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
            Assert.That(loadedValue.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通局部量复制不得建立地址载体关系()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable(
            "source",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemObjectType);
        var destination = new LocalVariable("destination", new Register(null, "X1", 1));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, destination, source),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未解引用的地址不得污染槽位与载体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var slot = new LocalVariable("slot", new Register(null, "stack_-24", 1));
        var staleElementType = appContext.SystemTypes.SystemObjectType;
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X1", 8),
            staleElementType.MakeByReferenceType());
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, new AddressOf(slot)),
        };

        var changed = LocalVariables.BindAddressCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(slot.Type, Is.Null);
            Assert.That(((ByRefTypeAnalysisContext)carrier.Type!).ElementType,
                Is.SameAs(staleElementType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 后置调用返回类型必须立即解析后续字段读取()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateFieldOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var field = new InjectedFieldAnalysisContext(
            "Text",
            appContext.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            owner,
            0x10);
        owner.Fields.Add(field);
        var getter = new InjectedMethodAnalysisContext(
            owner,
            "GetOwner",
            owner,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var loaded = new LocalVariable("loaded", new Register(null, "X1", 1));
        var call = new Instruction(0, OpCode.Call, getter, returned);
        var load = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(returned, addend: 0x10));
        var method = new InjectedMethodAnalysisContext(
            owner,
            "ReadLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([call, load, new Instruction(2, OpCode.Return)]);
        method.Locals = [returned, loaded];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(returned.Type, Is.SameAs(owner));
            Assert.That(load.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)load.Operands[1]).Field, Is.SameAs(field));
            Assert.That(loaded.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后置字段链必须跨多轮收敛到末端值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var inner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateInner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var valueField = new InjectedFieldAnalysisContext(
            "Value",
            appContext.SystemTypes.SystemInt32Type,
            FieldAttributes.Public,
            inner,
            0x18);
        inner.Fields.Add(valueField);
        var outer = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateOuter",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var innerField = new InjectedFieldAnalysisContext(
            "Inner",
            inner,
            FieldAttributes.Public,
            outer,
            0x10);
        outer.Fields.Add(innerField);
        var getter = new InjectedMethodAnalysisContext(
            outer,
            "GetOuter",
            outer,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var innerValue = new LocalVariable("innerValue", new Register(null, "X1", 1));
        var value = new LocalVariable("value", new Register(null, "W2", 1));
        var firstLoad = new Instruction(1, OpCode.Move, innerValue, new MemoryOperand(returned, addend: 0x10));
        var secondLoad = new Instruction(2, OpCode.Move, value, new MemoryOperand(innerValue, addend: 0x18));
        var method = new InjectedMethodAnalysisContext(
            outer,
            "ReadNestedLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, getter, returned),
            firstLoad,
            secondLoad,
            new Instruction(3, OpCode.Return),
        ]);
        method.Locals = [returned, innerValue, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(firstLoad.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(secondLoad.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(innerValue.Type, Is.SameAs(inner));
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 后置调用后的未知字段偏移必须保持未解析()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var assembly = appContext.Assemblies[0];
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "LateUnknownOffsetOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        owner.Fields.Add(new InjectedFieldAnalysisContext(
            "Known",
            appContext.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            owner,
            0x10));
        var getter = new InjectedMethodAnalysisContext(
            owner,
            "GetOwner",
            owner,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var returned = new LocalVariable("returned", new Register(null, "X0", 1));
        var loaded = new LocalVariable("loaded", new Register(null, "X1", 1));
        var unresolved = new MemoryOperand(returned, addend: 0x28);
        var load = new Instruction(1, OpCode.Move, loaded, unresolved);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "ReadUnknownLateField",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, getter, returned),
            load,
            new Instruction(2, OpCode.Return),
        ]);
        method.Locals = [returned, loaded];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(load.Operands[1], Is.TypeOf<MemoryOperand>());
            Assert.That(loaded.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析接口调用覆盖不兼容的共享泛型接收者类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var pollutedDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var pollutedType = pollutedDefinition.MakeGenericInstanceType(pollutedDefinition.GenericParameters);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X20", 14),
            pollutedType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(receiver.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已解析接口接收者类型沿唯一复制链传播到长期局部()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var pollutedDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var pollutedType = pollutedDefinition.MakeGenericInstanceType(pollutedDefinition.GenericParameters);
        var longLived = new LocalVariable(
            "longLived",
            new Register(null, "X20", 14),
            pollutedType);
        var callReceiver = new LocalVariable(
            "callReceiver",
            new Register(null, "X0", 47),
            pollutedType);
        var copy = new Instruction(0, OpCode.Move, callReceiver, longLived);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [callReceiver] = copy,
        };

        var changed = LocalVariables.BindResolvedInstanceReceiverCopySources(
            callReceiver,
            moveNext,
            definitions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(callReceiver.Type, Is.SameAs(enumerator));
            Assert.That(longLived.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 隐藏MethodInfo复制载体拒绝伪递归接收者定型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var target = new InjectedMethodAnalysisContext(
            objectType,
            "GenericTarget",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var methodInfo = new LocalVariable(
            "methodInfo",
            new Register(null, "X5"),
            new RuntimeMethodInfoAnalysisContext(target, objectType.DeclaringAssembly));
        var saved = new LocalVariable("savedMethodInfo", new Register(null, "X21"));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [saved] = new Instruction(0, OpCode.Move, saved, methodInfo),
        };

        var changed = LocalVariables.BindResolvedInstanceReceiverCopySources(
            saved,
            target,
            definitions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(saved.Type, Is.Null);
            Assert.That(methodInfo.Type, Is.TypeOf<RuntimeMethodInfoAnalysisContext>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 两级Move后的MethodInfo身份仍阻断实例接收者传播()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var target = new InjectedMethodAnalysisContext(
            objectType,
            "GenericTarget",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var methodInfo = new LocalVariable(
            "methodInfo",
            new Register(null, "X5"),
            new RuntimeMethodInfoAnalysisContext(target, objectType.DeclaringAssembly));
        var saved = new LocalVariable("savedMethodInfo", new Register(null, "X21"));
        var callReceiver = new LocalVariable("callReceiver", new Register(null, "X0", 1));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [saved] = new Instruction(0, OpCode.Move, saved, methodInfo),
            [callReceiver] = new Instruction(1, OpCode.Move, callReceiver, saved),
        };

        var changed = LocalVariables.BindResolvedInstanceReceiverCopySources(
            callReceiver,
            target,
            definitions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(callReceiver.Type, Is.Null);
            Assert.That(saved.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Phi任一入边为运行时元数据时整个接收者闭包保持原类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var target = new InjectedMethodAnalysisContext(
            objectType,
            "GenericTarget",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var methodInfo = new LocalVariable(
            "methodInfo",
            new Register(null, "X5"),
            new RuntimeMethodInfoAnalysisContext(target, objectType.DeclaringAssembly));
        var ordinary = new LocalVariable("ordinary", new Register(null, "X0", 1));
        var receiver = new LocalVariable("receiver", new Register(null, "PHI", 1));
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [receiver] = new Instruction(0, OpCode.Phi, receiver, methodInfo, ordinary),
        };

        var changed = LocalVariables.BindResolvedInstanceReceiverCopySources(
            receiver,
            target,
            definitions);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.Null);
            Assert.That(ordinary.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已闭合List调用目标闭合同定义开放接收者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var stringType = appContext.SystemTypes.SystemStringType;
        var closedTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [stringType],
            []);
        var openReceiverType = listDefinition.MakeGenericInstanceType([genericElement]);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openReceiverType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, closedTarget);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, closedTarget.DeclaringType),
                Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 共享ListObject实例目标不得降级结构化开放接收者()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var clear = listDefinition.Methods.First(method => method.Name == "Clear");
        var sharedTarget = new ConcreteGenericMethodAnalysisContext(
            clear,
            [appContext.SystemTypes.SystemObjectType],
            []);
        var structuredElement = listDefinition.MakeGenericInstanceType([genericElement]);
        var structuredReceiverType = listDefinition.MakeGenericInstanceType([structuredElement]);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            structuredReceiverType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, sharedTarget);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(structuredReceiverType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 开放接口接收者跨继承接口调用时保持原声明身份()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listInterfaceDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.IList`1")!;
        var collectionInterfaceDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.ICollection`1")!;
        var openListInterface = listInterfaceDefinition.MakeGenericInstanceType(
            listInterfaceDefinition.GenericParameters);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openListInterface);
        var countGetter = collectionInterfaceDefinition.Methods.First(method => method.Name == "get_Count");

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, countGetter);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(openListInterface));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已是目标接口的接收者保持精确类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X20", 14),
            enumerator);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(enumerator));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非接口调用不得覆盖已有接收者类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var toString = systemObject.Methods.First(method => method.Name == "ToString");
        var existingType = appContext.SystemTypes.SystemStringType;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            existingType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, toString);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(existingType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不兼容但封闭的接口类型不得被覆盖()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var enumerator = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = enumerator.Methods.First(method => method.Name == "MoveNext");
        var existingType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IConvertible")!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            existingType);

        var changed = LocalVariables.BindResolvedInstanceReceiverType(receiver, moveNext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(existingType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 后置类型收敛必须按具体实参闭合开放List调用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [genericElement],
            []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openTarget.DeclaringType);
        var value = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, value);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "CloseLateListCall",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(
                rebound.TypeGenericParameters,
                Is.EqualTo(new[] { appContext.SystemTypes.SystemStringType }));
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, rebound.DeclaringType),
                Is.True);
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后置类型收敛必须优先保持封闭List接收者实参()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [genericElement],
            []);
        var objectType = appContext.SystemTypes.SystemObjectType;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            listDefinition.MakeGenericInstanceType([objectType]));
        var value = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, value);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "PreserveClosedListReceiver",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, value];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(rebound.TypeGenericParameters, Is.EqualTo(new[] { objectType }));
            Assert.That(
                GenericCallRebinder.TypesEquivalent(receiver.Type, rebound.DeclaringType),
                Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 后置类型收敛遇到冲突实参必须保持开放调用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var pair = new InjectedMethodAnalysisContext(
            listDefinition,
            "Pair",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private,
            [genericElement, genericElement]);
        var openTarget = new ConcreteGenericMethodAnalysisContext(pair, [genericElement], []);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            openTarget.DeclaringType);
        var first = new LocalVariable(
            "first",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemStringType);
        var second = new LocalVariable(
            "second",
            new Register(null, "X2", 1),
            appContext.SystemTypes.SystemInt32Type);
        var call = new Instruction(0, OpCode.CallVoid, openTarget, receiver, first, second);
        var method = new InjectedMethodAnalysisContext(
            listDefinition,
            "RejectConflictingLateArguments",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);
        method.Locals = [receiver, first, second];

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(call.Operands[0], Is.SameAs(openTarget));
            Assert.That(first.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
            Assert.That(second.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 封闭调用参数必须替换局部的开放泛型占位符()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            listDefinition.GenericParameters.Single());

        var changed = LocalVariables.SetTypeFromClosedCallParameter(
            local,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(local.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 封闭调用参数不得覆盖局部已有的具体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemObjectType);

        var changed = LocalVariables.SetTypeFromClosedCallParameter(
            local,
            appContext.SystemTypes.SystemStringType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(local.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 共享ListObject形参不得降级字段已证明的结构化开放泛型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var structuredElement = listDefinition.MakeGenericInstanceType([genericElement]);
        var structuredLocalType = listDefinition.MakeGenericInstanceType([structuredElement]);
        var sharedParameterType = listDefinition.MakeGenericInstanceType([
            appContext.SystemTypes.SystemObjectType
        ]);
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            structuredLocalType);

        var changed = LocalVariables.SetTypeFromClosedCallParameter(local, sharedParameterType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(local.Type, Is.SameAs(structuredLocalType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 开放调用参数不得改写开放局部类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericElement = listDefinition.GenericParameters.Single();
        var local = new LocalVariable(
            "value",
            new Register(null, "X1", 1),
            genericElement);

        var changed = LocalVariables.SetTypeFromClosedCallParameter(local, genericElement);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(local.Type, Is.SameAs(genericElement));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 构造器接收者必须覆盖分配结果的错误值类型猜测()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            new ByRefTypeAnalysisContext(appContext.SystemTypes.SystemInt32Type));
        var allocation = new Instruction(0, OpCode.Newobj, receiver, listType);
        var call = new Instruction(1, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(listType, "RecoverConstructedReceiver", [allocation, call], [receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(listType));
    }

    [Test]
    [Category("边界值")]
    public void 构造器接收者沿唯一Move闭合到分配结果()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var allocated = new LocalVariable(
            "allocated",
            new Register(null, "X0", 1),
            new ByRefTypeAnalysisContext(appContext.SystemTypes.SystemInt32Type));
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 2),
            new ByRefTypeAnalysisContext(appContext.SystemTypes.SystemInt32Type));
        var allocation = new Instruction(0, OpCode.Newobj, allocated, listType);
        var copy = new Instruction(1, OpCode.Move, receiver, allocated);
        var call = new Instruction(2, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(
            listType,
            "RecoverCopiedConstructedReceiver",
            [allocation, copy, call],
            [allocated, receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.Multiple(() =>
        {
            Assert.That(receiver.Type, Is.SameAs(listType));
            Assert.That(allocated.Type, Is.SameAs(listType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通实例调用不得覆盖接收者已有具体类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var objectType = appContext.SystemTypes.SystemObjectType;
        var toString = objectType.Methods.First(method => method.Name == "ToString" && method.Parameters.Count == 0);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 1), appContext.SystemTypes.SystemStringType);
        var result = new LocalVariable("result", new Register(null, "X0", 2));
        var call = new Instruction(0, OpCode.Call, toString, result, receiver);
        var method = CreateConstructorFixture(objectType, "PreserveNormalReceiver", [call], [receiver, result]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
    }

    [Test]
    [Category("异常输入")]
    public void 构造器接收者不来自Newobj时不得覆盖既有类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(
            listType,
            "PreserveNonAllocationConstructorReceiver",
            [call],
            [receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
    }

    [Test]
    [Category("异常输入")]
    public void 正常具体Newobj分配不得被构造器声明类型覆盖()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.ArrayList")!;
        var constructor = listType.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemStringType);
        var allocation = new Instruction(0, OpCode.Newobj, receiver, appContext.SystemTypes.SystemStringType);
        var call = new Instruction(1, OpCode.CallVoid, constructor, receiver);
        var method = CreateConstructorFixture(
            listType,
            "PreserveConcreteAllocationType",
            [allocation, call],
            [receiver]);

        LocalVariables.ResolveLateCallTypesAndAddressCarriers(method);

        Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
    }

    [Test]
    [Category("基本功能")]
    public void 后置Newobj精确类型覆盖共享Object实例并重绑定构造器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listObject = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemObjectType]);
        var listString = listDefinition.MakeGenericInstanceType([appContext.SystemTypes.SystemStringType]);
        var baseConstructor = listDefinition.Methods.First(method => method.Name == ".ctor" && method.Parameters.Count == 0);
        var objectConstructor = new ConcreteGenericMethodAnalysisContext(
            baseConstructor,
            [appContext.SystemTypes.SystemObjectType],
            []);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 1), listObject);
        var allocation = new Instruction(0, OpCode.Newobj, receiver, listString);
        var call = new Instruction(1, OpCode.CallVoid, objectConstructor, receiver);
        var method = CreateConstructorFixture(listDefinition, "RefreshConcreteAllocation", [allocation, call], [receiver]);

        var changed = LocalVariables.RefreshResolvedNewobjTypesAndCalls(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(receiver.Type, Is.SameAs(listString));
            Assert.That(call.Operands[0], Is.TypeOf<ConcreteGenericMethodAnalysisContext>());
            var rebound = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
            Assert.That(rebound.TypeGenericParameters.Single(), Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后置Newobj类型已精确时保持不变()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemStringType);
        var allocation = new Instruction(0, OpCode.Newobj, receiver, appContext.SystemTypes.SystemStringType);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemStringType,
            "PreserveResolvedAllocation",
            [allocation],
            [receiver]);

        var changed = LocalVariables.RefreshResolvedNewobjTypesAndCalls(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通Move携带类型操作数不得冒充分配结果()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemObjectType);
        var move = new Instruction(0, OpCode.Move, receiver, appContext.SystemTypes.SystemStringType);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RejectNonAllocation",
            [move],
            [receiver]);

        var changed = LocalVariables.RefreshResolvedNewobjTypesAndCalls(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(receiver.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 三十二位状态掩码覆盖退SSA留下的布尔占位类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X27", 1));
        var combined = new LocalVariable(
            "combined",
            new Register(null, "X8", 1),
            appContext.SystemTypes.SystemBooleanType);
        var instruction = new Instruction(0, OpCode.Or, combined, state, new Immediate(8))
        {
            IntegerWidthBits = 32,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(state.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(combined.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 六十四位循环增量沿已定型比较载体保持长整数()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var counter = new LocalVariable("counter", new Register(null, "X22", 1));
        var limit = new LocalVariable(
            "limit",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt64Type);
        var comparison = new Instruction(
            0,
            OpCode.CheckEqual,
            new LocalVariable("equal", new Register(null, "Z", 1)),
            counter,
            limit);
        var increment = new Instruction(1, OpCode.Add, counter, counter, new Immediate(1))
        {
            IntegerWidthBits = 64,
        };

        var comparisonChanged = LocalVariables.BindFinalComparisonOperandTypes(comparison);
        var incrementChanged = LocalVariables.BindSizedIntegerOperationTypes(increment, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(comparisonChanged, Is.True);
            Assert.That(incrementChanged, Is.False);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 整型字段比较恢复对象占位循环计数器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X22", 1),
            appContext.SystemTypes.SystemObjectType);
        var owner = new LocalVariable(
            "owner",
            new Register(null, "X23", 1),
            appContext.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "CommonnessScore",
            appContext.SystemTypes.SystemInt32Type,
            FieldAttributes.Public,
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckLess,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            counter,
            new FieldReference(field, owner, 0x10));

        var changed = LocalVariables.BindFinalComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 数组长度比较恢复反向操作数中的对象占位计数器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var arrayType = new SzArrayTypeAnalysisContext(appContext.SystemTypes.SystemStringType);
        var array = new LocalVariable("array", new Register(null, "X20", 1), arrayType);
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X21", 1),
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckGreater,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            new ArrayLength(array),
            counter);

        var changed = LocalVariables.BindFinalComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 后期数组长度操作数恢复对象占位循环计数器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var arrayType = new SzArrayTypeAnalysisContext(appContext.SystemTypes.SystemStringType);
        var array = new LocalVariable("array", new Register(null, "X20", 1), arrayType);
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X21", 1),
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckLess,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            counter,
            new ArrayLength(array));

        var changed = LocalVariables.BindRecoveredLengthComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 后期字符串长度操作数恢复反向对象占位循环计数器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var text = new LocalVariable(
            "text",
            new Register(null, "X20", 1),
            appContext.SystemTypes.SystemStringType);
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X21", 1),
            appContext.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(
            0,
            OpCode.CheckGreater,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            new StringLength(text),
            counter);

        var changed = LocalVariables.BindRecoveredLengthComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 后期长度操作数不得覆盖普通托管引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var arrayType = new SzArrayTypeAnalysisContext(appContext.SystemTypes.SystemStringType);
        var array = new LocalVariable("array", new Register(null, "X20", 1), arrayType);
        var text = new LocalVariable(
            "text",
            new Register(null, "X21", 1),
            appContext.SystemTypes.SystemStringType);
        var comparison = new Instruction(
            0,
            OpCode.CheckLess,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            text,
            new ArrayLength(array));

        var changed = LocalVariables.BindRecoveredLengthComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(text.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 后期属性Getter比较恢复对象占位循环计数器()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            appContext.GetAssemblyByName("mscorlib")!,
            "Fixture",
            "CounterOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var getter = owner.InjectMethodContext(
            "get_CommonnessScore",
            appContext.SystemTypes.SystemInt32Type,
            MethodAttributes.Public);
        var receiver = new LocalVariable("owner", new Register(null, "X0"), owner);
        var limit = new LocalVariable("limit", new Register(null, "W8"), appContext.SystemTypes.SystemInt32Type);
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X24"),
            appContext.SystemTypes.SystemObjectType);
        var condition = new LocalVariable("condition", new Register(null, "Z"), appContext.SystemTypes.SystemBooleanType);
        var call = new Instruction(0, OpCode.Call, getter, limit, receiver);
        var comparison = new Instruction(1, OpCode.CheckLess, condition, counter, limit);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RecoverPropertyCounter",
            [call, comparison],
            [receiver, limit, counter, condition]);

        var changed = LocalVariables.ResolveRecoveredPropertyComparisonCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通方法结果比较不得冒充后期属性证据()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            appContext.GetAssemblyByName("mscorlib")!,
            "Fixture",
            "CounterOwner",
            appContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var methodTarget = owner.InjectMethodContext(
            "ReadLimit",
            appContext.SystemTypes.SystemInt32Type,
            MethodAttributes.Public);
        var receiver = new LocalVariable("owner", new Register(null, "X0"), owner);
        var limit = new LocalVariable("limit", new Register(null, "W8"), appContext.SystemTypes.SystemInt32Type);
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X24"),
            appContext.SystemTypes.SystemObjectType);
        var condition = new LocalVariable("condition", new Register(null, "Z"), appContext.SystemTypes.SystemBooleanType);
        var call = new Instruction(0, OpCode.Call, methodTarget, limit, receiver);
        var comparison = new Instruction(1, OpCode.CheckLess, condition, counter, limit);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RejectOrdinaryCall",
            [call, comparison],
            [receiver, limit, counter, condition]);

        var changed = LocalVariables.ResolveRecoveredPropertyComparisonCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 单精度字段比较恢复对象占位浮点载体()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var value = new LocalVariable(
            "value",
            new Register(null, "V0", 1),
            appContext.SystemTypes.SystemObjectType);
        var owner = new LocalVariable(
            "owner",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "Threshold",
            appContext.SystemTypes.SystemSingleType,
            FieldAttributes.Public,
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckLessOrEqual,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            value,
            new FieldReference(field, owner, 0x10));

        var changed = LocalVariables.BindFinalComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemSingleType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 整型字段比较不得覆盖普通字符串引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var text = new LocalVariable(
            "text",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemStringType);
        var owner = new LocalVariable(
            "owner",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "Limit",
            appContext.SystemTypes.SystemInt32Type,
            FieldAttributes.Public,
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckEqual,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            text,
            new FieldReference(field, owner, 0x10));

        var changed = LocalVariables.BindFinalComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(text.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不同精确数值域比较保持既有局部类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemInt64Type);
        var owner = new LocalVariable(
            "owner",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext(
            "Limit",
            appContext.SystemTypes.SystemInt32Type,
            FieldAttributes.Public,
            appContext.SystemTypes.SystemObjectType);
        var comparison = new Instruction(
            0,
            OpCode.CheckLess,
            new LocalVariable("condition", new Register(null, "Z", 1)),
            counter,
            new FieldReference(field, owner, 0x10));

        var changed = LocalVariables.BindFinalComparisonOperandTypes(comparison, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 带位宽的整数运算不得覆盖引用载体()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var reference = new LocalVariable(
            "reference",
            new Register(null, "X0", 1),
            appContext.SystemTypes.SystemStringType);
        var destination = new LocalVariable("destination", new Register(null, "X1", 1));
        var instruction = new Instruction(0, OpCode.Or, destination, reference, new Immediate(8))
        {
            IntegerWidthBits = 64,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(reference.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
            Assert.That(destination.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 终态Int32沿Move反向恢复Not结果载体()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var notResult = new LocalVariable("notResult", new Register(null, "TEMP", 1));
        var counter = new LocalVariable(
            "counter",
            new Register(null, "X19", 1),
            appContext.SystemTypes.SystemInt32Type);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RecoverNotCarrier",
            [
                new Instruction(0, OpCode.Not, notResult, new Immediate(0)),
                new Instruction(-1, OpCode.Move, counter, notResult),
            ],
            [notResult, counter]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(notResult.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(counter.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 终态Int64穿透多级Move连通分量()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var first = new LocalVariable("first", new Register(null, "X8", 1));
        var second = new LocalVariable(
            "second",
            new Register(null, "X9", 1),
            appContext.SystemTypes.SystemObjectType);
        var third = new LocalVariable(
            "third",
            new Register(null, "X10", 1),
            appContext.SystemTypes.SystemInt64Type);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RecoverMoveChain",
            [
                new Instruction(-1, OpCode.Move, second, first),
                new Instruction(-1, OpCode.Move, third, second),
            ],
            [first, second, third]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(first.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
            Assert.That(second.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Move分量含引用或冲突数值域时拒绝标量覆盖()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var text = new LocalVariable(
            "text",
            new Register(null, "X8", 1),
            appContext.SystemTypes.SystemStringType);
        var int32Value = new LocalVariable(
            "int32Value",
            new Register(null, "W9", 1),
            appContext.SystemTypes.SystemInt32Type);
        var unknown = new LocalVariable("unknown", new Register(null, "X10", 1));
        var int64Value = new LocalVariable(
            "int64Value",
            new Register(null, "X11", 1),
            appContext.SystemTypes.SystemInt64Type);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RejectConflictingMoveComponents",
            [
                new Instruction(-1, OpCode.Move, int32Value, text),
                new Instruction(-1, OpCode.Move, unknown, int32Value),
                new Instruction(-1, OpCode.Move, int64Value, unknown),
            ],
            [text, int32Value, unknown, int64Value]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(text.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
            Assert.That(unknown.Type, Is.Null);
            Assert.That(int32Value.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(int64Value.Type, Is.SameAs(appContext.SystemTypes.SystemInt64Type));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 闭合调用Int32形参恢复多定义立即数载体()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X8", 1));
        var consume = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "ConsumeInt32",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            [appContext.SystemTypes.SystemInt32Type]);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RecoverCallParameterCarrier",
            [
                new Instruction(0, OpCode.Move, state, new Immediate(1)),
                new Instruction(1, OpCode.Move, state, new Immediate(5)),
                new Instruction(2, OpCode.CallVoid, consume, state),
            ],
            [state]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(state.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 闭合调用Byte形参接受完整字面量边界()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var value = new LocalVariable(
            "value",
            new Register(null, "W8", 1),
            appContext.SystemTypes.SystemObjectType);
        var consume = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "ConsumeByte",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            [appContext.SystemTypes.SystemByteType]);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RecoverByteBoundaryCarrier",
            [
                new Instruction(0, OpCode.Move, value, new Immediate(byte.MinValue)),
                new Instruction(1, OpCode.Move, value, new Immediate(byte.MaxValue)),
                new Instruction(2, OpCode.CallVoid, consume, value),
            ],
            [value]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(value.Type, Is.SameAs(appContext.SystemTypes.SystemByteType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一载体被冲突调用形参消费时拒绝定型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X8", 1));
        var consumeInt32 = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "ConsumeInt32",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            [appContext.SystemTypes.SystemInt32Type]);
        var consumeInt64 = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "ConsumeInt64",
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            [appContext.SystemTypes.SystemInt64Type]);
        var method = CreateConstructorFixture(
            appContext.SystemTypes.SystemObjectType,
            "RejectConflictingCallParameterCarrier",
            [
                new Instruction(0, OpCode.Move, state, new Immediate(1)),
                new Instruction(1, OpCode.CallVoid, consumeInt32, state),
                new Instruction(2, OpCode.CallVoid, consumeInt64, state),
            ],
            [state]);

        var changed = LocalVariables.ResolveFinalScalarCopyCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(state.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 丢失位宽的非布尔掩码从精确整型目标反向恢复源类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var masked = new LocalVariable(
            "masked",
            new Register(null, "X1", 1),
            appContext.SystemTypes.SystemObjectType);
        var maximum = new LocalVariable(
            "maximum",
            new Register(null, "X2", 1),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(0, OpCode.Or, maximum, masked, new Immediate(0x10000));

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(masked.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
            Assert.That(maximum.Type, Is.SameAs(appContext.SystemTypes.SystemInt32Type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 丢失位宽的一位布尔掩码不触发整型反向传播()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var flag = new LocalVariable("flag", new Register(null, "X1", 1));
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X2", 1),
            appContext.SystemTypes.SystemInt32Type);
        var instruction = new Instruction(0, OpCode.And, destination, flag, new Immediate(1));

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(flag.Type, Is.Null);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 丢失位宽且目标为引用类型时保留原类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "X1", 1));
        var destination = new LocalVariable(
            "destination",
            new Register(null, "X2", 1),
            appContext.SystemTypes.SystemStringType);
        var instruction = new Instruction(0, OpCode.Or, destination, source, new Immediate(8));

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(source.Type, Is.Null);
            Assert.That(destination.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退SSA布尔Or树从权威叶恢复全部对象载体()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var booleanType = app.SystemTypes.SystemBooleanType;
        var seed = new LocalVariable("seed", new Register(null, "X0", 1), booleanType);
        var left = new LocalVariable("left", new Register(null, "X20", 1), app.SystemTypes.SystemObjectType);
        var right = new LocalVariable("right", new Register(null, "X21", 1), app.SystemTypes.SystemObjectType);
        var combined = new LocalVariable("combined", new Register(null, "X8", 1), app.SystemTypes.SystemObjectType);
        var result = new LocalVariable("result", new Register(null, "X9", 1), booleanType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, left, seed),
            new(1, OpCode.Move, left, new Immediate(0)),
            new(2, OpCode.Move, right, seed),
            new(3, OpCode.Move, right, new Immediate(0)),
            new(4, OpCode.Or, combined, left, right),
            new(5, OpCode.Or, result, combined, seed),
        };

        var changed = LocalVariables.BindFinalBooleanBitwiseComponentTypes(instructions, booleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(left.Type, Is.SameAs(booleanType));
            Assert.That(right.Type, Is.SameAs(booleanType));
            Assert.That(combined.Type, Is.SameAs(booleanType));
            Assert.That(result.Type, Is.SameAs(booleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 多级边复制与Xor一共同组成的布尔分量保持闭合()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var booleanType = app.SystemTypes.SystemBooleanType;
        var seed = new LocalVariable("seed", new Register(null, "X0", 1), booleanType);
        var carrier = new LocalVariable("carrier", new Register(null, "X20", 1), app.SystemTypes.SystemObjectType);
        var alias = new LocalVariable("alias", new Register(null, "X21", 1), app.SystemTypes.SystemObjectType);
        var negated = new LocalVariable("negated", new Register(null, "X8", 1), app.SystemTypes.SystemObjectType);
        var result = new LocalVariable("result", new Register(null, "X9", 1), booleanType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, seed),
            new(1, OpCode.Move, carrier, new Immediate(0)),
            new(2, OpCode.Move, alias, carrier),
            new(3, OpCode.Xor, negated, alias, new Immediate(1)),
            new(4, OpCode.Or, result, negated, seed),
        };

        var changed = LocalVariables.BindFinalBooleanBitwiseComponentTypes(instructions, booleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(carrier.Type, Is.SameAs(booleanType));
            Assert.That(alias.Type, Is.SameAs(booleanType));
            Assert.That(negated.Type, Is.SameAs(booleanType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 布尔Or分量混入引用定义时保持对象与引用类型不变()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var booleanType = app.SystemTypes.SystemBooleanType;
        var seed = new LocalVariable("seed", new Register(null, "X0", 1), booleanType);
        var reference = new LocalVariable("reference", new Register(null, "X1", 1), app.SystemTypes.SystemStringType);
        var polluted = new LocalVariable("polluted", new Register(null, "X20", 1), app.SystemTypes.SystemObjectType);
        var result = new LocalVariable("result", new Register(null, "X8", 1), booleanType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, polluted, reference),
            new(1, OpCode.Or, result, polluted, seed),
        };

        var changed = LocalVariables.BindFinalBooleanBitwiseComponentTypes(instructions, booleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(polluted.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(reference.Type, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 运行时类布局读取参与移位时恢复原生整数结果()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            appContext.SystemTypes.SystemObjectType.DeclaringAssembly);
        var runtimeClass = new LocalVariable(
            "runtimeClass",
            new Register(null, "X8", 1),
            runtimeClassType);
        var shifted = new LocalVariable(
            "shifted",
            new Register(null, "X9", 1),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(
            0,
            OpCode.ShiftLeft,
            shifted,
            new MemoryOperand(runtimeClass, addend: 0x130),
            new Immediate(3))
        {
            IntegerWidthBits = appContext.Binary.PointerSizeBytes * 8,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(shifted.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
            Assert.That(runtimeClass.Type, Is.SameAs(runtimeClassType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 原生整数链与运行时类基址相加时保持地址域()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var runtimeClassType = new RuntimeClassTypeAnalysisContext(
            appContext.SystemTypes.SystemStringType,
            appContext.SystemTypes.SystemStringType.DeclaringAssembly);
        var runtimeClass = new LocalVariable(
            "runtimeClass",
            new Register(null, "X8", 1),
            runtimeClassType);
        var shifted = new LocalVariable(
            "shifted",
            new Register(null, "X9", 1),
            appContext.SystemTypes.SystemIntPtrType);
        var address = new LocalVariable(
            "address",
            new Register(null, "X10", 1),
            appContext.SystemTypes.SystemObjectType);
        var instruction = new Instruction(
            0,
            OpCode.Add,
            address,
            new MemoryOperand(runtimeClass, addend: 0xC8),
            shifted)
        {
            IntegerWidthBits = appContext.Binary.PointerSizeBytes * 8,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(address.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
            Assert.That(shifted.Type, Is.SameAs(appContext.SystemTypes.SystemIntPtrType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 托管对象内存读取不得触发原生地址算术推导()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var managedObject = new LocalVariable(
            "managedObject",
            new Register(null, "X8", 1),
            appContext.SystemTypes.SystemStringType);
        var destination = new LocalVariable("destination", new Register(null, "X9", 1));
        var instruction = new Instruction(
            0,
            OpCode.Add,
            destination,
            new MemoryOperand(managedObject, addend: 0x10),
            new Immediate(8))
        {
            IntegerWidthBits = appContext.Binary.PointerSizeBytes * 8,
        };

        var changed = LocalVariables.BindSizedIntegerOperationTypes(instruction, appContext);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(destination.Type, Is.Null);
            Assert.That(managedObject.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退SSA后单精度加法结果覆盖Object占位类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "V8", 1), app.SystemTypes.SystemSingleType);
        var result = new LocalVariable("result", new Register(null, "V0", 1), app.SystemTypes.SystemObjectType);
        var method = CreateConstructorFixture(
            app.SystemTypes.SystemObjectType,
            "RecoverFinalFloatingAdd",
            [new Instruction(0, OpCode.Add, result, source, source)],
            [source, result]);

        var changed = LocalVariables.ResolveFinalFloatingArithmeticCarrierTypes(method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(result.Type, Is.SameAs(app.SystemTypes.SystemSingleType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 数组长度复制跟随原生索引比较宽度()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var array = new LocalVariable(
            "array",
            new Register(null, "X0", 1),
            new SzArrayTypeAnalysisContext(app.SystemTypes.SystemStringType));
        var length = new LocalVariable("length", new Register(null, "X8", 1), app.SystemTypes.SystemObjectType);
        var index = new LocalVariable("index", new Register(null, "X19", 1), app.SystemTypes.SystemIntPtrType);
        var move = new Instruction(0, OpCode.Move, length, new ArrayLength(array));
        var compare = new Instruction(
            1,
            OpCode.CheckLess,
            new LocalVariable("condition", new Register(null, "Z", 1), app.SystemTypes.SystemBooleanType),
            index,
            length);
        var instructions = new[] { move, compare };

        var changed = LocalVariables.BindRecoveredLengthMoveCarrierType(move, instructions, app);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(length.Type, Is.SameAs(app.SystemTypes.SystemIntPtrType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 长度复制遇到冲突比较宽度时保持Object占位()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var array = new LocalVariable(
            "array",
            new Register(null, "X0", 1),
            new SzArrayTypeAnalysisContext(app.SystemTypes.SystemStringType));
        var length = new LocalVariable("length", new Register(null, "X8", 1), app.SystemTypes.SystemObjectType);
        var nativeIndex = new LocalVariable("nativeIndex", new Register(null, "X19", 1), app.SystemTypes.SystemIntPtrType);
        var intIndex = new LocalVariable("intIndex", new Register(null, "W20", 1), app.SystemTypes.SystemInt32Type);
        var condition = new LocalVariable("condition", new Register(null, "Z", 1), app.SystemTypes.SystemBooleanType);
        var move = new Instruction(0, OpCode.Move, length, new ArrayLength(array));
        var instructions = new[]
        {
            move,
            new Instruction(1, OpCode.CheckLess, condition, nativeIndex, length),
            new Instruction(2, OpCode.CheckLess, condition, intIndex, length),
        };

        var changed = LocalVariables.BindRecoveredLengthMoveCarrierType(move, instructions, app);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(length.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两路零一状态且仅参与分支比较时恢复Boolean()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X24", 1), app.SystemTypes.SystemObjectType);
        var condition = new LocalVariable("condition", new Register(null, "Z", 1), app.SystemTypes.SystemBooleanType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, state, new Immediate(1)),
            new(1, OpCode.Move, state, new Immediate(0)),
            new(2, OpCode.CheckNotEqual, condition, state, new Immediate(0)),
        };

        var changed = LocalVariables.BindFinalBooleanBranchCarrierTypes(instructions, app.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(state.Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零一状态允许被多个相等分支共同读取()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X24", 1));
        var first = new LocalVariable("first", new Register(null, "Z", 1), app.SystemTypes.SystemBooleanType);
        var second = new LocalVariable("second", new Register(null, "Z", 2), app.SystemTypes.SystemBooleanType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, state, new Immediate(0)),
            new(1, OpCode.Move, state, new Immediate(1)),
            new(2, OpCode.CheckEqual, first, state, new Immediate(0)),
            new(3, OpCode.CheckNotEqual, second, state, new Immediate(1)),
        };

        var changed = LocalVariables.BindFinalBooleanBranchCarrierTypes(instructions, app.SystemTypes.SystemBooleanType);

        Assert.That(changed, Is.True);
        Assert.That(state.Type, Is.SameAs(app.SystemTypes.SystemBooleanType));
    }

    [Test]
    [Category("异常输入")]
    public void 零一整数被算术消费时不得猜成Boolean()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var state = new LocalVariable("state", new Register(null, "X24", 1), app.SystemTypes.SystemObjectType);
        var condition = new LocalVariable("condition", new Register(null, "Z", 1), app.SystemTypes.SystemBooleanType);
        var sum = new LocalVariable("sum", new Register(null, "X8", 1));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, state, new Immediate(0)),
            new(1, OpCode.Move, state, new Immediate(1)),
            new(2, OpCode.CheckEqual, condition, state, new Immediate(0)),
            new(3, OpCode.Add, sum, state, new Immediate(1)),
        };

        var changed = LocalVariables.BindFinalBooleanBranchCarrierTypes(instructions, app.SystemTypes.SystemBooleanType);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(state.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    private static InjectedMethodAnalysisContext CreateConstructorFixture(
        TypeAnalysisContext declaringType,
        string name,
        IReadOnlyList<Instruction> body,
        IReadOnlyList<LocalVariable> locals)
    {
        var method = new InjectedMethodAnalysisContext(
            declaringType,
            name,
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemVoidType,
            MethodAttributes.Private | MethodAttributes.Static,
            []);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            .. body,
            new Instruction(body.Count, OpCode.Return),
        ]);
        method.Locals = [.. locals];
        return method;
    }
}
