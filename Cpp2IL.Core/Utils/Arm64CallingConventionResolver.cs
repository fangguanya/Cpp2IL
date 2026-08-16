using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
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
    /// 为直接返回的同质浮点聚合体建立调用后字段投影。
    /// 托管调用先把完整值类型保存在V0对应的聚合体局部量中，再把每个字段投影回原生ABI使用的
    /// 连续V寄存器。投影必须按高编号到V0的顺序发射；若先覆盖V0，后续字段便会错误地从第一个
    /// 标量分量而不是完整聚合体读取。
    /// </summary>
    internal static IReadOnlyList<(Register Destination, MemoryOperand Source)> ReturnProjections(
        MethodAnalysisContext method)
    {
        if (!TryGetHomogeneousFloatingAggregateFields(method.ReturnType, out var fields)
            || fields.Any(field => field.Offset < 0))
            return [];

        var aggregateCarrier = ReturnRegister(method);
        var projections = new List<(Register Destination, MemoryOperand Source)>(fields.Count);
        for (var fieldIndex = fields.Count - 1; fieldIndex >= 0; fieldIndex--)
        {
            projections.Add((
                new Register(null, $"V{fieldIndex}"),
                new MemoryOperand(aggregateCarrier, addend: fields[fieldIndex].Offset)));
        }

        return projections;
    }

    /// <summary>
    /// 按 AAPCS64 C.1-C.15 计算一个已知托管方法在调用点的实参载体。
    /// 浮点标量与 HFA 使用独立的 V0-V7 序列；HFA 若不能完整放入剩余 V 寄存器，
    /// 整体进入栈参数区。整数寄存器耗尽后的引用和标量同样依序进入该栈参数区。
    /// </summary>
    public static IReadOnlyList<IOperand> ArgumentOperands(MethodAnalysisContext method)
    {
        var operands = new List<IOperand>();
        var integerIndex = 0;
        var floatingIndex = 0;
        var stackOffset = 0;

        void AddStackArgument(int sizeBytes)
        {
            stackOffset = AlignUp(stackOffset, PointerSize);
            operands.Add(new StackOffset(stackOffset));
            stackOffset = checked(stackOffset + AlignUp(Math.Max(sizeBytes, 1), PointerSize));
        }

        void AddGeneralArgument(TypeAnalysisContext type)
        {
            var slots = Math.Max(GeneralRegisterSlotCount(type), 1);
            if (integerIndex + slots <= IntegerRegisters.Length)
            {
                operands.Add(new Register(null, IntegerRegisters[integerIndex]));
                integerIndex += slots;
                return;
            }

            // 一个多槽值若无法完整落入剩余 X 寄存器，AAPCS64 要求从栈重新开始，
            // 不得把同一个托管值拆成“部分寄存器、部分栈”。
            integerIndex = IntegerRegisters.Length;
            AddStackArgument(ArgumentSizeBytes(type));
        }

        if (!method.IsStatic)
            AddGeneralArgument(method.DeclaringType!);

        foreach (var parameter in method.Parameters)
        {
            var parameterType = parameter.ParameterType;
            if (TryGetHomogeneousFloatingAggregateFields(parameterType, out var fields))
            {
                if (floatingIndex + fields.Count <= FloatingRegisters.Length)
                {
                    var components = new IOperand[fields.Count];
                    for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                        components[fieldIndex] = new Register(null, FloatingRegisters[floatingIndex + fieldIndex]);

                    operands.Add(new HomogeneousFloatingAggregateArgument(parameterType, components));
                    floatingIndex += fields.Count;
                }
                else
                {
                    // HFA 不能部分占用 V6/V7；一旦溢出，后续 HFA 也从栈参数区取得。
                    floatingIndex = FloatingRegisters.Length;
                    AddStackArgument(ArgumentSizeBytes(parameterType));
                }

                continue;
            }

            if (X64CallingConventionResolver.IsFloatingPoint(parameterType))
            {
                if (floatingIndex < FloatingRegisters.Length)
                    operands.Add(new Register(null, FloatingRegisters[floatingIndex++]));
                else
                    AddStackArgument(ArgumentSizeBytes(parameterType));
                continue;
            }

            AddGeneralArgument(parameterType);
        }

        // 具体泛型调用的 RGCTX 恢复仍需观察物理 MethodInfo 载体；它不是托管形参，
        // 后续 CallArgumentTrimmer 会在 RGCTX 解析完成后统一删除。
        if (RequiresHiddenMethodInfo(method))
        {
            if (integerIndex < IntegerRegisters.Length)
                operands.Add(new Register(null, IntegerRegisters[integerIndex]));
            else
                AddStackArgument(PointerSize);
        }

        return operands;
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

    /// <summary>
    /// 计算整数/引用/非HFA值类型形参占用的AAPCS64通用寄存器槽数。
    /// 至多16字节的值类型按8字节向上取整；更大的值通过地址传递，只占一个槽。
    /// </summary>
    public static int GeneralRegisterSlotCount(TypeAnalysisContext type)
    {
        if (X64CallingConventionResolver.IsFloatingPoint(type)
            || TryGetHomogeneousFloatingAggregateFields(type, out _))
            return 0;

        if (!type.IsValueType)
            return 1;

        var size = type is GenericInstanceTypeAnalysisContext generic
                   && GenericInstanceFieldLayout.GetConcreteFieldLayout(generic) is { Count: > 0 } layout
            ? layout.Max(field => field.Offset + field.Size)
            : TypeSizes.UnboxedSize(type, PointerSize);
        return size is > 0 and <= 16
            ? checked((int)((size + PointerSize - 1) / PointerSize))
            : 1;
    }

    private static int ArgumentSizeBytes(TypeAnalysisContext type)
    {
        if (!type.IsValueType)
            return PointerSize;

        if (TryGetHomogeneousFloatingAggregateFields(type, out var fields))
        {
            var elementSize = fields[0].FieldType.FullName == "System.Double"
                ? sizeof(double)
                : sizeof(float);
            return checked(fields.Count * elementSize);
        }

        if (type.FullName == "System.Single")
            return sizeof(float);
        if (type.FullName == "System.Double")
            return sizeof(double);

        var size = TypeSizes.UnboxedSize(type, PointerSize);
        return size is > 0 and <= int.MaxValue ? checked((int)size) : PointerSize;
    }

    private static int AlignUp(int value, int alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    public static bool RequiresHiddenMethodInfo(MethodAnalysisContext method)
        => method.GenericParameters.Count > 0
           || method.DeclaringType?.GenericParameters.Count > 0;

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
