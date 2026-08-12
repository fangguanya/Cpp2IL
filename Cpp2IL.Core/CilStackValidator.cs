using System;
using System.Collections.Generic;
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
                $"CIL 栈验证失败：method={methodFullName}，offset=IL_{exception.Offset:X4}，" +
                $"detail={exception.Message}，window={FormatInstructionWindow(exception.Body, exception.Offset)}",
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

    /// <summary>
    /// 生成失败指令前后各三条的紧凑窗口，使全量账本可以直接追到具体发射器。
    /// </summary>
    private static string FormatInstructionWindow(CilMethodBody body, int failureOffset)
    {
        var instructions = body.Instructions;
        if (instructions.Count == 0)
            return "EMPTY";

        var failureIndex = -1;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset != failureOffset)
                continue;

            failureIndex = index;
            break;
        }

        // 异常偏移不落在指令首地址时，选择其后的第一条；末尾异常选择最后一条。
        if (failureIndex < 0)
        {
            failureIndex = instructions.Count - 1;
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Offset < failureOffset)
                    continue;

                failureIndex = index;
                break;
            }
        }

        var first = Math.Max(0, failureIndex - 3);
        var last = Math.Min(instructions.Count - 1, failureIndex + 3);
        var window = new List<string>(last - first + 1);
        for (var index = first; index <= last; index++)
        {
            var instruction = instructions[index];
            var marker = index == failureIndex ? ">" : string.Empty;
            var operand = instruction.Operand?.ToString()?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
            window.Add($"{marker}IL_{instruction.Offset:X4}:{instruction.OpCode.Code}:{operand}");
        }

        return string.Join("|", window);
    }
}
