using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        => new(null, X64CallingConventionResolver.IsFloatingPoint(method.ReturnType)
                     || TryGetHomogeneousFloatingAggregateFields(method.ReturnType, out _)
            ? "V0"
            : "X0");

    /// <summary>
    /// 返回方法在 AAPCS64 下承载直接返回值的全部寄存器。
    /// 同质浮点聚合体的每个成员分别占用一个连续的 V 寄存器，不能退化成 X0 中的对象引用。
    /// </summary>
    public static IReadOnlyList<IOperand> ReturnOperands(MethodAnalysisContext method)
    {
        if (method.IsVoid)
            return [];

        if (TryGetHomogeneousFloatingAggregateFields(method.ReturnType, out var fields))
            return fields
                .Select((_, index) => (IOperand)new Register(null, $"V{index}"))
                .ToArray();

        return [ReturnRegister(method)];
    }

    /// <summary>
    /// 按 AAPCS64 识别由一至四个同类型 Single 或 Double 实例字段组成的同质浮点聚合体。
    /// 静态字段和常量不属于实例布局；混合类型、空聚合体和超过四个成员都必须拒绝。
    /// </summary>
    public static bool TryGetHomogeneousFloatingAggregateFields(
        TypeAnalysisContext type,
        out IReadOnlyList<FieldAnalysisContext> fields)
    {
        fields = [];
        if (!type.IsValueType || X64CallingConventionResolver.IsFloatingPoint(type))
            return false;

        var instanceFields = type.Fields
            .Where(field => !field.IsStatic && (field.Attributes & FieldAttributes.Literal) == 0)
            .ToArray();
        if (instanceFields.Length is < 1 or > 4)
            return false;

        var elementType = instanceFields[0].FieldType;
        if (!X64CallingConventionResolver.IsFloatingPoint(elementType)
            || instanceFields.Any(field => !TypesExactlyMatch(field.FieldType, elementType)))
            return false;

        fields = instanceFields;
        return true;
    }

    public static Register? HiddenReturnBufferRegister(MethodAnalysisContext method)
        => ReturnsViaHiddenBuffer(method) ? new Register(null, "X8") : null;

    public static bool ReturnsViaHiddenBuffer(MethodAnalysisContext method)
    {
        if (method.IsVoid)
            return false;

        var returnType = method.ReturnType;
        if (!returnType.IsValueType
            || X64CallingConventionResolver.IsFloatingPoint(returnType)
            || TryGetHomogeneousFloatingAggregateFields(returnType, out _))
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

    private static bool TypesExactlyMatch(TypeAnalysisContext left, TypeAnalysisContext right)
        => ReferenceEquals(left, right)
           || left.Namespace == right.Namespace && left.Name == right.Name;
}
