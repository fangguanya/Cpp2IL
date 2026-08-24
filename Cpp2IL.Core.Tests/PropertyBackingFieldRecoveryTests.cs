using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class PropertyBackingFieldRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 跨类型自动属性字段读取恢复为公开Getter()
    {
        var fixture = CreateFixture("<Value>k__BackingField", write: false);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(fixture.Access.Operands[0], Is.SameAs(fixture.Getter));
            Assert.That(fixture.Access.Operands[1], Is.SameAs(fixture.Value));
            Assert.That(fixture.Access.Operands[2], Is.SameAs(fixture.Receiver));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 最终虚Getter可恢复值类型Enumerator当前元素访问()
    {
        var fixture = CreateFixture(
            "_current",
            write: false,
            propertyName: "Current",
            finalVirtualAccessors: true);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(fixture.Access.Operands[0], Is.SameAs(fixture.Getter));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 共享EnumeratorObject字段按String接收者恢复具体Getter()
    {
        var fixture = CreateSharedEnumeratorFixture(embedded: false);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        var target = (ConcreteGenericMethodAnalysisContext)fixture.Access.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(target.TypeGenericParameters, Is.EqualTo(new[] { fixture.StringType }));
            Assert.That(target.ReturnType, Is.SameAs(fixture.StringType));
            Assert.That(fixture.Value.Type, Is.SameAs(fixture.StringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 列表Getter把接口结果槽收紧为具体列表()
    {
        var fixture = CreateListGetterFixture();

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(fixture.Value.Type, Is.SameAs(fixture.ListType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 列表Getter保留已经精确的具体列表结果槽()
    {
        var fixture = CreateListGetterFixture(useExactDestination: true);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Value.Type, Is.SameAs(fixture.ListType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 列表Getter不覆盖元素类型冲突的接口结果槽()
    {
        var fixture = CreateListGetterFixture(useConflictingDestination: true);
        var originalType = fixture.Value.Type;

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(fixture.Value.Type, Is.SameAs(originalType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 嵌套比较中的共享Enumerator字段物化StringGetter局部()
    {
        var fixture = CreateSharedEnumeratorFixture(embedded: true);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);
        var instructions = fixture.Caller.ControlFlowGraph!.Instructions;
        var getterCall = instructions[0];
        var target = (ConcreteGenericMethodAnalysisContext)getterCall.Operands[0];
        var temporary = (LocalVariable)getterCall.Operands[1];

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(instructions, Has.Count.EqualTo(3));
            Assert.That(target.TypeGenericParameters, Is.EqualTo(new[] { fixture.StringType }));
            Assert.That(temporary.Type, Is.SameAs(fixture.StringType));
            Assert.That(fixture.Access.Operands[2], Is.SameAs(temporary));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Enumerator字段与接收者均为不同具体实例时保留字段红门()
    {
        var fixture = CreateSharedEnumeratorFixture(embedded: false, declaredString: true);
        fixture.Receiver.Type = fixture.EnumeratorDefinition.MakeGenericInstanceType([
            fixture.Caller.AppContext.SystemTypes.SystemInt32Type,
        ]);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.Access.Operands.OfType<FieldReference>(), Has.Exactly(1).Items);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 可覆盖虚Getter保持字段红门()
    {
        var fixture = CreateFixture(
            "_current",
            write: false,
            propertyName: "Current",
            virtualAccessors: true);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 嵌套调用接收者字段读取先物化为公开Getter()
    {
        var fixture = CreateFixture("<Value>k__BackingField", write: false);
        var consumer = ((InjectedTypeAnalysisContext)fixture.Caller.DeclaringType!).InjectMethodContext(
            "Consume",
            fixture.Caller.AppContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            fixture.Value.Type!);
        var call = new Instruction(1, OpCode.CallVoid, consumer, new FieldReference(
            ((FieldReference)fixture.Access.Operands[1]).Field,
            fixture.Receiver,
            16));
        fixture.Caller.ControlFlowGraph = new ISILControlFlowGraph([
            call,
            new Instruction(2, OpCode.Return),
        ]);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);
        var instructions = fixture.Caller.ControlFlowGraph.Instructions;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(instructions, Has.Count.EqualTo(3));
            Assert.That(instructions[0].OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(instructions[0].Operands[0], Is.SameAs(fixture.Getter));
            Assert.That(call.Operands[1], Is.SameAs(instructions[0].Operands[1]));
            Assert.That(call.Operands.OfType<FieldReference>(), Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 小驼峰字段写入恢复为公开Setter()
    {
        var fixture = CreateFixture("serializedValue", write: true, propertyName: "SerializedValue");

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(fixture.Access.Operands[0], Is.SameAs(fixture.Setter));
            Assert.That(fixture.Access.Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(fixture.Access.Operands[2], Is.SameAs(fixture.Value));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 字段到字段赋值同时恢复Getter与Setter()
    {
        var writeFixture = CreateFixture("<TargetValue>k__BackingField", write: true, propertyName: "TargetValue");
        var readFixture = CreateFixture("<SourceValue>k__BackingField", write: false, propertyName: "SourceValue");
        var sourceField = (FieldReference)readFixture.Access.Operands[1];
        writeFixture.Access.SetOperand(1, sourceField);

        var recovered = PropertyBackingFieldRecovery.Run(writeFixture.Caller);
        var instructions = writeFixture.Caller.ControlFlowGraph!.Instructions;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(instructions, Has.Count.EqualTo(3));
            Assert.That(instructions[0].OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(instructions[0].Operands[0], Is.SameAs(readFixture.Getter));
            Assert.That(instructions[1].OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(instructions[1].Operands[0], Is.SameAs(writeFixture.Setter));
            Assert.That(instructions[1].Operands[2], Is.SameAs(instructions[0].Operands[1]));
            Assert.That(instructions.SelectMany(instruction => instruction.Operands).OfType<FieldReference>(), Is.Empty);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 右值没有公开Getter时只恢复左侧Setter并保留字段红门()
    {
        var writeFixture = CreateFixture("<TargetValue>k__BackingField", write: true, propertyName: "TargetValue");
        var readFixture = CreateFixture(
            "<SourceValue>k__BackingField",
            write: false,
            propertyName: "SourceValue",
            publicAccessors: false);
        var sourceField = (FieldReference)readFixture.Access.Operands[1];
        writeFixture.Access.SetOperand(1, sourceField);

        var recovered = PropertyBackingFieldRecovery.Run(writeFixture.Caller);
        var instructions = writeFixture.Caller.ControlFlowGraph!.Instructions;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(instructions, Has.Count.EqualTo(2));
            Assert.That(instructions[0].OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(instructions[0].Operands[0], Is.SameAs(writeFixture.Setter));
            Assert.That(instructions[0].Operands[2], Is.SameAs(sourceField));
            Assert.That(instructions[0].Operands.OfType<FieldReference>(), Has.Exactly(1).Items);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 私有访问器保持字段红门()
    {
        var fixture = CreateFixture("<Value>k__BackingField", write: false, publicAccessors: false);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.Access.Operands.OfType<FieldReference>(), Has.Exactly(1).Items);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一私有访问域保持原字段语义()
    {
        var fixture = CreateFixture("<Value>k__BackingField", write: false, callerSharesOwner: true);

        var recovered = PropertyBackingFieldRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Access.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    private static Fixture CreateFixture(
        string fieldName,
        bool write,
        string propertyName = "Value",
        bool publicAccessors = true,
        bool callerSharesOwner = false,
        bool finalVirtualAccessors = false,
        bool virtualAccessors = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "Owner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var visibility = publicAccessors ? MethodAttributes.Public : MethodAttributes.Private;
        if (finalVirtualAccessors)
            visibility |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
        else if (virtualAccessors)
            visibility |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        var getter = owner.InjectMethodContext(
            $"get_{propertyName}",
            app.SystemTypes.SystemInt32Type,
            visibility);
        var setter = owner.InjectMethodContext(
            $"set_{propertyName}",
            app.SystemTypes.SystemVoidType,
            visibility,
            app.SystemTypes.SystemInt32Type);
        owner.InjectPropertyContext(
            propertyName,
            app.SystemTypes.SystemInt32Type,
            getter,
            setter,
            PropertyAttributes.None);
        var field = owner.InjectFieldContext(
            fieldName,
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Private);
        var callerType = callerSharesOwner
            ? owner
            : new InjectedTypeAnalysisContext(
                assembly,
                "Fixture",
                "Caller",
                app.SystemTypes.SystemObjectType,
                TypeAttributes.Public);
        var caller = callerType.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("owner", new Register(null, "X0"), owner);
        var value = new LocalVariable("value", new Register(null, "W8"), app.SystemTypes.SystemInt32Type);
        var fieldReference = new FieldReference(field, receiver, 16);
        var access = write
            ? new Instruction(0, OpCode.Move, fieldReference, value)
            : new Instruction(0, OpCode.Move, value, fieldReference);
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            access,
            new Instruction(1, OpCode.Return),
        ]);
        return new Fixture(caller, getter, setter, receiver, value, access);
    }

    private static SharedEnumeratorFixture CreateSharedEnumeratorFixture(
        bool embedded,
        bool declaredString = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var enumeratorDefinition = assembly
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var stringType = app.SystemTypes.SystemStringType;
        var declaredElementType = declaredString ? stringType : app.SystemTypes.SystemObjectType;
        var declaredEnumerator = enumeratorDefinition.MakeGenericInstanceType([declaredElementType]);
        var receiverEnumerator = enumeratorDefinition.MakeGenericInstanceType([stringType]);
        var field = GenericInstanceFieldLayout.FindConcreteFieldAtOffset(declaredEnumerator, 0x10)!;
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "EnumeratorCaller",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var caller = callerType.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("enumerator", new Register(null, "stack_-80"), receiverEnumerator);
        var value = new LocalVariable("value", new Register(null, "X0"), field.FieldType);
        var fieldReference = new FieldReference(field, receiver, 0x10);
        Instruction access;
        if (embedded)
        {
            var equals = app.SystemTypes.SystemStringType.Methods.Single(method =>
                method.Name == "op_Equality" && method.Parameters.Count == 2);
            var result = new LocalVariable("equal", new Register(null, "W0"), app.SystemTypes.SystemBooleanType);
            access = new Instruction(0, OpCode.Call, equals, result, fieldReference, new StringLiteral("circuit"));
        }
        else
        {
            access = new Instruction(0, OpCode.Move, value, fieldReference);
        }
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            access,
            new Instruction(1, OpCode.Return),
        ]);
        return new SharedEnumeratorFixture(
            caller,
            enumeratorDefinition,
            stringType,
            receiver,
            value,
            access);
    }

    private static ListGetterFixture CreateListGetterFixture(
        bool useExactDestination = false,
        bool useConflictingDestination = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var stringType = app.SystemTypes.SystemStringType;
        var listDefinition = assembly.GetTypeByFullName("System.Collections.Generic.List`1")!;
        var enumerableDefinition = assembly.GetTypeByFullName("System.Collections.Generic.IEnumerable`1")!;
        var listType = listDefinition.MakeGenericInstanceType([stringType]);
        var destinationType = useExactDestination
            ? listType
            : enumerableDefinition.MakeGenericInstanceType([
                useConflictingDestination ? app.SystemTypes.SystemInt32Type : stringType,
            ]);
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "ListOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var getter = owner.InjectMethodContext(
            "get_Items",
            listType,
            MethodAttributes.Public);
        owner.InjectPropertyContext(
            "Items",
            listType,
            getter,
            null,
            PropertyAttributes.None);
        var field = owner.InjectFieldContext(
            "<Items>k__BackingField",
            listType,
            FieldAttributes.Private);
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "ListCaller",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var caller = callerType.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("owner", new Register(null, "X0"), owner);
        var value = new LocalVariable("items", new Register(null, "X19"), destinationType);
        var access = new Instruction(
            0,
            OpCode.Move,
            value,
            new FieldReference(field, receiver, 0x10));
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            access,
            new Instruction(1, OpCode.Return),
        ]);
        return new ListGetterFixture(caller, listType, value, access);
    }

    private sealed record Fixture(
        MethodAnalysisContext Caller,
        MethodAnalysisContext Getter,
        MethodAnalysisContext Setter,
        LocalVariable Receiver,
        LocalVariable Value,
        Instruction Access);

    private sealed record SharedEnumeratorFixture(
        MethodAnalysisContext Caller,
        TypeAnalysisContext EnumeratorDefinition,
        TypeAnalysisContext StringType,
        LocalVariable Receiver,
        LocalVariable Value,
        Instruction Access);

    private sealed record ListGetterFixture(
        MethodAnalysisContext Caller,
        TypeAnalysisContext ListType,
        LocalVariable Value,
        Instruction Access);
}
