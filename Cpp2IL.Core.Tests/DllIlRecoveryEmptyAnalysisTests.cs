using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;

namespace Cpp2IL.Core.Tests;

public class DllIlRecoveryEmptyAnalysisTests
{
    private sealed class OutputProbe : AsmResolverDllOutputFormatIlRecovery
    {
        public int Successful => RecoveredCilCount;
        public int Total => AttemptedBodyCount;
        public void Fill(MethodDefinition definition, MethodAnalysisContext context) => FillMethodBody(definition, context);
        public void Receipt(string path) => WriteOutputReceipts(path);
        public Exception? BuildFailure;
        public Action? DuringBuild;
        public TimeSpan CheckpointInterval = TimeSpan.FromSeconds(60);
        protected override TimeSpan ProgressCheckpointInterval => CheckpointInterval;
        public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
        {
            DuringBuild?.Invoke();
            if (BuildFailure != null)
                throw BuildFailure;
            return [];
        }
    }

    private sealed class MethodProbe(TypeAnalysisContext owner, TypeAnalysisContext result, string name, ulong address)
        : InjectedMethodAnalysisContext(owner, name, result,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [])
    {
        public override ulong UnderlyingPointer => address;
    }

    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(true)]
    [TestCase(false)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 方法处理中按间隔发布同一账本且计数一致(bool due)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-Progress-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dll-il-recovery-method-ledger.json");
        var output = new OutputProbe { CheckpointInterval = due ? TimeSpan.Zero : TimeSpan.MaxValue };
        output.DuringBuild = () =>
        {
            output.Receipt(directory);
            var (context, definition) = Fixture("ProgressReturn", 0x1000);
            context.ConvertedIsil = [new Instruction(0, OpCode.Return)];
            context.ControlFlowGraph = new ISILControlFlowGraph(context.ConvertedIsil);
            output.Fill(definition, context);
            using var receipt = JsonDocument.Parse(File.ReadAllText(path));
            var root = receipt.RootElement;
            var expected = due ? 1 : 0;
            Assert.That(root.GetProperty("methods").GetArrayLength(), Is.EqualTo(expected));
            Assert.That(root.GetProperty("totalMethodsWithBody").GetInt32(), Is.EqualTo(expected));
            Assert.That(root.GetProperty("successfulMethods").GetInt32(), Is.EqualTo(expected));
            Assert.That(root.GetProperty("validatedMethods").GetInt32(), Is.EqualTo(expected));
            Assert.That(File.Exists(path + ".pending"), Is.False);
        };
        try
        {
            // 探针没有原始分母，最终发布仍应失败；断言在构造回调内部验证处理中状态。
            Assert.Throws<InvalidOperationException>(() => output.DoOutput(null!, directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 处理中写盘失败保留上一账本且不重复提交方法结果()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-ProgressFailure-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dll-il-recovery-method-ledger.json");
        var output = new OutputProbe { CheckpointInterval = TimeSpan.Zero };
        FileStream? heldFile = null;
        byte[] previous = [];
        output.DuringBuild = () =>
        {
            output.Receipt(directory);
            previous = File.ReadAllBytes(path);
            heldFile = new FileStream(path + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var (context, definition) = Fixture("ProgressFailure", 0x2000);
            definition.Attributes |= MethodAttributes.Abstract;
            output.Fill(definition, context);
        };
        try
        {
            var error = Assert.Throws<AggregateException>(() => output.DoOutput(null!, directory));
            Assert.That(error!.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(error.ToString(), Does.Not.Contain("方法恢复结果重复提交"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(previous));
            Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
        }
        finally
        {
            heldFile?.Dispose();
            File.Delete(path + ".pending");
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Test]
    [Category("边界值")]
    public void 并行方法检查点共用单写者且最终快照不丢记录()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-ProgressParallel-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dll-il-recovery-method-ledger.json");
        var output = new OutputProbe { CheckpointInterval = TimeSpan.Zero };
        var fixtures = Enumerable.Range(0, 32).Select(index => Fixture("Declaration" + index, (ulong)(0x3000 + index * 4))).ToArray();
        foreach (var fixture in fixtures)
            fixture.Definition.Attributes |= MethodAttributes.Abstract;
        output.DuringBuild = () =>
        {
            output.Receipt(directory);
            System.Threading.Tasks.Parallel.ForEach(fixtures, fixture => output.Fill(fixture.Definition, fixture.Context));
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(path));
            var rows = receipt.RootElement.GetProperty("methods").EnumerateArray().ToArray();
            Assert.That(rows, Has.Length.EqualTo(fixtures.Length));
            Assert.That(rows.All(row => row.GetProperty("outcome").GetString() == "DECLARATION"), Is.True);
            Assert.That(receipt.RootElement.GetProperty("totalMethodsWithBody").GetInt32(), Is.Zero);
            Assert.That(File.Exists(path + ".pending"), Is.False);
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => output.DoOutput(null!, directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static (MethodProbe Context, MethodDefinition Definition) Fixture(string name, ulong address, string moduleName = "EvidenceFixture.dll")
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = new MethodProbe(app.SystemTypes.SystemObjectType, app.SystemTypes.SystemVoidType, name, address);
        var module = new ModuleDefinition(moduleName, new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var type = new TypeDefinition("Fixture", "Evidence", TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(method);
        return (context, method);
    }

    [TestCase("ZeroEntry", 0UL)]
    [TestCase("FirstBody", 4096UL)]
    [TestCase("OtherBody", 8192UL)]
    [Category("异常输入")]
    public void 空分析不进入成功数并保留独立故障类别(string name, ulong address)
    {
        var (context, definition) = Fixture(name, address);
        context.ConvertedIsil = [];
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-EmptyAnalysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            var root = receipt.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(output.Total, Is.EqualTo(1));
                Assert.That(output.Successful, Is.Zero);
                Assert.That(root.GetProperty("validatedMethods").GetInt32(), Is.Zero);
                Assert.That(root.GetProperty("failedMethods").GetInt32(), Is.EqualTo(1));
                Assert.That(root.GetProperty("failures")[0].GetProperty("category").GetString(), Is.EqualTo("EMPTY_ANALYSIS"));
                Assert.That(root.GetProperty("sourceRecoveryAccepted").GetBoolean(), Is.False);
                Assert.That(root.GetProperty("formalPublishEligible").GetBoolean(), Is.False);
                // 缺失原生实现时只保留账本，不生成诊断业务体。
                Assert.That(definition.CilMethodBody, Is.Null);
                Assert.That(context.ConvertedIsil, Is.Null);
            });
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }

    [TestCase("EvidenceFixture.dll")]
    [TestCase("UnityEngine.Fixture.dll")]
    [TestCase("Unity.Fixture.dll")]
    [TestCase("System.Fixture.dll")]
    [TestCase("mscorlib.dll")]
    [Category("基本功能")]
    [Category("边界值")]
    public void 有明确返回指令的最短方法继续走真实生成路径(string moduleName)
    {
        var (context, definition) = Fixture("ExplicitReturn", 12288, moduleName);
        context.ConvertedIsil = [new Instruction(0, OpCode.Return)];
        context.ControlFlowGraph = new ISILControlFlowGraph(context.ConvertedIsil);
        var output = new OutputProbe();
        output.Fill(definition, context);
        Assert.Multiple(() =>
        {
            Assert.That(output.Successful, Is.EqualTo(1));
            Assert.That(definition.CilMethodBody!.Instructions.Last().OpCode, Is.EqualTo(CilOpCodes.Ret));
            Assert.That(context.ConvertedIsil, Is.Null);
        });
    }
    [Test]
    [Category("异常输入")]
    public void 缺失原始分母时在任何DLL写盘前拒绝输出并保存账本()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-Publication-" + Guid.NewGuid().ToString("N"));
        var output = new OutputProbe();
        try
        {
            Assert.Throws<InvalidOperationException>(() => output.DoOutput(null!, directory));
            Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.That(receipt.RootElement.GetProperty("originalDenominatorValidated").GetBoolean(), Is.False);
            Assert.That(receipt.RootElement.GetProperty("schema").GetString(), Is.EqualTo("Cpp2IL.DllIlRecovery.MethodLedger/v2"));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 全部原始方法含零入口进入同一账本且重复初始化逐字节一致()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var output = new OutputProbe();
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-Ledger-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dll-il-recovery-method-ledger.json");
        try
        {
            output.InitializeOriginalMethodLedger(app);
            output.Receipt(directory);
            var first = File.ReadAllText(path);
            using var receipt = JsonDocument.Parse(first);
            var root = receipt.RootElement;
            var rows = root.GetProperty("methods").EnumerateArray().ToArray();
            var expectedZero = app.Assemblies.SelectMany(a => a.Types).SelectMany(t => t.Methods)
                .Count(m => m.Definition != null && m.UnderlyingPointer == 0);
            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("originalDenominatorValidated").GetBoolean(), Is.True);
                Assert.That(rows.Length, Is.EqualTo(app.Metadata.MethodDefinitionCount));
                Assert.That(rows.All(row => row.GetProperty("outcome").GetString() == "PENDING"), Is.True);
                Assert.That(rows.Count(row => row.GetProperty("nativeAddress").GetUInt64() == 0), Is.EqualTo(expectedZero));
                Assert.That(root.GetProperty("successfulMethods").GetInt32(), Is.Zero);
            });
            output.InitializeOriginalMethodLedger(app);
            output.Receipt(directory);
            Assert.That(File.ReadAllText(path), Is.EqualTo(first));
            Assert.Throws<InvalidOperationException>(() => output.DoOutput(app, directory));
            Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Test]
    [Category("基本功能")]
    [Category("异常输入")]
    public void 账本绑定已加载字节而非输出时的可变路径()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var binaryDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Paths.Simple2019Game.GameAssembly))).ToLowerInvariant();
        var metadataDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Paths.Simple2019Game.Metadata))).ToLowerInvariant();
        var oldOptions = Cpp2IlApi.RuntimeOptions;
        try
        {
            Cpp2IlApi.RuntimeOptions = new Cpp2IlRuntimeArgs { PathToAssembly = "不存在的路径", PathToMetadata = "不存在的路径" };
            Assert.Multiple(() =>
            {
                Assert.That(app.LibCpp2IlContext.InputBinarySha256, Is.EqualTo(binaryDigest));
                Assert.That(app.LibCpp2IlContext.InputMetadataSha256, Is.EqualTo(metadataDigest));
                Assert.That(app.LibCpp2IlContext.EffectiveMetadataSha256, Is.EqualTo(metadataDigest));
            });
            var output = new OutputProbe();
            output.InitializeOriginalMethodLedger(app);
        }
        finally
        {
            Cpp2IlApi.RuntimeOptions = oldOptions;
        }
    }

    [Test]
    [Category("全目标集成")]
    public void 冻结目标全部方法账本保留输入身份且两次逐字节一致()
    {
        var root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        var binary = Environment.GetEnvironmentVariable("CPP2IL_ARM64_FIXTURE_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_ARM64_FIXTURE_METADATA");
        var unity = Environment.GetEnvironmentVariable("CPP2IL_ARM64_FIXTURE_UNITY_VERSION");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(binary) ||
            string.IsNullOrWhiteSpace(metadata) || string.IsNullOrWhiteSpace(unity))
            Assert.Ignore("全目标账本回归需要冻结输入路径、版本与独立证据目录。");
        var firstRoot = Path.Combine(root!, "first");
        var secondRoot = Path.Combine(root!, "second");
        Assert.That(Directory.Exists(firstRoot) || Directory.Exists(secondRoot), Is.False, "证据目录已有产物，禁止覆盖。");
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse(unity!));
        var app = Cpp2IlApi.CurrentAppContext!;
        var output = new OutputProbe();
        output.InitializeOriginalMethodLedger(app);
        output.Receipt(firstRoot);
        output.InitializeOriginalMethodLedger(app);
        output.Receipt(secondRoot);
        var firstPath = Path.Combine(firstRoot, "dll-il-recovery-method-ledger.json");
        var secondPath = Path.Combine(secondRoot, "dll-il-recovery-method-ledger.json");
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        Assert.That(SHA256.HashData(first), Is.EqualTo(SHA256.HashData(second)));
        using var document = JsonDocument.Parse(File.ReadAllText(firstPath));
        var rows = document.RootElement.GetProperty("methods").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(rows.Length, Is.EqualTo(app.Metadata.MethodDefinitionCount));
            Assert.That(rows.All(row => row.GetProperty("outcome").GetString() == "PENDING"), Is.True);
            Assert.That(rows.Select(row => (row.GetProperty("assembly").GetString(), row.GetProperty("originalToken").GetUInt32())).Distinct().Count(), Is.EqualTo(rows.Length));
            Assert.That(document.RootElement.GetProperty("sourceRecoveryAccepted").GetBoolean(), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 程序集构造异常保存同一账本并保留原异常()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-BuildFailure-" + Guid.NewGuid().ToString("N"));
        var failure = new InvalidOperationException("测试构造失败");
        var output = new OutputProbe { BuildFailure = failure };
        try
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() => output.DoOutput(null!, directory)), Is.SameAs(failure));
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.That(receipt.RootElement.GetProperty("pipelineFailure").GetString(), Does.Contain("测试构造失败"));
            Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
            Assert.That(Directory.GetFiles(directory, "*.pending"), Is.Empty);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }

    [Test]
    [Category("边界值")]
    public void 已有输出目录不被新恢复覆盖()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-ExistingOutput-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "existing.txt");
        File.WriteAllText(path, "原有产物");
        try
        {
            Assert.Throws<InvalidOperationException>(() => new OutputProbe().DoOutput(null!, directory));
            Assert.That(File.ReadAllText(path), Is.EqualTo("原有产物"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [TestCase(OpCode.Invalid)]
    [TestCase(OpCode.NotImplemented)]
    [TestCase(OpCode.Phi)]
    [TestCase(OpCode.IndirectJump)]
    [TestCase(OpCode.IndirectCall)]
    [TestCase(OpCode.ShiftStack)]
    [TestCase(OpCode.Interrupt)]
    [TestCase(OpCode.Call)]
    [TestCase(OpCode.CallVoid)]
    [TestCase(OpCode.Move)]
    [Category("异常输入")]
    public void 未解决语义不生成诊断CIL或计入成功(OpCode operation)
    {
        var (context, definition) = Fixture("UnresolvedOperation", 16384);
        var instruction = new Instruction(0, operation);
        if (operation is OpCode.Call or OpCode.CallVoid)
            instruction = new Instruction(0, operation, new Immediate(20480));
        if (operation == OpCode.Move)
        {
            // 固定夹具也须绑定实际发射器需要的类型定义，避免提前失败于局部表构造。
            var integerType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type;
            var integerDefinition = new TypeDefinition("System", "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
            definition.DeclaringModule!.TopLevelTypes.Add(integerDefinition);
            integerType.PutExtraData("AsmResolverType", integerDefinition);
            var local = new LocalVariable("loaded", new Register(null, "X0"), integerType);
            context.Locals = [local];
            instruction = new Instruction(0, operation, local, new MemoryOperand(addend: 24576));
        }
        var end = new Instruction(1, OpCode.Return);
        context.ConvertedIsil = [instruction, end];
        // 单独固定有效结构图，只让本测试的未知语义进入实际发射器。
        context.ControlFlowGraph = new ISILControlFlowGraph([end]);
        context.ControlFlowGraph.Blocks.Single(block => block.Instructions.Contains(end)).Instructions.Insert(0, instruction);
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-Unresolved-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.Multiple(() =>
            {
                Assert.That(output.Successful, Is.Zero);
                Assert.That(definition.CilMethodBody, Is.Null);
                Assert.That(receipt.RootElement.GetProperty("methods")[0].GetProperty("outcome").GetString(), Is.EqualTo("UNRESOLVED"));
                Assert.That(receipt.RootElement.GetProperty("failures")[0].GetProperty("category").GetString(), Is.EqualTo("UNRESOLVED_CIL_SEMANTICS"), receipt.RootElement.GetProperty("failures")[0].GetProperty("detail").GetString());
            });
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }

    [TestCase(true, 0, 0, true)]
    [TestCase(true, 1, 1, true)]
    [TestCase(true, 2, 1, false)]
    [TestCase(true, 1, 0, false)]
    [TestCase(false, 0, 0, false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 调用缺失实参或接收者不合成默认值(bool isStatic, int required, int supplied, bool accepted)
    {
        var (context, definition) = Fixture("CallArgumentEvidence", 28672);
        var app = Cpp2IlApi.CurrentAppContext!;
        var module = definition.DeclaringModule!;
        var integer = new TypeDefinition("System", "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        module.TopLevelTypes.Add(integer);
        app.SystemTypes.SystemInt32Type.PutExtraData("AsmResolverType", integer);
        var target = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "Consume",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | (isStatic ? System.Reflection.MethodAttributes.Static : 0),
            Enumerable.Repeat(app.SystemTypes.SystemInt32Type, required).ToArray());
        var parameters = Enumerable.Repeat<TypeSignature>(module.CorLibTypeFactory.Int32, required).ToArray();
        var signature = isStatic ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, parameters)
            : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, parameters);
        var targetDefinition = new MethodDefinition("Consume", MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0), signature);
        definition.DeclaringType!.Methods.Add(targetDefinition);
        target.PutExtraData("AsmResolverMethod", targetDefinition);
        var operands = new List<IOperand> { target };
        for (var i = 0; i < supplied; i++)
            operands.Add(new Immediate(i + 7));
        var call = new Instruction(0, OpCode.CallVoid, operands);
        var end = new Instruction(1, OpCode.Return);
        context.ConvertedIsil = [call, end];
        context.ControlFlowGraph = new ISILControlFlowGraph(context.ConvertedIsil);
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-CallArguments-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.That(output.Successful, Is.EqualTo(accepted ? 1 : 0), receipt.RootElement.ToString());
            if (accepted)
                Assert.That(definition.CilMethodBody!.Instructions.Count(i => i.OpCode == CilOpCodes.Call), Is.EqualTo(1));
            else
            {
                Assert.That(definition.CilMethodBody, Is.Null);
                var failure = receipt.RootElement.GetProperty("failures")[0];
                Assert.That(failure.GetProperty("category").GetString(), Is.EqualTo("UNRESOLVED_CIL_SEMANTICS"), failure.ToString());
                Assert.That(failure.GetProperty("detail").GetString(), Does.Contain(isStatic ? "CALL_ARGUMENTS" : "CALL_RECEIVER"));
            }
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }
    private sealed class UnknownOperand : IOperand
    {
        public override string ToString() => "未知夹具操作数";
    }

    [TestCase("local_load", "MEMORY_LOAD")]
    [TestCase("empty_throw", "THROW_VALUE")]
    [TestCase("type_throw", "THROW_CONSTRUCTOR")]
    [TestCase("type", "TYPE_OPERAND")]
    [TestCase("array", "ARRAY_ALLOCATION")]
    [TestCase("object", "OBJECT_ALLOCATION")]
    [TestCase("empty_object", "OBJECT_ALLOCATION")]
    [TestCase("opcode", "UNKNOWN_OPCODE")]
    [TestCase("return", "RETURN_VALUE")]
    [TestCase("load", "UNKNOWN_LOAD")]
    [TestCase("store", "UNKNOWN_STORE")]
    [TestCase("absolute_store", "MEMORY_STORE")]
    [TestCase("local_store", "MEMORY_STORE")]
    [Category("异常输入")]
    [Category("边界值")]
    public void 未知读写和缺失返回不伪装为成功(string shape, string operation)
    {
        var (context, definition) = Fixture("UnresolvedDataFlow", 32768);
        var app = Cpp2IlApi.CurrentAppContext!;
        var module = definition.DeclaringModule!;
        var integer = new TypeDefinition("System", "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        module.TopLevelTypes.Add(integer);
        app.SystemTypes.SystemInt32Type.PutExtraData("AsmResolverType", integer);
        var local = new LocalVariable("value", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        context.Locals = [local];
        var instruction = shape switch
        {
            "local_load" => new Instruction(0, OpCode.Move, local, new MemoryOperand(local)),
            "empty_throw" => new Instruction(0, OpCode.Throw),
            "type_throw" => new Instruction(0, OpCode.Throw, app.SystemTypes.SystemVoidType),
            "type" => new Instruction(0, OpCode.Move, local, app.SystemTypes.SystemObjectType),
            "array" => new Instruction(0, OpCode.NewArr, local),
            "object" => new Instruction(0, OpCode.Newobj, local),
            "empty_object" => new Instruction(0, OpCode.Newobj),
            "opcode" => new Instruction(0, (OpCode)int.MaxValue),
            "return" => new Instruction(0, OpCode.Return),
            "load" => new Instruction(0, OpCode.Move, local, new UnknownOperand()),
            "store" => new Instruction(0, OpCode.Move, new UnknownOperand(), new Immediate(7)),
            "absolute_store" => new Instruction(0, OpCode.Move, new MemoryOperand(addend: 40960), new Immediate(7)),
            "local_store" => new Instruction(0, OpCode.Move, new MemoryOperand(baseRegister: local), new Immediate(7)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        if (shape == "return")
        {
            context = new MethodProbe(app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt32Type, "MissingReturn", 36864);
            definition.Signature = MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32);
        }
        var end = new Instruction(1, OpCode.Return);
        context.ConvertedIsil = [instruction, end];
        context.ControlFlowGraph = new ISILControlFlowGraph([end]);
        context.ControlFlowGraph.Blocks.Single(block => block.Instructions.Contains(end)).Instructions.Insert(0, instruction);
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-UnresolvedData-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.That(output.Successful, Is.Zero, receipt.RootElement.ToString());
            Assert.That(definition.CilMethodBody, Is.Null);
            var failure = receipt.RootElement.GetProperty("failures")[0];
            Assert.That(failure.GetProperty("category").GetString(), Is.EqualTo("UNRESOLVED_CIL_SEMANTICS"), failure.ToString());
            Assert.That(failure.GetProperty("detail").GetString(), Does.Contain(operation));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(0, true)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 分析警告与丢失桥接目标不注入业务诊断(int warningCount, bool unresolvedBridge)
    {
        var (context, definition) = Fixture("DiagnosticEvidence", 45056);
        context.AnalysisWarnings = Enumerable.Range(0, warningCount).Select(i => "未闭合分析证据" + i).ToList();
        var terminal = new Instruction(0, unresolvedBridge ? OpCode.Nop : OpCode.Return);
        context.ConvertedIsil = [terminal];
        var entry = new Block { ID = 0, BlockType = BlockType.Entry };
        var exit = new Block { ID = 1, BlockType = BlockType.Exit };
        var body = new Block { ID = 2, BlockType = BlockType.OneWay, Instructions = [terminal] };
        var empty = new Block { ID = 3, BlockType = BlockType.OneWay };
        entry.Successors.Add(body);
        body.Predecessors.Add(entry);
        var next = unresolvedBridge ? empty : exit;
        body.Successors.Add(next);
        next.Predecessors.Add(body);
        if (unresolvedBridge)
        {
            empty.Successors.Add(exit);
            exit.Predecessors.Add(empty);
        }
        context.ControlFlowGraph = new ISILControlFlowGraph([])
        {
            EntryBlock = entry, ExitBlock = exit, Blocks = [entry, body, empty, exit]
        };
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-Diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            if (warningCount == 0 && !unresolvedBridge)
            {
                Assert.That(output.Successful, Is.EqualTo(1), receipt.RootElement.ToString());
                Assert.That(definition.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Ret }));
            }
            else
            {
                Assert.That(output.Successful, Is.Zero);
                Assert.That(definition.CilMethodBody, Is.Null);
                var failure = receipt.RootElement.GetProperty("failures")[0];
                Assert.That(failure.GetProperty("category").GetString(), Is.EqualTo("UNRESOLVED_CIL_SEMANTICS"), failure.ToString());
                var detail = failure.GetProperty("detail").GetString();
                Assert.That(detail, Does.Contain(unresolvedBridge ? "BRANCH_TARGET" : "ANALYSIS_WARNINGS"));
                for (var i = 0; i < warningCount; i++)
                    Assert.That(detail, Does.Contain("未闭合分析证据" + i));
            }
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }
    [TestCase(true)]
    [TestCase(false)]
    [Category("基本功能")]
    [Category("异常输入")]
    public void 方法句柄只在已支持的方法指针目标发射Ldftn(bool pointerTarget)
    {
        var (context, definition) = Fixture("MethodHandleEvidence", 49152);
        var app = Cpp2IlApi.CurrentAppContext!;
        var module = definition.DeclaringModule!;
        var slotType = pointerTarget ? app.SystemTypes.SystemIntPtrType : app.SystemTypes.SystemInt32Type;
        var slotDefinition = new TypeDefinition("System", pointerTarget ? "IntPtr" : "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        module.TopLevelTypes.Add(slotDefinition);
        slotType.PutExtraData("AsmResolverType", slotDefinition);
        context.PutExtraData("AsmResolverMethod", definition);
        var local = new LocalVariable("pointer", new Register(null, "X1"), slotType);
        var methodHandle = new RuntimeMethodInfoAnalysisContext(context, context.DeclaringType!.DeclaringAssembly);
        var move = new Instruction(0, OpCode.Move, local, methodHandle);
        var end = new Instruction(1, OpCode.Return);
        context.Locals = [local];
        context.ConvertedIsil = [move, end];
        context.ControlFlowGraph = new ISILControlFlowGraph(context.ConvertedIsil);
        var output = new OutputProbe();
        output.Fill(definition, context);
        var directory = Path.Combine(Path.GetTempPath(), "Cpp2IL-MethodHandle-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.Receipt(directory);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            Assert.That(output.Successful, Is.EqualTo(pointerTarget ? 1 : 0), receipt.RootElement.ToString());
            if (pointerTarget)
            {
                var instructions = definition.CilMethodBody!.Instructions;
                Assert.That(instructions.Count(i => i.OpCode == CilOpCodes.Ldftn), Is.EqualTo(1));
                Assert.That(instructions.Single(i => i.OpCode == CilOpCodes.Ldftn).Operand, Is.SameAs(definition));
                Assert.That(instructions.Any(i => i.OpCode == CilOpCodes.Ldc_I4_0 || i.OpCode == CilOpCodes.Ldnull), Is.False);
            }
            else
            {
                Assert.That(definition.CilMethodBody, Is.Null);
                var failure = receipt.RootElement.GetProperty("failures")[0];
                Assert.That(failure.GetProperty("category").GetString(), Is.EqualTo("UNRESOLVED_CIL_SEMANTICS"), failure.ToString());
                Assert.That(failure.GetProperty("detail").GetString(), Does.Contain("RUNTIME_METHOD_HANDLE"));
            }
        }
        finally
        {
            File.Delete(Path.Combine(directory, "dll-il-recovery-method-ledger.json"));
            Directory.Delete(directory);
        }
    }
    [Test]
    [Category("基本功能")]
    public void 原始显式抛出空值仍生成真实Throw()
    {
        var (context, definition) = Fixture("ExplicitThrowNull", 53248);
        context.ConvertedIsil = [new Instruction(0, OpCode.Throw, new Immediate(0))];
        context.ControlFlowGraph = new ISILControlFlowGraph(context.ConvertedIsil);
        var output = new OutputProbe();
        output.Fill(definition, context);
        Assert.That(output.Successful, Is.EqualTo(1));
        Assert.That(definition.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Ldnull, CilOpCodes.Throw }));
    }
}
