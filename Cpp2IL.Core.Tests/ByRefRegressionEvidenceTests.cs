using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ByRefRegressionEvidenceTests
{
    [Test]
    [Category("fixture集成")]
    public void 全量回退样本保留原始身份及即时数引用分析图()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_BYREF_REGRESSION_INPUT");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("本轮未指定全量回退账本。");
        using var document = JsonDocument.Parse(File.ReadAllText(input!));
        Assert.That(document.RootElement.GetProperty("schema").GetString(),
            Is.EqualTo("FullRecoveryEmissionRegressionProjection/v1"));
        var samples = document.RootElement.GetProperty("methods").EnumerateArray()
            .Where(row => row.GetProperty("operation").GetString() == "IMMEDIATE_MANAGED_BYREF").ToArray();
        Assert.That(samples, Is.Not.Empty);
        var root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        var output = Path.Combine(root!, "byref-regression-graphs");
        Directory.CreateDirectory(output);
        var app = Arm64StartupFixtureIntegrationTests.LoadFixture();
        // 身份仅用于找到全量回归样本；不进入任何生产恢复规则。
        var methods = app.Assemblies.SelectMany(assembly => assembly.Types.SelectMany(type => type.Methods))
            .Where(method => method.Definition != null)
            .ToLookup(method => (method.DeclaringType!.DeclaringAssembly.Name, method.Definition!.token));
        foreach (var row in samples)
        {
            var assembly = row.GetProperty("assembly").GetString()!;
            var token = row.GetProperty("originalToken").GetUInt32();
            var method = methods[(assembly, token)].Single();
            Assert.That(method.UnderlyingPointer, Is.EqualTo(row.GetProperty("nativeAddress").GetUInt64()));
            method.Analyze();
            Assert.That(method.ControlFlowGraph, Is.Not.Null);
            var instructions = method.ControlFlowGraph!.Instructions.Select(instruction => new
            {
                instruction.Index, opcode = instruction.OpCode.ToString(), instruction.MemoryAccessWidthBits,
                text = instruction.ToString(),
                operands = instruction.Operands.Select(operand => new
                {
                    kind = operand.GetType().Name, text = operand.ToString(),
                    type = operand switch
                    {
                        LocalVariable local => local.Type?.FullName,
                        MemoryOperand { Base: LocalVariable local } => local.Type?.FullName,
                        FieldReference field => field.Field.FieldType.FullName,
                        _ => null
                    }
                }).ToArray()
            }).ToArray();
            using var stream = File.Create(Path.Combine(output, $"{assembly}-{token:X8}.json"));
            JsonSerializer.Serialize(stream, new
            {
                schema = "OriginalByRefRegressionGraph/v1", assembly, token, address = method.UnderlyingPointer,
                method = method.FullName, parameters = method.ParameterLocals.Select(local => local.ToString()).ToArray(),
                warnings = method.AnalysisWarnings.ToArray(), instructions,
                recoveryProved = false
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            count = samples.Length, inputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input!))).ToLowerInvariant(),
            originalGraphsCaptured = true, causeVerified = false, fullRecoveryProved = false
        }));
    }
}
