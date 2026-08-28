using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.CIL;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatIlRecovery : AsmResolverDllOutputFormat
{
    private HashSet<TypeAnalysisContext>? selectedRecoveryTypes;
    private HashSet<MethodAnalysisContext>? selectedRecoveryMethods;
    private int validatedMethodCount;
    private int selectedMethodCount;
    private readonly ConcurrentBag<MethodFailure> methodFailures = [];

    private sealed record MethodFailure(
        string Assembly,
        string Type,
        string Method,
        string Category,
        string Detail);

    public override string OutputFormatId => "dll_il_recovery";

    public override string OutputFormatName => "DLL files with IL Recovery";

    public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
    {
        var runtimeOptions = Cpp2IlApi.RuntimeOptions;
        var assemblyFilters = runtimeOptions?.IsilDumpAssemblyFilters ?? [];
        var typeFilters = runtimeOptions?.IsilDumpTypeFilters ?? [];
        var methodFilters = runtimeOptions?.IsilDumpMethodFilters ?? [];
        var hasSelection = assemblyFilters.Count != 0 || typeFilters.Count != 0 || methodFilters.Count != 0;
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

            selectedRecoveryTypes = new HashSet<TypeAnalysisContext>(types);
            selectedRecoveryMethods = new HashSet<MethodAnalysisContext>(methods);
            selectedMethodCount = methods.Count;
            Logger.InfoNewline(
                $"IL恢复已精确选择 {assemblies.Count} 个程序集、{selectedTypeRoots.Count} 个根类型、" +
                $"{types.Count} 个含嵌套闭包类型与 {methods.Count} 个方法；其他成员只保留声明。",
                "DllOutput");
        }

        Volatile.Write(ref validatedMethodCount, 0);
        TotalMethodCount = 0;
        SuccessfulMethodCount = 0;
        while (methodFailures.TryTake(out _))
        {
        }
        try
        {
            var builtAssemblies = base.BuildAssemblies(context);
            Logger.InfoNewline(
                $"CIL 栈验证通过 {Volatile.Read(ref validatedMethodCount)} 个已选择方法。",
                "DllOutput");
            return builtAssemblies;
        }
        finally
        {
            selectedRecoveryTypes = null;
            selectedRecoveryMethods = null;
        }
    }

    protected override bool ShouldFillMethodBody(
        AssemblyAnalysisContext assemblyContext,
        TypeAnalysisContext typeContext)
    {
        return selectedRecoveryTypes == null || selectedRecoveryTypes.Contains(typeContext);
    }

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        var module = methodDefinition.DeclaringModule!;
        var moduleName = module.Name!.ToString();
        var shouldSkip = moduleName.StartsWith("UnityEngine.") || moduleName.StartsWith("Unity.") ||
                         moduleName.StartsWith("System.") || moduleName == "System" ||
                         moduleName.StartsWith("mscorlib");
        var importer = new ReferenceImporter(module);

        if (!methodDefinition.IsManagedMethodWithBody())
            return;

        if (selectedRecoveryMethods != null && !selectedRecoveryMethods.Contains(methodContext))
        {
            methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            return;
        }

        methodDefinition.CilMethodBody = new();
        var instructions = methodDefinition.CilMethodBody.Instructions;

        if (shouldSkip)
        {
            methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            return;
        }

        try
        {
            TotalMethodCount++;

            methodContext.Analyze();

            if (methodContext.ConvertedIsil.Count == 0)
                methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            else
                IlGenerator.GenerateIl(methodContext, methodDefinition);

            CilStackValidator.Validate(methodDefinition.CilMethodBody!, methodContext.FullName);
            Interlocked.Increment(ref validatedMethodCount);

            //WriteControlFlowGraph(methodContext, Path.Combine(Environment.CurrentDirectory, "Cpp2IL", "bin", "Debug", "net9.0", "cpp2il_out", "cfg"));

            SuccessfulMethodCount++;
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

            methodFailures.Add(new MethodFailure(
                methodContext.DeclaringType?.DeclaringAssembly.Name ?? string.Empty,
                methodContext.DeclaringType?.FullName ?? string.Empty,
                methodContext.FullName,
                ClassifyFailure(detail),
                detail));

            // 保留失败方法的最终 CFG，账本中的 CIL 窗口可由同一方法图追溯到具体 SSA/边复制。
            var outputRoot = Cpp2IlApi.RuntimeOptions?.OutputRootDirectory;
            if (!string.IsNullOrWhiteSpace(outputRoot) && methodContext.ControlFlowGraph != null)
                WriteControlFlowGraph(methodContext, Path.Combine(outputRoot, "FailedMethodGraphs"));
            
            methodDefinition.CilMethodBody = new();
            instructions = methodDefinition.CilMethodBody.Instructions;

            var factory = module.CorLibTypeFactory;
            var exceptionCtor = factory.CorLibScope
                .CreateTypeReference("System", "Exception")
                .CreateMemberReference(".ctor", MethodSignature.CreateInstance(factory.Void, [factory.String]))
                .ImportWith(importer);

            instructions.Add(CilOpCodes.Ldstr, detail);
            instructions.Add(CilOpCodes.Newobj, exceptionCtor);
            instructions.Add(CilOpCodes.Throw);
        }

        methodContext.ReleaseAnalysisData();
    }

    protected override void WriteOutputReceipts(string outputRoot)
    {
        var failures = methodFailures
            .OrderBy(failure => failure.Assembly, StringComparer.Ordinal)
            .ThenBy(failure => failure.Type, StringComparer.Ordinal)
            .ThenBy(failure => failure.Method, StringComparer.Ordinal)
            .ToArray();
        var path = Path.Combine(outputRoot, "dll-il-recovery-method-ledger.json");
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "Cpp2IL.DllIlRecovery.MethodLedger/v1");
            writer.WriteNumber("selectedMethods", selectedMethodCount);
            writer.WriteNumber("totalMethodsWithBody", TotalMethodCount);
            writer.WriteNumber("successfulMethods", SuccessfulMethodCount);
            writer.WriteNumber("validatedMethods", Volatile.Read(ref validatedMethodCount));
            writer.WriteNumber("failedMethods", failures.Length);
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
