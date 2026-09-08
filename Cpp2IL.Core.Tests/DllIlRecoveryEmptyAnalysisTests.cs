using System;
using System.IO;
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
        public int Successful => SuccessfulMethodCount;
        public int Total => TotalMethodCount;
        public void Fill(MethodDefinition definition, MethodAnalysisContext context) => FillMethodBody(definition, context);
        public void Receipt(string path) => WriteOutputReceipts(path);
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

    private static (MethodProbe Context, MethodDefinition Definition) Fixture(string name, ulong address)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = new MethodProbe(app.SystemTypes.SystemObjectType, app.SystemTypes.SystemVoidType, name, address);
        var module = new ModuleDefinition("EvidenceFixture.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
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
                // 旧诊断产物保留供分析，绝不把它视作业务实现或原版 throw。
                Assert.That(definition.CilMethodBody!.Instructions.Last().OpCode, Is.EqualTo(CilOpCodes.Throw));
                Assert.That(context.ConvertedIsil, Is.Null);
            });
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
    public void 有明确返回指令的最短方法继续走真实生成路径()
    {
        var (context, definition) = Fixture("ExplicitReturn", 12288);
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
}
