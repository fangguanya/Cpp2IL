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
        public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
        {
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

}
