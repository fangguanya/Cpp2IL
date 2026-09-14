using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ParameterSourceIdentityTests
{
    [SetUp]
    public void 初始化()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Category("基本功能")]
    public void 未使用或聚合形参之后的标量按原始身份传播(bool instance, bool aggregate)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var method = 创建方法(instance);
        var first = new Register(null, "X1");
        var scalar = new Register(null, "stack_20");
        if (instance)
            method.ParameterOperands.Add(new Register(null, "X0"));
        method.ParameterOperands.Add(aggregate
            ? new HomogeneousFloatingAggregateArgument(method.Parameters[0].ParameterType,
                [new Register(null, "V0"), new Register(null, "V1")])
            : first);
        method.ParameterOperands.Add(scalar);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, new Register(null, "V2", 0), scalar),
            new Instruction(1, OpCode.Return)
        ]);

        LocalVariables.CreateAll(method);
        var local = method.ParameterLocals.Single();
        Assert.That(local.SourceParameter, Is.SameAs(method.Parameters[1]));
        // 故意制造同名和错误旧类型，证明传播既不看名字也不看压缩列表下标。
        local.Name = method.Parameters[0].ParameterName;
        local.Type = method.Parameters[0].ParameterType;
        LocalVariables.PropagateFromParameters(method);
        Assert.That(local.Type, Is.SameAs(app.SystemTypes.SystemSingleType));
        LocalVariables.PropagateFromParameters(method);
        Assert.That(local.SourceParameter!.ParameterIndex, Is.EqualTo(1));
        Assert.That(local.Type, Is.SameAs(app.SystemTypes.SystemSingleType));
    }

    [Test]
    [Category("边界值")]
    public void 无来源的局部变量不冒充同名形参()
    {
        var method = 创建方法(false);
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt64Type;
        var local = new LocalVariable(method.Parameters[0].ParameterName, new Register(null, "X2"), type);
        method.ParameterLocals = [local];
        LocalVariables.PropagateFromParameters(method);
        Assert.That(local.Type, Is.SameAs(type));
        Assert.That(local.SourceParameter, Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 其他方法的形参身份必须拒绝传播()
    {
        var method = 创建方法(false);
        var foreign = 创建方法(false);
        method.ParameterLocals = [new LocalVariable("foreign", new Register(null, "X2"))
            { SourceParameter = foreign.Parameters[1] }];
        Assert.Throws<InvalidOperationException>(() => LocalVariables.PropagateFromParameters(method));
        Assert.That(method.ParameterLocals[0].Type, Is.Null);
    }

    private static InjectedMethodAnalysisContext 创建方法(bool instance)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.SystemTypes.SystemObjectType;
        var pair = new InjectedTypeAnalysisContext(owner.DeclaringAssembly, "ParameterFixture", "Pair",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"), TypeAttributes.Public);
        pair.Fields.Add(new InjectedFieldAnalysisContext("x", app.SystemTypes.SystemSingleType, FieldAttributes.Public, pair, 0));
        pair.Fields.Add(new InjectedFieldAnalysisContext("y", app.SystemTypes.SystemSingleType, FieldAttributes.Public, pair, 4));
        return new InjectedMethodAnalysisContext(owner, "Read", app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | (instance ? 0 : MethodAttributes.Static),
            [pair, app.SystemTypes.SystemSingleType], ["pair", "scalar"]);
    }
}
