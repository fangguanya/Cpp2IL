using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class Arm64CallingConventionResolverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void RawArgumentsCoverAllAapcs64RegistersInOrder()
    {
        Assert.That(
            Arm64CallingConventionResolver.RawArgumentRegisterNames,
            Is.EqualTo(new[]
            {
                "X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7",
                "V0", "V1", "V2", "V3", "V4", "V5", "V6", "V7"
            }));
    }

    [Test]
    [Category("基本功能")]
    public void 共享EnumTryParse泛型候选统一保留X0定义()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var first = CreateStaticMethod(app.SystemTypes.EnumType, "TryParse", app.SystemTypes.SystemBooleanType);
        var second = CreateStaticMethod(app.SystemTypes.EnumType, "TryParse", app.SystemTypes.SystemBooleanType);

        var resolved = Arm64CallingConventionResolver.TryGetSharedEnumTryParseReturnRegister(
            [first, second],
            out var returnRegister);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(returnRegister.Name, Is.EqualTo("X0"));
        }
    }

    [Test]
    [Category("边界值")]
    public void 单个EnumTryParse候选沿唯一目标路线处理而不冒充共享地址()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var tryParse = CreateStaticMethod(
            app.SystemTypes.EnumType,
            "TryParse",
            app.SystemTypes.SystemBooleanType);

        Assert.That(
            Arm64CallingConventionResolver.TryGetSharedEnumTryParseReturnRegister(
                [tryParse],
                out _),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 共享地址混入非EnumTryParse候选时拒绝猜测寄存器()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var tryParse = CreateStaticMethod(
            app.SystemTypes.EnumType,
            "TryParse",
            app.SystemTypes.SystemBooleanType);
        var unrelated = CreateStaticMethod(
            app.SystemTypes.SystemObjectType,
            "TryParse",
            app.SystemTypes.SystemBooleanType);

        Assert.That(
            Arm64CallingConventionResolver.TryGetSharedEnumTryParseReturnRegister(
                [tryParse, unrelated],
                out _),
            Is.False);
    }

    [Test]
    [Category("边界值")]
    public void IndirectCallAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void IndirectJumpAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var jump = new Instruction(0, OpCode.IndirectJump, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(jump), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void MissingFinalFloatingRegisterIsRejected()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.RemoveAt(operands.Count - 1);
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void TrailingX8IndirectReturnCandidateIsAccepted()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.Add(new Register(null, "X8"));
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void TrailingOrdinaryRegisterIsRejectedAsReturnCandidate()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.Add(new Register(null, "X10"));
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void TwoSingleFieldsReturnThroughV0AndV1()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var aggregate = CreateValueType(
            "TwoSingleFields",
            appContext.SystemTypes.SystemSingleType,
            appContext.SystemTypes.SystemSingleType);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var registers = Arm64CallingConventionResolver.ReturnOperands(method)
            .Cast<Register>()
            .Select(register => register.Name)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registers, Is.EqualTo(new[] { "V0", "V1" }));
            Assert.That(Arm64CallingConventionResolver.ReturnsViaHiddenBuffer(method), Is.False);
        }
    }

    [Test]
    [Category("边界值")]
    public void FourDoubleFieldsUseAllFourHfaReturnRegisters()
    {
        var doubleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemDoubleType;
        var aggregate = CreateValueType(
            "FourDoubleFields",
            doubleType,
            doubleType,
            doubleType,
            doubleType);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var registers = Arm64CallingConventionResolver.ReturnOperands(method)
            .Cast<Register>()
            .Select(register => register.Name)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registers, Is.EqualTo(new[] { "V0", "V1", "V2", "V3" }));
            Assert.That(Arm64CallingConventionResolver.ReturnsViaHiddenBuffer(method), Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void 两个Single字段按V1到V0逆序投影完整返回值()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var aggregate = CreateValueType(
            "ProjectedVector2",
            singleType,
            singleType);
        SetSequentialFieldOffsets(aggregate, sizeof(float));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var projections = Arm64CallingConventionResolver.ReturnProjections(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                projections.Select(projection => projection.Destination.Name),
                Is.EqualTo(new[] { "V1", "V0" }));
            Assert.That(
                projections.Select(projection => projection.Source.Addend),
                Is.EqualTo(new long[] { sizeof(float), 0 }));
            Assert.That(
                projections.Select(projection => ((Register)projection.Source.Base!).Name),
                Is.EqualTo(new[] { "V0", "V0" }));
        }
    }

    [Test]
    [Category("边界值")]
    public void 四个Double字段从V3到V0完整投影()
    {
        var doubleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemDoubleType;
        var aggregate = CreateValueType(
            "ProjectedDouble4",
            doubleType,
            doubleType,
            doubleType,
            doubleType);
        SetSequentialFieldOffsets(aggregate, sizeof(double));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var projections = Arm64CallingConventionResolver.ReturnProjections(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                projections.Select(projection => projection.Destination.Name),
                Is.EqualTo(new[] { "V3", "V2", "V1", "V0" }));
            Assert.That(
                projections.Select(projection => projection.Source.Addend),
                Is.EqualTo(new long[] { 24, 16, 8, 0 }));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 缺失字段偏移的Hfa不生成猜测投影()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var aggregate = CreateValueType(
            "UnknownLayoutVector2",
            singleType,
            singleType);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(Arm64CallingConventionResolver.ReturnProjections(method), Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void 两个引用字段按X1到X0逆序投影完整返回值()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var aggregate = CreateValueType("TwoReferenceFields", stringType, stringType);
        SetSequentialFieldOffsets(aggregate, sizeof(long));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var projections = Arm64CallingConventionResolver.ReferenceRegisterReturnProjections(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                projections.Select(projection => projection.Destination.Name),
                Is.EqualTo(new[] { "X1", "X0" }));
            Assert.That(
                projections.Select(projection => projection.Source.Addend),
                Is.EqualTo(new long[] { sizeof(long), 0 }));
            Assert.That(
                Arm64CallingConventionResolver.ReturnOperands(method)
                    .Cast<Register>()
                    .Select(register => register.Name),
                Is.EqualTo(new[] { "X0", "X1" }));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 封闭KeyValuePair按具体键值类型生成两个引用返回槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var pairDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = pairDefinition.MakeGenericInstanceType([
            app.SystemTypes.SystemStringType,
            app.SystemTypes.SystemStringType,
        ]);
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "First",
            pair,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var projections = Arm64CallingConventionResolver.ReferenceRegisterReturnProjections(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                projections.Select(projection => projection.Destination.Name),
                Is.EqualTo(new[] { "X1", "X0" }));
            Assert.That(
                projections.Select(projection => projection.Source.Addend),
                Is.EqualTo(new long[] { sizeof(long), 0 }));
        }
    }

    [Test]
    [Category("边界值")]
    public void 开放泛型与单一封闭KeyValuePair共享地址时选择封闭布局原型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var pairDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = pairDefinition.MakeGenericInstanceType([
            app.SystemTypes.SystemObjectType,
            app.SystemTypes.SystemObjectType,
        ]);
        var openMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "First",
            pairDefinition.GenericParameters[0],
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var closedMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "First",
            pair,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var resolved = Arm64CallingConventionResolver.TryGetReferenceRegisterAggregateReturnPrototype(
            [openMethod, closedMethod],
            out var prototype);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(prototype, Is.SameAs(closedMethod));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 共享地址含封闭非聚合返回候选时拒绝引用槽原型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var pairDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = pairDefinition.MakeGenericInstanceType([
            app.SystemTypes.SystemObjectType,
            app.SystemTypes.SystemObjectType,
        ]);
        var pairMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "First",
            pair,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var stringMethod = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "First",
            app.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(
            Arm64CallingConventionResolver.TryGetReferenceRegisterAggregateReturnPrototype(
                [pairMethod, stringMethod],
                out _),
            Is.False);
    }

    [Test]
    [Category("边界值")]
    public void 单引用字段只投影X0零偏移槽()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var aggregate = CreateValueType("OneReferenceField", stringType);
        aggregate.Fields[0].Offset = 0;
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var projection = Arm64CallingConventionResolver
            .ReferenceRegisterReturnProjections(method)
            .Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(projection.Destination.Name, Is.EqualTo("X0"));
            Assert.That(((Register)projection.Source.Base!).Name, Is.EqualTo("X0"));
            Assert.That(projection.Source.Addend, Is.Zero);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 非八字节对齐引用字段不生成通用寄存器投影()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var aggregate = CreateValueType("MisalignedReferenceFields", stringType, stringType);
        aggregate.Fields[0].Offset = 0;
        aggregate.Fields[1].Offset = sizeof(int);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(
            Arm64CallingConventionResolver.ReferenceRegisterReturnProjections(method),
            Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void 引用和值字段混合聚合体不生成引用槽投影()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var aggregate = CreateValueType(
            "MixedReferenceAndValueFields",
            app.SystemTypes.SystemStringType,
            app.SystemTypes.SystemInt64Type);
        SetSequentialFieldOffsets(aggregate, sizeof(long));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(
            Arm64CallingConventionResolver.ReferenceRegisterReturnProjections(method),
            Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void 两引用返回的X1后继读取构成确定字段消费者()
    {
        var stringType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var aggregate = CreateValueType("ConsumedTwoReferenceFields", stringType, stringType);
        SetSequentialFieldOffsets(aggregate, sizeof(long));
        var returnMethod = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var storeSecondField = new Instruction(
            0,
            OpCode.Move,
            new MemoryOperand(new Register(null, "X19"), addend: sizeof(long)),
            new Register(null, "X1"))
        {
            MemoryAccessWidthBits = 64
        };

        Assert.That(
            NewArmV8InstructionSet.HasReferenceRegisterAggregateFieldConsumer(
                [storeSecondField],
                0,
                returnMethod,
                new Dictionary<ulong, List<MethodAnalysisContext>>()),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 单引用返回传给精确字段类型实参时保留投影()
    {
        const long targetAddress = 0x1234;
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var aggregate = CreateValueType("ConsumedOneReferenceField", stringType);
        aggregate.Fields[0].Offset = 0;
        var returnMethod = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var consumeString = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "ConsumeString",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [stringType]);
        var callOperands = new List<IOperand> { new Immediate(targetAddress) };
        callOperands.AddRange(Arm64CallingConventionResolver.ArgumentOperands(consumeString));
        var call = new Instruction(0, OpCode.CallVoid, callOperands);
        var methodsByAddress = new Dictionary<ulong, List<MethodAnalysisContext>>
        {
            [(ulong)targetAddress] = [consumeString]
        };

        Assert.That(
            NewArmV8InstructionSet.HasReferenceRegisterAggregateFieldConsumer(
                [call],
                0,
                returnMethod,
                methodsByAddress),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 单引用返回作为完整聚合体接收者时裁剪字段投影()
    {
        const long targetAddress = 0x5678;
        var app = Cpp2IlApi.CurrentAppContext!;
        var aggregate = CreateValueType(
            "CompleteOneReferenceAggregate",
            app.SystemTypes.SystemObjectType);
        aggregate.Fields[0].Offset = 0;
        var returnMethod = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var inspectAggregate = aggregate.InjectMethodContext(
            "InspectAggregate",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var callOperands = new List<IOperand> { new Immediate(targetAddress) };
        callOperands.AddRange(Arm64CallingConventionResolver.ArgumentOperands(inspectAggregate));
        var call = new Instruction(0, OpCode.CallVoid, callOperands);
        var methodsByAddress = new Dictionary<ulong, List<MethodAnalysisContext>>
        {
            [(ulong)targetAddress] = [inspectAggregate]
        };

        Assert.That(
            NewArmV8InstructionSet.HasReferenceRegisterAggregateFieldConsumer(
                [call],
                0,
                returnMethod,
                methodsByAddress),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 浮点标量返回值不生成聚合体字段投影()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "ReturnSingle",
            app.SystemTypes.SystemSingleType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(Arm64CallingConventionResolver.ReturnProjections(method), Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void Hfa实参消费使返回字段投影保持有效()
    {
        var projections = TwoSingleReturnProjections();
        var aggregate = new HomogeneousFloatingAggregateArgument(
            null!,
            [new Register(null, "V0"), new Register(null, "V1")]);
        var instructions = new Instruction[]
        {
            new(0, OpCode.CallVoid, new Immediate(0x1234), aggregate),
        };

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                instructions,
                0,
                projections),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 仅V0参与浮点算术仍保留返回字段投影()
    {
        var projections = TwoSingleReturnProjections();
        var instructions = new Instruction[]
        {
            new(
                0,
                OpCode.Add,
                new Register(null, "V2"),
                new Register(null, "V0"),
                new FloatLiteral(1f)),
        };

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                instructions,
                0,
                projections),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 一百二十八位完整值存储不触发标量字段投影()
    {
        var projections = TwoSingleReturnProjections();
        var store = new Instruction(
            0,
            OpCode.Move,
            new MemoryOperand(new Register(null, "X0")),
            new Register(null, "V0"))
        {
            MemoryAccessWidthBits = 128
        };

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                [store],
                0,
                projections),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 四个连续Single字段存储识别为完整Color赋值()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var aggregate = CreateValueType(
            "StoredColor",
            singleType,
            singleType,
            singleType,
            singleType);
        SetSequentialFieldOffsets(aggregate, sizeof(float));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var projections = Arm64CallingConventionResolver.ReturnProjections(method);
        var instructions = Enumerable.Range(0, 4)
            .Select(index => new Instruction(
                index,
                OpCode.Move,
                new MemoryOperand(new Register(null, "X19"), addend: 0x48 + index * sizeof(float)),
                new Register(null, $"V{index}"))
            {
                MemoryAccessWidthBits = 32
            })
            .ToArray();

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                instructions,
                0,
                projections),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 缺少最后字段的连续存储仍按标量消费者处理()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var aggregate = CreateValueType(
            "PartiallyStoredColor",
            singleType,
            singleType,
            singleType,
            singleType);
        SetSequentialFieldOffsets(aggregate, sizeof(float));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var projections = Arm64CallingConventionResolver.ReturnProjections(method);
        var instructions = Enumerable.Range(0, 3)
            .Select(index => new Instruction(
                index,
                OpCode.Move,
                new MemoryOperand(new Register(null, "X19"), addend: 0x48 + index * sizeof(float)),
                new Register(null, $"V{index}"))
            {
                MemoryAccessWidthBits = 32
            })
            .ToArray();

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                instructions,
                0,
                projections),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 极端字段偏移不溢出聚合体存储判定()
    {
        var projections = new (Register Destination, MemoryOperand Source)[]
        {
            (
                new Register(null, "V0"),
                new MemoryOperand(new Register(null, "V0"), addend: long.MaxValue))
        };
        var store = new Instruction(
            0,
            OpCode.Move,
            new MemoryOperand(new Register(null, "X19"), addend: long.MinValue),
            new Register(null, "V0"))
        {
            MemoryAccessWidthBits = 32
        };

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                [store],
                0,
                projections),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 缺操作数的原始Move不触发返回投影扫描异常()
    {
        var projections = TwoSingleReturnProjections();
        var malformed = new Instruction(0, OpCode.Move);

        Assert.That(
            NewArmV8InstructionSet.HasHomogeneousFloatingComponentConsumer(
                [malformed],
                0,
                projections),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void MixedOrFiveFieldAggregatesAreRejectedAsHfa()
    {
        var systemTypes = Cpp2IlApi.CurrentAppContext!.SystemTypes;
        var mixed = CreateValueType(
            "MixedFields",
            systemTypes.SystemSingleType,
            systemTypes.SystemInt32Type);
        var five = CreateValueType(
            "FiveSingleFields",
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(mixed, out _),
                Is.False);
            Assert.That(
                Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(five, out _),
                Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void MultiOperandReturnTreatsEveryComponentAsSource()
    {
        var first = new LocalVariable("first", new Register(null, "V0"));
        var second = new LocalVariable("second", new Register(null, "V1"));
        var instruction = new Instruction(0, OpCode.Return, first, second);

        Assert.That(instruction.Sources, Is.EqualTo(new IOperand[] { first, second }));
    }

    [Test]
    [Category("基本功能")]
    public void 十六字节泛型KeyValuePair占用两个通用寄存器槽()
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = definition.MakeGenericInstanceType(definition.GenericParameters);

        Assert.That(Arm64CallingConventionResolver.GeneralRegisterSlotCount(pair), Is.EqualTo(2));
    }

    [Test]
    [Category("边界值")]
    public void 普通引用参数只占一个通用寄存器槽()
    {
        Assert.That(
            Arm64CallingConventionResolver.GeneralRegisterSlotCount(
                Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType),
            Is.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 普通非泛型方法不追加隐藏MethodInfo()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Read",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        Assert.That(Arm64CallingConventionResolver.RequiresHiddenMethodInfo(method), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void Hfa实参由连续浮点寄存器组成一个托管操作数()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var color = CreateValueType(
            "ArgumentColor",
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType);
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [app.SystemTypes.SystemSingleType, color]);

        var operands = Arm64CallingConventionResolver.ArgumentOperands(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((Register)operands[0]).Name, Is.EqualTo("V0"));
            Assert.That(operands[1], Is.TypeOf<HomogeneousFloatingAggregateArgument>());
            Assert.That(
                ((HomogeneousFloatingAggregateArgument)operands[1]).Components
                    .Cast<Register>()
                    .Select(register => register.Name),
                Is.EqualTo(new[] { "V1", "V2", "V3", "V4" }));
        }
    }

    [Test]
    [Category("边界值")]
    public void Hfa剩余浮点寄存器不足时整体进入栈参数区()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var color = CreateValueType(
            "SpilledColor",
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType,
            app.SystemTypes.SystemSingleType);
        var parameterTypes = Enumerable
            .Repeat(app.SystemTypes.SystemSingleType, 6)
            .Concat(new TypeAnalysisContext[] { color, color })
            .ToArray();
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            parameterTypes);

        var operands = Arm64CallingConventionResolver.ArgumentOperands(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operands[6], Is.EqualTo(new StackOffset(0)));
            Assert.That(operands[7], Is.EqualTo(new StackOffset(16)));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 通用寄存器耗尽后的引用实参按八字节依序落栈()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var method = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            Enumerable.Repeat(app.SystemTypes.SystemObjectType, 10).ToArray());

        var operands = Arm64CallingConventionResolver.ArgumentOperands(method);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((Register)operands[7]).Name, Is.EqualTo("X7"));
            Assert.That(operands[8], Is.EqualTo(new StackOffset(0)));
            Assert.That(operands[9], Is.EqualTo(new StackOffset(8)));
        }
    }

    private static InjectedTypeAnalysisContext CreateValueType(
        string name,
        params TypeAnalysisContext[] fieldTypes)
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var valueType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.ValueType")!;
        var aggregate = appContext.InjectTypeIntoAllAssemblies(
                "Cpp2IL.Core.Tests",
                name,
                valueType,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
            .InjectedTypes[0];

        for (var index = 0; index < fieldTypes.Length; index++)
            aggregate.InjectFieldContext($"Component{index}", fieldTypes[index], FieldAttributes.Public);

        return aggregate;
    }

    private static InjectedMethodAnalysisContext CreateStaticMethod(
        TypeAnalysisContext declaringType,
        string name,
        TypeAnalysisContext returnType)
        => new(
            declaringType,
            name,
            returnType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

    private static void SetSequentialFieldOffsets(
        InjectedTypeAnalysisContext aggregate,
        int componentSize)
    {
        for (var index = 0; index < aggregate.Fields.Count; index++)
            aggregate.Fields[index].Offset = checked(index * componentSize);
    }

    private static IReadOnlyList<(Register Destination, MemoryOperand Source)>
        TwoSingleReturnProjections()
    {
        var singleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType;
        var aggregate = CreateValueType(
            "ConsumerVector2",
            singleType,
            singleType);
        SetSequentialFieldOffsets(aggregate, sizeof(float));
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        return Arm64CallingConventionResolver.ReturnProjections(method);
    }
}
