using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
        public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context) => [];
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

}
