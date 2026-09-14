using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

// 直接分析与真实发射共用同一个快照写入器，避免两条取证路径的身份和摘要合同分叉。
internal sealed class AnalysisStageTraceEvidence : IDisposable
{
    private readonly MethodAnalysisContext method;
    private readonly StreamWriter writer;
    private readonly AnalysisStageRecorder recorder;
    private readonly string path;
    private readonly string assembly;
    private readonly string rawHash;
    private readonly uint token;
    private int count;
    private int unstructured;
    private bool sinkFailed;
    private bool disposed;
    private string? analysisFailure;
    private string? lastStage;

    internal AnalysisStageTraceEvidence(MethodAnalysisContext method, string directory)
    {
        if (method.StageRecorder != null || method.ConvertedIsil != null)
            throw new InvalidOperationException("阶段取证必须绑定当前生产器首次分析，不继承缓存或覆盖记录器。");
        this.method = method;
        assembly = method.DeclaringType!.DeclaringAssembly.Name;
        token = method.Definition!.token;
        var file = $"{assembly}-{token:X8}.stages.jsonl";
        if (Path.GetFileName(file) != file) throw new InvalidDataException("程序集身份包含路径分隔符。");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, file);
        rawHash = Convert.ToHexString(SHA256.HashData(method.RawBytes.AsSpan())).ToLowerInvariant();
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        recorder = new AnalysisStageRecorder(snapshot =>
        {
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(snapshot));
                count++;
                unstructured += snapshot.UnstructuredOperands;
                lastStage = snapshot.Stage;
            }
            catch { sinkFailed = true; throw; }
        });
        method.StageRecorder = recorder;
    }

    internal void CaptureAnalysisFailure(Exception error)
    {
        if (sinkFailed) throw new IOException("阶段证据写入失败。", error);
        analysisFailure = error.GetType().FullName + ": " + error.Message;
        recorder.Capture("AnalysisFailure", method, failure: analysisFailure);
    }

    internal object Complete(bool? emittedBody = null)
    {
        Dispose();
        if (sinkFailed || count == 0) throw new InvalidDataException("阶段证据没有完整写入。");
        using var input = File.OpenRead(path);
        return new
        {
            assembly, token, address = method.UnderlyingPointer, stageCount = count,
            unstructuredOperands = unstructured, analysisFailure, lastStage,
            path, sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(),
            rawMethodBytesSha256 = rawHash, emissionMethodBodyPresent = emittedBody
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        method.StageRecorder = null;
        writer.Dispose();
    }
}
