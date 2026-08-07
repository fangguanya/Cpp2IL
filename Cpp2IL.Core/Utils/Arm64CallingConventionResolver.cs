using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

/// <summary>
/// 统一处理 AAPCS64 的托管参数、原始参数和返回寄存器布局。
/// </summary>
public static class Arm64CallingConventionResolver
{
    private const int PointerSize = 8;

    private static readonly string[] IntegerRegisters =
        ["X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7"];

    private static readonly string[] FloatingRegisters =
        ["V0", "V1", "V2", "V3", "V4", "V5", "V6", "V7"];

    internal static IReadOnlyList<string> RawArgumentRegisterNames =>
        IntegerRegisters.Concat(FloatingRegisters).ToArray();

    public static IOperand[] ResolveForUnmanaged()
        => RawArgumentRegisterNames
            .Select(name => (IOperand)new Register(null, name))
            .ToArray();

    public static Register ReturnRegister(MethodAnalysisContext method)
        => new(null, X64CallingConventionResolver.IsFloatingPoint(method.ReturnType) ? "V0" : "X0");

    public static Register? HiddenReturnBufferRegister(MethodAnalysisContext method)
        => ReturnsViaHiddenBuffer(method) ? new Register(null, "X8") : null;

    public static bool ReturnsViaHiddenBuffer(MethodAnalysisContext method)
    {
        if (method.IsVoid)
            return false;

        var returnType = method.ReturnType;
        if (!returnType.IsValueType || X64CallingConventionResolver.IsFloatingPoint(returnType))
            return false;

        var size = TypeSizes.UnboxedSize(returnType, PointerSize);
        return size > 16;
    }

    public static bool HasRawArgumentLayout(Instruction call)
    {
        var argumentBase = ArgumentBase(call);
        var expected = RawArgumentRegisterNames;
        var expectedCount = argumentBase + expected.Count;
        var actualCount = call.Operands.Count;

        if (actualCount < expectedCount
            || actualCount > expectedCount + 1)
            return false;

        for (var index = 0; index < expected.Count; index++)
        {
            if (RegisterName(call.Operands[argumentBase + index]) != expected[index])
                return false;
        }

        return call.Operands.Count == expectedCount
               || RegisterName(call.Operands[^1]) == "X8";
    }

    public static void RemapRawArguments(Instruction call, MethodAnalysisContext resolved)
    {
        if (!HasRawArgumentLayout(call))
            return;

        var argumentBase = ArgumentBase(call);
        var slots = new List<(bool IsFloating, bool Emit)>();
        var hasIndirectReturnCandidate = call.Operands.Count
                                         == argumentBase + RawArgumentRegisterNames.Count + 1;
        var indirectReturnCandidate = hasIndirectReturnCandidate ? call.Operands[^1] : null;

        if (!resolved.IsStatic)
            slots.Add((false, true));

        foreach (var parameter in resolved.Parameters)
            slots.Add((X64CallingConventionResolver.IsFloatingPoint(parameter.ParameterType), true));

        // IL2CPP 托管调用的最后一个整数参数是 MethodInfo 指针。
        slots.Add((false, true));

        var operands = call.Operands.Take(argumentBase).ToList();
        var integerIndex = 0;
        var floatingIndex = 0;

        foreach (var (isFloating, emit) in slots)
        {
            var registerIndex = isFloating ? floatingIndex++ : integerIndex++;
            var registerLimit = isFloating ? FloatingRegisters.Length : IntegerRegisters.Length;
            if (registerIndex >= registerLimit)
                break;

            var rawOffset = isFloating
                ? IntegerRegisters.Length + registerIndex
                : registerIndex;

            if (emit)
                operands.Add(call.Operands[argumentBase + rawOffset]);
        }

        if (indirectReturnCandidate != null)
            operands.Add(indirectReturnCandidate);

        call.SetOperands(operands);
    }

    private static int ArgumentBase(Instruction call)
        => call.OpCode == OpCode.CallVoid ? 1 : 2;

    private static string? RegisterName(IOperand operand) => operand switch
    {
        Register register => register.Name,
        LocalVariable { Register.Name: var name } => name,
        _ => null
    };
}
