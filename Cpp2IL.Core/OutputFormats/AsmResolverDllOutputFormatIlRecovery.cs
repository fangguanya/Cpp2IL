using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AsmResolver.DotNet;
using AssetRipper.CIL;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatIlRecovery : AsmResolverDllOutputFormat
{
    private HashSet<MethodAnalysisContext>? selectedRecoveryMethods;
    private bool hasSelection;
    private bool originalDenominatorValidated;
    private string? inputBinarySha256;
    private string? inputMetadataSha256;
    private string? effectiveMetadataSha256;
    private string? inputUnityVersion;
    private float inputMetadataVersion;
    private readonly ConcurrentDictionary<MethodAnalysisContext, MethodResult> methodResults = new();

    private sealed record MethodResult(
        string Assembly, uint Token, string Type, string Method, ulong Address,
        bool Original, bool Selected, string Outcome, bool CilStackValidated,
        string Category, string Detail);

    protected int RecoveredCilCount => methodResults.Values.Count(row => row.Outcome == "CIL_EMITTED");
    protected int AttemptedBodyCount => methodResults.Values.Count(row =>
        row.Outcome is "CIL_EMITTED" or "EMPTY_ANALYSIS" or "UNRESOLVED");

    private void Record(MethodAnalysisContext method, string outcome, bool validated = false,
        string category = "", string detail = "")
    {
        var row = new MethodResult(method.DeclaringType?.DeclaringAssembly.Name ?? string.Empty,
            method.Definition?.token ?? 0, method.DeclaringType?.FullName ?? string.Empty,
            method.FullNameWithSignature, method.UnderlyingPointer, method.Definition != null,
            selectedRecoveryMethods == null || selectedRecoveryMethods.Contains(method),
            outcome, validated, category, detail);
        methodResults.AddOrUpdate(method, row, (_, before) => before.Outcome == "PENDING"
            ? row : throw new InvalidOperationException($"方法恢复结果重复提交：{row.Method}"));
    }

    public override string OutputFormatId => "dll_il_recovery";

    public override string OutputFormatName => "DLL files with IL Recovery";

    public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
    {
        var runtimeOptions = Cpp2IlApi.RuntimeOptions;
        var assemblyFilters = runtimeOptions?.IsilDumpAssemblyFilters ?? [];
        var typeFilters = runtimeOptions?.IsilDumpTypeFilters ?? [];
        var methodFilters = runtimeOptions?.IsilDumpMethodFilters ?? [];
        hasSelection = assemblyFilters.Count != 0 || typeFilters.Count != 0 || methodFilters.Count != 0;
        if (hasSelection)
        {
            var assemblies = IsilDumpSelectionHelper.SelectExact(
                context.Assemblies,
                assemblyFilters,
                assembly => assembly.Name,
                "IL恢复程序集");
            var typeCandidates = assemblies
                .SelectMany(assembly => assembly.Types)
                .ToArray();
            var selectedTypeRoots = IsilDumpSelectionHelper.SelectExact(
                typeCandidates,
                typeFilters,
                type => type.Definition?.FullName ?? string.Empty,
                "IL恢复类型");
            if (typeFilters.Count > 0 && selectedTypeRoots.Any(type => type is InjectedTypeAnalysisContext))
                throw new InvalidOperationException("IL恢复类型筛选命中了注入类型。");

            // 精确类型是恢复根；编译器生成的状态机、闭包及其更深嵌套类型必须进入同一恢复闭包。
            var types = IsilDumpSelectionHelper.ExpandDescendantClosure(
                typeCandidates,
                selectedTypeRoots,
                type => type.NestedTypes);
            if (typeFilters.Count > 0 && !types
                    .SelectMany(type => type.Methods)
                    .Any(method => method is not InjectedMethodAnalysisContext))
                throw new InvalidOperationException("IL恢复类型筛选命中的类型及其嵌套类型都没有可恢复方法。");

            var methods = IsilDumpSelectionHelper.SelectExact(
                types.SelectMany(type => type.Methods)
                    .Where(method => method is not InjectedMethodAnalysisContext),
                methodFilters,
                method => method.Definition?.HumanReadableSignature ?? string.Empty,
                "IL恢复方法");

            selectedRecoveryMethods = new HashSet<MethodAnalysisContext>(methods);
            Logger.InfoNewline(
                $"IL恢复已精确选择 {assemblies.Count} 个程序集、{selectedTypeRoots.Count} 个根类型、" +
                $"{types.Count} 个含嵌套闭包类型与 {methods.Count} 个方法；其他成员只保留声明。",
                "DllOutput");
        }

        InitializeOriginalMethodLedger(context);
        try
        {
            var builtAssemblies = base.BuildAssemblies(context);
            TotalMethodCount = AttemptedBodyCount;
            SuccessfulMethodCount = RecoveredCilCount;
            Logger.InfoNewline(
                $"CIL 栈验证通过 {RecoveredCilCount} 个已选择方法。",
                "DllOutput");
            return builtAssemblies;
        }
        finally
        {
            selectedRecoveryMethods = null;
        }
    }

    internal void InitializeOriginalMethodLedger(ApplicationAnalysisContext context)
    {
        methodResults.Clear();
        inputBinarySha256 = context.LibCpp2IlContext.InputBinarySha256;
        inputMetadataSha256 = context.LibCpp2IlContext.InputMetadataSha256;
        effectiveMetadataSha256 = context.LibCpp2IlContext.EffectiveMetadataSha256;
        inputUnityVersion = context.UnityVersion.ToString();
        inputMetadataVersion = context.MetadataVersion;
        originalDenominatorValidated = false;
        TotalMethodCount = 0;
        SuccessfulMethodCount = 0;
        var originalMethods = context.Assemblies.SelectMany(assembly => assembly.Types)
            .SelectMany(type => type.Methods).Where(method => method.Definition != null).ToArray();
        var expected = context.Assemblies.Where(assembly => assembly.Definition != null)
            .SelectMany(assembly => assembly.Definition!.Image.Types.SelectMany(type => type.Methods ?? [])
                .Select(method => new MethodIndexCompletenessHelper.Identity(assembly.Name, method.token)));
        var completeness = MethodIndexCompletenessHelper.Validate(expected, originalMethods.Select(method =>
            new MethodIndexCompletenessHelper.Entry(new MethodIndexCompletenessHelper.Identity(
                method.DeclaringType!.DeclaringAssembly.Name, method.Definition!.token), method.UnderlyingPointer)));
        if (completeness.ExpectedMethods != context.Metadata.MethodDefinitionCount ||
            context.Assemblies.Count(assembly => assembly.Definition != null) != context.Metadata.AssemblyDefinitions.Length)
            throw new InvalidOperationException("方法恢复上下文未覆盖原始 metadata 的全部程序集和方法。");
        foreach (var method in originalMethods)
            Record(method, "PENDING");
        originalDenominatorValidated = true;
    }

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        if (!methodDefinition.IsManagedMethodWithBody())
        {
            // 抽象声明无需方法体；其他运行时/原生边界须另行审定，不按名称豁免。
            Record(methodContext, methodDefinition.IsAbstract ? "DECLARATION" : "EXTERNAL_BOUNDARY_UNRESOLVED");
            return;
        }

        if (selectedRecoveryMethods != null && !selectedRecoveryMethods.Contains(methodContext))
        {
            Record(methodContext, "NOT_SELECTED", category: "SELECTION_EXCLUDED");
            return;
        }

        methodDefinition.CilMethodBody = new();

        try
        {
            methodContext.Analyze();

            if (methodContext.ConvertedIsil.Count == 0)
                throw new EmptyMethodAnalysisException(methodContext);

            // 原版合法返回仍由指令与 CFG 生成，不用默认方法体代替缺失的分析。
            IlGenerator.GenerateIl(methodContext, methodDefinition);

            CilStackValidator.Validate(methodDefinition.CilMethodBody!, methodContext.FullName);
            Record(methodContext, "CIL_EMITTED", validated: true);

            //WriteControlFlowGraph(methodContext, Path.Combine(Environment.CurrentDirectory, "Cpp2IL", "bin", "Debug", "net9.0", "cpp2il_out", "cfg"));

        }
        catch (Exception e)
        {
            // Known analysis limitations (DecompilerException) get a one-line warning; anything
            // else is an unexpected bug and keeps its (collapsed) stack trace.
            var detail = e is DecompilerException ? e.Message : e.ToCollapsedString();

            if (e is DecompilerException)
                Logger.WarnNewline($"Skipping {methodContext.FullName}: {e.Message}");
            else
                Logger.ErrorNewline($"Decompiling {methodContext.FullName} failed: {detail}");

            Record(methodContext, e is EmptyMethodAnalysisException ? "EMPTY_ANALYSIS" : "UNRESOLVED",
                category: e is EmptyMethodAnalysisException ? "EMPTY_ANALYSIS" : ClassifyFailure(detail), detail: detail);

            // 保留失败方法的最终 CFG，账本中的 CIL 窗口可由同一方法图追溯到具体 SSA/边复制。
            var outputRoot = Cpp2IlApi.RuntimeOptions?.OutputRootDirectory;
            if (!string.IsNullOrWhiteSpace(outputRoot) && methodContext.ControlFlowGraph != null)
                WriteControlFlowGraph(methodContext, Path.Combine(outputRoot, "FailedMethodGraphs"));
            
            // 失败只保留证据，不合成诊断 throw 或默认业务方法体。
            methodDefinition.CilMethodBody = null;
        }

        methodContext.ReleaseAnalysisData();
    }

    protected override void WriteOutputReceipts(string outputRoot)
    {
        var rows = methodResults.Values.OrderBy(row => row.Assembly, StringComparer.Ordinal)
            .ThenBy(row => row.Token).ThenBy(row => row.Method, StringComparer.Ordinal).ToArray();
        var failures = rows.Where(row => row.Outcome is "EMPTY_ANALYSIS" or "UNRESOLVED").ToArray();
        TotalMethodCount = AttemptedBodyCount;
        SuccessfulMethodCount = RecoveredCilCount;
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, "dll-il-recovery-method-ledger.json");
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "Cpp2IL.DllIlRecovery.MethodLedger/v2");
            writer.WriteStartObject("input");
            writer.WriteString("binarySha256", inputBinarySha256);
            writer.WriteString("metadataSha256", inputMetadataSha256);
            writer.WriteString("effectiveMetadataSha256", effectiveMetadataSha256);
            writer.WriteString("unityVersion", inputUnityVersion);
            writer.WriteNumber("metadataVersion", inputMetadataVersion);
            writer.WriteEndObject();
            writer.WriteString("producerVersion", typeof(AsmResolverDllOutputFormatIlRecovery).Assembly.GetName().Version?.ToString());
            // 当前收据仍包含诊断输出，不具备完整源码或正式发布的验收资格。
            writer.WriteBoolean("sourceRecoveryAccepted", false);
            writer.WriteBoolean("formalPublishEligible", false);
            writer.WriteNumber("selectedMethods", rows.Count(row => row.Selected && row.Original));
            writer.WriteNumber("originalMethods", rows.Count(row => row.Original));
            writer.WriteBoolean("originalDenominatorValidated", originalDenominatorValidated);
            writer.WriteNumber("totalMethodsWithBody", TotalMethodCount);
            writer.WriteNumber("successfulMethods", SuccessfulMethodCount);
            writer.WriteNumber("validatedMethods", rows.Count(row => row.CilStackValidated));
            writer.WriteNumber("failedMethods", failures.Length);
            writer.WriteStartArray("methods");
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("assembly", row.Assembly);
                writer.WriteNumber("originalToken", row.Token);
                writer.WriteString("type", row.Type);
                writer.WriteString("method", row.Method);
                writer.WriteNumber("nativeAddress", row.Address);
                writer.WriteBoolean("original", row.Original);
                writer.WriteBoolean("selected", row.Selected);
                writer.WriteString("outcome", row.Outcome);
                writer.WriteBoolean("cilStackValidated", row.CilStackValidated);
                writer.WriteBoolean("semanticAccepted", false);
                writer.WriteString("category", row.Category);
                writer.WriteString("detail", row.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("failures");
            foreach (var failure in failures)
            {
                writer.WriteStartObject();
                writer.WriteString("assembly", failure.Assembly);
                writer.WriteString("type", failure.Type);
                writer.WriteString("method", failure.Method);
                writer.WriteString("category", failure.Category);
                writer.WriteString("detail", failure.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        Logger.InfoNewline($"方法恢复账本已写入 {path}；失败 {failures.Length} 个。", "DllOutput");
    }

    protected override void ValidateOutputForPublication(string outputRoot)
    {
        // 不完整结果只持久化同一账本供排错；任何 DLL 都尚未写入。
        if (!originalDenominatorValidated || inputBinarySha256 == null || inputMetadataSha256 == null ||
            inputMetadataSha256 != effectiveMetadataSha256 || hasSelection || methodResults.Values.Any(row =>
                row.Outcome is not ("CIL_EMITTED" or "DECLARATION")))
        {
            WriteOutputReceipts(outputRoot);
            throw new InvalidOperationException("IL 恢复发布失败：方法分母、选择范围或实现结果尚未闭合；详见方法账本。");
        }
    }

    internal static string ClassifyFailure(string detail)
    {
        if (detail.Contains("CIL 栈验证失败", StringComparison.Ordinal))
            return "CIL_STACK_IMBALANCE";
        if (detail.Contains("CIL 标签验证失败", StringComparison.Ordinal))
            return "CIL_LABEL_INVALID";
        if (detail.Contains("Type and field resolution not settling", StringComparison.Ordinal))
            return "TYPE_FIELD_NOT_SETTLING";
        if (detail.Contains("Late call and address type resolution not settling", StringComparison.Ordinal))
            return "LATE_CALL_TYPE_NOT_SETTLING";
        if (detail.Contains("Stack state not settling", StringComparison.Ordinal))
            return "STACK_STATE_NOT_SETTLING";
        return "ANALYSIS_FAILURE";
    }

    public static void WriteControlFlowGraph(MethodAnalysisContext method, string outputPath)
    {
        var graph = method.ControlFlowGraph;

        var sb = new StringBuilder();
        var edges = new List<(int, int)>();

        sb.AppendLine("digraph ControlFlowGraph {");
        sb.AppendLine("    \"label\"=\"Control flow graph\"");

        // no instructions
        graph ??= new ISILControlFlowGraph([]);

        var methodText = $@"{CsFileUtils.GetKeyWordsForMethod(method)} {method.FullNameWithSignature}
parameter locals: {string.Join(", ", method.ParameterLocals)}
parameter operands: {string.Join(", ", method.ParameterOperands)}";

        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
            {
                var isEntry = block == graph.EntryBlock;
                sb.AppendLine($"""
                               	{block.ID} [
                               		"color"="{(isEntry ? "green" : "red")}"
                               		"label"="{(isEntry ? $"Entry ({block.ID})\n{methodText}" : $"Exit ({block.ID})")}"
                               	]
                               """);
            }
            else
            {
                sb.AppendLine($"""
                               	{block.ID} [
                               		"shape"="box"
                               		"label"="{block.ToString().EscapeString().Replace("\\r", "")}"
                               	]
                               """);
            }

            edges.AddRange(block.Successors.Select(b => (block.ID, b.ID)));
        }

        foreach (var edge in edges)
            sb.AppendLine($"    {edge.Item1} -> {edge.Item2}");

        sb.AppendLine("}");

        var type = method.DeclaringType!;
        var assemblyName = MiscUtils.CleanPathElement(type.DeclaringAssembly.CleanAssemblyName);
        var typePath = Path.Combine(type.FullName.Split('.').Select(MiscUtils.CleanPathElement).ToArray());
        var directoryPath = Path.Combine(outputPath, assemblyName, typePath);

        var methodName = MiscUtils.CleanPathElement(method.Name + "_" + string.Join("_",
            method.Parameters.Select(p => MiscUtils.CleanPathElement(p.ParameterType.Name))));
        var path = Path.Combine(directoryPath, methodName) + ".dot";

        if (path.Length > 260)
        {
            path = path[..250];
            path += ".dot";
        }

        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, sb.ToString());
    }
}
