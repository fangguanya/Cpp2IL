using System;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

// 临时诊断：输出 store_gap_or_partial_field 拒因族参数类型的真实字段布局；诊断完成后删除本文件。
[NonParallelizable]
public class TempStackAggregateLayoutDiagnostic
{
    [Test]
    [Category("fixture集成")]
    public void 输出真实布局与门判定()
    {
        var context = Arm64StartupFixtureIntegrationTests.LoadFixture();
        var evidenceRoot = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT")!;
        Directory.CreateDirectory(evidenceRoot);
        var pointerSize = context.Binary.PointerSizeBytes;
        var lines = new StringBuilder();
        foreach (var fullName in new[]
                 {
                     "Gadsme.GadsmeAdContentInfo",
                     "EmbraceSDK.AndroidPushNotificationArgs",
                     "System.TimeZoneInfo+TransitionTime",
                     "UnityEngine.UIElements.Rotate",
                     "UnityEngine.UIElements.Translate",
                     "UnityEngine.UIElements.BackgroundSize",
                     "UnityEngine.UIElements.TextShadow",
                     "UnityEngine.Ray",
                     "System.Text.RegularExpressions.Regex+CachedCodeEntryKey",
                     "UnityEngine.ContactFilter2D",
                     "UnityEngine.UIElements.StyleTextShadow",
                     "System.Data.SqlTypes.SqlString",
                     "UnityEngine.UIElements.Cursor",
                     "UnityEngine.UI.Navigation",
                     "TMPro.HighlightState",
                 })
        {
            var type = context.Assemblies.SelectMany(assembly => assembly.Types)
                .FirstOrDefault(candidate => candidate.Definition?.FullName == fullName);
            if (type == null)
            {
                lines.AppendLine($"MISSING {fullName}");
                continue;
            }
            lines.AppendLine($"TYPE {fullName} IsValueType={type.IsValueType} unboxed={TypeSizes.UnboxedSize(type, pointerSize)}");
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
            {
                var scalar = ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(field.FieldType, pointerSize, true, out var size, out var kind)
                    ? $"{kind}/{size}" : "未知";
                lines.AppendLine($"  field {field.Name} type={field.FieldType.FullName} off={field.Offset} vis={field.Visibility} scalar={scalar}");
            }
        }
        File.WriteAllText(Path.Combine(evidenceRoot, "stack-aggregate-layout-diag2.txt"), lines.ToString());
    }
}
