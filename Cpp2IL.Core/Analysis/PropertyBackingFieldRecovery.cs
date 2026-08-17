using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把跨类型内联后的私有属性字段读写恢复为公开属性访问器调用。
/// </summary>
public static class PropertyBackingFieldRecovery
{
    /// <summary>
    /// 恢复当前方法中具有唯一字段/属性身份、精确类型和公开非虚访问器的读写。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is null || method.DeclaringType is null)
            return 0;

        var recovered = 0;
        var temporaryIndex = 0;
        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, FieldReference source] }
                    && TryResolveAccessor(method.DeclaringType, source.Field, read: true, out var getter))
                {
                    instruction.OpCode = OpCode.Call;
                    instruction.SetOperands(source.Field.IsStatic
                        ? [getter, destination]
                        : [getter, destination, source.Local]);
                    recovered++;
                    continue;
                }

                if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference fieldDestination, var sourceValue] }
                    && TryResolveAccessor(method.DeclaringType, fieldDestination.Field, read: false, out var setter))
                {
                    instruction.OpCode = OpCode.CallVoid;
                    instruction.SetOperands(fieldDestination.Field.IsStatic
                        ? [setter, sourceValue]
                        : [setter, fieldDestination.Local, sourceValue]);
                    recovered++;
                    continue;
                }

                // 中文注释：字段读取可能已被折叠进另一调用或返回的操作数；先物化公开 getter
                // 结果，再由原指令消费同一个临时局部，避免 CIL 继续直接访问跨类型私有字段。
                for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (instruction.Operands[operandIndex] is not FieldReference embedded
                        || !IsSourceOperand(instruction, operandIndex)
                        || !TryResolveAccessor(method.DeclaringType, embedded.Field, read: true, out var embeddedGetter))
                        continue;

                    var temporary = new LocalVariable(
                        $"propertyValue{temporaryIndex}",
                        new Register(null, $"PROPERTY_VALUE_{temporaryIndex}"),
                        embedded.Field.FieldType);
                    temporaryIndex++;
                    var getterCall = new Instruction(
                        instruction.Index,
                        OpCode.Call,
                        embedded.Field.IsStatic
                            ? [embeddedGetter, temporary]
                            : [embeddedGetter, temporary, embedded.Local]);
                    block.Instructions.Insert(instructionIndex, getterCall);
                    instructionIndex++;
                    instruction.SetOperand(operandIndex, temporary);
                    recovered++;
                }
            }
        }

        return recovered;
    }

    /// <summary>
    /// 区分字段读取操作数与 Move 的字段写入目标，防止把 setter 目标再次当作 getter 物化。
    /// </summary>
    private static bool IsSourceOperand(Instruction instruction, int operandIndex) =>
        instruction.OpCode != OpCode.Move || operandIndex != 0;

    /// <summary>
    /// 只接受跨私有访问边界、唯一命名匹配、同类型且公开不可覆盖的访问器；
    /// 接口实现产生的最终虚方法与普通非虚方法具有相同的确定分派语义。
    /// </summary>
    private static bool TryResolveAccessor(
        TypeAnalysisContext callerType,
        FieldAnalysisContext field,
        bool read,
        out MethodAnalysisContext accessor)
    {
        accessor = null!;
        if (field.Visibility != FieldAttributes.Private
            || SharesPrivateAccessScope(callerType, field.DeclaringType))
            return false;

        var owner = field.DeclaringType;
        var definitionOwner = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? genericOwner.GenericType
            : owner;
        var candidateNames = PropertyNameCandidates(field.Name);
        var properties = definitionOwner.Properties
            .Where(property => candidateNames.Contains(property.Name, StringComparer.Ordinal))
            .Where(property => property.IsStatic == field.IsStatic)
            .Where(property => PropertyTypeMatchesField(property, owner, field.FieldType))
            .ToArray();
        if (properties.Length != 1)
            return false;

        var selected = read ? properties[0].Getter : properties[0].Setter;
        if (selected is null
            || selected.Visibility != MethodAttributes.Public
            || selected.IsVirtual && !selected.IsFinal)
            return false;

        accessor = InstantiateAccessor(selected, owner);
        return true;
    }

    /// <summary>
    /// 编译器 backing field 与普通小驼峰字段都必须映射到唯一同名属性。
    /// </summary>
    private static HashSet<string> PropertyNameCandidates(string fieldName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        const string compilerSuffix = ">k__BackingField";
        if (fieldName.StartsWith('<') && fieldName.EndsWith(compilerSuffix, StringComparison.Ordinal))
            names.Add(fieldName[1..^compilerSuffix.Length]);

        const string recoveredPrefix = "__cg_";
        const string recoveredSuffix = "_k__BackingField";
        if (fieldName.StartsWith(recoveredPrefix, StringComparison.Ordinal)
            && fieldName.EndsWith(recoveredSuffix, StringComparison.Ordinal))
            names.Add(fieldName[recoveredPrefix.Length..^recoveredSuffix.Length]);

        var conventionalName = fieldName.TrimStart('_');
        if (conventionalName.Length != 0)
            names.Add(char.ToUpperInvariant(conventionalName[0]) + conventionalName[1..]);
        return names;
    }

    /// <summary>
    /// 泛型实例上的属性类型先按实例实参具体化，再与字段声明类型比较。
    /// </summary>
    private static bool PropertyTypeMatchesField(
        PropertyAnalysisContext property,
        TypeAnalysisContext owner,
        TypeAnalysisContext fieldType)
    {
        var propertyType = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? GenericInstantiation.Instantiate(property.PropertyType, genericOwner.GenericArguments, [])
            : property.PropertyType;
        return GenericCallRebinder.TypesEquivalent(propertyType, fieldType);
    }

    /// <summary>
    /// 泛型类型上的访问器必须绑定到同一具体类型实例，避免生成开放泛型调用。
    /// </summary>
    private static MethodAnalysisContext InstantiateAccessor(
        MethodAnalysisContext accessor,
        TypeAnalysisContext owner) => owner is GenericInstanceTypeAnalysisContext genericOwner
        ? new ConcreteGenericMethodAnalysisContext(accessor, genericOwner.GenericArguments, [])
        : accessor;

    /// <summary>
    /// 同一顶层类型及其嵌套类型共享 C# 私有成员访问域，应保留原字段语义。
    /// </summary>
    private static bool SharesPrivateAccessScope(TypeAnalysisContext left, TypeAnalysisContext right) =>
        ReferenceEquals(TopLevelDefinition(left), TopLevelDefinition(right));

    /// <summary>
    /// 归一化泛型实例并上溯到最外层声明类型。
    /// </summary>
    private static TypeAnalysisContext TopLevelDefinition(TypeAnalysisContext type)
    {
        var current = type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : type;
        while (current.DeclaringType is { } declaringType)
            current = declaringType is GenericInstanceTypeAnalysisContext declaringGeneric
                ? declaringGeneric.GenericType
                : declaringType;
        return current;
    }
}
