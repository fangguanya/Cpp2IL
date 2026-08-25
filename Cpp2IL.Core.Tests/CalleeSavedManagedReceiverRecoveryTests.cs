using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class CalleeSavedManagedReceiverRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 保存寄存器的单一数组复制覆盖IntPtr占位类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var arrayType = app.SystemTypes.SystemStringType.MakeSzArrayType();
        var source = new LocalVariable("source", new Register(null, "X0", 1), arrayType);
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X19", 1),
            app.SystemTypes.SystemIntPtrType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, source),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(carrier.Type, Is.SameAs(arrayType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 保存寄存器的同型多定义与空值共同收敛为数组类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var arrayType = app.SystemTypes.SystemStringType.MakeSzArrayType();
        var first = new LocalVariable("first", new Register(null, "X0", 1), arrayType);
        var second = new LocalVariable("second", new Register(null, "X0", 2), arrayType);
        var carrier = new LocalVariable(
            "carrier",
            new Register(null, "X29", 1),
            app.SystemTypes.SystemObjectType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, carrier, first),
            new(1, OpCode.Move, carrier, new Immediate(0)),
            new(2, OpCode.Move, carrier, second),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(carrier.Type, Is.SameAs(arrayType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 保存寄存器的异型引用或真实算术定义保持原类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringValue = new LocalVariable(
            "stringValue",
            new Register(null, "X0", 1),
            app.SystemTypes.SystemStringType);
        var objectArray = new LocalVariable(
            "objectArray",
            new Register(null, "X0", 2),
            app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var conflicting = new LocalVariable(
            "conflicting",
            new Register(null, "X19", 1),
            app.SystemTypes.SystemIntPtrType);
        var arithmetic = new LocalVariable(
            "arithmetic",
            new Register(null, "X20", 1),
            app.SystemTypes.SystemIntPtrType);
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, conflicting, stringValue),
            new(1, OpCode.Move, conflicting, objectArray),
            new(2, OpCode.Add, arithmetic, arithmetic, new Immediate(8)),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(conflicting.Type, Is.SameAs(app.SystemTypes.SystemIntPtrType));
            Assert.That(arithmetic.Type, Is.SameAs(app.SystemTypes.SystemIntPtrType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 引用Phi的字段与局部入边恢复业务数据类型()
    {
        var fixture = CreateReferencePhiCarrierFixture();
        var fieldReference = new FieldReference(
            fixture.ItemField,
            fixture.Closure,
            fixture.ItemField.Offset);
        var instructions = new Instruction[]
        {
            new(290, OpCode.Move, fixture.Carrier, fieldReference),
            new(-1, OpCode.Move, fixture.Carrier, fixture.Current),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Carrier.Type, Is.SameAs(fixture.ItemType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 引用Phi含空值与同型字段时仍恢复业务数据类型()
    {
        var fixture = CreateReferencePhiCarrierFixture();
        var fieldReference = new FieldReference(
            fixture.ItemField,
            fixture.Closure,
            fixture.ItemField.Offset);
        var instructions = new Instruction[]
        {
            new(290, OpCode.Move, fixture.Carrier, fieldReference),
            new(-1, OpCode.Move, fixture.Carrier, new Immediate(0)),
            new(-1, OpCode.Move, fixture.Carrier, fixture.Current),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Carrier.Type, Is.SameAs(fixture.ItemType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 内存字段与引用Phi互相推进后恢复后续业务字段()
    {
        var fixture = CreateReferencePhiCarrierFixture();
        var result = new LocalVariable(
            "result",
            new Register(null, "X8", 8),
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var sourceDefinition = new Instruction(
            290,
            OpCode.Move,
            fixture.Carrier,
            new MemoryOperand(fixture.Closure, addend: fixture.ItemField.Offset));
        var phiDefinition = new Instruction(-1, OpCode.Move, fixture.Carrier, fixture.Current);
        var businessFieldRead = new Instruction(
            307,
            OpCode.Move,
            result,
            new MemoryOperand(fixture.Carrier, addend: fixture.ItemValueField.Offset));
        var callerOwner = new InjectedTypeAnalysisContext(
            Cpp2IlApi.CurrentAppContext.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ReferencePhiCaller",
            Cpp2IlApi.CurrentAppContext.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            callerOwner,
            "Read",
            Cpp2IlApi.CurrentAppContext.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        caller.ControlFlowGraph = new ISILControlFlowGraph(
            [sourceDefinition, phiDefinition, businessFieldRead, new Instruction(308, OpCode.Return, result)]);
        caller.Locals = [fixture.Closure, fixture.Current, fixture.Carrier, result];
        caller.ParameterLocals = [];

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypesAndFields(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Carrier.Type, Is.SameAs(fixture.ItemType));
            Assert.That(sourceDefinition.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)sourceDefinition.Operands[1]).Field, Is.SameAs(fixture.ItemField));
            Assert.That(businessFieldRead.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)businessFieldRead.Operands[1]).Field, Is.SameAs(fixture.ItemValueField));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 引用Phi的异型托管入边保持Object红门()
    {
        var fixture = CreateReferencePhiCarrierFixture();
        var conflicting = new LocalVariable(
            "conflicting",
            new Register(null, "X1", 2),
            fixture.ItemType.MakeSzArrayType());
        var instructions = new Instruction[]
        {
            new(290, OpCode.Move, fixture.Carrier, fixture.Current),
            new(-1, OpCode.Move, fixture.Carrier, conflicting),
        };

        var recovered = CalleeSavedManagedReceiverRecovery.ResolveManagedCopyCarrierTypes(instructions);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(
                fixture.Carrier.Type,
                Is.SameAs(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一具体List分配恢复未定义X19接收者并重绑ToArray()
    {
        var fixture = CreateFixture();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);
        var rebound = (ConcreteGenericMethodAnalysisContext)fixture.Call.Operands[0];

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.FirstCandidate));
            Assert.That(rebound.TypeGenericParameters.Single().FullName, Is.EqualTo("System.String"));
            Assert.That(fixture.Result.Type?.FullName, Is.EqualTo("System.String[]"));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一泛型Enumerator生产值恢复非泛型MoveNext接收者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var genericEnumerator = mscorlib
            .GetTypeByFullName("System.Collections.Generic.IEnumerator`1")!
            .MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var nonGenericEnumerator = mscorlib.GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = nonGenericEnumerator.Methods.Single(method => method.Name == "MoveNext");
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "EnumeratorCallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var producerTarget = new InjectedMethodAnalysisContext(
            owner,
            "CreateEnumerator",
            genericEnumerator,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var produced = new LocalVariable("produced", new Register(null, "X0", 1), genericEnumerator);
        var saved = new LocalVariable("saved", new Register(null, "X20", 2), nonGenericEnumerator);
        var result = new LocalVariable("result", new Register(null, "W0", 3), app.SystemTypes.SystemBooleanType);
        var call = new Instruction(10, OpCode.Call, moveNext, result, saved);
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, producerTarget, produced),
            call,
            new Instruction(11, OpCode.Return, result),
        ]);
        caller.Locals = [produced, saved, result];
        caller.ParameterLocals = [];

        var recovered = CalleeSavedManagedReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(produced));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一具体List分配恢复未定义X19字段接收者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var sizeField = CreateConcreteSizeField(listDefinition, objectList);
        var produced = new LocalVariable("produced", new Register(null, "X0", 1), stringList);
        var saved = new LocalVariable("saved", new Register(null, "X19", 2), objectList);
        var count = new LocalVariable("count", new Register(null, "W0", 3), app.SystemTypes.SystemInt32Type);
        var field = new FieldReference(sizeField, saved, 0x18);
        var read = new Instruction(10, OpCode.Move, count, field);
        var caller = CreateFieldFixture(produced, saved, count, read);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(field.Local, Is.SameAs(produced));
        });
    }

    [Test]
    [Category("边界值")]
    public void 两个相容List生产值存在时保持字段接收者红门()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var sizeField = CreateConcreteSizeField(listDefinition, objectList);
        var first = new LocalVariable("first", new Register(null, "X0", 1), stringList);
        var second = new LocalVariable("second", new Register(null, "X0", 2), stringList);
        var saved = new LocalVariable("saved", new Register(null, "X19", 3), objectList);
        var count = new LocalVariable("count", new Register(null, "W0", 4), app.SystemTypes.SystemInt32Type);
        var field = new FieldReference(sizeField, saved, 0x18);
        var read = new Instruction(10, OpCode.Move, count, field);
        var caller = CreateFieldFixture(first, saved, count, read, second);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(field.Local, Is.SameAs(saved));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 字段接收者已有定义时禁止覆盖真实数据流()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var sizeField = CreateConcreteSizeField(listDefinition, objectList);
        var produced = new LocalVariable("produced", new Register(null, "X0", 1), stringList);
        var saved = new LocalVariable("saved", new Register(null, "X19", 2), objectList);
        var count = new LocalVariable("count", new Register(null, "W0", 3), app.SystemTypes.SystemInt32Type);
        var field = new FieldReference(sizeField, saved, 0x18);
        var read = new Instruction(10, OpCode.Move, count, field);
        var caller = CreateFieldFixture(produced, saved, count, read, definedReceiver: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(field.Local, Is.SameAs(saved));
        });
    }

    [Test]
    [Category("边界值")]
    public void 两个相容生产值存在时保持未定义接收者作为红门()
    {
        var fixture = CreateFixture(addSecondCandidate: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 接收者已有显式定义时禁止覆盖真实数据流()
    {
        var fixture = CreateFixture(defineReceiver: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两个相容分配存在时按冻结的X27直接复制恢复首个列表()
    {
        var fixture = CreateFixture(addSecondCandidate: true);
        var saved = new LocalVariable(
            "savedDirect",
            new Register(null, fixture.OriginalReceiver.Register.Name, 1),
            fixture.OriginalReceiver.Type);
        var undefined = new LocalVariable(
            "savedEntry",
            new Register(null, fixture.OriginalReceiver.Register.Name, 2),
            fixture.OriginalReceiver.Type);
        var savedMove = new Instruction(2, OpCode.Move, saved, fixture.FirstCandidate);
        var phi = new Instruction(-1, OpCode.Phi, fixture.OriginalReceiver, saved, undefined);
        var instructions = fixture.Caller.ControlFlowGraph!.Instructions.ToList();
        instructions.Insert(1, savedMove);
        instructions.Insert(2, phi);
        fixture.Caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(fixture.Caller);
        savedMove.OpCode = OpCode.Nop;
        savedMove.SetOperands();
        phi.OpCode = OpCode.Nop;
        phi.SetOperands();
        // 中文注释：复制合并可能重建同一 SSA 寄存器的 LocalVariable 对象；证据必须按
        // 完整寄存器版本匹配，而不是依赖旧对象引用。
        var refreshedCandidate = new LocalVariable(
            "refreshedFirst",
            fixture.FirstCandidate.Register,
            fixture.FirstCandidate.Type);
        fixture.Caller.ControlFlowGraph.Instructions
            .Single(instruction => instruction.Index == 0)
            .Destination = refreshedCandidate;

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[2], Is.SameAs(refreshedCandidate));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一X27版本冻结两个不同托管来源时保持红门()
    {
        var fixture = CreateFixture(addSecondCandidate: true);
        var secondCandidate = (LocalVariable)fixture.Caller.ControlFlowGraph!.Instructions[1].Destination!;
        var firstSaved = new LocalVariable(
            "firstSaved",
            new Register(null, fixture.OriginalReceiver.Register.Name, 1),
            fixture.OriginalReceiver.Type);
        var secondSaved = new LocalVariable(
            "secondSaved",
            new Register(null, fixture.OriginalReceiver.Register.Name, 2),
            fixture.OriginalReceiver.Type);
        var firstMove = new Instruction(2, OpCode.Move, firstSaved, fixture.FirstCandidate);
        var secondMove = new Instruction(3, OpCode.Move, secondSaved, secondCandidate);
        var phi = new Instruction(-1, OpCode.Phi, fixture.OriginalReceiver, firstSaved, secondSaved);
        var instructions = fixture.Caller.ControlFlowGraph.Instructions.ToList();
        instructions.Insert(2, firstMove);
        instructions.Insert(3, secondMove);
        instructions.Insert(4, phi);
        fixture.Caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(fixture.Caller);
        firstMove.OpCode = OpCode.Nop;
        firstMove.SetOperands();
        secondMove.OpCode = OpCode.Nop;
        secondMove.SetOperands();
        phi.OpCode = OpCode.Nop;
        phi.SetOperands();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void X27复制来源不是托管生产结果时禁止伪造接收者()
    {
        var fixture = CreateFixture();
        var unrelated = new LocalVariable(
            "unrelated",
            new Register(null, "X0", 20),
            fixture.FirstCandidate.Type);
        var saved = new LocalVariable(
            "savedDirect",
            new Register(null, fixture.OriginalReceiver.Register.Name, 1),
            fixture.OriginalReceiver.Type);
        var savedMove = new Instruction(2, OpCode.Move, saved, unrelated);
        var phi = new Instruction(-1, OpCode.Phi, fixture.OriginalReceiver, saved);
        var instructions = fixture.Caller.ControlFlowGraph!.Instructions.ToList();
        instructions.Insert(2, savedMove);
        instructions.Insert(3, phi);
        fixture.Caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(fixture.Caller);
        savedMove.OpCode = OpCode.Nop;
        savedMove.SetOperands();
        phi.OpCode = OpCode.Nop;
        phi.SetOperands();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一Phi来源恢复未定义X27的空值比较读取()
    {
        var fixture = CreateDirectUseFixture();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.FirstCandidate));
        });
    }

    [Test]
    [Category("边界值")]
    public void 空值比较存在两个相容Phi来源时保持红门()
    {
        var fixture = CreateDirectUseFixture(compatibleSourceCount: 2);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空值比较接收者已有定义时禁止覆盖真实数据流()
    {
        var fixture = CreateDirectUseFixture(defineReceiver: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void X29保存的唯一This参数恢复实例空值比较()
    {
        var fixture = CreateParameterUseFixture();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.FirstParameter));
        });
    }

    [Test]
    [Category("边界值")]
    public void X29的Phi含两个相容引用参数时保持红门()
    {
        var fixture = CreateParameterUseFixture(parameterSourceCount: 2);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void X29来源不是参数或托管生产结果时禁止恢复()
    {
        var fixture = CreateParameterUseFixture(useNonParameterSource: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void X29仅有同源退Ssa边复制时仍恢复This参数()
    {
        var fixture = CreateParameterUseFixture();
        AddReceiverDefinition(
            fixture,
            new Instruction(-1, OpCode.Move, fixture.Receiver, fixture.FirstParameter));

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.FirstParameter));
        });
    }

    [Test]
    [Category("边界值")]
    public void X29生成边复制含未证明来源时保持红门()
    {
        var fixture = CreateParameterUseFixture();
        var unproven = new LocalVariable(
            "unproven",
            new Register(null, "X29", 99),
            fixture.Receiver.Type);
        AddReceiverDefinition(fixture, new Instruction(-1, OpCode.Move, fixture.Receiver, unproven));

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void X29存在真实索引定义时禁止按生成边覆盖()
    {
        var fixture = CreateParameterUseFixture();
        AddReceiverDefinition(
            fixture,
            new Instruction(30, OpCode.Move, fixture.Receiver, fixture.FirstParameter));

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Comparison.Operands[1], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void X29保存的This搬回X0后恢复实例调用接收者()
    {
        var fixture = CreateCallerSavedCallFixture();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.FirstParameter));
        });
    }

    [Test]
    [Category("边界值")]
    public void X0回搬链含两个相容参数来源时保持红门()
    {
        var fixture = CreateCallerSavedCallFixture(parameterSourceCount: 2);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void X0回搬链没有托管参数或生产来源时禁止恢复()
    {
        var fixture = CreateCallerSavedCallFixture(useNonParameterSource: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.Receiver));
        });
    }

    private static Fixture CreateFixture(bool addSecondCandidate = false, bool defineReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var toArray = listDefinition.Methods.Single(method =>
            method.Name == "ToArray" && method.Parameters.Count == 0);
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var oldTarget = new ConcreteGenericMethodAnalysisContext(
            toArray,
            [app.SystemTypes.SystemObjectType],
            []);
        var firstCandidate = new LocalVariable("first", new Register(null, "X0", 1), stringList);
        var secondCandidate = new LocalVariable("second", new Register(null, "X0", 2), stringList);
        var receiver = new LocalVariable("saved", new Register(null, "X19", 3), objectList);
        var result = new LocalVariable("result", new Register(null, "X0", 4), oldTarget.ReturnType);
        var instructions = new System.Collections.Generic.List<Instruction>
        {
            new(0, OpCode.Newobj, firstCandidate, stringList),
        };
        if (addSecondCandidate)
            instructions.Add(new Instruction(1, OpCode.Newobj, secondCandidate, stringList));
        if (defineReceiver)
            instructions.Add(new Instruction(2, OpCode.Move, receiver, firstCandidate));
        var call = new Instruction(10, OpCode.Call, oldTarget, result, receiver);
        instructions.Add(call);
        instructions.Add(new Instruction(11, OpCode.Return, result));

        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "CallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            oldTarget.ReturnType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [firstCandidate, secondCandidate, receiver, result];
        caller.ParameterLocals = [];

        return new Fixture(caller, call, receiver, firstCandidate, result);
    }

    private static DirectUseFixture CreateDirectUseFixture(
        int compatibleSourceCount = 1,
        bool defineReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var first = new LocalVariable("first", new Register(null, "X0", 1), stringList);
        var receiver = new LocalVariable("merged", new Register(null, "X27", 8), objectList);
        var condition = new LocalVariable("condition", new Register(null, "W0", 1), app.SystemTypes.SystemBooleanType);
        var instructions = new System.Collections.Generic.List<Instruction>();
        var savedLocals = new System.Collections.Generic.List<LocalVariable>();
        var evidenceMoves = new System.Collections.Generic.List<Instruction>();

        for (var index = 0; index < compatibleSourceCount; index++)
        {
            var candidate = index == 0
                ? first
                : new LocalVariable($"candidate{index}", new Register(null, "X0", index + 1), stringList);
            var saved = new LocalVariable($"saved{index}", new Register(null, "X27", index + 1), objectList);
            instructions.Add(new Instruction(index, OpCode.Newobj, candidate, stringList));
            var move = new Instruction(index + 20, OpCode.Move, saved, candidate);
            instructions.Add(move);
            savedLocals.Add(saved);
            evidenceMoves.Add(move);
        }

        var phi = new Instruction(
            -1,
            OpCode.Phi,
            new[] { receiver }.Concat(savedLocals).Cast<IOperand>().ToList());
        instructions.Add(phi);
        if (defineReceiver)
            instructions.Add(new Instruction(30, OpCode.Move, receiver, first));
        var comparison = new Instruction(40, OpCode.CheckNotEqual, condition, receiver, new Immediate(0));
        instructions.Add(comparison);
        instructions.Add(new Instruction(41, OpCode.Return, condition));

        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "DirectUseCallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [first, receiver, condition, .. savedLocals];
        caller.ParameterLocals = [];
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(caller);

        foreach (var move in evidenceMoves)
        {
            move.OpCode = OpCode.Nop;
            move.SetOperands();
        }
        phi.OpCode = OpCode.Nop;
        phi.SetOperands();

        return new DirectUseFixture(caller, comparison, receiver, first);
    }

    private static ParameterUseFixture CreateParameterUseFixture(
        int parameterSourceCount = 1,
        bool useNonParameterSource = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ParameterUseCallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public,
            []);
        var firstParameter = new LocalVariable("this", new Register(null, "X0", 0), owner)
        {
            IsThis = true,
        };
        var parameterLocals = new System.Collections.Generic.List<LocalVariable> { firstParameter };
        for (var index = 1; index < parameterSourceCount; index++)
            parameterLocals.Add(new LocalVariable($"parameter{index}", new Register(null, $"X{index}", 0), owner));

        var evidenceSources = useNonParameterSource
            ? [new LocalVariable("unrelated", new Register(null, "X8", 1), owner)]
            : parameterLocals;
        var savedLocals = new System.Collections.Generic.List<LocalVariable>();
        var evidenceMoves = new System.Collections.Generic.List<Instruction>();
        var instructions = new System.Collections.Generic.List<Instruction>();
        for (var index = 0; index < evidenceSources.Count; index++)
        {
            var saved = new LocalVariable($"saved{index}", new Register(null, "X29", index + 1), owner);
            var move = new Instruction(index, OpCode.Move, saved, evidenceSources[index]);
            savedLocals.Add(saved);
            evidenceMoves.Add(move);
            instructions.Add(move);
        }

        var receiver = new LocalVariable("merged", new Register(null, "X29", 20), owner);
        var phi = new Instruction(
            -1,
            OpCode.Phi,
            new[] { receiver }.Concat(savedLocals).Cast<IOperand>().ToList());
        var condition = new LocalVariable("condition", new Register(null, "W0", 1), app.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(40, OpCode.CheckNotEqual, condition, receiver, new Immediate(0));
        instructions.Add(phi);
        instructions.Add(comparison);
        instructions.Add(new Instruction(41, OpCode.Return, condition));
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [firstParameter, receiver, condition, .. savedLocals];
        caller.ParameterLocals = parameterLocals;
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(caller);

        foreach (var move in evidenceMoves)
        {
            move.OpCode = OpCode.Nop;
            move.SetOperands();
        }
        phi.OpCode = OpCode.Nop;
        phi.SetOperands();

        return new ParameterUseFixture(caller, comparison, receiver, firstParameter);
    }

    private static void AddReceiverDefinition(ParameterUseFixture fixture, Instruction definition)
    {
        var instructions = fixture.Caller.ControlFlowGraph!.Instructions.ToList();
        var comparisonIndex = instructions.IndexOf(fixture.Comparison);
        instructions.Insert(comparisonIndex, definition);
        fixture.Caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
    }

    private static CallerSavedCallFixture CreateCallerSavedCallFixture(
        int parameterSourceCount = 1,
        bool useNonParameterSource = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "CallerSavedOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var target = new InjectedMethodAnalysisContext(
            owner,
            "Target",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public,
            []);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public,
            []);
        var firstParameter = new LocalVariable("this", new Register(null, "X0", 0), owner)
        {
            IsThis = true,
        };
        var parameterLocals = new System.Collections.Generic.List<LocalVariable> { firstParameter };
        for (var index = 1; index < parameterSourceCount; index++)
            parameterLocals.Add(new LocalVariable($"parameter{index}", new Register(null, $"X{index}", 0), owner));

        var sources = useNonParameterSource
            ? [new LocalVariable("unrelated", new Register(null, "X8", 1), owner)]
            : parameterLocals;
        var savedLocals = new System.Collections.Generic.List<LocalVariable>();
        var evidenceMoves = new System.Collections.Generic.List<Instruction>();
        var instructions = new System.Collections.Generic.List<Instruction>();
        for (var index = 0; index < sources.Count; index++)
        {
            var saved = new LocalVariable($"saved{index}", new Register(null, "X29", index + 1), owner);
            var move = new Instruction(index, OpCode.Move, saved, sources[index]);
            savedLocals.Add(saved);
            evidenceMoves.Add(move);
            instructions.Add(move);
        }

        LocalVariable savedSource;
        Instruction? phi = null;
        if (savedLocals.Count == 1)
        {
            savedSource = savedLocals[0];
        }
        else
        {
            savedSource = new LocalVariable("savedMerged", new Register(null, "X29", 20), owner);
            phi = new Instruction(
                -1,
                OpCode.Phi,
                new[] { savedSource }.Concat(savedLocals).Cast<IOperand>().ToList());
            instructions.Add(phi);
        }

        var receiver = new LocalVariable("receiver", new Register(null, "X0", 408), owner);
        var receiverMove = new Instruction(30, OpCode.Move, receiver, savedSource);
        var result = new LocalVariable("result", new Register(null, "W0", 409), app.SystemTypes.SystemBooleanType);
        var call = new Instruction(40, OpCode.Call, target, result, receiver);
        instructions.Add(receiverMove);
        instructions.Add(call);
        instructions.Add(new Instruction(41, OpCode.Return, result));
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [firstParameter, receiver, result, .. savedLocals];
        caller.ParameterLocals = parameterLocals;
        CalleeSavedManagedReceiverRecovery.CaptureSsaCopyEvidence(caller);

        foreach (var move in evidenceMoves.Append(receiverMove))
        {
            move.OpCode = OpCode.Nop;
            move.SetOperands();
        }
        if (phi != null)
        {
            phi.OpCode = OpCode.Nop;
            phi.SetOperands();
        }

        return new CallerSavedCallFixture(caller, call, receiver, firstParameter);
    }

    private static MethodAnalysisContext CreateFieldFixture(
        LocalVariable first,
        LocalVariable receiver,
        LocalVariable result,
        Instruction read,
        LocalVariable? second = null,
        bool definedReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var instructions = new System.Collections.Generic.List<Instruction>
        {
            new(0, OpCode.Newobj, first, first.Type!),
        };
        if (second != null)
            instructions.Add(new Instruction(1, OpCode.Newobj, second, second.Type!));
        if (definedReceiver)
            instructions.Add(new Instruction(2, OpCode.Move, receiver, first));
        instructions.Add(read);
        instructions.Add(new Instruction(11, OpCode.Return, result));
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "FieldCallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            result.Type!,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = second == null
            ? [first, receiver, result]
            : [first, second, receiver, result];
        caller.ParameterLocals = [];
        return caller;
    }

    private static FieldAnalysisContext CreateConcreteSizeField(
        TypeAnalysisContext listDefinition,
        GenericInstanceTypeAnalysisContext concreteList)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var baseField = new InjectedFieldAnalysisContext(
            "_size",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Private,
            listDefinition,
            offset: 24);
        return new ConcreteGenericFieldAnalysisContext(baseField, concreteList);
    }

    private static ReferencePhiCarrierFixture CreateReferencePhiCarrierFixture()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var itemType = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ReferencePhiItem",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var closureType = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ReferencePhiClosure",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var itemField = new InjectedFieldAnalysisContext(
            "item",
            itemType,
            FieldAttributes.Public,
            closureType,
            offset: 0x10);
        closureType.Fields.Add(itemField);
        var itemValueField = new InjectedFieldAnalysisContext(
            "Disguise",
            app.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            itemType,
            offset: 0x10);
        itemType.Fields.Add(itemValueField);

        return new ReferencePhiCarrierFixture(
            itemType,
            itemField,
            itemValueField,
            new LocalVariable("closure", new Register(null, "X8", 1), closureType),
            new LocalVariable("current", new Register(null, "X0", 1), itemType),
            new LocalVariable(
                "carrier",
                new Register(null, "X0", 70),
                app.SystemTypes.SystemObjectType));
    }

    private sealed record Fixture(
        MethodAnalysisContext Caller,
        Instruction Call,
        LocalVariable OriginalReceiver,
        LocalVariable FirstCandidate,
        LocalVariable Result);

    private sealed record DirectUseFixture(
        MethodAnalysisContext Caller,
        Instruction Comparison,
        LocalVariable Receiver,
        LocalVariable FirstCandidate);

    private sealed record ParameterUseFixture(
        MethodAnalysisContext Caller,
        Instruction Comparison,
        LocalVariable Receiver,
        LocalVariable FirstParameter);

    private sealed record CallerSavedCallFixture(
        MethodAnalysisContext Caller,
        Instruction Call,
        LocalVariable Receiver,
        LocalVariable FirstParameter);

    private sealed record ReferencePhiCarrierFixture(
        TypeAnalysisContext ItemType,
        FieldAnalysisContext ItemField,
        FieldAnalysisContext ItemValueField,
        LocalVariable Closure,
        LocalVariable Current,
        LocalVariable Carrier);
}
