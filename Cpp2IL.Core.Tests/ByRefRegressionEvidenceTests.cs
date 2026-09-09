using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ByRefRegressionEvidenceTests
{
    [TearDown]
    public void 释放原始全声明图并隔离后续夹具()
    {
        // 全声明发射会持有核心库及原始类型图；测试结束先清除静态根，再回收，保持既定内存上限。
        Cpp2IlApi.ResetInternalState();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

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
        if (Environment.GetEnvironmentVariable("CPP2IL_BYREF_REGRESSION_EMIT") == "1")
        {
            var selected = samples.Select(row => methods[(row.GetProperty("assembly").GetString()!,
                row.GetProperty("originalToken").GetUInt32())].Single()).ToArray();
            File.WriteAllText(Path.Combine(root!, "original-parameter-abi.json"), JsonSerializer.Serialize(selected.Select(method => new
            {
                method = method.FullNameWithSignature, address = method.UnderlyingPointer,
                operands = Arm64CallingConventionResolver.ArgumentOperands(method).Select(operand => operand.ToString()).ToArray(),
                parameters = method.Parameters.Select(parameter => new
                {
                    name = parameter.ParameterName, layout = DescribeType(parameter.ParameterType, 0)
                }).ToArray()
            }), new JsonSerializerOptions { WriteIndented = true }));
            var options = Cpp2IlApi.RuntimeOptions!;
            options.IsilDumpAssemblyFilters = selected.Select(method => method.DeclaringType!.DeclaringAssembly.Name).Distinct().ToArray();
            options.IsilDumpTypeFilters = selected.Select(method => method.DeclaringType!.Definition!.FullName ?? throw new InvalidDataException("原始类型名称缺失。")).Distinct().ToArray();
            options.IsilDumpMethodFilters = selected.Select(method => method.Definition!.HumanReadableSignature ?? throw new InvalidDataException("原始方法签名缺失。")).Distinct().ToArray();
            var directory = Path.Combine(root!, "byref-original-emission");
            // 使用同一生产分析与发射流程，仅以回归输入界定测试范围；不发布局部程序集。
            new RegressionEmitter().BuildWithReceipt(app, directory);
            using var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "dll-il-recovery-method-ledger.json")));
            var rows = ledger.RootElement.GetProperty("methods").EnumerateArray()
                .Where(row => row.GetProperty("original").GetBoolean() && row.GetProperty("selected").GetBoolean()).ToArray();
            Assert.That(rows.Length, Is.EqualTo(samples.Length));
            foreach (var sample in samples)
            {
                var row = rows.Single(row => row.GetProperty("assembly").GetString() == sample.GetProperty("assembly").GetString()
                    && row.GetProperty("originalToken").GetUInt32() == sample.GetProperty("originalToken").GetUInt32());
                Assert.That(row.GetProperty("nativeAddress").GetUInt64(), Is.EqualTo(sample.GetProperty("nativeAddress").GetUInt64()));
                Assert.That(row.GetProperty("outcome").GetString(), Is.Not.EqualTo("PENDING"));
            }
            File.WriteAllText(Path.Combine(directory, "regression-summary.json"), JsonSerializer.Serialize(new
            {
                schema = "OriginalByRefEmissionRegression/v1", count = rows.Length,
                outcomes = rows.GroupBy(row => row.GetProperty("outcome").GetString()!).ToDictionary(group => group.Key, group => group.Count()),
                methods = rows, filteredRegression = true, semanticEquivalenceProved = false, fullRecoveryProved = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
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

    private sealed class RegressionEmitter : AsmResolverDllOutputFormatIlRecovery
    {
        internal void BuildWithReceipt(ApplicationAnalysisContext app, string directory)
        {
            BuildAssembliesForOutput(app, directory);
            WriteOutputReceipts(directory);
        }
    }

    private static object DescribeType(TypeAnalysisContext type, int depth)
    {
        // 只输出有界原始布局证据，不在测试中重新实现生产 ABI 尺寸算法。
        var layout = GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8);
        var generic = type as GenericInstanceTypeAnalysisContext;
        return new
        {
            type = type.FullName, type.IsValueType,
            contextKind = type.GetType().Name,
            genericDefinition = generic?.GenericType.FullName,
            genericArguments = generic?.GenericArguments.Select(argument => argument.FullName).ToArray(),
            slots = Arm64CallingConventionResolver.GeneralRegisterSlotCount(type),
            unboxedSize = TypeSizes.UnboxedSize(type, 8), knownLayout = layout != null,
            size = layout?.Size, alignment = layout?.Alignment,
            concreteLayout = generic == null ? null : GenericInstanceFieldLayout.GetConcreteFieldLayout(generic)?
                .Select(field => new { field = field.Field.Name, field.Offset, field.Size }).ToArray(),
            fields = depth >= 3 ? null : (generic?.GenericType ?? type).Fields.Where(field => !field.IsStatic).Select(field => new
            {
                field = field.Name, field.Offset, declaredType = field.FieldType.FullName,
                type = DescribeType(generic == null ? field.FieldType
                    : GenericInstantiation.Instantiate(field.FieldType, generic.GenericArguments, []), depth + 1)
            }).ToArray()
        };
    }
}
