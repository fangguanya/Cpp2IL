using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    private delegate ref byte 字节引用偏移委托(ref byte value);

    [TestCase(false, false, 0L)]
    [TestCase(false, false, 7L)]
    [TestCase(false, false, -7L)]
    [TestCase(false, true, 7L)]
    [TestCase(false, true, -7L)]
    [TestCase(true, false, 7L)]
    [TestCase(true, false, -7L)]
    [Category("边界值")]
    public void 托管引用字节偏移生成真实PE保持引用身份(bool subtract, bool commuted, long offset)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Byte")!;
        var reference = element.MakeByReferenceType();
        var source = new LocalVariable("value", new Register(null, "X0"), reference);
        var result = new LocalVariable("result", new Register(null, "X1"), reference);
        var operation = new Instruction(0, subtract ? OpCode.Subtract : OpCode.Add, result,
            commuted ? new Immediate(offset) : source, commuted ? source : new Immediate(offset));
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType,
            "Offset", reference, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, [reference]);
        context.ParameterLocals = [source];
        context.Locals = [result];
        context.AnalysisWarnings = [];
        context.ControlFlowGraph = new ISILControlFlowGraph([operation, new Instruction(1, OpCode.Return, result)]);
        var module = new ModuleDefinition("ByRefOffset.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        // 基元声明属于真实核心库身份；不在输出程序集定义同名伪 Byte 类型。
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, element, "Byte", TypeAttributes.Public | TypeAttributes.Sealed);
        var host = new TypeDefinition("Fixture", "ByRefOffsetHost", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var signature = module.CorLibTypeFactory.Byte.MakeByReferenceType();
        var method = new MethodDefinition("Offset", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(signature, [signature]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(method);
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
        Assert.That(method.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
        var assembly = new AssemblyDefinition("ByRefOffset", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var pe = stream.ToArray();
        var runtime = System.Reflection.Assembly.Load(pe).GetType("Fixture.ByRefOffsetHost")!.GetMethod("Offset")!
            .CreateDelegate<字节引用偏移委托>();
        var bytes = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        ref var actual = ref runtime(ref bytes[16]);
        var index = checked(16 + (int)(subtract ? -offset : offset));
        Assert.That(System.Runtime.CompilerServices.Unsafe.AreSame(ref actual, ref bytes[index]), Is.True);
        actual = 0x39;
        // 写入返回引用，核对全部前后哨兵，而非只核对返回数值。
        for (var i = 0; i < bytes.Length; i++) Assert.That(bytes[i], Is.EqualTo(i == index ? (byte)0x39 : (byte)0xA5));
        var root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var directory = System.IO.Path.Combine(root, $"byref-offset-{subtract}-{commuted}-{offset}");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "ByRefOffset.dll"), pe);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "runtime.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                subtract, commuted, offset, index, actualBytes = bytes.Select(value => (int)value).ToArray(),
                peSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pe)).ToLowerInvariant(),
                sameReferenceProved = true, sourceRoundTripProved = false, fullRecoveryProved = false
            }));
        }
    }

    [TestCase(0, 0)]
    [TestCase(8, 32)]
    [TestCase(4, 64)]
    [Category("异常输入")]
    public void 引用偏移排除未知架构及截断宽度(int pointerSize, int width)
    {
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type.MakeByReferenceType();
        var source = new LocalVariable("source", new Register(null, "X0"), type);
        var result = new LocalVariable("result", new Register(null, "X1"), type);
        var instruction = new Instruction(0, OpCode.Add, result, source, new Immediate(1)) { IntegerWidthBits = width };
        Assert.That(ByRefMemoryAccessHelper.TryDescribeByteOffset(instruction, pointerSize, out _, out _), Is.False);
    }

    [TestCase(OpCode.Multiply)]
    [TestCase(OpCode.And)]
    [TestCase(OpCode.Or)]
    [Category("异常输入")]
    public void 位运算和乘法不冒充引用偏移(OpCode operation)
    {
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type.MakeByReferenceType();
        var source = new LocalVariable("source", new Register(null, "X0"), type);
        var result = new LocalVariable("result", new Register(null, "X1"), type);
        Assert.That(ByRefMemoryAccessHelper.TryDescribeByteOffset(new Instruction(0, operation, result, source, new Immediate(1)),
            8, out _, out _), Is.False);
        Assert.That(ByRefMemoryAccessHelper.TryDescribeByteOffset(new Instruction(0, OpCode.Subtract, result, new Immediate(1), source),
            8, out _, out _), Is.False);
    }

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
    [Category("基本功能")]
    public void 有符号与无符号整数除法映射到不同Cil操作码()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                IlGenerator.GetBinaryNumericCilOpCode(OpCode.Divide),
                Is.EqualTo(CilOpCodes.Div));
            Assert.That(
                IlGenerator.GetBinaryNumericCilOpCode(OpCode.DivideUnsigned),
                Is.EqualTo(CilOpCodes.Div_Un));
        });
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
    public void 紧邻分配与构造调用保持单一Newobj融合()
    {
        var result = 生成构造器融合顺序Cil(插入独立实参计算: false, 插入对象读取: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
            Assert.That(result.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 构造实参在分配后计算时Newobj延后到实参定义之后()
    {
        var result = 生成构造器融合顺序Cil(插入独立实参计算: true, 插入对象读取: false);
        var 常量索引 = result.FindIndex(instruction => instruction.OpCode.Code is CilCode.Ldc_I4 or CilCode.Ldc_I4_7);
        var 首次存储索引 = result.FindIndex(instruction => instruction.OpCode == CilOpCodes.Stloc);
        var 构造索引 = result.FindIndex(instruction => instruction.OpCode == CilOpCodes.Newobj);

        Assert.Multiple(() =>
        {
            Assert.That(常量索引, Is.GreaterThanOrEqualTo(0));
            Assert.That(首次存储索引, Is.GreaterThan(常量索引));
            Assert.That(构造索引, Is.GreaterThan(首次存储索引));
            Assert.That(result.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 构造前读取新对象时禁止延后融合()
    {
        var result = 生成构造器融合顺序Cil(插入独立实参计算: false, 插入对象读取: true);
        var 构造索引 = result.FindIndex(instruction => instruction.OpCode == CilOpCodes.Newobj);
        var 读取索引 = result.FindIndex(instruction => instruction.OpCode == CilOpCodes.Ldloc);

        Assert.Multiple(() =>
        {
            Assert.That(构造索引, Is.GreaterThanOrEqualTo(0));
            Assert.That(读取索引, Is.GreaterThan(构造索引));
            Assert.That(result.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// 构造一个最小分配/构造序列，分别覆盖紧邻融合、独立实参计算和对象提前读取。
    /// </summary>
    private static List<CilInstruction> 生成构造器融合顺序Cil(bool 插入独立实参计算, bool 插入对象读取)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemInt32 = app.SystemTypes.SystemInt32Type;
        var systemVoid = app.SystemTypes.SystemVoidType;
        var constructor = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName
                                              | ReflectionMethodAttributes.RTSpecialName,
            [systemInt32]);
        var caller = new InjectedMethodAnalysisContext(
            systemObject,
            "Construct",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            插入独立实参计算 ? [] : [systemInt32]);
        var constructedObject = new LocalVariable(
            "constructedObject",
            new Register(null, "X0"),
            systemObject);
        var argument = new LocalVariable(
            "argument",
            new Register(null, "W1"),
            systemInt32);
        var observedObject = new LocalVariable(
            "observedObject",
            new Register(null, "X2"),
            systemObject);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Newobj, constructedObject, systemObject),
        };
        if (插入独立实参计算)
            instructions.Add(new Instruction(1, OpCode.Move, argument, new Immediate(7)));
        if (插入对象读取)
            instructions.Add(new Instruction(2, OpCode.Move, observedObject, constructedObject));
        instructions.Add(new Instruction(3, OpCode.CallVoid, constructor, constructedObject, argument));
        instructions.Add(new Instruction(4, OpCode.Return));
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [constructedObject, observedObject];
        caller.ParameterLocals = 插入独立实参计算 ? [] : [argument];
        if (插入独立实参计算)
            caller.Locals.Add(argument);
        caller.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ConstructorFusionOrderTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ConstructorFusionOrderType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        systemObject.PutExtraData("AsmResolverType", typeDefinition);
        绑定AsmResolver系统类型(
            module,
            systemInt32,
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int32]));
        constructorDefinition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        var callerDefinition = new MethodDefinition(
            "Construct",
            MethodAttributes.Public | MethodAttributes.Static,
            插入独立实参计算
                ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void)
                : MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int32]));
        if (!插入独立实参计算)
            callerDefinition.ParameterDefinitions.Add(new ParameterDefinition(1, "argument", (ParameterAttributes)0));
        typeDefinition.Methods.Add(constructorDefinition);
        typeDefinition.Methods.Add(callerDefinition);
        constructor.PutExtraData("AsmResolverMethod", constructorDefinition);

        IlGenerator.GenerateIl(caller, callerDefinition);
        CilStackValidator.Validate(callerDefinition.CilMethodBody!, "ConstructorFusionOrderType::Construct");
        return callerDefinition.CilMethodBody!.Instructions.ToList();
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

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(12)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 托管结构字段解析后发射精确字段读写且保持引用接收者(int offset)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.GetAssemblyByName("mscorlib")!, "Fixture", "ReferencedStruct" + offset,
            app.SystemTypes.SystemValueTypeType, ReflectionTypeAttributes.Public | ReflectionTypeAttributes.SequentialLayout);
        var field = owner.InjectFieldContext("Value", app.SystemTypes.SystemInt32Type, System.Reflection.FieldAttributes.Public);
        field.OverrideOffset = offset;
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), owner.MakeByReferenceType());
        var value = new LocalVariable("value", new Register(null, "X2"), app.SystemTypes.SystemInt32Type);
        var read = new Instruction(0, OpCode.Move, value, new MemoryOperand(receiver, addend: offset)) { MemoryAccessWidthBits = 32 };
        var write = new Instruction(1, OpCode.Move, new MemoryOperand(receiver, addend: offset), value) { MemoryAccessWidthBits = 32 };
        var context = owner.InjectMethodContext("ReadWrite" + offset, app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, owner.MakeByReferenceType());
        context.ControlFlowGraph = new ISILControlFlowGraph([read, write, new Instruction(2, OpCode.Return)]);
        context.ParameterLocals = [receiver];
        context.Locals = [value];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("ByRefFieldIntegration.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        // 集成夹具自行构造程序集，显式绑定局部值类型，保持与生产程序集填充阶段相同的前置条件。
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemInt32Type, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition("Fixture", owner.Name, TypeAttributes.Public | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        module.TopLevelTypes.Add(type);
        owner.PutExtraData("AsmResolverType", type);
        var fieldDefinition = new FieldDefinition("Value", FieldAttributes.Public, module.CorLibTypeFactory.Int32);
        type.Fields.Add(fieldDefinition);
        field.PutExtraData("AsmResolverField", fieldDefinition);
        var definition = new MethodDefinition(context.Name, MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [new TypeDefOrRefSignature(type, true).MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "receiver", (ParameterAttributes)0));
        type.Methods.Add(definition);

        Assert.That(MetadataResolver.ResolveFieldOffsets(context), Is.True);
        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var instructions = definition.CilMethodBody!.Instructions;
        Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(1));
        Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
        foreach (var instruction in instructions.Where(instruction => instruction.OpCode == CilOpCodes.Ldfld || instruction.OpCode == CilOpCodes.Stfld))
            Assert.That(((IFieldDescriptor)instruction.Operand!).FullName, Is.EqualTo(fieldDefinition.FullName));
        Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldarg), Is.EqualTo(2));
        Assert.That(instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldarga || instruction.OpCode == CilOpCodes.Ldloca), Is.False);
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
    public void 无构造器类型操作数写入IntPtr拒绝无证据零值()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("TYPE_OPERAND"));
    }

    [Test]
    [Category("基本功能")]
    public void 运行时类型句柄默认值按IntPtr拒绝无证据零值()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("TYPE_OPERAND"));
    }

    [Test]
    [Category("基本功能")]
    public void 托管类型操作数写入IntPtr槽时拒绝无证据零值()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("TYPE_OPERAND"));
    }

    [Test]
    [Category("基本功能")]
    public void 托管引用上的运行时类布局探针拒绝无证据零值()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("RUNTIME_CLASS_LAYOUT"));
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
    public void 原生内存值与无构造器类型比较时拒绝无证据零值()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("MEMORY_LOAD"));
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
    public void 未配对Newobj不以UIntPtr零值冒充分配()
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

        // 原生分配不是整数零；直接发射器应抛出恢复异常，不通过旧默认值断言掩盖缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("OBJECT_ALLOCATION"));
        Assert.That(definition.CilMethodBody!.Instructions, Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void 无构造器类型操作数写入引用拒绝无证据Null()
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

        // 旧测试曾要求无证据默认值；当前合同要求保留明确恢复缺口。
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("TYPE_OPERAND"));
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
    public void 类型操作数进入IntPtr形参拒绝猜测原生地址()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var error = Assert.Throws<Cpp2IL.Core.Utils.UnresolvedCilSemanticException>(() =>
            生成类型实参加载Cil(appContext.SystemTypes.SystemObjectType, appContext.SystemTypes.SystemIntPtrType));
        Assert.That(error!.Message, Does.Contain("TYPE_OPERAND"));
    }
    private static TypeAnalysisContext 获取运行时类型句柄(ApplicationAnalysisContext appContext)
        => appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.RuntimeTypeHandle")!;

    [TestCase("System.Int64", 0L)]
    [TestCase("System.Int64", 1L)]
    [TestCase("System.Int64", long.MinValue)]
    [TestCase("System.UInt64", 1L)]
    [TestCase("System.UInt64", -1L)]
    [TestCase("System.UInt64", long.MaxValue)]
    [Category("边界值")]
    public void 八字节目标立即数保持完整I8栈类型(string typeName, long value)
    {
        var type = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("mscorlib")!.GetTypeByFullName(typeName)!;
        var emitted = 生成局部量立即数赋值Cil(type, value);
        Assert.That(emitted[0].OpCode, Is.EqualTo(CilOpCodes.Ldc_I8));
        Assert.That(emitted[0].Operand, Is.EqualTo(value));
        Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I4), Is.False);
    }

    [TestCase("System.IntPtr", 0L, true)]
    [TestCase("System.IntPtr", -1L, true)]
    [TestCase("System.UIntPtr", 1L, false)]
    [TestCase("pointer", 0L, false)]
    [TestCase("pointer", -1L, false)]
    [Category("基本功能")]
    public void 原生地址立即数使用目标宽度而非托管空引用(string typeName, long value, bool signed)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = typeName == "pointer" ? new PointerTypeAnalysisContext(app.SystemTypes.SystemInt32Type)
            : app.GetAssemblyByName("mscorlib")!.GetTypeByFullName(typeName)!;
        var emitted = 生成局部量立即数赋值Cil(type, value);
        Assert.That(emitted[0].OpCode, Is.EqualTo(CilOpCodes.Ldc_I8));
        Assert.That(emitted[0].Operand, Is.EqualTo(value));
        Assert.That(emitted[1].OpCode, Is.EqualTo(signed ? CilOpCodes.Conv_I : CilOpCodes.Conv_U));
        Assert.That(emitted.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 整数零不伪造托管引用()
    {
        var error = Assert.Throws<UnresolvedCilSemanticException>(() => 生成局部量立即数赋值Cil(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type.MakeByReferenceType(), 0));
        Assert.That(error!.Message, Does.Contain("IMMEDIATE_MANAGED_BYREF"));
        Assert.That(error.Message, Does.Contain("expectedType=System.Int32&").And.Contain("isil=0 Move").And.Contain("value=0"));
    }

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
            // 包装类型的签名递归引用元素，测试绑定必须落到真实元素声明。
            var boundType = localType;
            while (boundType is WrappedTypeAnalysisContext wrapped) boundType = wrapped.ElementType;
            var separator = boundType.FullName.LastIndexOf('.');
            var name = separator >= 0 ? boundType.FullName[(separator + 1)..] : boundType.FullName;
            绑定AsmResolver系统类型(
                module,
                boundType,
                name,
                TypeAttributes.Public
                | (boundType.IsValueType
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
    [TestCase(0L, 64, true)]
    [TestCase(4L, 32, true)]
    [TestCase(7L, 8, true)]
    [TestCase(8L, 8, false)]
    [TestCase(-1L, 8, false)]
    [TestCase(long.MaxValue, 64, false)]
    [TestCase(0L, 0, false)]
    [TestCase(0L, 7, false)]
    [TestCase(0L, 128, false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 引用零块写仅接受原始值类型边界内的精确字节(long offset, int width, bool accepted)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), app.SystemTypes.SystemInt64Type.MakeByReferenceType());
        var write = new Instruction(0, OpCode.Move, new MemoryOperand(receiver, addend: offset), new Immediate(0))
            { MemoryAccessWidthBits = width };
        Assert.That(ByRefZeroBlockRecovery.TryDescribe(write, app.Binary.PointerSizeBytes, out var block), Is.EqualTo(accepted));
        if (accepted)
        {
            Assert.That(block.Receiver, Is.SameAs(receiver));
            Assert.That(block.Offset, Is.EqualTo(offset));
            Assert.That(block.ByteCount, Is.EqualTo(width / 8));
        }
    }

    [TestCase(0, 64)]
    [TestCase(4, 32)]
    [TestCase(7, 8)]
    [Category("fixture集成")]
    public void 引用零块写发射原始范围而非整个值类型写回(int offset, int width)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), app.SystemTypes.SystemInt64Type.MakeByReferenceType());
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "ZeroRange", app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, [receiver.Type!]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, new MemoryOperand(receiver, addend: offset), new Immediate(0)) { MemoryAccessWidthBits = width },
            new Instruction(1, OpCode.Return)]);
        context.ParameterLocals = [receiver];
        context.Locals = [];
        context.AnalysisWarnings = [];
        var module = new ModuleDefinition("ZeroRange.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition("Fixture", "ZeroRangeHost", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var definition = new MethodDefinition("ZeroRange", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int64.MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "receiver", (ParameterAttributes)0));
        type.Methods.Add(definition);
        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, "Fixture.ZeroRange::ZeroRange");
        var il = definition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Initblk), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Stobj), Is.False);
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Add), Is.EqualTo(offset == 0 ? 0 : 1));
        var blockIndex = il.ToList().FindIndex(instruction => instruction.OpCode == CilOpCodes.Initblk);
        Assert.That(il[blockIndex - 1].OpCode, Is.EqualTo(CilOpCodes.Unaligned));
        Assert.That(il[blockIndex - 2].Operand, Is.EqualTo(width / 8));

        // 执行生产器真正生成的程序集，而非在测试中重写一份 initblk 指令实现。
        var assembly = new AssemblyDefinition("ZeroRangeRuntime", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var emittedBytes = stream.ToArray();
        var runtimeAssembly = System.Reflection.Assembly.Load(emittedBytes);
        var runtimeMethod = runtimeAssembly.GetType("Fixture.ZeroRangeHost", throwOnError: true)!.GetMethod("ZeroRange")!;
        var invoke = runtimeMethod.CreateDelegate<引用零写委托>();
        var bytes = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        ref var target = ref System.Runtime.CompilerServices.Unsafe.As<byte, long>(ref bytes[8]);
        invoke(ref target);
        for (var index = 0; index < bytes.Length; index++)
            Assert.That(bytes[index], Is.EqualTo(index >= 8 + offset && index < 8 + offset + width / 8 ? (byte)0 : (byte)0xA5),
                $"字节 {index} 必须保持原始写入范围，前后哨兵及未覆盖部分均不得改变。");

        var evidenceRoot = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        if (!string.IsNullOrEmpty(evidenceRoot))
        {
            // 持久化同一份已执行 PE，后续源码导出使用它而不是重新拼装方法体。
            var directory = System.IO.Path.Combine(evidenceRoot, $"zero-block-{offset}-{width}");
            System.IO.Directory.CreateDirectory(directory);
            using (var output = new System.IO.FileStream(System.IO.Path.Combine(directory, "ZeroRange.dll"), System.IO.FileMode.CreateNew))
                output.Write(emittedBytes);
            using var receipt = new System.IO.FileStream(System.IO.Path.Combine(directory, "runtime-bytes.json"), System.IO.FileMode.CreateNew);
            System.Text.Json.JsonSerializer.Serialize(receipt, new
            {
                schema = "GeneratedZeroBlockRuntime/v1", offset, width, receiverOffset = 8,
                initialByte = 0xA5, actualBytes = bytes.Select(value => (int)value).ToArray(),
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(emittedBytes)).ToLowerInvariant(),
                runtimeByteChecksPassed = true, sourceRoundTripProved = false
            });
        }
    }

    [TestCase("System.Int64", 0L)]
    [TestCase("System.Int64", 1L)]
    [TestCase("System.Int64", long.MinValue)]
    [TestCase("System.UInt64", 0L)]
    [TestCase("System.UInt64", 1L)]
    [TestCase("System.UInt64", -1L)]
    [TestCase("System.IntPtr", 0L)]
    [TestCase("System.IntPtr", -1L)]
    [TestCase("System.UIntPtr", 0L)]
    [TestCase("System.UIntPtr", -1L)]
    [Category("边界值")]
    public void 目标类型常量生成PE后实际执行保持位模式(string typeName, long value)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var returnType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName(typeName)!;
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType,
            "ReadConstant", returnType, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, []);
        context.ControlFlowGraph = new ISILControlFlowGraph([new Instruction(0, OpCode.Return, new Immediate(value))]);
        context.Locals = [];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];
        var module = new ModuleDefinition("TypedImmediate.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        var signature = typeName switch
        {
            "System.Int64" => module.CorLibTypeFactory.Int64,
            "System.UInt64" => module.CorLibTypeFactory.UInt64,
            "System.IntPtr" => module.CorLibTypeFactory.IntPtr,
            "System.UIntPtr" => module.CorLibTypeFactory.UIntPtr,
            _ => throw new InvalidOperationException("测试返回类型缺少真实运行签名。")
        };
        var host = new TypeDefinition("Fixture", "TypedImmediateHost", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("ReadConstant", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(signature));
        host.Methods.Add(definition);
        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("TypedImmediate", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        byte[] pe = stream.ToArray();
        var runtime = System.Reflection.Assembly.Load(pe);
        object result = runtime.GetType("Fixture.TypedImmediateHost", true)!.GetMethod("ReadConstant")!.Invoke(null, null)!;
        // 按 CLR 实际返回类型读取位模式，不在测试侧重写待验证的方法体。
        ulong actualBits = result switch
        {
            long signed => unchecked((ulong)signed),
            ulong unsigned => unsigned,
            IntPtr signedPointer => unchecked((ulong)signedPointer.ToInt64()),
            UIntPtr unsignedPointer => unsignedPointer.ToUInt64(),
            _ => throw new InvalidOperationException("运行结果类型不符合生成签名。")
        };
        ulong expectedBits = unchecked((ulong)value);
        if (typeName == "System.UIntPtr" && IntPtr.Size == 4) expectedBits &= uint.MaxValue;
        Assert.That(actualBits, Is.EqualTo(expectedBits));
        string? root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string directory = System.IO.Path.Combine(root, $"typed-immediate-{returnType.Name}-{unchecked((ulong)value):X16}");
            System.IO.Directory.CreateDirectory(directory);
            using (var output = new System.IO.FileStream(System.IO.Path.Combine(directory, "TypedImmediate.dll"), System.IO.FileMode.CreateNew)) output.Write(pe);
            using var receipt = new System.IO.FileStream(System.IO.Path.Combine(directory, "runtime-bits.json"), System.IO.FileMode.CreateNew);
            System.Text.Json.JsonSerializer.Serialize(receipt, new
            {
                schema = "TypedImmediateRuntime/v1", typeName, value, expectedBits, actualBits,
                pointerSize = IntPtr.Size, runtimeExecutionProved = true, sourceRoundTripProved = false,
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pe)).ToLowerInvariant()
            });
        }
    }

    private delegate void 引用零写委托(ref long value);

    [TestCase(2, 32, 0, 0x3344)]
    [TestCase(4, 32, 5, 0x1122)]
    [TestCase(2, 64, 7, 0xFEDC)]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP通用寄存器广播生成PE并执行全部六十四与一百二十八位块(
        int laneCount,
        int elementWidthBits,
        int extractedUInt16Lane,
        int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var inputType = elementWidthBits == 64
            ? app.SystemTypes.SystemInt64Type
            : app.SystemTypes.SystemInt32Type;
        var input = new LocalVariable("value", new Register(null, elementWidthBits == 64 ? "X0" : "W0"), inputType);
        // 故意保留未定型槽，证明 CIL 载体只依赖原生布局而不依赖业务类型推断。
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var result = new LocalVariable("result", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        long inputValue = elementWidthBits == 64
            ? unchecked((long)0xFEDCBA9876543210UL)
            : unchecked((int)0x11223344);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "DuplicateAndExtract",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [inputType]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(laneCount), new Immediate(elementWidthBits), new Immediate(-1)),
            new Instruction(1, OpCode.VectorExtractUnsignedInt16, result, vector, new Immediate(extractedUInt16Lane)),
            new Instruction(2, OpCode.Return, result),
        ]);
        context.ParameterLocals = [input];
        context.Locals = [vector, result];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("VectorDuplicateRuntime.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        // 局部量的 Int32 身份必须来自真实核心库作用域，不能在夹具程序集内伪造同名基元。
        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        if (elementWidthBits == 64)
            绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64",
                TypeAttributes.Public | TypeAttributes.Sealed);
        var host = new TypeDefinition("Fixture", "VectorDuplicateHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var parameterSignature = elementWidthBits == 64
            ? module.CorLibTypeFactory.Int64
            : module.CorLibTypeFactory.Int32;
        var definition = new MethodDefinition("DuplicateAndExtract",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [parameterSignature]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var words = definition.CilMethodBody!.LocalVariables
            .Count(local => local.VariableType.FullName == "System.UInt64");
        Assert.That(words, Is.EqualTo(laneCount * elementWidthBits / 64));
        using var stream = new System.IO.MemoryStream();
        var assembly = new AssemblyDefinition("VectorDuplicateRuntime", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorDuplicateHost", true)!.GetMethod("DuplicateAndExtract")!;
        object runtimeArgument = elementWidthBits == 64
            ? (object)inputValue
            : unchecked((int)inputValue);
        var actual = (int)runtimeMethod.Invoke(null, [runtimeArgument])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(false, 2, 32, 3, 0x89AB)]
    [TestCase(true, 2, 64, 7, 0xFEDC)]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP浮点标量首通道按原始位模式广播并执行(
        bool doubleSource,
        int laneCount,
        int elementWidthBits,
        int extractedUInt16Lane,
        int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var sourceType = doubleSource ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
        var source = new LocalVariable("value", new Register(null, "V1"), sourceType);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var result = new LocalVariable("result", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType,
            "DuplicateScalarLane", app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, [sourceType]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, source,
                new Immediate(laneCount), new Immediate(elementWidthBits), new Immediate(0)),
            new Instruction(1, OpCode.VectorExtractUnsignedInt16, result, vector, new Immediate(extractedUInt16Lane)),
            new Instruction(2, OpCode.Return, result),
        ]);
        context.ParameterLocals = [source];
        context.Locals = [vector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, sourceType, doubleSource ? "Double" : "Single",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var assemblyName = doubleSource ? "VectorDoubleLane" : "VectorSingleLane";
        var module = new ModuleDefinition(assemblyName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", assemblyName + "Host", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var parameterType = doubleSource ? module.CorLibTypeFactory.Double : module.CorLibTypeFactory.Single;
        var definition = new MethodDefinition("DuplicateScalarLane", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [parameterType]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var bitCall = definition.CilMethodBody!.Instructions
            .Single(item => item.OpCode == CilOpCodes.Call
                            && item.Operand is IMethodDescriptor descriptor
                            && descriptor.Name == (doubleSource ? "DoubleToInt64Bits" : "SingleToInt32Bits"));
        Assert.That(bitCall, Is.Not.Null);
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture." + assemblyName + "Host", true)!.GetMethod("DuplicateScalarLane")!;
        // 条件表达式会先把Single提升为Double；两支分别装箱，保持真实方法签名。
        object argument = doubleSource
            ? (object)BitConverter.Int64BitsToDouble(unchecked((long)0xFEDCBA9876543210UL))
            : BitConverter.Int32BitsToSingle(unchecked((int)0x89ABCDEF));
        var actual = (int)runtimeMethod.Invoke(null, [argument])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(OpCode.Add, 32, 3, 0x3FC00000L, 0x40100000L, 0x4070, 1)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x40000000L, 0x4040, 0, 0)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 7, 0x3FC00000L, 0x40000000L, 0x4040, 1, 0, false, 4)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x40000000L, 0x4040, 2, 0)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x40000000L, 0x4040, 0, 0, true)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 7, 0x3FC00000L, 0x400000003F800000L, 0x4040, 0, 3, false, 4, true)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x400000003F800000L, 0x3FC0, 0, 0, false, 2, true)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x80000000L, 0x40000000L, 0x8000, 0, 0)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x7F800000L, 0x40000000L, 0x7F80, 0, 0)]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x40000000L, 0, 0, 2, false, 2, false, "VECTOR_LANE_SOURCE_RANGE")]
    [TestCase(OpCode.VectorMultiplyByElement, 32, 3, 0x3FC00000L, 0x40000000L, 0, 0, 1, true, 2, false, "VECTOR_LANE_SOURCE_STATE")]
    [TestCase(OpCode.Multiply, 32, 3, 0x3FC00000L, 0x40000000L, 0x4040, 2)]
    [TestCase(OpCode.Divide, 64, 7, 0x401E000000000000L, 0x4004000000000000L, 0x4008, 1)]
    [TestCase(OpCode.Subtract, 64, 7, 0x401E000000000000L, 0x4004000000000000L, 0x4014, 2)]
    [TestCase(OpCode.Add, 32, 1, 0x3FC00000L, 0x40100000L, 0x4070)]
    [TestCase(OpCode.Divide, 64, 3, 0x401E000000000000L, 0x4004000000000000L, 0x4008)]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP载体执行逐通道浮点四则运算并保持位模式(
        OpCode operation,
        int elementWidthBits,
        int extractedUInt16Lane,
        long leftBits,
        long rightBits,
        int expected, int alias = 0, int sourceLane = -1, bool scalarSource = false,
        int laneCount = 2, bool packedRight = false, string? expectedFailure = null)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var inputType = elementWidthBits == 32
            ? app.SystemTypes.SystemInt32Type
            : app.SystemTypes.SystemInt64Type;
        var leftInput = new LocalVariable("left", new Register(null, elementWidthBits == 32 ? "W0" : "X0"), inputType);
        var rightInputType = scalarSource ? app.SystemTypes.SystemSingleType
            : packedRight ? app.SystemTypes.SystemInt64Type : inputType;
        var rightInput = new LocalVariable("right", new Register(null, scalarSource ? "V1" : packedRight ? "X1" : elementWidthBits == 32 ? "W1" : "X1"), rightInputType);
        var leftVector = new LocalVariable("leftVector", new Register(null, "V0"));
        var rightVector = new LocalVariable("rightVector", new Register(null, "V1"));
        var resultVector = alias == 1 ? leftVector : alias == 2 ? rightVector : new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType,
            "VectorBinary", app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, [inputType, rightInputType]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, leftVector, leftInput,
                new Immediate(laneCount), new Immediate(elementWidthBits), new Immediate(-1)),
            scalarSource ? new Instruction(1, OpCode.Nop) :
                new Instruction(1, OpCode.VectorDuplicate, rightVector, rightInput,
                    new Immediate(packedRight ? 2 : laneCount), new Immediate(packedRight ? 64 : elementWidthBits), new Immediate(-1)),
            sourceLane >= 0
                ? new Instruction(2, operation, resultVector, leftVector, scalarSource ? rightInput : rightVector,
                    new Immediate(sourceLane), new Immediate(laneCount), new Immediate(elementWidthBits))
                : new Instruction(2, operation, resultVector, leftVector, rightVector,
                    new Immediate(laneCount), new Immediate(elementWidthBits)),
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(extractedUInt16Lane)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [leftInput, rightInput];
        context.Locals = new[] { leftVector, rightVector, resultVector, result }.Distinct().ToList();
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        if (elementWidthBits == 64 || packedRight)
            绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64",
                TypeAttributes.Public | TypeAttributes.Sealed);
        if (scalarSource)
            绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemSingleType, "Single",
                TypeAttributes.Public | TypeAttributes.Sealed);
        var assemblyName = $"Vector{operation}{elementWidthBits}Alias{alias}Lane{sourceLane}Scalar{scalarSource}Count{laneCount}Packed{packedRight}";
        var module = new ModuleDefinition(assemblyName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", assemblyName + "Host", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var parameterType = elementWidthBits == 32 ? module.CorLibTypeFactory.Int32 : module.CorLibTypeFactory.Int64;
        var definition = new MethodDefinition("VectorBinary", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [parameterType,
                scalarSource ? module.CorLibTypeFactory.Single : packedRight ? module.CorLibTypeFactory.Int64 : parameterType]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
        host.Methods.Add(definition);

        if (expectedFailure != null)
        {
            var error = Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
            Assert.That(error!.Message, Does.Contain(expectedFailure));
            return;
        }
        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture." + assemblyName + "Host", true)!.GetMethod("VectorBinary")!;
        object leftArgument = elementWidthBits == 32 ? (object)unchecked((int)leftBits) : leftBits;
        object rightArgument = scalarSource ? (object)BitConverter.Int32BitsToSingle(unchecked((int)rightBits))
            : packedRight || elementWidthBits == 64 ? (object)rightBits : unchecked((int)rightBits);
        var actual = (int)runtimeMethod.Invoke(null, [leftArgument, rightArgument])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0x00010002, 0x00030004, 6)]
    [TestCase(-1, 1, 0)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 整数向量ADD按元素位宽执行并保留溢出截断(int leftValue, int rightValue, int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var leftInput = new LocalVariable("left", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var rightInput = new LocalVariable("right", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var leftVector = new LocalVariable("leftVector", new Register(null, "V0"));
        var rightVector = new LocalVariable("rightVector", new Register(null, "V1"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var vectorAdd = new Instruction(
            2,
            OpCode.Add,
            resultVector,
            leftVector,
            rightVector,
            new Immediate(2),
            new Immediate(32))
        {
            IntegerWidthBits = 32,
        };
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorIntegerAdd",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, leftVector, leftInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, rightVector, rightInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            vectorAdd,
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, resultVector, new Immediate(0)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [leftInput, rightInput];
        context.Locals = [leftVector, rightVector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorIntegerAdd.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorIntegerAddHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorIntegerAdd",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorIntegerAdd", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorIntegerAddHost", true)!.GetMethod("VectorIntegerAdd")!;
        var actual = (int)runtimeMethod.Invoke(null, [leftValue, rightValue])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(3, 1, 6)]
    [TestCase(6, -1, 3)]
    [TestCase(7, 32, 0)]
    [Category("基本功能")]
    [Category("边界值")]
    public void USHL按有符号移位通道执行无符号逐通道移位(int value, int shift, int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var valueInput = new LocalVariable("value", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var shiftInput = new LocalVariable("shift", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var valueVector = new LocalVariable("valueVector", new Register(null, "V0"));
        var shiftVector = new LocalVariable("shiftVector", new Register(null, "V1"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorUnsignedShift",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, valueVector, valueInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, shiftVector, shiftInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(2, OpCode.VectorShiftLeftUnsignedVariable, resultVector,
                valueVector, shiftVector, new Immediate(2), new Immediate(32)),
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(0)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [valueInput, shiftInput];
        context.Locals = [valueVector, shiftVector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorUnsignedShift.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorUnsignedShiftHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorUnsignedShift",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "shift", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorUnsignedShift", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorUnsignedShiftHost", true)!.GetMethod("VectorUnsignedShift")!;
        var actual = (int)runtimeMethod.Invoke(null, [value, shift])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(-1082130432, 65535)]
    [TestCase(1065353216, 0)]
    [TestCase(int.MinValue, 0)]
    [Category("基本功能")]
    [Category("边界值")]
    public void FCMLT按浮点位模式逐通道生成比较掩码(int valueBits, int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var input = new LocalVariable("value", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V1"));
        var result = new LocalVariable("result", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorFloatingLessThanZero",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorCompareFloatingLessThanZero, resultVector, vector,
                new Immediate(2), new Immediate(32)),
            new Instruction(2, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(0)),
            new Instruction(3, OpCode.Return, result),
        ]);
        context.ParameterLocals = [input];
        context.Locals = [vector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorFloatingLessThanZero.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorFloatingLessThanZeroHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorFloatingLessThanZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorFloatingLessThanZero", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorFloatingLessThanZeroHost", true)!
            .GetMethod("VectorFloatingLessThanZero")!;
        var actual = (int)runtimeMethod.Invoke(null, [valueBits])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0x3F800000, 0x3F000000, 65535, false, TestName = "FCMGT正数大于比较值生成全掩码")]
    [TestCase(0x3F000000, 0x3F800000, 0, false, TestName = "FCMGT正数小于比较值生成零掩码")]
    [TestCase(0x3F800000, 0x3F800000, 0, false, TestName = "FCMGT相等值生成零掩码")]
    [TestCase(int.MinValue, 0, 0, false, TestName = "FCMGT负零不大于正零")]
    [TestCase(unchecked((int)0x7FC00000), 0x3F800000, 0, false, TestName = "FCMGT非数字比较生成零掩码")]
    [TestCase(0x3F800000, 0x3F000000, 0, true, TestName = "FCMEQ不相等值生成零掩码")]
    [TestCase(0x3F800000, 0x3F800000, 65535, true, TestName = "FCMEQ相等值生成全掩码")]
    [TestCase(int.MinValue, 0, 65535, true, TestName = "FCMEQ负零等于正零")]
    [TestCase(unchecked((int)0x7FC00000), unchecked((int)0x7FC00000), 0, true, TestName = "FCMEQ非数字比较生成零掩码")]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void FCMGT与FCMEQ按浮点位模式逐通道生成比较掩码(
        int leftBits,
        int rightBits,
        int expected,
        bool equal)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var leftInput = new LocalVariable("left", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var rightInput = new LocalVariable("right", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var leftVector = new LocalVariable("leftVector", new Register(null, "V0"));
        var rightVector = new LocalVariable("rightVector", new Register(null, "V1"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            equal ? "VectorFloatingEqual" : "VectorFloatingGreaterThan",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, leftVector, leftInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, rightVector, rightInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(2, equal
                ? OpCode.VectorCompareFloatingEqual
                : OpCode.VectorCompareFloatingGreaterThan, resultVector,
                leftVector, rightVector, new Immediate(2), new Immediate(32)),
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(0)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [leftInput, rightInput];
        context.Locals = [leftVector, rightVector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var comparisonName = equal ? "VectorFloatingEqual" : "VectorFloatingGreaterThan";
        var module = new ModuleDefinition(comparisonName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", comparisonName + "Host",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition(comparisonName,
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition(comparisonName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture." + comparisonName + "Host", true)!
            .GetMethod(comparisonName)!;
        var actual = (int)runtimeMethod.Invoke(null, [leftBits, rightBits])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0x40700000, 3)]
    [TestCase(unchecked((int)0xC0300000), 65534)]
    [TestCase(0x3F000000, 0)]
    [TestCase(0x46FFFF80, 32767)]
    [Category("基本功能")]
    [Category("边界值")]
    public void FCVTZS按浮点位模式逐通道向零转换(int valueBits, int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var input = new LocalVariable("value", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V1"));
        var result = new LocalVariable("result", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorFloatingToSignedInteger",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorConvertFloatToSignedInteger, resultVector, vector,
                new Immediate(2), new Immediate(32)),
            new Instruction(2, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(0)),
            new Instruction(3, OpCode.Return, result),
        ]);
        context.ParameterLocals = [input];
        context.Locals = [vector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorFloatingToSignedInteger.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorFloatingToSignedIntegerHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorFloatingToSignedInteger",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorFloatingToSignedInteger", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorFloatingToSignedIntegerHost", true)!
            .GetMethod("VectorFloatingToSignedInteger")!;
        var actual = (int)runtimeMethod.Invoke(null, [valueBits])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0x0000AAAA, 0x00005555, 0x00000F0F, 0x00005A5A)]
    [TestCase(0x0000AAAA, 0x00005555, 0x00000000, 0x00005555)]
    [TestCase(0x0000AAAA, 0x00005555, -1, 0x0000AAAA)]
    [Category("基本功能")]
    [Category("边界值")]
    public void BIT按目标旧值和掩码逐位选择(int destinationValue, int value, int mask, int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var destinationInput = new LocalVariable("destination", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var valueInput = new LocalVariable("value", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var maskInput = new LocalVariable("mask", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var destinationVector = new LocalVariable("destinationVector", new Register(null, "V0"));
        var valueVector = new LocalVariable("valueVector", new Register(null, "V1"));
        var maskVector = new LocalVariable("maskVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W3"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorBitwiseInsert",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, destinationVector, destinationInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, valueVector, valueInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(2, OpCode.VectorDuplicate, maskVector, maskInput,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(3, OpCode.VectorBitwiseInsert, destinationVector,
                valueVector, maskVector, new Immediate(64)),
            new Instruction(4, OpCode.VectorExtractUnsignedInt16, result, destinationVector,
                new Immediate(0)),
            new Instruction(5, OpCode.Return, result),
        ]);
        context.ParameterLocals = [destinationInput, valueInput, maskInput];
        context.Locals = [destinationVector, valueVector, maskVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorBitwiseInsert.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorBitwiseInsertHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorBitwiseInsert",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32,
                    module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "destination", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "value", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(3, "mask", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorBitwiseInsert", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorBitwiseInsertHost", true)!
            .GetMethod("VectorBitwiseInsert")!;
        var actual = (int)runtimeMethod.Invoke(null, [destinationValue, value, mask])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0, 0x00001234, unchecked((int)0xFFFFABCD), 0x0000ABCD)]
    [TestCase(-1, 0x00001234, unchecked((int)0xFFFFABCD), 0x00001234)]
    [TestCase(0x0000AAAA, 0x00005555, unchecked((int)0x0000F0F0), 0x00005050)]
    [TestCase(unchecked((int)0x0000F0F0), unchecked((int)0x0000AAAA), 0x00005555, unchecked((int)0x0000A5A5))]
    [Category("基本功能")]
    [Category("边界值")]
    public void BSL按目标掩码在两侧向量之间逐位选择(
        int mask,
        int selectedWhenSet,
        int selectedWhenClear,
        int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var maskInput = new LocalVariable("mask", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var selectedWhenSetInput = new LocalVariable("selectedWhenSet", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var selectedWhenClearInput = new LocalVariable("selectedWhenClear", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var maskVector = new LocalVariable("maskVector", new Register(null, "V0"));
        var selectedWhenSetVector = new LocalVariable("selectedWhenSetVector", new Register(null, "V1"));
        var selectedWhenClearVector = new LocalVariable("selectedWhenClearVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W3"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorBitwiseSelect",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type, app.SystemTypes.SystemInt32Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, maskVector, maskInput,
                new Immediate(4), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, selectedWhenSetVector, selectedWhenSetInput,
                new Immediate(4), new Immediate(32), new Immediate(-1)),
            new Instruction(2, OpCode.VectorDuplicate, selectedWhenClearVector, selectedWhenClearInput,
                new Immediate(4), new Immediate(32), new Immediate(-1)),
            new Instruction(3, OpCode.VectorBitwiseSelect, maskVector, maskVector,
                selectedWhenSetVector, selectedWhenClearVector, new Immediate(128)),
            new Instruction(4, OpCode.VectorExtractUnsignedInt16, result, maskVector,
                new Immediate(0)),
            new Instruction(5, OpCode.Return, result),
        ]);
        context.ParameterLocals = [maskInput, selectedWhenSetInput, selectedWhenClearInput];
        context.Locals = [maskVector, selectedWhenSetVector, selectedWhenClearVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorBitwiseSelect.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorBitwiseSelectHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorBitwiseSelect",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32,
                    module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "mask", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "selectedWhenSet", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(3, "selectedWhenClear", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorBitwiseSelect", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorBitwiseSelectHost", true)!
            .GetMethod("VectorBitwiseSelect")!;
        var actual = (int)runtimeMethod.Invoke(null, [mask, selectedWhenSet, selectedWhenClear])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(32)]
    [TestCase(192)]
    [Category("异常输入")]
    public void BSL拒绝未支持的向量布局(int vectorWidth)
    {
        var destination = new LocalVariable("destination", new Register(null, "V0"));
        var mask = new LocalVariable("mask", new Register(null, "V0"));
        var selectedWhenSet = new LocalVariable("selectedWhenSet", new Register(null, "V1"));
        var selectedWhenClear = new LocalVariable("selectedWhenClear", new Register(null, "V2"));
        var instruction = new Instruction(0, OpCode.VectorBitwiseSelect,
            destination, mask, selectedWhenSet, selectedWhenClear, new Immediate(vectorWidth));

        var accepted = VectorCilRecoveryHelper.TryDescribeBitwiseSelect(instruction, out _, out var failure);
        Assert.That(accepted, Is.False);
        Assert.That(failure, Does.Contain("向量位宽"));
    }

    [TestCase(16, 0x00001234L, false, 0x3434, TestName = "XTN半字到字节并清理高半区")]
    [TestCase(16, 0x00001234L, true, 0x3434, TestName = "XTN2半字到字节并保留低半区")]
    [TestCase(32, 0xABCD1234L, false, 0x1234, TestName = "XTN字到半字并清理高半区")]
    [TestCase(32, 0xABCD1234L, true, 0x1234, TestName = "XTN2字到半字并保留低半区")]
    [TestCase(64, unchecked((long)0xFEDCBA9876543210UL), false, 0x3210, TestName = "XTN双字到字并清理高半区")]
    [TestCase(64, unchecked((long)0xFEDCBA9876543210UL), true, 0x3210, TestName = "XTN2双字到字并保留低半区")]
    [Category("基本功能")]
    [Category("边界值")]
    public void XTN按元素低半宽截取并执行半区写入(
        int sourceElementWidth,
        long sourceBits,
        bool upper,
        int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var sourceInput = new LocalVariable("source", new Register(null, "X0"), app.SystemTypes.SystemInt64Type);
        var destinationInput = new LocalVariable("destination", new Register(null, "X1"), app.SystemTypes.SystemInt64Type);
        var sourceVector = new LocalVariable("sourceVector", new Register(null, "V0"));
        var destinationVector = new LocalVariable("destinationVector", new Register(null, "V1"));
        var resultVector = upper
            ? destinationVector
            : new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var sourceLaneCount = 128 / sourceElementWidth;
        var destinationElementWidth = sourceElementWidth / 2;
        var destinationLaneCount = sourceLaneCount * 2;
        var instructions = new List<Instruction>();
        var index = 0;
        if (upper)
        {
            instructions.Add(new Instruction(index++, OpCode.VectorDuplicate, destinationVector, destinationInput,
                new Immediate(destinationLaneCount), new Immediate(destinationElementWidth), new Immediate(-1)));
        }

        instructions.Add(new Instruction(index++, OpCode.VectorDuplicate, sourceVector, sourceInput,
            new Immediate(sourceLaneCount), new Immediate(sourceElementWidth), new Immediate(-1)));
        if (upper)
        {
            instructions.Add(new Instruction(index++, OpCode.VectorNarrowExtractUpper,
                resultVector, resultVector, sourceVector,
                new Immediate(sourceLaneCount), new Immediate(sourceElementWidth)));
        }
        else
        {
            instructions.Add(new Instruction(index++, OpCode.VectorNarrowExtract,
                resultVector, sourceVector,
                new Immediate(sourceLaneCount), new Immediate(sourceElementWidth)));
        }

        instructions.Add(new Instruction(index++, OpCode.VectorExtractUnsignedInt16, result,
            resultVector, new Immediate(upper ? 4 : 0)));
        instructions.Add(new Instruction(index, OpCode.Return, result));

        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "VectorNarrowExtract",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt64Type, app.SystemTypes.SystemInt64Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        context.ParameterLocals = [sourceInput, destinationInput];
        context.Locals = new[] { sourceVector, destinationVector, resultVector, result }.Distinct().ToList();
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorNarrowExtract.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorNarrowExtractHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("VectorNarrowExtract",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int64, module.CorLibTypeFactory.Int64]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "destination", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorNarrowExtract", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorNarrowExtractHost", true)!
            .GetMethod("VectorNarrowExtract")!;
        var actual = (int)runtimeMethod.Invoke(null, [sourceBits, unchecked((long)0xA5A5A5A5A5A5A5A5UL)])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0xFFFFFFFFFFFFFFFFUL, 1UL, 0xFFFF, false, TestName = "CMHI无符号最大值大于一")]
    [TestCase(0x8000000000000000UL, 0x7FFFFFFFFFFFFFFFUL, 0xFFFF, false, TestName = "CMHI无符号高位值大于低位值")]
    [TestCase(1UL, 1UL, 0, false, TestName = "CMHI相等值生成零掩码")]
    [TestCase(0UL, 1UL, 0, false, TestName = "CMHI较小值生成零掩码")]
    [TestCase(0xFFFFFFFFFFFFFFFFUL, 1UL, 0xFFFF, true, TestName = "CMHS无符号最大值大于一")]
    [TestCase(0x8000000000000000UL, 0x7FFFFFFFFFFFFFFFUL, 0xFFFF, true, TestName = "CMHS无符号高位值大于低位值")]
    [TestCase(1UL, 1UL, 0xFFFF, true, TestName = "CMHS相等值生成全掩码")]
    [TestCase(0UL, 1UL, 0, true, TestName = "CMHS较小值生成零掩码")]
    [Category("基本功能")]
    [Category("边界值")]
    public void CMHI按无符号逐通道比较并生成全元素掩码(
        ulong leftBits,
        ulong rightBits,
        int expected,
        bool higherOrSame)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var leftInput = new LocalVariable("left", new Register(null, "X0"), app.SystemTypes.SystemUInt64Type);
        var rightInput = new LocalVariable("right", new Register(null, "X1"), app.SystemTypes.SystemUInt64Type);
        var leftVector = new LocalVariable("leftVector", new Register(null, "V0"));
        var rightVector = new LocalVariable("rightVector", new Register(null, "V1"));
        var resultVector = new LocalVariable("resultVector", new Register(null, "V2"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            higherOrSame ? "VectorCompareUnsignedHigherOrSame" : "VectorCompareUnsignedHigher",
            app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemUInt64Type, app.SystemTypes.SystemUInt64Type]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, leftVector, leftInput,
                new Immediate(2), new Immediate(64), new Immediate(-1)),
            new Instruction(1, OpCode.VectorDuplicate, rightVector, rightInput,
                new Immediate(2), new Immediate(64), new Immediate(-1)),
            new Instruction(2, higherOrSame
                ? OpCode.VectorCompareUnsignedHigherOrSame
                : OpCode.VectorCompareUnsignedHigher, resultVector,
                leftVector, rightVector, new Immediate(2), new Immediate(64)),
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, resultVector,
                new Immediate(0)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [leftInput, rightInput];
        context.Locals = [leftVector, rightVector, resultVector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemUInt64Type, "UInt64",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var comparisonName = higherOrSame ? "VectorCompareUnsignedHigherOrSame" : "VectorCompareUnsignedHigher";
        var module = new ModuleDefinition(comparisonName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", comparisonName + "Host",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition(comparisonName,
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.UInt64, module.CorLibTypeFactory.UInt64]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition(comparisonName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture." + comparisonName + "Host", true)!
            .GetMethod(comparisonName)!;
        var actual = (int)runtimeMethod.Invoke(null, [leftBits, rightBits])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP六十四位载体按等宽布局写入静态与实例字段并执行(bool isStatic)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Fixture",
            isStatic ? "VectorStaticFieldHost" : "VectorInstanceFieldHost",
            app.SystemTypes.SystemObjectType,
            ReflectionTypeAttributes.Public);
        var attributes = System.Reflection.FieldAttributes.Public
                         | (isStatic ? System.Reflection.FieldAttributes.Static : 0);
        var field = owner.InjectFieldContext("Bits", app.SystemTypes.SystemInt64Type, attributes);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var input = new LocalVariable("value", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var fieldReference = new FieldReference(field, receiver, 0);
        var context = owner.InjectMethodContext(
            "WriteBits",
            app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            owner,
            app.SystemTypes.SystemInt32Type);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.Move, fieldReference, vector) { MemoryAccessWidthBits = 64 },
            new Instruction(2, OpCode.Return),
        ]);
        context.ParameterLocals = [receiver, input];
        context.Locals = [vector];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var assemblyName = isStatic ? "VectorStaticField" : "VectorInstanceField";
        var module = new ModuleDefinition(assemblyName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", owner.Name, TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        var fieldAttributes = FieldAttributes.Public | (isStatic ? FieldAttributes.Static : 0);
        var fieldDefinition = new FieldDefinition("Bits", fieldAttributes, module.CorLibTypeFactory.Int64);
        host.Fields.Add(fieldDefinition);
        field.PutExtraData("AsmResolverField", fieldDefinition);
        var hostSignature = new TypeDefOrRefSignature(host, false);
        var definition = new MethodDefinition("WriteBits", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [hostSignature, module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "receiver", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var expectedAddressOpCode = isStatic ? CilOpCodes.Ldsflda : CilOpCodes.Ldflda;
        Assert.That(definition.CilMethodBody!.Instructions.Count(item => item.OpCode == expectedAddressOpCode), Is.EqualTo(1));
        Assert.That(definition.CilMethodBody.Instructions.Count(item => item.OpCode == CilOpCodes.Stind_I8), Is.EqualTo(1));
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeType = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Fixture." + owner.Name, true)!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeType);
        const int inputValue = unchecked((int)0x89ABCDEF);
        runtimeType.GetMethod("WriteBits")!.Invoke(null, [instance, inputValue]);
        var actual = (long)runtimeType.GetField("Bits")!.GetValue(isStatic ? null : instance)!;
        Assert.That(unchecked((ulong)actual), Is.EqualTo(0x89ABCDEF89ABCDEFUL));
    }

    [TestCase(4, 32, 128, "System.Int64")]
    [TestCase(2, 32, 32, "System.Int64")]
    [TestCase(2, 32, 64, "System.String")]
    [Category("异常输入")]
    public void DUP字段桥接拒绝宽度或非托管布局证据不一致(
        int laneCount,
        int elementWidthBits,
        int memoryWidthBits,
        string fieldTypeName)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var fieldType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName(fieldTypeName)!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Fixture", "RejectedVectorFieldHost",
            app.SystemTypes.SystemObjectType, ReflectionTypeAttributes.Public);
        var field = owner.InjectFieldContext("Value", fieldType,
            System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var input = new LocalVariable("value", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var context = owner.InjectMethodContext("RejectedWrite", app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, owner, app.SystemTypes.SystemInt32Type);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(laneCount), new Immediate(elementWidthBits), new Immediate(-1)),
            new Instruction(1, OpCode.Move, new FieldReference(field, receiver, 0), vector)
                { MemoryAccessWidthBits = memoryWidthBits },
            new Instruction(2, OpCode.Return),
        ]);
        context.ParameterLocals = [receiver, input];
        context.Locals = [vector];
        context.AnalysisWarnings = [];
        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("RejectedVectorField.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", owner.Name, TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        // 异常夹具只验证布局门；字段签名仍使用真实核心库引用，避免伪造同名类型抢先失败。
        var fieldSignature = fieldTypeName == "System.Int64"
            ? module.CorLibTypeFactory.Int64
            : module.CorLibTypeFactory.String;
        var fieldDefinition = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static,
            fieldSignature);
        host.Fields.Add(fieldDefinition);
        field.PutExtraData("AsmResolverField", fieldDefinition);
        var definition = new MethodDefinition("RejectedWrite", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [new TypeDefOrRefSignature(host, false), module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "receiver", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        var error = Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("VECTOR_FIELD_STORE"));
    }

    [TestCase(false, false, 0x3FC0, false)]
    [TestCase(true, false, 0x3FC0, false)]
    [TestCase(false, true, 0x401E, false)]
    [TestCase(true, true, 0x401E, false)]
    [TestCase(false, false, 0x3FC0, true)]
    [TestCase(true, false, 0x3FC0, true)]
    [TestCase(false, true, 0x401E, true)]
    [TestCase(true, true, 0x401E, true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP连续字段跨度按元数据布局写入读取并执行(bool isStatic, bool wide, int expected, bool nativeLiteral)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Fixture",
            $"VectorSpan{(isStatic ? "Static" : "Instance")}{(wide ? "Double" : "Single")}",
            app.SystemTypes.SystemObjectType, ReflectionTypeAttributes.Public);
        var analysisAttributes = System.Reflection.FieldAttributes.Public
                                 | (isStatic ? System.Reflection.FieldAttributes.Static : 0);
        var fieldType = wide ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
        var first = owner.InjectFieldContext("First", fieldType, analysisAttributes);
        var second = owner.InjectFieldContext("Second", fieldType, analysisAttributes);
        var startOffset = isStatic ? 0 : 16;
        var fieldBytes = wide ? 8 : 4;
        first.Offset = startOffset;
        second.Offset = startOffset + fieldBytes;

        var inputType = wide ? app.SystemTypes.SystemInt64Type : app.SystemTypes.SystemInt32Type;
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var input = new LocalVariable("value", new Register(null, wide ? "X1" : "W1"), inputType);
        var vector = new LocalVariable("vector", new Register(null, nativeLiteral ? "RAW_BITS" : "V0"));
        var loaded = new LocalVariable("loaded", new Register(null, "V1"));
        var result = new LocalVariable("result", new Register(null, "W2"), app.SystemTypes.SystemInt32Type);
        var reference = new FieldReference(first, receiver, startOffset);
        var laneCount = 2;
        var elementWidth = wide ? 64 : 32;
        var context = owner.InjectMethodContext("RoundTrip", app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, owner, inputType);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            nativeLiteral
                ? new Instruction(0, OpCode.Move, vector, new NativeBitPatternLiteral(
                    wide ? 0x401E000000000000UL : 0x3FC000013FC00000UL,
                    wide ? 0x401E000000000001UL : 0, laneCount * elementWidth))
                    { MemoryAccessWidthBits = laneCount * elementWidth }
                : new Instruction(0, OpCode.VectorDuplicate, vector, input,
                    new Immediate(laneCount), new Immediate(elementWidth), new Immediate(-1)),
            new Instruction(1, OpCode.Move, reference, vector) { MemoryAccessWidthBits = laneCount * elementWidth },
            new Instruction(2, OpCode.Move, loaded, reference) { MemoryAccessWidthBits = laneCount * elementWidth },
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, loaded,
                new Immediate(wide ? 7 : 3)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [receiver, input];
        context.Locals = [vector, loaded, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        if (wide)
            绑定AsmResolver系统类型(coreModule, inputType, "Int64",
                TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, fieldType, wide ? "Double" : "Single",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var assemblyName = owner.Name;
        var module = new ModuleDefinition(assemblyName + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", owner.Name, TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        var managedAttributes = FieldAttributes.Public | (isStatic ? FieldAttributes.Static : 0);
        var managedFieldType = wide ? module.CorLibTypeFactory.Double : module.CorLibTypeFactory.Single;
        var firstDefinition = new FieldDefinition("First", managedAttributes, managedFieldType);
        var secondDefinition = new FieldDefinition("Second", managedAttributes, managedFieldType);
        host.Fields.Add(firstDefinition);
        host.Fields.Add(secondDefinition);
        first.PutExtraData("AsmResolverField", firstDefinition);
        second.PutExtraData("AsmResolverField", secondDefinition);
        var inputSignature = wide ? module.CorLibTypeFactory.Int64 : module.CorLibTypeFactory.Int32;
        var definition = new MethodDefinition("RoundTrip", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [new TypeDefOrRefSignature(host, false), inputSignature]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "receiver", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        Assert.That(definition.CilMethodBody!.Instructions.Count(item => item.OpCode == CilOpCodes.Stfld
                                                                          || item.OpCode == CilOpCodes.Stsfld),
            Is.EqualTo(2));
        Assert.That(definition.CilMethodBody.Instructions.Count(item => item.OpCode == CilOpCodes.Ldfld
                                                                 || item.OpCode == CilOpCodes.Ldsfld),
            Is.EqualTo(2));
        var assembly = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeType = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Fixture." + owner.Name, true)!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeType);
        // 条件表达式会把Int32分支先提升为Int64；分别装箱以保持实际签名。
        object argument = wide
            ? (object)unchecked((long)0x401E000000000000UL)
            : unchecked((int)0x3FC00000);
        var actual = (int)runtimeType.GetMethod("RoundTrip")!.Invoke(null, [instance, argument])!;
        Assert.That(actual, Is.EqualTo(expected));
        foreach (var name in new[] { "First", "Second" })
        {
            var value = runtimeType.GetField(name)!.GetValue(isStatic ? null : instance)!;
            var bits = wide
                ? unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value))
                : unchecked((uint)BitConverter.SingleToInt32Bits((float)value));
            // 第二通道故意不同，实际 PE 执行必须保留读取的全部位，而不是重复首通道。
            var expectedBits = (wide ? 0x401E000000000000UL : 0x3FC00000UL)
                               + (nativeLiteral && name == "Second" ? 1UL : 0UL);
            Assert.That(bits, Is.EqualTo(expectedBits));
        }
    }

    [Test]
    [Category("边界值")]
    public void DUP连续字段中间字段跨六十四位边界仍保持全部位模式()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Fixture", "VectorCrossWordSpan",
            app.SystemTypes.SystemObjectType, ReflectionTypeAttributes.Public);
        var attributes = System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static;
        var first = owner.InjectFieldContext("First", app.SystemTypes.SystemInt32Type, attributes);
        var middle = owner.InjectFieldContext("Middle", app.SystemTypes.SystemInt64Type, attributes);
        var last = owner.InjectFieldContext("Last", app.SystemTypes.SystemInt32Type, attributes);
        first.Offset = 0;
        middle.Offset = 4;
        last.Offset = 12;
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var input = new LocalVariable("value", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var vector = new LocalVariable("vector", new Register(null, "V0"));
        var loaded = new LocalVariable("loaded", new Register(null, "V1"));
        var result = new LocalVariable("result", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var reference = new FieldReference(first, receiver, 0);
        var context = owner.InjectMethodContext("RoundTrip", app.SystemTypes.SystemInt32Type,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, app.SystemTypes.SystemInt32Type);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, vector, input,
                new Immediate(4), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.Move, reference, vector) { MemoryAccessWidthBits = 128 },
            new Instruction(2, OpCode.Move, loaded, reference) { MemoryAccessWidthBits = 128 },
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, loaded, new Immediate(7)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [input];
        context.Locals = [vector, loaded, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorCrossWordSpan.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", owner.Name, TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        foreach (var pair in new[]
                 {
                     (Context: first, Definition: new FieldDefinition("First", FieldAttributes.Public | FieldAttributes.Static,
                         module.CorLibTypeFactory.Int32)),
                     (Context: middle, Definition: new FieldDefinition("Middle", FieldAttributes.Public | FieldAttributes.Static,
                         module.CorLibTypeFactory.Int64)),
                     (Context: last, Definition: new FieldDefinition("Last", FieldAttributes.Public | FieldAttributes.Static,
                         module.CorLibTypeFactory.Int32)),
                 })
        {
            host.Fields.Add(pair.Definition);
            pair.Context.PutExtraData("AsmResolverField", pair.Definition);
        }
        var definition = new MethodDefinition("RoundTrip", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "value", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var assembly = new AssemblyDefinition("VectorCrossWordSpan", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtimeType = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Fixture." + owner.Name, true)!;
        var actual = (int)runtimeType.GetMethod("RoundTrip")!.Invoke(null, [unchecked((int)0x11223344)])!;
        Assert.That(actual, Is.EqualTo(0x1122));
        Assert.That(unchecked((ulong)(long)runtimeType.GetField("Middle")!.GetValue(null)!),
            Is.EqualTo(0x1122334411223344UL));
    }

    [TestCase("gap", "间隙")]
    [TestCase("overlap", "重叠")]
    [TestCase("managed", "托管引用字段重叠")]
    [TestCase("partial", "一部分")]
    [Category("异常输入")]
    public void DUP连续字段跨度拒绝间隙重叠托管引用和部分字段(string kind, string expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Fixture", "RejectedVectorSpan",
            app.SystemTypes.SystemObjectType, ReflectionTypeAttributes.Public);
        var attributes = System.Reflection.FieldAttributes.Public;
        var firstType = kind == "partial" ? app.SystemTypes.SystemInt64Type : app.SystemTypes.SystemSingleType;
        var first = owner.InjectFieldContext("First", firstType, attributes);
        var secondType = kind == "managed" ? app.SystemTypes.SystemStringType : app.SystemTypes.SystemSingleType;
        var second = owner.InjectFieldContext("Second", secondType, attributes);
        first.Offset = 16;
        second.Offset = kind switch
        {
            "gap" => 24,
            "overlap" => 18,
            _ => 20,
        };
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var width = kind == "partial" ? 4 : 8;
        var accepted = ManagedFieldSpanRecoveryHelper.TryDescribe(
            new FieldReference(first, receiver, 16), app.Binary.PointerSizeBytes, width,
            out _, out var failure);
        Assert.That(accepted, Is.False);
        Assert.That(failure, Does.Contain(expected));
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void DUP共享SIMD槽的标量除法与后继向量定义保持双视图并执行()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var left = new LocalVariable("left", new Register(null, "V1"), app.SystemTypes.SystemSingleType);
        var right = new LocalVariable("right", new Register(null, "V2"), app.SystemTypes.SystemSingleType);
        var scalar = new LocalVariable("scalar", new Register(null, "V0", 7), app.SystemTypes.SystemSingleType);
        var vector = new LocalVariable("vector", new Register(null, "V3"));
        var result = new LocalVariable("result", new Register(null, "W0"), app.SystemTypes.SystemInt32Type);
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "ScalarThenVector",
            app.SystemTypes.SystemInt32Type, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemSingleType, app.SystemTypes.SystemSingleType]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Divide, scalar, left, right),
            new Instruction(1, OpCode.VectorDuplicate, vector, scalar,
                new Immediate(4), new Immediate(32), new Immediate(0)),
            // 同一SSA身份在后继控制流重新作为向量目标，复现原始图中的标量/向量双重身份。
            new Instruction(2, OpCode.VectorDuplicate, scalar, scalar,
                new Immediate(4), new Immediate(32), new Immediate(0)),
            new Instruction(3, OpCode.VectorExtractUnsignedInt16, result, vector, new Immediate(5)),
            new Instruction(4, OpCode.Return, result),
        ]);
        context.ParameterLocals = [left, right];
        context.Locals = [scalar, vector, result];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemSingleType, "Single",
            TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt32Type, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("VectorScalarIdentity.dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("Fixture", "VectorScalarIdentityHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("ScalarThenVector", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Single, module.CorLibTypeFactory.Single]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "left", (ParameterAttributes)0));
        definition.ParameterDefinitions.Add(new ParameterDefinition(2, "right", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        Assert.That(definition.CilMethodBody!.Instructions.Count(item => item.OpCode == CilOpCodes.Div), Is.EqualTo(1));
        var assembly = new AssemblyDefinition("VectorScalarIdentity", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var actual = (int)System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.VectorScalarIdentityHost", true)!
            .GetMethod("ScalarThenVector")!.Invoke(null, [6f, 2f])!;
        Assert.That(actual, Is.EqualTo(0x4040));
    }

    [TestCase(2, 1, 3)]
    [TestCase(0, -1, -1)]
    [TestCase(0, int.MinValue, int.MinValue)]
    [Category("基本功能")]
    [Category("边界值")]
    public void V寄存器标量整型加法按FloatLiteral原始位模式生成并执行(
        int seed,
        int rawBits,
        int expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var integerType = app.SystemTypes.SystemInt32Type;
        var marker = new LocalVariable("marker", new Register(null, "V1"), integerType);
        var result = new LocalVariable("result", new Register(null, "V0"), integerType);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "ScalarIntegerWithVectorView",
            integerType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [integerType]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, marker, marker,
                new Immediate(2), new Immediate(32), new Immediate(-1)),
            new Instruction(1, OpCode.Add, result, marker,
                new FloatLiteral(BitConverter.Int32BitsToSingle(rawBits))),
            new Instruction(2, OpCode.Return, result),
        ]);
        context.ParameterLocals = [marker];
        context.Locals = [marker, result];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "ScalarIntegerVectorView.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemObjectType,
            "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(module, integerType, "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var host = new TypeDefinition("Fixture", "ScalarIntegerVectorViewHost",
            TypeAttributes.Class | TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition(
            "ScalarIntegerWithVectorView",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int32]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, "marker", (ParameterAttributes)0));
        host.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, context.FullName);
        var rawConstant = definition.CilMethodBody!.Instructions.Single(instruction =>
            instruction.OpCode == CilOpCodes.Ldc_I4
            && instruction.Operand is int value
            && value == rawBits);
        Assert.That(rawConstant, Is.Not.Null);

        using var stream = new System.IO.MemoryStream();
        var assembly = new AssemblyDefinition("ScalarIntegerVectorView", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        module.Write(stream);
        var runtimeMethod = System.Reflection.Assembly.Load(stream.ToArray())
            .GetType("Fixture.ScalarIntegerVectorViewHost", true)!
            .GetMethod("ScalarIntegerWithVectorView")!;
        var actual = (int)runtimeMethod.Invoke(null, [seed])!;
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    [Category("异常输入")]
    public void V寄存器整型旁路拒绝混合标量来源()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var integerType = app.SystemTypes.SystemInt32Type;
        var otherType = app.SystemTypes.SystemInt64Type;
        var marker = new LocalVariable("marker", new Register(null, "V1"), integerType);
        var other = new LocalVariable("other", new Register(null, "V2"), otherType);
        var result = new LocalVariable("result", new Register(null, "V0"), integerType);
        var instruction = new Instruction(2, OpCode.Add, result, marker, other);

        Assert.That(IlGenerator.IsProvenScalarIntegerBinary(instruction), Is.False);
    }

    [TestCase(-1, 2, 32, false)]
    [TestCase(4, 4, 32, false)]
    [TestCase(0, 3, 32, false)]
    [TestCase(0, 2, 64, false)]
    [TestCase(0, 2, 32, true)]
    [Category("异常输入")]
    public void VectorByElement拒绝错误布局和缺失来源身份(int lane, int count, int width, bool immediateSource)
    {
        var destination = new LocalVariable("target", new Register(null, "V0"));
        var left = new LocalVariable("left", new Register(null, "V1"));
        IOperand right = immediateSource ? new Immediate(1) : new LocalVariable("right", new Register(null, "V2"));
        var instruction = new Instruction(0, OpCode.VectorMultiplyByElement, destination, left, right,
            new Immediate(lane), new Immediate(count), new Immediate(width));
        Assert.That(VectorCilRecoveryHelper.TryDescribeFloatingBinary(instruction, out _, out var failure), Is.False);
        Assert.That(failure, Is.Not.Empty);
    }

    [TestCase(3, 32, -1, "目标总位宽")]
    [TestCase(2, 24, -1, "通道布局无效")]
    [TestCase(2, 64, 2, "来源通道超出")]
    [Category("异常输入")]
    public void DUP位载体拒绝不完整或越界布局(int laneCount, int elementWidthBits, int sourceLane, string expectedFailure)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var destination = new LocalVariable("vector", new Register(null, "V0"));
        IOperand source = sourceLane < 0
            ? new Immediate(1)
            : new LocalVariable("source", new Register(null, "V1"), app.SystemTypes.SystemInt64Type);
        var instruction = new Instruction(0, OpCode.VectorDuplicate, destination, source,
            new Immediate(laneCount), new Immediate(elementWidthBits), new Immediate(sourceLane));
        var accepted = VectorCilRecoveryHelper.TryDescribe(instruction, out _, out var failure);
        Assert.That(accepted, Is.False);
        Assert.That(failure, Does.Contain(expectedFailure));
    }

    [Test]
    [Category("异常输入")]
    public void DUP向量通道缺少SSA位模式时保持显式未解决()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var source = new LocalVariable("source", new Register(null, "V1"));
        var destination = new LocalVariable("destination", new Register(null, "V0"));
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "MissingLaneSource",
            app.SystemTypes.SystemVoidType, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.VectorDuplicate, destination, source,
                new Immediate(4), new Immediate(32), new Immediate(1)),
            new Instruction(1, OpCode.Return),
        ]);
        context.ParameterLocals = [];
        context.Locals = [source, destination];
        context.AnalysisWarnings = [];
        var module = new ModuleDefinition("MissingVectorLane.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        var host = new TypeDefinition("Fixture", "MissingVectorLaneHost",
            TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("MissingLaneSource", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        host.Methods.Add(definition);
        var error = Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain("VECTOR_LANE_SOURCE_STATE"));
    }

    [TestCase("managed_reference")]
    [TestCase("native_pointer")]
    [TestCase("unknown_layout")]
    [TestCase("nonzero")]
    [TestCase("indexed")]
    [TestCase("invalid_pointer_size")]
    [Category("异常输入")]
    public void 引用零块写拒绝类型或访问证据不足(string kind)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        TypeAnalysisContext type = app.SystemTypes.SystemInt64Type.MakeByReferenceType();
        if (kind == "managed_reference")
            type = app.SystemTypes.SystemStringType.MakeByReferenceType();
        if (kind == "native_pointer")
            type = new PointerTypeAnalysisContext(app.SystemTypes.SystemInt64Type);
        if (kind == "unknown_layout")
            type = new InjectedTypeAnalysisContext(app.Assemblies[0], "Fixture", "UnknownLayout",
                app.SystemTypes.SystemValueTypeType, ReflectionTypeAttributes.Public).MakeByReferenceType();
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), type);
        var memory = new MemoryOperand(receiver);
        if (kind == "indexed")
            memory = new MemoryOperand(receiver, new Immediate(1));
        var write = new Instruction(0, OpCode.Move, memory, new Immediate(kind == "nonzero" ? 1 : 0))
            { MemoryAccessWidthBits = 32 };
        Assert.That(ByRefZeroBlockRecovery.TryDescribe(write, kind == "invalid_pointer_size" ? 0 : app.Binary.PointerSizeBytes, out _), Is.False);
    }
}
