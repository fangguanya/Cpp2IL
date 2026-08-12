using System;
using AsmResolver.DotNet.Code.Cil;

namespace Cpp2IL.Core;

/// <summary>
/// 对单个已生成方法体执行一次确定性的标签与求值栈验证。
/// </summary>
internal static class CilStackValidator
{
    internal readonly record struct ValidationResult(int InstructionCount, int MaxStack);

    internal static ValidationResult Validate(CilMethodBody body, string methodFullName)
    {
        try
        {
            // 标签验证同时计算指令偏移；栈计算复用这些偏移，禁止重复遍历计算偏移。
            body.VerifyLabels();
            var maxStack = body.ComputeMaxStack(calculateOffsets: false);
            body.MaxStack = maxStack;
            body.ComputeMaxStackOnBuild = false;
            body.VerifyLabelsOnBuild = false;
            return new ValidationResult(body.Instructions.Count, maxStack);
        }
        catch (StackImbalanceException exception)
        {
            throw new DecompilerException(
                $"CIL 栈验证失败：method={methodFullName}，offset=IL_{exception.Offset:X4}",
                exception);
        }
        catch (InvalidCilInstructionException exception)
        {
            throw new DecompilerException(
                $"CIL 标签验证失败：method={methodFullName}，detail={exception.Message}",
                exception);
        }
        catch (AggregateException exception)
        {
            throw new DecompilerException(
                $"CIL 标签验证失败：method={methodFullName}，errors={exception.InnerExceptions.Count}",
                exception);
        }
    }
}
