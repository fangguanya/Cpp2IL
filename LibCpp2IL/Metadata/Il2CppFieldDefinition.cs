using System.IO;
using System.Reflection;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Reflection;

namespace LibCpp2IL.Metadata;

public class Il2CppFieldDefinition : ReadableClass
{
    public int nameIndex;
    public Il2CppVariableWidthIndex<Il2CppType> typeIndex;
    [Version(Max = 24)] public int customAttributeIndex;
    public uint token;

    public string? Name { get; private set; }

    public Il2CppType? RawFieldType => OwningContext.Binary.GetType(typeIndex);
    public Il2CppTypeReflectionData? FieldType => RawFieldType == null ? null : LibCpp2ILUtils.GetTypeReflectionData(RawFieldType);

    public Il2CppVariableWidthIndex<Il2CppFieldDefinition> FieldIndex => OwningContext.ReflectionCache.GetFieldIndexFromField(this);

    public Il2CppFieldDefaultValue? DefaultValue => OwningContext.Metadata.GetFieldDefaultValue(this);

    public Il2CppTypeDefinition DeclaringType => OwningContext.ReflectionCache.GetDeclaringTypeFromField(this);

    public override string? ToString()
    {
        return $"Il2CppFieldDefinition[Name={Name}, FieldType={FieldType}]";
    }

    public byte[] StaticArrayInitialValue
    {
        get
        {
            if (RawFieldType is not { } rawType || (rawType.Attrs & (uint)FieldAttributes.HasFieldRVA) == 0)
                return [];
            var fieldType = FieldType;
            if (fieldType is not { isArray: false, isPointer: false, isType: true, isGenericType: false, baseType: { } baseType })
                throw new InvalidDataException("FieldRVA 类型缺少可验证的初始化布局。");
            var metadata = OwningContext.Metadata;
            var data = DefaultValue ?? throw new InvalidDataException("FieldRVA 缺少原始初始化数据记录。");
            var length = StaticArrayInitializationHelper.ResolveLength(baseType.Name, baseType.Size);
            var section = metadata.metadataHeader.fieldAndParameterDefaultValueData;
            var pointer = StaticArrayInitializationHelper.ResolvePointer(section.Offset, section.Size, data.dataIndex.Value, length, metadata.Length);
            var result = metadata.ReadByteArrayAtRawAddress(pointer, length);
            if (result.Length != length)
                throw new InvalidDataException("FieldRVA 原始初始化数据截断。");
            return result;
        }
    }

    public override void Read(ClassReadingBinaryReader reader)
    {
        nameIndex = reader.ReadInt32();

        //Cache name now
        var pos = reader.Position;
        Name = ((Il2CppMetadata)reader).ReadStringFromIndexNoReadLock(nameIndex);
        reader.Position = pos;

        typeIndex = Il2CppVariableWidthIndex<Il2CppType>.Read(reader);
        if (IsAtMost(24f))
            customAttributeIndex = reader.ReadInt32();
        token = reader.ReadUInt32();
    }
}
