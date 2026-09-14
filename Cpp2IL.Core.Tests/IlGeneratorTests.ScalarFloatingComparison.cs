using System;
using System.IO;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Disarm;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase("同精度", true)]
    [TestCase("右零", true)]
    [TestCase("左零", true)]
    [TestCase("字面量", true)]
    [TestCase("精度冲突", false)]
    [TestCase("未知来源", false)]
    [TestCase("整数来源", false)]
    [TestCase("非零整数", false)]
    [TestCase("错误目标", false)]
    [TestCase("向量操作数", false)]
    [TestCase("位操作", false)]
    [Category("基本功能")]
    [Category("异常输入")]
    public void 浮点标量比较分类要求闭合类型证据(string kind, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        IOperand left = new LocalVariable("left", new Register(null, "V0"), app.SystemTypes.SystemSingleType);
        IOperand right = kind switch
        {
            "右零" => new Immediate(0),
            "非零整数" => new Immediate(1),
            "字面量" => new FloatLiteral(1),
            "精度冲突" => new LocalVariable("right", new Register(null, "V1"), app.SystemTypes.SystemDoubleType),
            "未知来源" => new LocalVariable("right", new Register(null, "V1")),
            "整数来源" => new LocalVariable("right", new Register(null, "V1"), app.SystemTypes.SystemInt32Type),
            _ => new LocalVariable("right", new Register(null, "V1"), app.SystemTypes.SystemSingleType)
        };
        if (kind == "左零") left = new Immediate(0);
        var target = new LocalVariable("target", new Register(null, "W0"),
            kind == "错误目标" ? app.SystemTypes.SystemSingleType : app.SystemTypes.SystemBooleanType);
        var instruction = new Instruction(0, kind == "位操作" ? OpCode.And : OpCode.CheckLess, target, left, right);
        if (kind == "向量操作数") instruction.AddOperands([new Immediate(4), new Immediate(32)]);
        Assert.That(IlGenerator.IsProvenScalarFloatingBinary(instruction), Is.EqualTo(expected));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 浮点标量比较按照原生NZCV在真实PE执行(bool useDouble, bool dualView)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var floating = useDouble ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, floating, useDouble ? "Double" : "Single",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemBooleanType, "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var name = $"ScalarFloating_{useDouble}_{dualView}_{Guid.NewGuid():N}";
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "FloatingConditions", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var conditions = new[] { Arm64ConditionCode.GT, Arm64ConditionCode.LT, Arm64ConditionCode.GE,
            Arm64ConditionCode.LE, Arm64ConditionCode.HI, Arm64ConditionCode.CC,
            Arm64ConditionCode.CS, Arm64ConditionCode.LS };
        foreach (var condition in conditions)
        {
            var left = new LocalVariable("left", new Register(null, "V1"), floating);
            var right = new LocalVariable("right", new Register(null, "V2"), floating);
            var value = dualView ? new LocalVariable("value", new Register(null, "V0", 1), floating) : left;
            var result = new LocalVariable("result", new Register(null, "W0"), app.SystemTypes.SystemBooleanType);
            var instructions = new System.Collections.Generic.List<Instruction>();
            if (dualView)
            {
                // 向量定义同时保留低通道标量视图；比较必须消费标量，而不是整数位载体。
                instructions.Add(new Instruction(0, OpCode.VectorDuplicate, value, left,
                    new Immediate(useDouble ? 2 : 4), new Immediate(useDouble ? 64 : 32), new Immediate(0)));
            }
            var opcode = NewArmV8InstructionSet.GetConditionalSetRelationalOpCode(condition, Arm64FlagState.FloatingComparison);
            Assert.That(opcode, Is.Not.Null);
            instructions.Add(new Instruction(instructions.Count, opcode!.Value, result, value, right));
            instructions.Add(new Instruction(instructions.Count, OpCode.Return, result));
            var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, condition.ToString(),
                app.SystemTypes.SystemBooleanType, System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
                [floating, floating])
            {
                ControlFlowGraph = new ISILControlFlowGraph(instructions), ParameterLocals = [left, right],
                Locals = dualView ? [value, result] : [result], AnalysisWarnings = []
            };
            var signatureType = useDouble ? module.CorLibTypeFactory.Double : module.CorLibTypeFactory.Single;
            var definition = new MethodDefinition(condition.ToString(), MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean, [signatureType, signatureType]));
            definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
            definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
            host.Methods.Add(definition);
            IlGenerator.GenerateIl(context, definition);
            CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        }
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Fixture.FloatingConditions", true)!;
        double epsilon = useDouble ? double.Epsilon : float.Epsilon;
        double[] values = [double.NegativeInfinity, -1, -epsilon, -0.0, 0, epsilon, 1, double.PositiveInfinity, double.NaN];
        foreach (var condition in conditions)
        {
            var method = runtime.GetMethod(condition.ToString())!;
            foreach (var left in values)
            foreach (var right in values)
            {
                // 独立预期来自 FCMP 的四种 NZCV，而不是重复被测的操作码映射。
                var unordered = double.IsNaN(left) || double.IsNaN(right);
                var negative = !unordered && left < right;
                var zero = !unordered && left == right;
                var carry = unordered || left >= right;
                var overflow = unordered;
                var expected = condition switch
                {
                    Arm64ConditionCode.GT => !zero && negative == overflow,
                    Arm64ConditionCode.LT => negative != overflow,
                    Arm64ConditionCode.GE => negative == overflow,
                    Arm64ConditionCode.LE => zero || negative != overflow,
                    Arm64ConditionCode.HI => carry && !zero,
                    Arm64ConditionCode.CC => !carry,
                    Arm64ConditionCode.CS => carry,
                    Arm64ConditionCode.LS => !carry || zero,
                    _ => throw new ArgumentOutOfRangeException()
                };
                object leftArgument = useDouble ? (object)left : (float)left;
                object rightArgument = useDouble ? (object)right : (float)right;
                var actual = (bool)method.Invoke(null, [leftArgument, rightArgument])!;
                Assert.That(actual, Is.EqualTo(expected), $"condition={condition}, left={left}, right={right}, dualView={dualView}");
            }
        }
    }
}
