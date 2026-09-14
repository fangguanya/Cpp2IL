using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase("exact", true)]
    [TestCase("partial", false)]
    [TestCase("wide", false)]
    [TestCase("unknown_width", false)]
    [TestCase("wrong_type", false)]
    [TestCase("unknown_type", false)]
    [TestCase("same_name", false)]
    [TestCase("not_move", false)]
    [TestCase("missing_operand", false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 标量字段种子分类拒绝缺失及矛盾证据(string mode, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var field = 创建身份跨度字段(app.SystemTypes.SystemDoubleType);
        TypeAnalysisContext? type = mode switch
        {
            "wrong_type" => app.SystemTypes.SystemSingleType,
            "unknown_type" => null,
            "same_name" => new InjectedTypeAnalysisContext(app.Assemblies[0], "System", "Double",
                app.SystemTypes.SystemValueTypeType, System.Reflection.TypeAttributes.Public),
            _ => app.SystemTypes.SystemDoubleType
        };
        var value = new LocalVariable("scalar", new Register(null, "arbitrary"), type);
        var instruction = new Instruction(0, mode == "not_move" ? OpCode.Add : OpCode.Move, value, field)
        {
            MemoryAccessWidthBits = mode switch { "partial" => 32, "wide" => 128, "unknown_width" => 0, _ => 64 }
        };
        if (mode == "missing_operand") instruction.SetOperands(value);
        Assert.That(ManagedFieldSpanRecoveryHelper.IsExactScalarFloatingAccess(instruction, 8), Is.EqualTo(expected));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 精确浮点字段不将标量合流污染为向量(bool single, bool physicalVectorName)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var scalarType = single ? app.SystemTypes.SystemSingleType : app.SystemTypes.SystemDoubleType;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "ScalarField", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext("Value", scalarType,
            System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        field.Offset = 0;
        var receiver = new LocalVariable("storage", new Register(null, "storage"), owner);
        var input = new LocalVariable("input", new Register(null, "input"), scalarType);
        var condition = new LocalVariable("condition", new Register(null, "condition"), app.SystemTypes.SystemBooleanType);
        var value = new LocalVariable("value", new Register(null, physicalVectorName ? "V8" : "scalar"), scalarType);
        var loaded = new LocalVariable("loaded", new Register(null, "loaded"), scalarType);
        var reference = new FieldReference(field, receiver, 0);
        IOperand literal = single ? new FloatLiteral(-1) : new DoubleLiteral(-1);
        var context = new InjectedMethodAnalysisContext(owner, "RoundTrip", scalarType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [scalarType, app.SystemTypes.SystemBooleanType], ["input", "condition"]);
        context.ParameterLocals = [input, condition];
        context.Locals = [receiver, input, condition, value, loaded];
        context.AnalysisWarnings = [];
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, literal),
            new Instruction(1, OpCode.ConditionalSelect, value, condition, input, value),
            new Instruction(2, OpCode.Move, reference, value) { MemoryAccessWidthBits = single ? 32 : 64 },
            new Instruction(3, OpCode.Move, loaded, reference) { MemoryAccessWidthBits = single ? 32 : 64 },
            new Instruction(4, OpCode.Return, loaded)
        ]);

        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemBooleanType, "Boolean", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, scalarType, single ? "Single" : "Double", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "ScalarField_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("ScalarField", "Host", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        var fieldDefinition = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static,
            scalarType.ToTypeSignature(module));
        host.Fields.Add(fieldDefinition);
        field.PutExtraData("AsmResolverField", fieldDefinition);
        var signature = single ? module.CorLibTypeFactory.Single : module.CorLibTypeFactory.Double;
        var method = new MethodDefinition("RoundTrip", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(signature, [signature, module.CorLibTypeFactory.Boolean]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "input", (ParameterAttributes)0));
        method.ParameterDefinitions.Add(new ParameterDefinition(2, "condition", (ParameterAttributes)0));
        host.Methods.Add(method);
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        TestContext.Out.WriteLine(string.Join("\n", method.CilMethodBody!.Instructions));
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("ScalarField.Host")!;
        var invoke = runtime.GetMethod("RoundTrip")!;
        // 同一生成体覆盖有限值、正负零、无穷和NaN；逐位核对字段与返回值，不重建测试方法。
        foreach (var number in new[] { -7.25, 0d, -0d, double.PositiveInfinity, double.NegativeInfinity, double.NaN })
        foreach (var chooseInput in new[] { false, true })
        {
            object argument = single ? (object)(float)number : number;
            var actual = invoke.Invoke(null, [argument, chooseInput])!;
            var stored = runtime.GetField("Value")!.GetValue(null)!;
            if (single)
            {
                var expected = BitConverter.SingleToInt32Bits(chooseInput ? (float)number : -1f);
                Assert.That(BitConverter.SingleToInt32Bits((float)actual), Is.EqualTo(expected));
                Assert.That(BitConverter.SingleToInt32Bits((float)stored), Is.EqualTo(expected));
            }
            else
            {
                var expected = BitConverter.DoubleToInt64Bits(chooseInput ? number : -1d);
                Assert.That(BitConverter.DoubleToInt64Bits((double)actual), Is.EqualTo(expected));
                Assert.That(BitConverter.DoubleToInt64Bits((double)stored), Is.EqualTo(expected));
            }
        }
    }
}
