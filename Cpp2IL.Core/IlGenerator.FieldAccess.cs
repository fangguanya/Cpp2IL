using AsmResolver.DotNet;
using System.Collections.Generic;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    /// <summary>
    /// 判断字段跨度是否可以逐段使用托管字段指令。该判定只检查真实声明身份和访问边界，
    /// 不以显示名称、字段偏移或调用方法名称推断可见性。
    /// </summary>
    private static bool HasDirectFieldSpanAccess(
        ManagedFieldSpanRecoveryHelper.Description span,
        TypeAnalysisContext? callerType)
    {
        if (callerType == null)
            return false;

        foreach (var segment in span.Segments)
        {
            if (segment.ParentFields is { Count: > 0 } parents)
            {
                foreach (var parent in parents)
                    if (!PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(callerType, parent))
                        return false;
            }

            if (!PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(callerType, segment.Field))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 完整非托管聚合的根字段可直接寻址时，允许发射端一次读取或写入其精确位模式。
    /// 这只用于整字段访问；部分聚合、含托管引用或根字段跨访问边界均保持原有红门。
    /// </summary>
    private static bool CanUseExactUnmanagedAggregateAddress(
        FieldReference field,
        ManagedFieldSpanRecoveryHelper.Description span,
        MethodAnalysisContext context,
        int pointerSize)
    {
        if (span.WholeAggregateField == null
            || field.Offset != field.Field.Offset
            || context.DeclaringType == null
            || (field.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0
            || !PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(
                context.DeclaringType, field.Field)
            || !ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(
                field.Field.FieldType, pointerSize, span.WidthBytes))
            return false;

        // 单一小型聚合段已经是完整原始位模式，字段自身的托管可访问性足以支撑整字段寻址；
        // 不再把它误判成需要逐个展开的字段跨度。
        if (span.Segments is [{ Kind: ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate } segment]
            && segment.SizeBytes == span.WidthBytes
            && ReferenceEquals(segment.Field, field.Field))
            return true;

        if (HasDirectFieldSpanAccess(span, context.DeclaringType))
            return false;

        return true;
    }

    /// <summary>沿精确值字段链构造托管接收者地址；不使用原生指针重解释整个对象。</summary>
    private static FieldReference LoadRecoveredFieldSpanReceiver(FieldReference original,
        ManagedFieldSpanRecoveryHelper.Segment segment, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, bool read,
        CilLocalVariable? wholeValue = null)
    {
        var leaf = new FieldReference(segment.Field, original.Local, checked(original.Offset + segment.RelativeOffsetBytes));
        if (segment.ParentFields is not { Count: > 0 } parents)
        {
            if (!segment.Field.IsStatic) LoadFieldReceiver(leaf, method, locals);
            return leaf;
        }
        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;
        var root = new FieldReference(parents[0], original.Local, parents[0].Offset);
        if (wholeValue != null) instructions.Add(CilOpCodes.Ldloca, wholeValue);
        else if (!parents[0].IsStatic) LoadFieldReceiver(root, method, locals);
        for (var index = wholeValue == null ? 0 : 1; index < parents.Count; index++)
        {
            var parent = parents[index];
            if (!parent.FieldType.IsValueType || context.DeclaringType == null
                || !PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(context.DeclaringType, parent))
                throw new UnresolvedCilSemanticException(method.FullName, "FIELD_SPAN_ACCESSIBILITY", "嵌套值字段链缺少直接访问边界。");
            if (!read && (parent.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0)
                throw new UnresolvedCilSemanticException(method.FullName, "READONLY_NATIVE_STORE", "嵌套只读字段的写入尚缺初始化或合法间接写入合同。");
            instructions.Add(parent.IsStatic ? CilOpCodes.Ldsflda : CilOpCodes.Ldflda, parent.ToFieldDescriptor(module));
        }
        // 中间接收者是值类型地址；私有叶字段只在其自身声明类型存在公开访问器时改写。
        var receiverType = parents[^1].FieldType;
        var hasAccessor = context.DeclaringType != null
            && (read
                ? PropertyBackingFieldRecovery.TryResolveGetter(context.DeclaringType, leaf, receiverType, out _)
                : PropertyBackingFieldRecovery.TryResolveSetter(context.DeclaringType, leaf, receiverType, out _));
        if (context.DeclaringType == null
            || !PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(context.DeclaringType, segment.Field)
                && !hasAccessor)
            throw new UnresolvedCilSemanticException(method.FullName, "FIELD_SPAN_ACCESSIBILITY", "嵌套叶字段缺少精确的直接访问边界。");
        if (!read && (segment.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0)
            throw new UnresolvedCilSemanticException(method.FullName, "READONLY_NATIVE_STORE", "嵌套只读叶字段的写入尚缺初始化合同。");
        return leaf;
    }

    /// <summary>完整只读字段赋值只能发生在同一声明类型的真实初始化方法，不放宽部分地址写。</summary>
    private static void ValidateWholeReadOnlyStore(FieldReference reference, MethodAnalysisContext context, MethodDefinition method)
    {
        if ((reference.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) == 0) return;
        const System.Reflection.MethodAttributes constructorFlags = System.Reflection.MethodAttributes.SpecialName | System.Reflection.MethodAttributes.RTSpecialName;
        var valid = context.DeclaringType != null
            && GenericCallRebinder.TypesEquivalent(context.DeclaringType, reference.Field.DeclaringType, requireDefinitionIdentity: true)
            && (context.Attributes & constructorFlags) == constructorFlags
            && ((System.Reflection.MethodAttributes)method.Attributes & constructorFlags) == constructorFlags
            && method.Name == context.Name
            && (reference.Field.IsStatic
                ? context.IsStatic && context.Name == ".cctor" && context.Parameters.Count == 0
                : !context.IsStatic && context.Name == ".ctor" && reference.Local.IsThis);
        if (!valid)
            throw new UnresolvedCilSemanticException(method.FullName, "READONLY_NATIVE_STORE", "完整只读字段缺少同声明类型的初始化身份。");
    }

    /// <summary>接收者及写入值已在栈上；数值与引用跨度共用同一字段访问合同。</summary>
    private static void EmitRecoveredFieldOperation(FieldReference reference, MethodAnalysisContext context,
        MethodDefinition method, bool read, TypeAnalysisContext? receiverType = null)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var module = method.DeclaringModule!;
        MethodAnalysisContext accessor;
        if (context.DeclaringType != null
            && (read
                ? PropertyBackingFieldRecovery.TryResolveGetter(context.DeclaringType, reference, receiverType, out accessor)
                : PropertyBackingFieldRecovery.TryResolveSetter(context.DeclaringType, reference, receiverType, out accessor)))
        {
            instructions.Add(CilOpCodes.Call, module.DefaultImporter!.ImportMethod(accessor.ToMethodDescriptor(module)));
            return;
        }
        if (context.DeclaringType != null
            && !PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(context.DeclaringType, reference.Field))
            throw new UnresolvedCilSemanticException(method.FullName, "FIELD_SPAN_ACCESSIBILITY", "完整字段分量缺少合法访问边界或已解析的公开访问器。");
        var opcode = read
            ? reference.Field.IsStatic ? CilOpCodes.Ldsfld : CilOpCodes.Ldfld
            : reference.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld;
        instructions.Add(opcode, reference.Field.ToFieldDescriptor(module));
    }
}
