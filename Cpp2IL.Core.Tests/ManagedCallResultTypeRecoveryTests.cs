using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ManagedCallResultTypeRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 唯一列表调用结果从枚举接口收紧为具体列表()
    {
        var fixture = CreateFixture();

        var recovered = ManagedCallResultTypeRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Result.Type, Is.SameAs(fixture.ReturnType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一寄存器局部存在多个定义时保持上界类型()
    {
        var fixture = CreateFixture(addSecondDefinition: true);
        var originalType = fixture.Result.Type;

        var recovered = ManagedCallResultTypeRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Result.Type, Is.SameAs(originalType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 元素类型冲突的枚举接口保持诊断红门()
    {
        var fixture = CreateFixture(useConflictingUpperBound: true);
        var originalType = fixture.Result.Type;

        var recovered = ManagedCallResultTypeRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Result.Type, Is.SameAs(originalType));
        });
    }

    private static Fixture CreateFixture(
        bool addSecondDefinition = false,
        bool useConflictingUpperBound = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var stringType = app.SystemTypes.SystemStringType;
        var listType = assembly.GetTypeByFullName("System.Collections.Generic.List`1")!
            .MakeGenericInstanceType([stringType]);
        // 中文注释：IL2CPP 共享泛型先把 IEnumerable<string> 登记成 IEnumerable<object>；
        // 冲突用例则使用具体 Int32，确保规则只晋级弱 object 占位。
        var upperElementType = useConflictingUpperBound
            ? app.SystemTypes.SystemInt32Type
            : app.SystemTypes.SystemObjectType;
        var upperType = assembly.GetTypeByFullName("System.Collections.Generic.IEnumerable`1")!
            .MakeGenericInstanceType([upperElementType]);
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "CallOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var target = owner.InjectMethodContext(
            "GetItems",
            listType,
            MethodAttributes.Public | MethodAttributes.Static);
        var caller = owner.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var result = new LocalVariable("items", new Register(null, "X0"), upperType);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Call, target, result),
        };
        if (addSecondDefinition)
            instructions.Add(new Instruction(1, OpCode.Move, result, new Immediate(0)));
        instructions.Add(new Instruction(2, OpCode.Return));
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        return new Fixture(caller, listType, result);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        TypeAnalysisContext ReturnType,
        LocalVariable Result);
}
