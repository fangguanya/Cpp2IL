using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace Cpp2IL.Core.Tests;

public class IlGeneratorTests
{
    private static void 绑定AsmResolver系统类型(
        ModuleDefinition module,
        TypeAnalysisContext context,
        string name,
        TypeAttributes attributes)
    {
        var definition = new TypeDefinition("System", name, attributes);
        module.TopLevelTypes.Add(definition);
        context.PutExtraData("AsmResolverType", definition);
    }

    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 条件Dispose生成平衡的Isinst与Callvirt控制流()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var candidate = new LocalVariable("candidate", new Register(null, "X0"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "DisposeCandidate",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemObject]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.DisposeIfSupported, candidate),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [];
        context.ParameterLocals = [candidate];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ConditionalDisposeTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ConditionalDisposeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "DisposeCandidate",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Object]));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        var validation = CilStackValidator.Validate(
            definition.CilMethodBody!,
            "ConditionalDisposeType::DisposeCandidate");
        var il = definition.CilMethodBody!.Instructions;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Isinst), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Dup), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Brfalse), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Pop), Is.EqualTo(1));
            Assert.That(validation.MaxStack, Is.EqualTo(2));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 空分支块解析到直接后继的首条CIL指令()
    {
        var empty = new Block { ID = 10 };
        var targetBlock = new Block { ID = 11 };
        var targetInstruction = new CilInstruction(CilOpCodes.Ret);
        empty.Successors.Add(targetBlock);

        var result = IlGenerator.ResolveBlockEntryInstruction(
            empty,
            new Dictionary<Block, CilInstruction> { [targetBlock] = targetInstruction });

        Assert.That(result, Is.SameAs(targetInstruction));
    }

    [Test]
    [Category("边界值")]
    public void 连续空分支块解析到末端首条CIL指令()
    {
        var first = new Block { ID = 20 };
        var second = new Block { ID = 21 };
        var targetBlock = new Block { ID = 22 };
        var targetInstruction = new CilInstruction(CilOpCodes.Nop);
        first.Successors.Add(second);
        second.Successors.Add(targetBlock);

        var result = IlGenerator.ResolveBlockEntryInstruction(
            first,
            new Dictionary<Block, CilInstruction> { [targetBlock] = targetInstruction });

        Assert.That(result, Is.SameAs(targetInstruction));
    }

    [Test]
    [Category("异常输入")]
    public void 循环空分支块确定性返回未解析()
    {
        var first = new Block { ID = 30 };
        var second = new Block { ID = 31 };
        first.Successors.Add(second);
        second.Successors.Add(first);

        var result = IlGenerator.ResolveBlockEntryInstruction(first, new Dictionary<Block, CilInstruction>());

        Assert.That(result, Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void 逻辑右移唯一映射到ShrUn()
    {
        Assert.That(
            IlGenerator.GetBinaryNumericCilOpCode(OpCode.ShiftRightUnsigned),
            Is.EqualTo(CilOpCodes.Shr_Un));
    }

    [Test]
    [Category("边界值")]
    public void 算术右移继续映射到Shr()
    {
        Assert.That(
            IlGenerator.GetBinaryNumericCilOpCode(OpCode.ShiftRight),
            Is.EqualTo(CilOpCodes.Shr));
    }

    [Test]
    [Category("异常输入")]
    public void 非二元数值操作码拒绝进入Cil映射()
    {
        Assert.That(
            () => IlGenerator.GetBinaryNumericCilOpCode(OpCode.Move),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    [Category("基本功能")]
    public void Arm64字目标在写回前规范化为I4()
    {
        Assert.That(
            IlGenerator.GetIntegerResultNormalizationCilOpCode(32),
            Is.EqualTo(CilOpCodes.Conv_I4));
    }

    [TestCase(0, TestName = "边界_无原生位宽证据保持托管栈类型")]
    [TestCase(64, TestName = "边界_X目标保持I8结果")]
    [Category("边界值")]
    public void 非字目标不增加结果转换(int widthBits)
    {
        Assert.That(IlGenerator.GetIntegerResultNormalizationCilOpCode(widthBits), Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 非法标量算术位宽拒绝生成猜测转换()
    {
        Assert.That(
            () => IlGenerator.GetIntegerResultNormalizationCilOpCode(16),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Void方法尾部为普通调用时需要返回终结点()
    {
        var finalInstruction = new CilInstruction(CilOpCodes.Call, null);

        Assert.That(IlGenerator.RequiresTerminalReturn(true, finalInstruction), Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void 泛型参数接收者要求Constrained虚调用()
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericParameter = definition.GenericParameters.Single();
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), genericParameter);

        Assert.That(IlGenerator.ConstrainedReceiverType(receiver), Is.SameAs(genericParameter));
    }

    [Test]
    [Category("基本功能")]
    public void 值类型局部实例接收者生成Ldloca()
    {
        var instructions = 生成实例调用接收者指令(valueType: true, explicitAddress: false);
        var callIndex = instructions.FindIndex(instruction => instruction.OpCode == CilOpCodes.Call);

        Assert.Multiple(() =>
        {
            Assert.That(callIndex, Is.GreaterThan(0));
            Assert.That(instructions[callIndex - 1].OpCode, Is.EqualTo(CilOpCodes.Ldloca));
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 引用类型局部实例接收者保持Ldloc()
    {
        var instructions = 生成实例调用接收者指令(valueType: false, explicitAddress: false);
        var callIndex = instructions.FindIndex(instruction => instruction.OpCode == CilOpCodes.Call);

        Assert.Multiple(() =>
        {
            Assert.That(callIndex, Is.GreaterThan(0));
            Assert.That(instructions[callIndex - 1].OpCode, Is.EqualTo(CilOpCodes.Ldloc));
            Assert.That(instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 已显式取址的值类型接收者不得重复取址()
    {
        var instructions = 生成实例调用接收者指令(valueType: true, explicitAddress: true);
        var callIndex = instructions.FindIndex(instruction => instruction.OpCode == CilOpCodes.Call);

        Assert.Multiple(() =>
        {
            Assert.That(callIndex, Is.GreaterThan(0));
            Assert.That(instructions[callIndex - 1].OpCode, Is.EqualTo(CilOpCodes.Ldloca));
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.EqualTo(1));
        });
    }

    private static List<CilInstruction> 生成实例调用接收者指令(bool valueType, bool explicitAddress)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var baseType = valueType
            ? assembly.GetTypeByFullName("System.ValueType")!
            : app.SystemTypes.SystemObjectType;
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            valueType ? "Counter" : "Holder",
            baseType,
            ReflectionTypeAttributes.Public
            | (valueType
                ? ReflectionTypeAttributes.Sealed | ReflectionTypeAttributes.SequentialLayout
                : ReflectionTypeAttributes.Class));
        var getter = owner.InjectMethodContext(
            "GetValue",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public);
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "Caller",
            app.SystemTypes.SystemObjectType,
            ReflectionTypeAttributes.Public | ReflectionTypeAttributes.Class);
        var caller = callerType.InjectMethodContext(
            "Read",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var result = new LocalVariable("result", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        IOperand receiverOperand = explicitAddress ? new AddressOf(receiver) : receiver;
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, getter, result, receiverOperand),
            new Instruction(1, OpCode.Return, result),
        ]);
        caller.Locals = [receiver, result];
        caller.ParameterLocals = [];
        caller.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ReceiverTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemInt32Type, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        if (valueType)
            绑定AsmResolver系统类型(module, baseType, "ValueType", TypeAttributes.Class | TypeAttributes.Public);

        var ownerDefinition = new TypeDefinition(
            "Fixture",
            owner.Name,
            TypeAttributes.Public
            | (valueType
                ? TypeAttributes.Sealed | TypeAttributes.SequentialLayout
                : TypeAttributes.Class));
        var callerDefinition = new TypeDefinition("Fixture", "Caller", TypeAttributes.Public | TypeAttributes.Class);
        module.TopLevelTypes.Add(ownerDefinition);
        module.TopLevelTypes.Add(callerDefinition);
        owner.PutExtraData("AsmResolverType", ownerDefinition);
        callerType.PutExtraData("AsmResolverType", callerDefinition);
        var getterDefinition = new MethodDefinition(
            "GetValue",
            MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
        var callerMethodDefinition = new MethodDefinition(
            "Read",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        ownerDefinition.Methods.Add(getterDefinition);
        callerDefinition.Methods.Add(callerMethodDefinition);
        getter.PutExtraData("AsmResolverMethod", getterDefinition);

        IlGenerator.GenerateIl(caller, callerMethodDefinition);
        CilStackValidator.Validate(callerMethodDefinition.CilMethodBody!, "Fixture.Caller::Read");
        return callerMethodDefinition.CilMethodBody!.Instructions.ToList();
    }

    [Test]
    [Category("边界值")]
    public void 泛型参数传入Object形参要求BoxT()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var definition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var argument = new LocalVariable(
            "argument",
            new Register(null, "X1"),
            definition.GenericParameters.Single());

        Assert.That(IlGenerator.ShouldBoxGenericArgument(argument, app.SystemTypes.SystemObjectType), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 普通引用参数不生成泛型Box()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var argument = new LocalVariable("argument", new Register(null, "X1"), app.SystemTypes.SystemStringType);

        Assert.That(IlGenerator.ShouldBoxGenericArgument(argument, app.SystemTypes.SystemObjectType), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void Box指令生成值类型装箱而非原生地址传递()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemBoolean = app.SystemTypes.SystemBooleanType;
        var systemVoid = app.SystemTypes.SystemVoidType;
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "BoxBoolean",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        var value = new LocalVariable("value", new Register(null, "value"), systemBoolean);
        var result = new LocalVariable("result", new Register(null, "result"), systemObject);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new Immediate(1)),
            new Instruction(1, OpCode.Box, result, value, systemBoolean),
            new Instruction(2, OpCode.Return),
        ]);
        context.Locals = [value, result];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "BoxTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "BoxTestType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "BoxBoolean",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);

        var box = method.CilMethodBody!.Instructions.Single(instruction => instruction.OpCode == CilOpCodes.Box);
        Assert.That(box.Operand!.ToString(), Does.Contain("System.Boolean"));
    }

    [Test]
    [Category("基本功能")]
    public void 浮点比较中的ARM64立即数零按Single入栈()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemSingle = app.SystemTypes.SystemSingleType;
        var systemBoolean = app.SystemTypes.SystemBooleanType;
        var value = new LocalVariable("value", new Register(null, "V0"), systemSingle);
        var result = new LocalVariable("result", new Register(null, "Z"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareSingleWithZero",
            systemBoolean,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new FloatLiteral(1f)),
            new Instruction(1, OpCode.CheckLessOrEqual, result, value, new Immediate(0)),
            new Instruction(2, OpCode.Return, result),
        ]);
        context.Locals = [value, result];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "FloatComparisonTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(
            module,
            systemSingle,
            "Single",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "FloatComparisonTestType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "CompareSingleWithZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);

        var floatingZeros = method.CilMethodBody!.Instructions
            .Where(instruction => instruction.OpCode == CilOpCodes.Ldc_R4)
            .Select(instruction => instruction.Operand)
            .OfType<float>()
            .Count(value => BitConverter.SingleToInt32Bits(value) == 0);
        Assert.That(floatingZeros, Is.EqualTo(1));
    }

    [Test]
    [Category("基本功能")]
    public void CastClass生成取参强转存储与返回的平衡CIL栈()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemString = app.SystemTypes.SystemStringType;
        var source = new LocalVariable("source", new Register(null, "source"), systemObject);
        var result = new LocalVariable("result", new Register(null, "result"), systemString);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CastString",
            systemString,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemObject],
            ["source"]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CastClass, result, source, systemString),
            new Instruction(1, OpCode.Return, result),
        ]);
        context.Locals = [result];
        context.ParameterLocals = [source];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "CastClassTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(module, systemString, "String", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "CastClassTestType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "CastString",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                systemString.ToTypeSignature(module),
                [systemObject.ToTypeSignature(module)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);

        var opCodes = method.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(opCodes.Count(opCode => opCode == CilOpCodes.Castclass), Is.EqualTo(1));
            Assert.That(opCodes.Count(opCode => opCode == CilOpCodes.Stloc), Is.EqualTo(1));
            Assert.That(
                opCodes,
                Is.EqualTo(new[]
                {
                    CilOpCodes.Ldarg,
                    CilOpCodes.Castclass,
                    CilOpCodes.Stloc,
                    CilOpCodes.Ldloc,
                    CilOpCodes.Ret,
                }));
        });
    }

    [Test]
    [Category("基本功能")]
    public void IsInst生成取参测试存储与返回的平衡CIL栈()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemString = app.SystemTypes.SystemStringType;
        var source = new LocalVariable("source", new Register(null, "source"), systemObject);
        var result = new LocalVariable("result", new Register(null, "result"), systemString);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "TestString",
            systemString,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemObject],
            ["source"]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.IsInst, result, source, systemString),
            new Instruction(1, OpCode.Return, result),
        ]);
        context.Locals = [result];
        context.ParameterLocals = [source];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "IsInstTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(module, systemString, "String", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "IsInstTestType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "TestString",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                systemString.ToTypeSignature(module),
                [systemObject.ToTypeSignature(module)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);

        var opCodes = method.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(opCodes.Count(opCode => opCode == CilOpCodes.Isinst), Is.EqualTo(1));
            Assert.That(opCodes.Count(opCode => opCode == CilOpCodes.Stloc), Is.EqualTo(1));
            Assert.That(opCodes[^1], Is.EqualTo(CilOpCodes.Ret));
        });
    }

    [TestCaseSource(nameof(不可顺序落出的终结指令))]
    public void Void方法已有控制流终结点时不重复补返回(CilOpCode opCode)
    {
        var finalInstruction = new CilInstruction(opCode);

        Assert.That(IlGenerator.RequiresTerminalReturn(true, finalInstruction), Is.False);
    }

    [Test]
    public void 非Void方法尾部为普通调用时不补无值返回()
    {
        var finalInstruction = new CilInstruction(CilOpCodes.Call, null);

        Assert.That(IlGenerator.RequiresTerminalReturn(false, finalInstruction), Is.False);
    }

    private static IEnumerable<CilOpCode> 不可顺序落出的终结指令()
    {
        yield return CilOpCodes.Ret;
        yield return CilOpCodes.Throw;
        yield return CilOpCodes.Rethrow;
        yield return CilOpCodes.Br;
        yield return CilOpCodes.Br_S;
        yield return CilOpCodes.Leave;
        yield return CilOpCodes.Leave_S;
        yield return CilOpCodes.Jmp;
        yield return CilOpCodes.Endfinally;
        yield return CilOpCodes.Endfilter;
    }

    [Test]
    public void StaticCall_DoesNotLoadMethodInfoOperand()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemInt = appContext.SystemTypes.SystemInt32Type;

        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "TargetStatic",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemInt, systemInt]);

        var x = new LocalVariable("x", new Register(null, "x"));
        var y = new LocalVariable("y", new Register(null, "y"));

        var instructions = new List<Instruction>
        {
            
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Move, y, Imm(10)),
            // Operand layout: target, arg0, arg1, trailing-non-parameter.
            new(2, OpCode.CallVoid, targetContext, x, y, Imm(999)),
            new(3, OpCode.Return),
        };

        callerContext.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        callerContext.Locals = [x, y];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "IlGeneratorTestType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);

        var callerMethodDef = new MethodDefinition("Caller", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerMethodDef);

        var targetMethodDef = new MethodDefinition("TargetStatic", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        typeDef.Methods.Add(targetMethodDef);
        targetContext.PutExtraData("AsmResolverMethod", targetMethodDef);

        IlGenerator.GenerateIl(callerContext, callerMethodDef);

        var il = callerMethodDef.CilMethodBody!.Instructions;

        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Call), Is.True, "expected generated method to contain a call");
        Assert.That(il.Any(i => i.Operand is int intOperand && intOperand == 999), Is.False,
            "trailing non-parameter operand must not be emitted as a call argument");
        Assert.That(il.Count(i => i.OpCode == CilOpCodes.Ldloc), Is.EqualTo(2),
            "expected exactly two Ldloc instructions for the two parameters of the target method");
    }

    [Test]
    public void 非This局部变量的构造调用生成Newobj并写回()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var constructedObject = new LocalVariable("constructedObject", new Register(null, "constructedObject"), systemObject);
        var constructorContext = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName |
            ReflectionMethodAttributes.RTSpecialName,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, constructorContext, constructedObject),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [constructedObject];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "ConstructedType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);
        systemObject.PutExtraData("AsmResolverType", typeDef);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(constructorDefinition);
        constructorContext.PutExtraData("AsmResolverMethod", constructorDefinition);
        var callerDefinition = new MethodDefinition(
            "Caller",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);

        var il = callerDefinition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Call), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void CallVoid调用非Void目标时必须丢弃返回值并保持栈平衡()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "CreateObject",
            systemObject,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "DiscardCreatedObject",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, targetContext),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "DiscardReturnTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "DiscardReturnType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var targetDefinition = new MethodDefinition(
            "CreateObject",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Object));
        var callerDefinition = new MethodDefinition(
            "DiscardCreatedObject",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(targetDefinition);
        typeDefinition.Methods.Add(callerDefinition);
        targetContext.PutExtraData("AsmResolverMethod", targetDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);
        var validation = CilStackValidator.Validate(
            callerDefinition.CilMethodBody!,
            "DiscardReturnType::DiscardCreatedObject");
        var emitted = callerDefinition.CilMethodBody!.Instructions.ToArray();
        var callIndex = Array.FindIndex(emitted, instruction => instruction.OpCode == CilOpCodes.Call);

        Assert.Multiple(() =>
        {
            Assert.That(callIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(emitted[callIndex + 1].OpCode, Is.EqualTo(CilOpCodes.Pop));
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Pop), Is.EqualTo(1));
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ret), Is.True);
            Assert.That(validation.MaxStack, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void CallVoid调用Void目标时不得生成Pop()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Run",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "CallRun",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, targetContext),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "VoidCallTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "VoidCallType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var targetDefinition = new MethodDefinition(
            "Run",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        var callerDefinition = new MethodDefinition(
            "CallRun",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(targetDefinition);
        typeDefinition.Methods.Add(callerDefinition);
        targetContext.PutExtraData("AsmResolverMethod", targetDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);
        CilStackValidator.Validate(callerDefinition.CilMethodBody!, "VoidCallType::CallRun");

        Assert.That(
            callerDefinition.CilMethodBody!.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Pop),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void CallVoid构造器融合为Newobj写回时不得额外生成Pop()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var constructedObject = new LocalVariable(
            "constructedObject",
            new Register(null, "X0"),
            systemObject);
        var constructorContext = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            appContext.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName |
            ReflectionMethodAttributes.RTSpecialName,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Construct",
            appContext.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, constructorContext, constructedObject),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [constructedObject];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ConstructorDiscardTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ConstructorDiscardType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        systemObject.PutExtraData("AsmResolverType", typeDefinition);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        var callerDefinition = new MethodDefinition(
            "Construct",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(constructorDefinition);
        typeDefinition.Methods.Add(callerDefinition);
        constructorContext.PutExtraData("AsmResolverMethod", constructorDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);
        CilStackValidator.Validate(callerDefinition.CilMethodBody!, "ConstructorDiscardType::Construct");
        var il = callerDefinition.CilMethodBody!.Instructions;

        Assert.Multiple(() =>
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Pop), Is.False);
        });
    }

    [Test]
    public void This上的构造调用保持普通Call()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var thisObject = new LocalVariable("this", new Register(null, "this"), systemObject) { IsThis = true };
        var constructorContext = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName |
            ReflectionMethodAttributes.RTSpecialName,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, constructorContext, thisObject),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [];
        callerContext.ParameterLocals = [thisObject];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "ConstructedType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);
        systemObject.PutExtraData("AsmResolverType", typeDef);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(constructorDefinition);
        constructorContext.PutExtraData("AsmResolverMethod", constructorDefinition);
        var callerDefinition = new MethodDefinition(
            "Caller",
            MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);

        var il = callerDefinition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Call), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void Hfa返回值按原始浮点位重组为值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var valueType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.ValueType")!;
        var aggregate = appContext.InjectTypeIntoAllAssemblies(
                "Cpp2IL.Core.Tests",
                "RecoveredVector2",
                valueType,
                System.Reflection.TypeAttributes.Public
                | System.Reflection.TypeAttributes.Sealed
                | System.Reflection.TypeAttributes.SequentialLayout)
            .InjectedTypes[0];
        var xField = aggregate.InjectFieldContext(
            "x",
            appContext.SystemTypes.SystemSingleType,
            System.Reflection.FieldAttributes.Public);
        var yField = aggregate.InjectFieldContext(
            "y",
            appContext.SystemTypes.SystemSingleType,
            System.Reflection.FieldAttributes.Public);
        var context = aggregate.InjectMethodContext(
            "GetActualSize",
            aggregate,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var xBits = new LocalVariable("xBits", new Register(null, "X8"), appContext.SystemTypes.SystemInt32Type);
        var yBits = new LocalVariable("yBits", new Register(null, "X9"), appContext.SystemTypes.SystemInt32Type);
        var x = new LocalVariable("x", new Register(null, "V0"), appContext.SystemTypes.SystemSingleType);
        var y = new LocalVariable("y", new Register(null, "V1"), appContext.SystemTypes.SystemSingleType);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, xBits, Imm(0x44480000)),
            new Instruction(1, OpCode.Move, yBits, Imm(0x43870000)),
            new Instruction(2, OpCode.ReinterpretIntegerBitsAsFloat, x, xBits, Imm(32)),
            new Instruction(3, OpCode.ReinterpretIntegerBitsAsFloat, y, yBits, Imm(32)),
            new Instruction(4, OpCode.Return, x, y),
        ]);
        context.Locals = [xBits, yBits, x, y];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var corlibAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        var corlibModule = new ModuleDefinition("mscorlib.dll");
        corlibAssembly.Modules.Add(corlibModule);
        var valueTypeDefinition = new TypeDefinition(
            "System",
            "ValueType",
            TypeAttributes.Public | TypeAttributes.Abstract);
        var int32Definition = new TypeDefinition(
            "System",
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        var singleDefinition = new TypeDefinition(
            "System",
            "Single",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        corlibModule.TopLevelTypes.Add(valueTypeDefinition);
        corlibModule.TopLevelTypes.Add(int32Definition);
        corlibModule.TopLevelTypes.Add(singleDefinition);
        appContext.SystemTypes.SystemInt32Type.PutExtraData("AsmResolverType", int32Definition);
        appContext.SystemTypes.SystemSingleType.PutExtraData("AsmResolverType", singleDefinition);

        var module = new ModuleDefinition("Test.dll", new AssemblyReference(corlibAssembly));
        var aggregateDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RecoveredVector2",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType")
        };
        module.TopLevelTypes.Add(aggregateDefinition);
        aggregate.PutExtraData("AsmResolverType", aggregateDefinition);

        var xDefinition = new FieldDefinition("x", FieldAttributes.Public, module.CorLibTypeFactory.Single);
        var yDefinition = new FieldDefinition("y", FieldAttributes.Public, module.CorLibTypeFactory.Single);
        aggregateDefinition.Fields.Add(xDefinition);
        aggregateDefinition.Fields.Add(yDefinition);
        xField.PutExtraData("AsmResolverField", xDefinition);
        yField.PutExtraData("AsmResolverField", yDefinition);

        var definition = new MethodDefinition(
            "GetActualSize",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(aggregate.ToTypeSignature(module)));
        aggregateDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        var bitConversionCalls = il
            .Where(instruction => instruction.OpCode == CilOpCodes.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethodDescriptor>()
            .Where(method => method.Name == "Int32BitsToSingle")
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bitConversionCalls, Has.Length.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Initobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ret), Is.EqualTo(1));
            Assert.That(
                il.Any(instruction => instruction.Operand is int value && value == 0x44480000),
                Is.True);
            Assert.That(
                il.Any(instruction => instruction.Operand is int value && value == 0x43870000),
                Is.True);
        }
    }

    [Test]
    [Category("基本功能")]
    public void Ref浮点内存读写生成Ldobj和Stobj()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemSingle = appContext.SystemTypes.SystemSingleType;
        var byRefSingle = systemSingle.MakeByReferenceType();
        var runningHeight = new LocalVariable("runningHeight", new Register(null, "X5"), byRefSingle);
        var increment = new LocalVariable("increment", new Register(null, "V1"), systemSingle);
        var sum = new LocalVariable("sum", new Register(null, "V0"), systemSingle);
        var runningHeightMemory = new MemoryOperand(runningHeight);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "AddHeight",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [byRefSingle]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, increment, new FloatLiteral(32f)),
            new Instruction(1, OpCode.Add, sum, runningHeightMemory, increment),
            new Instruction(2, OpCode.Move, runningHeightMemory, sum),
            new Instruction(3, OpCode.Return),
        ]);
        context.Locals = [increment, sum];
        context.ParameterLocals = [runningHeight];
        context.AnalysisWarnings = [];

        var corlibAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        var corlibModule = new ModuleDefinition("mscorlib.dll");
        corlibAssembly.Modules.Add(corlibModule);
        var valueTypeDefinition = new TypeDefinition(
            "System",
            "ValueType",
            TypeAttributes.Public | TypeAttributes.Abstract);
        var singleDefinition = new TypeDefinition(
            "System",
            "Single",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        corlibModule.TopLevelTypes.Add(valueTypeDefinition);
        corlibModule.TopLevelTypes.Add(singleDefinition);
        systemSingle.PutExtraData("AsmResolverType", singleDefinition);

        var module = new ModuleDefinition("Test.dll", new AssemblyReference(corlibAssembly));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ByRefMemoryType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "AddHeight",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Single.MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(
            1,
            "runningHeight",
            (ParameterAttributes)0));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Add), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldarg), Is.EqualTo(2));
        }
    }

    [Test]
    [Category("基本功能")]
    public void Out引用元素写入整数零生成Ldnull与Stobj()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemString = appContext.SystemTypes.SystemStringType;
        var byRefString = systemString.MakeByReferenceType();
        var output = new LocalVariable("output", new Register(null, "X0"), byRefString);
        var outputMemory = new MemoryOperand(output);
        var context = new InjectedMethodAnalysisContext(
            appContext.SystemTypes.SystemObjectType,
            "SetNull",
            appContext.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [byRefString]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, outputMemory, new Immediate(0)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [];
        context.ParameterLocals = [output];
        context.AnalysisWarnings = [];

        var corlibAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        corlibAssembly.Modules.Add(new ModuleDefinition("mscorlib.dll"));
        var module = new ModuleDefinition("Test.dll", new AssemblyReference(corlibAssembly));
        绑定AsmResolver系统类型(
            module,
            systemString,
            "String",
            TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "OutReferenceType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "SetNull",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.String.MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "output", (ParameterAttributes)0));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        Assert.Multiple(() =>
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stobj), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 无构造器类型操作数写入IntPtr生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemIntPtr = appContext.SystemTypes.SystemIntPtrType;
        var pointer = new LocalVariable("pointer", new Register(null, "X8"), systemIntPtr);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadStaticTypeAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemIntPtr,
            "IntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "NativeDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadStaticTypeAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 运行时类型句柄默认值按IntPtr生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var runtimeClass = new RuntimeClassTypeAnalysisContext(systemObject, systemObject.DeclaringAssembly);
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), runtimeClass);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadRuntimeClassAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadRuntimeClassAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 托管类型操作数写入IntPtr槽时生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemIntPtr = appContext.SystemTypes.SystemIntPtrType;
        var pointer = new LocalVariable("pointer", new Register(null, "X8"), systemIntPtr);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadManagedTypeIntoNativeSlot",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, systemObject),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemIntPtr,
            "IntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ManagedTypeNativeSlotType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadManagedTypeIntoNativeSlot",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 托管引用上的运行时类布局探针生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var runtimeClass = new RuntimeClassTypeAnalysisContext(
            systemObject,
            systemObject.DeclaringAssembly);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), systemObject);
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), runtimeClass);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "ProbeRuntimeClass",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, new MemoryOperand(receiver)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [receiver, pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassProbeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "ProbeRuntimeClass",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloc), Is.False);
        }
    }

    [Test]
    [Category("边界值")]
    public void 托管引用取址写入托管槽时恢复原引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var source = new LocalVariable("source", new Register(null, "X0"), systemObject);
        var destination = new LocalVariable("destination", new Register(null, "X1"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "RecoverManagedReference",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, destination, new AddressOf(source)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [source, destination];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ManagedReferenceProbeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "RecoverManagedReference",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloc), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void 接口引用取址写入Object槽时恢复接口引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var enumeratorType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var source = new LocalVariable("enumerator", new Register(null, "X0"), enumeratorType);
        var destination = new LocalVariable("disposableSource", new Register(null, "X1"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "RecoverInterfaceReference",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, destination, new AddressOf(source)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [source, destination];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var enumeratorDefinition = new TypeDefinition(
            "System.Collections",
            "IEnumerator",
            TypeAttributes.Interface | TypeAttributes.Public | TypeAttributes.Abstract);
        module.TopLevelTypes.Add(enumeratorDefinition);
        enumeratorType.PutExtraData("AsmResolverType", enumeratorDefinition);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "InterfaceReferenceProbeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "RecoverInterfaceReference",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloc), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void 原生内存值与无构造器类型比较时生成同型原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var systemIntPtr = appContext.SystemTypes.SystemIntPtrType;
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), systemIntPtr);
        var memoryValue = new MemoryOperand(pointer);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareRuntimeClassAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, memoryValue, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemIntPtr,
            "IntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareRuntimeClassAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("边界值")]
    public void 托管引用与整数零比较时按引用空值生成()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var target = new LocalVariable("target", new Register(null, "X8"), systemObject);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareReferenceWithZero",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, target, Imm(0)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [target, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemObject,
            "Object",
            TypeAttributes.Public | TypeAttributes.Class);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ReferenceComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareReferenceWithZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.False);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 数值与整数零比较不得改写为引用空值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var systemInt32 = appContext.SystemTypes.SystemInt32Type;
        var value = new LocalVariable("value", new Register(null, "W8"), systemInt32);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareIntegerWithZero",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, value, Imm(0)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [value, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemInt32,
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "IntegerComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareIntegerWithZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("边界值")]
    public void 未配对Newobj写入UIntPtr生成无符号原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemUIntPtr = appContext.SystemTypes.SystemUIntPtrType;
        var pointer = new LocalVariable("pointer", new Register(null, "X9"), systemUIntPtr);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "AllocateMetadataAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Newobj, pointer),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemUIntPtr,
            "UIntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "UnsignedNativeDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "AllocateMetadataAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_U), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 无构造器类型操作数写入引用仍生成Null()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var target = new LocalVariable("target", new Register(null, "X10"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadMissingReference",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, target, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [target];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemObject,
            "Object",
            TypeAttributes.Public | TypeAttributes.Class);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ReferenceDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadMissingReference",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.False);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_U), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 未定型Object局部量写入原生零生成Ldnull()
    {
        var emitted = 生成局部量立即数赋值Cil(null, 0);

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.False);
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已定型Int32局部量写入零保持LdcI4()
    {
        var emitted = 生成局部量立即数赋值Cil(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type,
            0);

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Count(instruction =>
                instruction.OpCode == CilOpCodes.Ldc_I4_0
                || instruction.OpCode == CilOpCodes.Ldc_I4 && instruction.Operand is 0), Is.EqualTo(1));
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void Int32形参把W寄存器全一位模式恢复为负一()
    {
        var result = IlGenerator.TryGetI4Immediate(
            new Immediate(uint.MaxValue),
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type,
            out var value);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(value, Is.EqualTo(-1));
        });
    }

    [Test]
    [Category("边界值")]
    public void UInt32形参保持最高位位模式而不提升到I8()
    {
        var result = IlGenerator.TryGetI4Immediate(
            new Immediate(0x80000000L),
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemUInt32Type,
            out var value);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(value, Is.EqualTo(int.MinValue));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Int64形参禁止把同一立即数错误收窄为I4()
    {
        var result = IlGenerator.TryGetI4Immediate(
            new Immediate(uint.MaxValue),
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt64Type,
            out _);

        Assert.That(result, Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 未定型Object局部量非零立即数保持数值指令()
    {
        var emitted = 生成局部量立即数赋值Cil(null, 7);

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Any(instruction =>
                instruction.OpCode.Code == CilCode.Ldc_I4
                && instruction.Operand is 7), Is.True);
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 引用类型进入RuntimeTypeHandle形参生成Ldtoken()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var emitted = 生成类型实参加载Cil(
            appContext.SystemTypes.SystemObjectType,
            获取运行时类型句柄(appContext));

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Ldtoken), Is.EqualTo(1));
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
            Assert.That(emitted.Single(instruction => instruction.OpCode == CilOpCodes.Ldtoken).Operand!.ToString(),
                Does.Contain("System.Object"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 值类型进入RuntimeTypeHandle形参仍生成Ldtoken()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var emitted = 生成类型实参加载Cil(
            appContext.SystemTypes.SystemInt32Type,
            获取运行时类型句柄(appContext));

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Ldtoken), Is.EqualTo(1));
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
            Assert.That(emitted.Single(instruction => instruction.OpCode == CilOpCodes.Ldtoken).Operand!.ToString(),
                Does.Contain("System.Int32"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 类型操作数进入IntPtr形参保持原生地址语义()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var emitted = 生成类型实参加载Cil(
            appContext.SystemTypes.SystemObjectType,
            appContext.SystemTypes.SystemIntPtrType);

        Assert.Multiple(() =>
        {
            Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldtoken), Is.False);
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(emitted.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
        });
    }

    private static TypeAnalysisContext 获取运行时类型句柄(ApplicationAnalysisContext appContext)
        => appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.RuntimeTypeHandle")!;

    private static CilInstruction[] 生成局部量立即数赋值Cil(
        TypeAnalysisContext? localType,
        long value)
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var local = new LocalVariable("value", new Register(null, "X8"), localType);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "AssignImmediate",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, local, new Immediate(value)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [local];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ImmediateLocalTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemObject,
            "Object",
            TypeAttributes.Public | TypeAttributes.Class);
        if (localType != null && !ReferenceEquals(localType, systemObject))
        {
            var separator = localType.FullName.LastIndexOf('.');
            var name = separator >= 0 ? localType.FullName[(separator + 1)..] : localType.FullName;
            绑定AsmResolver系统类型(
                module,
                localType,
                name,
                TypeAttributes.Public
                | (localType.IsValueType
                    ? TypeAttributes.Sealed | TypeAttributes.SequentialLayout
                    : TypeAttributes.Class));
        }

        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ImmediateLocalType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "AssignImmediate",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        return definition.CilMethodBody!.Instructions.ToArray();
    }

    private static CilInstruction[] 生成类型实参加载Cil(
        TypeAnalysisContext operandType,
        TypeAnalysisContext parameterType)
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "ConsumeTypeOperand",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [parameterType]);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadTypeOperand",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, targetContext, operandType),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "RuntimeTypeHandleTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var boundTypes = new HashSet<TypeAnalysisContext>();
        void BindSystemType(TypeAnalysisContext context)
        {
            if (!boundTypes.Add(context))
                return;

            var separator = context.FullName.LastIndexOf('.');
            var name = separator >= 0 ? context.FullName[(separator + 1)..] : context.FullName;
            var attributes = TypeAttributes.Public
                             | (context.IsValueType
                                 ? TypeAttributes.Sealed | TypeAttributes.SequentialLayout
                                 : TypeAttributes.Class);
            绑定AsmResolver系统类型(module, context, name, attributes);
        }

        BindSystemType(systemObject);
        BindSystemType(operandType);
        BindSystemType(parameterType);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeTypeHandleConsumer",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var targetDefinition = new MethodDefinition(
            "ConsumeTypeOperand",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                module.CorLibTypeFactory.Void,
                [parameterType.ToTypeSignature(module)]));
        var callerDefinition = new MethodDefinition(
            "LoadTypeOperand",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(targetDefinition);
        typeDefinition.Methods.Add(callerDefinition);
        targetContext.PutExtraData("AsmResolverMethod", targetDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);
        return callerDefinition.CilMethodBody!.Instructions.ToArray();
    }
}
