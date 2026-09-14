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
    /// 供晚期新建字段读取的恢复器复用同一访问器判定，避免再次扫描整张方法图。
    /// </summary>
    internal static bool TryResolveGetter(
        TypeAnalysisContext callerType,
        FieldReference reference,
        out MethodAnalysisContext getter) =>
        TryResolveAccessor(callerType, reference, receiverType: null, read: true, out getter);

    /// <summary>
    /// 按字段链末端的实际值类型解析公开 getter；嵌套值类型不能借用根局部类型闭合泛型 owner。
    /// </summary>
    internal static bool TryResolveGetter(
        TypeAnalysisContext callerType,
        FieldReference reference,
        TypeAnalysisContext? receiverType,
        out MethodAnalysisContext getter) =>
        TryResolveAccessor(callerType, reference, receiverType, read: true, out getter);

    internal static bool TryResolveSetter(
        TypeAnalysisContext callerType,
        FieldReference reference,
        out MethodAnalysisContext setter) =>
        TryResolveAccessor(callerType, reference, receiverType: null, read: false, out setter);

    /// <summary>
    /// 按字段链末端的实际值类型解析公开 setter；只接受公开且不可覆盖的精确属性。
    /// </summary>
    internal static bool TryResolveSetter(
        TypeAnalysisContext callerType,
        FieldReference reference,
        TypeAnalysisContext? receiverType,
        out MethodAnalysisContext setter) =>
        TryResolveAccessor(callerType, reference, receiverType, read: false, out setter);

    internal static bool CanDirectlyAccessPrivateField(TypeAnalysisContext callerType, FieldAnalysisContext field)
        => field.Visibility != FieldAttributes.Private || IsWithinPrivateAccessScope(callerType, field.DeclaringType);

    private static bool IsCompleteFieldAccess(Instruction instruction, FieldReference reference, int pointerSize)
    {
        // 原生宽访问可能覆盖多个相邻字段；单个访问器只代表一个完整声明值。
        // 零宽表示已是托管字段操作数，不把未知原生跨度伪造为标量大小。
        if (instruction.MemoryAccessWidthBits == 0)
            return true;
        return instruction.MemoryAccessWidthBits > 0 && instruction.MemoryAccessWidthBits % 8 == 0
               && ManagedFieldSpanRecoveryHelper.TryDescribe(reference, pointerSize,
                   instruction.MemoryAccessWidthBits / 8, out var span, out _, includeManagedReferences: true)
               && span.Segments.Count == 1 && span.Segments[0].ParentFields is not { Count: > 0 };
    }

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
                    && IsCompleteFieldAccess(instruction, source, method.AppContext.Binary.PointerSizeBytes)
                    && TryResolveAccessor(method.DeclaringType, source, read: true, out var getter))
                {
                    instruction.OpCode = OpCode.Call;
                    instruction.SetOperands(source.Field.IsStatic
                        ? [getter, destination]
                        : [getter, destination, source.Local]);
                    // 中文注释：共享泛型字段可能早期把 Current 结果登记为 object；
                    // 具体 getter 已由 Enumerator<T> 接收者唯一闭合时，同步返回局部类型。
                    if (CanSharpenGetterResultType(
                            destination.Type,
                            source.Field.FieldType,
                            getter.ReturnType))
                        destination.Type = getter.ReturnType;
                    recovered++;
                    continue;
                }

                if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference fieldDestination, var sourceValue] }
                    && IsCompleteFieldAccess(instruction, fieldDestination, method.AppContext.Binary.PointerSizeBytes)
                    && TryResolveAccessor(method.DeclaringType, fieldDestination, read: false, out var setter))
                {
                    instruction.OpCode = OpCode.CallVoid;
                    instruction.SetOperands(fieldDestination.Field.IsStatic
                        ? [setter, sourceValue]
                        : [setter, fieldDestination.Local, sourceValue]);
                    recovered++;
                    // 中文注释：同一条赋值的右值也可能是另一个跨类型私有字段。setter 改写后
                    // 必须继续扫描新的调用实参；否则 target.Property = source.<Property>k__BackingField
                    // 只恢复左侧写入，右侧读取仍会生成 CS0122。
                }

                // 中文注释：字段读取可能已被折叠进另一调用或返回的操作数；先物化公开 getter
                // 结果，再由原指令消费同一个临时局部，避免 CIL 继续直接访问跨类型私有字段。
                for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (instruction.Operands[operandIndex] is not FieldReference embedded
                        || !IsSourceOperand(instruction, operandIndex)
                        || !IsCompleteFieldAccess(instruction, embedded, method.AppContext.Binary.PointerSizeBytes)
                        || !TryResolveAccessor(method.DeclaringType, embedded, read: true, out var embeddedGetter))
                        continue;

                    var temporary = new LocalVariable(
                        $"propertyValue{temporaryIndex}",
                        new Register(null, $"PROPERTY_VALUE_{temporaryIndex}"),
                        embeddedGetter.ReturnType);
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
    /// 公开 getter 的声明返回类型是直接调用结果的权威类型。字段恢复前，后续 LINQ 参数可能
    /// 已把同一结果槽先登记成 IEnumerable 等上界；仅当 getter 返回值可赋给该上界时向下收紧，
    /// 具体类型冲突继续保留为诊断红门。
    /// </summary>
    private static bool CanSharpenGetterResultType(
        TypeAnalysisContext? destinationType,
        TypeAnalysisContext fieldType,
        TypeAnalysisContext getterReturnType)
    {
        if (destinationType == null
            || GenericCallRebinder.TypesEquivalent(destinationType, fieldType)
            || GenericCallRebinder.TypesEquivalent(destinationType, getterReturnType))
            return true;

        return GenericCallRebinder.IsAssignableToManagedProjection(
            getterReturnType,
            destinationType);
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
        FieldReference reference,
        bool read,
        out MethodAnalysisContext accessor) =>
        TryResolveAccessor(callerType, reference, receiverType: null, read, out accessor);

    private static bool TryResolveAccessor(
        TypeAnalysisContext callerType,
        FieldReference reference,
        TypeAnalysisContext? receiverType,
        bool read,
        out MethodAnalysisContext accessor)
    {
        accessor = null!;
        var field = reference.Field;
        if (field.Visibility != FieldAttributes.Private
            || IsWithinPrivateAccessScope(callerType, field.DeclaringType))
            return false;

        if (!TryResolveAccessorOwner(field.DeclaringType, receiverType ?? reference.Local.Type, out var owner,
                out var ownerConcretized))
            return false;
        var definitionOwner = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? genericOwner.GenericType
            : owner;
        var candidateNames = PropertyNameCandidates(field.Name);
        var properties = definitionOwner.Properties
            .Where(property => candidateNames.Contains(property.Name, StringComparer.Ordinal))
            .Where(property => property.IsStatic == field.IsStatic)
            .Where(property => PropertyTypeMatchesField(
                property,
                owner,
                field.FieldType,
                ownerConcretized))
            .ToArray();
        if (properties.Length != 1)
        {
            if (properties.Length != 0
                || !TryResolveAccessorMethod(definitionOwner, owner, field, ownerConcretized,
                    candidateNames, read, out accessor))
                return false;

            return true;
        }

        var selected = read ? properties[0].Getter : properties[0].Setter;
        if (selected is null
            || selected.Visibility != MethodAttributes.Public
            || selected.IsVirtual && !selected.IsFinal)
            return false;

        accessor = InstantiateAccessor(selected, owner);
        return true;
    }

    /// <summary>
    /// 属性表被裁剪时，仍以同声明类型中的精确公开访问器方法作为可验证入口。
    /// </summary>
    private static bool TryResolveAccessorMethod(
        TypeAnalysisContext definitionOwner,
        TypeAnalysisContext owner,
        FieldAnalysisContext field,
        bool ownerConcretized,
        IEnumerable<string> candidateNames,
        bool read,
        out MethodAnalysisContext accessor)
    {
        accessor = null!;
        var prefix = read ? "get_" : "set_";
        var candidates = definitionOwner.Methods
            .Where(method => candidateNames.Contains(method.Name.StartsWith(prefix, StringComparison.Ordinal)
                ? method.Name[prefix.Length..]
                : string.Empty, StringComparer.Ordinal))
            .Where(method => method.IsStatic == field.IsStatic)
            .Where(method => method.Visibility == MethodAttributes.Public)
            .Where(method => !method.IsVirtual || method.IsFinal)
            .Where(method => read ? method.Parameters.Count == 0 : method.Parameters.Count == 1)
            .Where(method => MethodTypeMatchesField(method, owner, field.FieldType, ownerConcretized, read))
            .ToArray();
        if (candidates.Length != 1)
            return false;

        accessor = InstantiateAccessor(candidates[0], owner);
        return true;
    }

    /// <summary>
    /// 访问器返回值或唯一写入参数必须与字段的具体泛型类型完全一致。
    /// </summary>
    private static bool MethodTypeMatchesField(
        MethodAnalysisContext method,
        TypeAnalysisContext owner,
        TypeAnalysisContext fieldType,
        bool ownerConcretized,
        bool read)
    {
        var type = read ? method.ReturnType : method.Parameters[0].ParameterType;
        var concreteType = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? GenericInstantiation.Instantiate(type, genericOwner.GenericArguments, [])
            : type;
        return GenericCallRebinder.TypesEquivalent(concreteType, fieldType)
               || ownerConcretized
               && (fieldType.FullName == "System.Object"
                   || GenericCallRebinder.IsSharedObjectPlaceholder(fieldType, concreteType));
    }

    /// <summary>
    /// 以字段声明类型为基准；仅当接收者是同一泛型定义的更具体实例时闭合 owner。
    /// </summary>
    private static bool TryResolveAccessorOwner(
        TypeAnalysisContext declaredOwner,
        TypeAnalysisContext? receiverType,
        out TypeAnalysisContext owner,
        out bool ownerConcretized)
    {
        owner = declaredOwner;
        ownerConcretized = false;
        if (declaredOwner is not GenericInstanceTypeAnalysisContext declaredGeneric
            || receiverType is not GenericInstanceTypeAnalysisContext receiverGeneric
            || !GenericCallRebinder.TypesEquivalent(
                declaredGeneric.GenericType,
                receiverGeneric.GenericType))
            return true;

        if (GenericCallRebinder.TypesEquivalent(declaredGeneric, receiverGeneric))
            return true;
        if (!GenericCallRebinder.IsSharedObjectPlaceholder(declaredGeneric, receiverGeneric))
            return false;

        owner = receiverGeneric;
        ownerConcretized = true;
        return true;
    }

    /// <summary>
    /// 编译器 backing field 与普通小驼峰字段都必须映射到唯一同名属性。
    /// </summary>
    private static HashSet<string> PropertyNameCandidates(string fieldName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        const string compilerSuffix = ">k__BackingField";
        if (fieldName.Length > 0 && fieldName[0] == '<'
            && fieldName.EndsWith(compilerSuffix, StringComparison.Ordinal))
            names.Add(fieldName[1..^compilerSuffix.Length]);

        const string recoveredPrefix = "__cg_";
        const string recoveredSuffix = "_k__BackingField";
        if (fieldName.StartsWith(recoveredPrefix, StringComparison.Ordinal)
            && fieldName.EndsWith(recoveredSuffix, StringComparison.Ordinal))
            names.Add(fieldName[recoveredPrefix.Length..^recoveredSuffix.Length]);

        var conventionalName = fieldName.TrimStart('_');
        if (conventionalName.StartsWith("m_", StringComparison.Ordinal)
            && conventionalName.Length > 2)
            conventionalName = conventionalName[2..];
        if (conventionalName.Length != 0)
        {
            // 中文注释：保留去除 m_ 成员前缀后的原始小驼峰候选；Unity 等程序集常用 m_Value 字段对应 value 属性，
            // 同时保留首字母大写候选，二者都必须经过唯一属性、类型和访问级别校验。
            names.Add(conventionalName);
            if (fieldName.StartsWith("m_", StringComparison.Ordinal)
                && char.IsUpper(conventionalName[0]))
                names.Add(char.ToLowerInvariant(conventionalName[0]) + conventionalName[1..]);
            names.Add(char.ToUpperInvariant(conventionalName[0]) + conventionalName[1..]);
        }

        // 中文注释：Unity 值类型的静态常量字段常以 *Vector 结尾，而公开属性使用去掉该后缀的名称；
        // 仅加入去后缀候选，最终仍由唯一性、类型、静态性和访问级别共同闭合，不把任意字段名直接当作属性。
        const string vectorSuffix = "Vector";
        if (conventionalName.EndsWith(vectorSuffix, StringComparison.Ordinal)
            && conventionalName.Length > vectorSuffix.Length)
            names.Add(conventionalName[..^vectorSuffix.Length]);
        return names;
    }

    /// <summary>
    /// 泛型实例上的属性类型先按实例实参具体化，再与字段声明类型比较。
    /// </summary>
    private static bool PropertyTypeMatchesField(
        PropertyAnalysisContext property,
        TypeAnalysisContext owner,
        TypeAnalysisContext fieldType,
        bool ownerConcretized)
    {
        var propertyType = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? GenericInstantiation.Instantiate(property.PropertyType, genericOwner.GenericArguments, [])
            : property.PropertyType;
        return GenericCallRebinder.TypesEquivalent(propertyType, fieldType)
               || ownerConcretized
               && (fieldType.FullName == "System.Object"
                   || GenericCallRebinder.IsSharedObjectPlaceholder(fieldType, propertyType));
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
    /// 私有访问域有方向：调用者位于声明类型或其嵌套后代中才成立。
    /// 外层访问内层及兄弟互访不因共享顶层声明而自动获得资格。
    /// </summary>
    private static bool IsWithinPrivateAccessScope(TypeAnalysisContext caller, TypeAnalysisContext declaringType)
    {
        var owner = OriginalDefinition(declaringType);
        var visited = new HashSet<TypeAnalysisContext>();
        for (TypeAnalysisContext? current = OriginalDefinition(caller); current != null;
             current = current.DeclaringType is { } parent ? OriginalDefinition(parent) : null)
        {
            if (!visited.Add(current)) return false;
            if (ReferenceEquals(current, owner)) return true;
        }
        return false;
    }

    private static TypeAnalysisContext OriginalDefinition(TypeAnalysisContext type) =>
        type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : type;
}
