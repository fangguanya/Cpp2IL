using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

/// <summary>投影全部原始声明与泛型表；仅提供来源和身份，不生成托管业务实现。</summary>
internal static class MetadataDeclarationInventoryHelper
{
    internal static void Write(ApplicationAnalysisContext application, string path)
    {
        var context = application.LibCpp2IlContext;
        var metadata = context.Metadata;
        var binary = context.Binary;
        var types = metadata.typeDefs;
        var methods = metadata.methodDefs;
        var typeOwners = MetadataOwnershipHelper.BuildOwners(types.Length,
            metadata.imageDefinitions.Select((image, index) =>
                new MetadataOwnershipHelper.Range(index, image.firstTypeIndex.Value, checked((int)image.typeCount))));
        var methodOwners = MetadataOwnershipHelper.BuildOwners(methods.Length,
            types.Select((type, index) => new MetadataOwnershipHelper.Range(index, type.FirstMethodIdx.Value, type.MethodCount)));
        var fieldOwners = MetadataOwnershipHelper.BuildOwners(metadata.FieldDefinitions.Count,
            types.Select((type, index) => new MetadataOwnershipHelper.Range(index, type.FirstFieldIdx.Value, type.FieldCount)));
        var propertyOwners = MetadataOwnershipHelper.BuildOwners(metadata.PropertyDefinitions.Count,
            types.Select((type, index) => new MetadataOwnershipHelper.Range(index, type.FirstPropertyId.Value, type.PropertyCount)));
        var eventOwners = MetadataOwnershipHelper.BuildOwners(metadata.EventDefinitions.Count,
            types.Select((type, index) => new MetadataOwnershipHelper.Range(index, type.FirstEventId.Value, type.EventCount)));
        var parameterOwners = MetadataOwnershipHelper.BuildOwners(metadata.ParameterDefinitions.Count,
            methods.Select((method, index) => new MetadataOwnershipHelper.Range(index, method.parameterStart.Value, method.parameterCount)));
        var genericParameterOwners = MetadataOwnershipHelper.BuildOwners(metadata.GenericParameters.Count,
            metadata.GenericContainers.Select((container, index) =>
                new MetadataOwnershipHelper.Range(index, container.genericParameterStart.Value, container.genericParameterCount)));

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "Cpp2IL.RawMetadataDeclarations/v1");
        writer.WriteBoolean("sourceRecoveryAccepted", false);
        writer.WriteBoolean("ownershipPartitionsValidated", true);
        writer.WriteNumber("metadataVersion", metadata.MetadataVersion);
        // 全局表索引保持原始顺序；声明 token 只在所属原始程序集内解释。
        WriteTable(writer, "assemblies", metadata.AssemblyDefinitions, (item, index) =>
        {
            writer.WriteString("name", item.AssemblyName.Name);
            writer.WriteNumber("token", item.Token);
            writer.WriteNumber("moduleToken", item.ModuleToken);
            WriteIndex(writer, "imageIndex", item.ImageIndex, metadata.imageDefinitions.Length);
            writer.WriteString("culture", item.AssemblyName.Culture);
            writer.WriteNumber("flags", item.AssemblyName.flags);
            writer.WriteNumber("publicKeyToken", item.AssemblyName.publicKeyToken);
            writer.WriteString("version", $"{item.AssemblyName.major}.{item.AssemblyName.minor}.{item.AssemblyName.build}.{item.AssemblyName.revision}");
            writer.WriteNumber("referencedAssemblyStart", item.ReferencedAssemblyStart);
            writer.WriteNumber("referencedAssemblyCount", item.ReferencedAssemblyCount);
        });
        WriteTable(writer, "images", metadata.imageDefinitions, (item, index) =>
        {
            writer.WriteString("name", item.Name);
            WriteIndex(writer, "assemblyIndex", item.assemblyIndex, metadata.AssemblyDefinitions.Length);
            writer.WriteNumber("token", item.token);
            writer.WriteNumber("firstTypeIndex", item.firstTypeIndex.Value);
            writer.WriteNumber("typeCount", item.typeCount);
            writer.WriteNumber("exportedTypeStart", item.exportedTypeStart.Value);
            writer.WriteNumber("exportedTypeCount", item.exportedTypeCount);
            writer.WriteNumber("entryPointIndex", item.entryPointIndex.Value);
            writer.WriteNumber("customAttributeStart", item.customAttributeStart);
            writer.WriteNumber("customAttributeCount", item.customAttributeCount);
        });
        WriteTable(writer, "types", types, (item, index) =>
        {
            writer.WriteNumber("imageIndex", typeOwners[index]);
            writer.WriteNumber("token", item.Token);
            writer.WriteString("fullName", item.FullName);
            writer.WriteNumber("flags", item.Flags);
            writer.WriteNumber("bitfield", item.Bitfield);
            WriteIndex(writer, "byvalTypeIndex", item.ByvalTypeIndex.Value, binary.NumTypes);
            WriteIndex(writer, "declaringTypeReferenceIndex", item.DeclaringTypeIndex.Value, binary.NumTypes, true);
            WriteIndex(writer, "parentTypeReferenceIndex", item.ParentIndex.Value, binary.NumTypes, true);
            WriteIndex(writer, "genericContainerIndex", item.GenericContainerIndex.Value, metadata.GenericContainers.Count, true);
            writer.WriteNumber("nestedTypesStart", item.NestedTypesStart.Value);
            writer.WriteNumber("nestedTypeCount", item.NestedTypeCount);
            writer.WriteNumber("interfacesStart", item.InterfacesStart.Value);
            writer.WriteNumber("interfaceCount", item.InterfacesCount);
            writer.WriteNumber("interfaceOffsetsStart", item.InterfaceOffsetsStart.Value);
            writer.WriteNumber("interfaceOffsetsCount", item.InterfaceOffsetsCount);
            writer.WriteNumber("vtableStart", item.VtableStart);
            writer.WriteNumber("vtableCount", item.VtableCount);
        });
        WriteTable(writer, "fields", metadata.FieldDefinitions, (item, index) =>
        {
            writer.WriteNumber("declaringTypeIndex", fieldOwners[index]);
            writer.WriteNumber("token", item.token);
            writer.WriteString("name", item.Name);
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
        });
        WriteTable(writer, "methods", methods, (item, index) =>
        {
            if (item.declaringTypeIdx.Value != methodOwners[index])
                throw new InvalidOperationException($"方法 {index} 的原始声明归属与范围不一致。");
            writer.WriteNumber("declaringTypeIndex", methodOwners[index]);
            writer.WriteNumber("token", item.token);
            writer.WriteString("name", item.Name);
            writer.WriteNumber("flags", item.flags);
            writer.WriteNumber("implementationFlags", item.iflags);
            writer.WriteNumber("slot", item.slot);
            writer.WriteNumber("parameterStart", item.parameterStart.Value);
            writer.WriteNumber("parameterCount", item.parameterCount);
            if (metadata.MetadataVersion >= 31)
                writer.WriteNumber("returnParameterToken", item.returnParameterToken);
            WriteIndex(writer, "returnTypeReferenceIndex", item.returnTypeIdx.Value, binary.NumTypes);
            WriteIndex(writer, "genericContainerIndex", item.genericContainerIndex.Value, metadata.GenericContainers.Count, true);
            // 原生导入仅按原始 PInvokeImpl 标志登记，不凭名称或零指针猜测外部实现。
            writer.WriteBoolean("pinvokeDeclaration", (item.flags & 0x2000) != 0);
        });
        WriteTable(writer, "parameters", metadata.ParameterDefinitions, (item, index) =>
        {
            writer.WriteNumber("declaringMethodIndex", parameterOwners[index]);
            writer.WriteNumber("token", item.token);
            writer.WriteString("name", item.Name);
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
        });
        WriteTable(writer, "properties", metadata.PropertyDefinitions, (item, index) =>
        {
            writer.WriteNumber("declaringTypeIndex", propertyOwners[index]);
            writer.WriteNumber("token", item.token);
            writer.WriteString("name", item.Name);
            writer.WriteNumber("attributes", item.attrs);
            writer.WriteNumber("getterOffsetInDeclaringType", item.get.Value);
            writer.WriteNumber("setterOffsetInDeclaringType", item.set.Value);
        });
        WriteTable(writer, "events", metadata.EventDefinitions, (item, index) =>
        {
            writer.WriteNumber("declaringTypeIndex", eventOwners[index]);
            writer.WriteNumber("token", item.token);
            writer.WriteString("name", item.Name);
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
            writer.WriteNumber("adderOffsetInDeclaringType", item.add.Value);
            writer.WriteNumber("removerOffsetInDeclaringType", item.remove.Value);
            writer.WriteNumber("raiserOffsetInDeclaringType", item.raise.Value);
        });
        WriteTable(writer, "genericContainers", metadata.GenericContainers, (item, index) =>
        {
            writer.WriteBoolean("isMethod", item.isGenericMethod);
            WriteIndex(writer, "ownerIndex", item.ownerIndex, item.isGenericMethod ? methods.Length : types.Length);
            writer.WriteNumber("parameterStart", item.genericParameterStart.Value);
            writer.WriteNumber("parameterCount", item.genericParameterCount);
        });
        WriteTable(writer, "genericParameters", metadata.GenericParameters, (item, index) =>
        {
            if (item.ownerIndex.Value != genericParameterOwners[index])
                throw new InvalidOperationException($"泛型参数 {index} 的原始归属与范围不一致。");
            writer.WriteNumber("ownerIndex", genericParameterOwners[index]);
            writer.WriteString("name", item.Name);
            writer.WriteNumber("ordinal", item.genericParameterIndexInOwner);
            writer.WriteNumber("flags", item.flags);
            writer.WriteNumber("constraintsStart", item.constraintsStart);
            writer.WriteNumber("constraintsCount", item.constraintsCount);
        });
        WriteTable(writer, "methodSpecs", metadata.methodSpecs, (item, index) =>
        {
            WriteIndex(writer, "methodDefinitionIndex", item.methodDefinitionIndex.Value, methods.Length);
            WriteIndex(writer, "classInstanceIndex", item.classIndexIndex.Value, binary.GenericInstances.Count, true);
            WriteIndex(writer, "methodInstanceIndex", item.methodIndexIndex.Value, binary.GenericInstances.Count, true);
        });
        WriteTable(writer, "genericMethodFunctions", metadata.genericMethodTables, (item, index) =>
        {
            WriteIndex(writer, "methodSpecIndex", item.GenericMethodIndex, metadata.methodSpecs.Length);
            WriteIndex(writer, "methodPointerIndex", item.methodIndex, binary.GenericMethodPointers.Count, true);
            writer.WriteNumber("invokerIndex", item.invokerIndex);
            writer.WriteNumber("adjustorThunkIndex", item.adjustorThunk);
        });
        WriteTable(writer, "genericInstances", binary.GenericInstances, (item, index) =>
        {
            writer.WriteNumber("argumentCount", item.pointerCount);
            writer.WriteNumber("argumentPointerStart", item.pointerStart);
            writer.WriteStartArray("argumentTypePointers");
            foreach (var pointer in item.Pointers)
                writer.WriteNumberValue(pointer);
            writer.WriteEndArray();
        });
        WriteTable(writer, "typeReferences", binary.AllTypes, (item, index) =>
        {
            writer.WriteNumber("data", item.Datapoint);
            writer.WriteNumber("bits", item.Bits);
            writer.WriteString("kind", item.Type.ToString());
            writer.WriteNumber("attributes", item.Attrs);
            writer.WriteNumber("byref", item.Byref);
            writer.WriteNumber("pinned", item.Pinned);
        });
        // 默认值保留原始类型、数据索引及来源区段，不把解码失败或空索引解释为默认实现。
        WriteTable(writer, "fieldDefaultValues", metadata.FieldDefaultValues, (item, index) =>
        {
            WriteIndex(writer, "fieldIndex", item.fieldIndex.Value, metadata.FieldDefinitions.Count);
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
            WriteIndex(writer, "dataIndex", item.dataIndex.Value, metadata.metadataHeader.fieldAndParameterDefaultValueData.Size, true);
            var field = metadata.FieldDefinitions[item.fieldIndex.Value];
            if ((binary.GetType(field.typeIndex).Attrs & 0x100) != 0)
            {
                var bytes = field.StaticArrayInitialValue;
                writer.WriteString("decodeStatus", "initializer-data");
                writer.WriteNumber("initializerLength", bytes.Length);
                writer.WriteBase64String("initializerBytes", bytes);
            }
            else
                MetadataDefaultValueHelper.Write(writer, () => item.Value);
        });
        WriteTable(writer, "parameterDefaultValues", metadata.ParameterDefaultValues, (item, index) =>
        {
            WriteIndex(writer, "parameterIndex", item.parameterIndex.Value, metadata.ParameterDefinitions.Count);
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
            WriteIndex(writer, "dataIndex", item.dataIndex.Value, metadata.metadataHeader.fieldAndParameterDefaultValueData.Size, true);
            MetadataDefaultValueHelper.Write(writer, () => item.ContainedDefaultValue);
        });
        writer.WriteNumber("defaultValueDataOffset", metadata.metadataHeader.fieldAndParameterDefaultValueData.Offset);
        writer.WriteNumber("defaultValueDataSize", metadata.metadataHeader.fieldAndParameterDefaultValueData.Size);
        if (metadata.MetadataVersion >= 29)
        {
            writer.WriteNumber("attributeDataOffset", metadata.metadataHeader.attributeData.Offset);
            writer.WriteNumber("attributeDataSize", metadata.metadataHeader.attributeData.Size);
        }
        WriteTable(writer, "nestedTypes", metadata.NestedTypeIndices, (item, index) =>
            WriteIndex(writer, "typeDefinitionIndex", item.Value, types.Length));
        WriteTable(writer, "interfaceOffsets", metadata.InterfaceOffsets, (item, index) =>
        {
            WriteIndex(writer, "typeReferenceIndex", item.typeIndex.Value, binary.NumTypes);
            writer.WriteNumber("offset", item.offset);
        });
        WriteNumbers(writer, "encodedVtableMethods", metadata.VTableMethodIndices.Select(value => (long)value));
        WriteTable(writer, "attributeDataRanges", metadata.AttributeDataRanges ?? [], (item, index) =>
        {
            writer.WriteNumber("token", item.token);
            writer.WriteNumber("startOffset", item.startOffset);
        });
        if (metadata.MetadataVersion >= 29)
        {
            var ranges = metadata.AttributeDataRanges ?? throw new InvalidDataException("缺少原始特性范围表。");
            var section = metadata.metadataHeader.attributeData;
            if (section.Offset < 0 || section.Size < 0 || (long)section.Offset + section.Size > metadata.Length)
                throw new InvalidDataException("特性数据区段超出原始文件。");
            var lengths = MetadataAttributeRangeHelper.BuildLengths(ranges.Select(range => range.startOffset).ToArray(), section.Size);
            if ((ranges[ranges.Count - 1].token & 0x00FFFFFF) != 0)
                throw new InvalidDataException("特性终止条目不是零 RID 哨兵。");
            // 区段可以包含末尾对齐字节；保留全部原始内容，不将其拼入最后一个特性。
            var tailStart = ranges[ranges.Count - 1].startOffset;
            writer.WriteNumber("attributeSectionTailStart", tailStart);
            writer.WriteBase64String("attributeSectionTailBytes", metadata.ReadByteArrayAtRawAddress(
                (long)section.Offset + tailStart, checked(section.Size - (int)tailStart)));
            var owners = MetadataOwnershipHelper.BuildOwners(lengths.Length,
                metadata.imageDefinitions.Select((item, index) =>
                    new MetadataOwnershipHelper.Range(index, item.customAttributeStart, checked((int)item.customAttributeCount))));
            var identities = new HashSet<(int Image, uint Token)>();
            // 内容仅从已验证的 metadata 区段读取；末项只提供终止偏移，不生成虚假身份。
            WriteTable(writer, "attributeBlobs", lengths, (length, index) =>
            {
                var range = ranges[index];
                if (range.token == 0 || !identities.Add((owners[index], range.token)))
                    throw new InvalidDataException("原始特性对象身份为空或重复。");
                var bytes = metadata.ReadByteArrayAtRawAddress((long)section.Offset + range.startOffset, length);
                if (bytes.Length != length)
                    throw new InvalidDataException("原始特性内容截断。");
                writer.WriteNumber("imageIndex", owners[index]);
                writer.WriteNumber("token", range.token);
                writer.WriteNumber("startOffset", range.startOffset);
                writer.WriteNumber("byteLength", length);
                writer.WriteBase64String("bytes", bytes);
                writer.WriteBoolean("semanticDecodeAccepted", false);
                MetadataAttributeDecodeHelper.Write(writer, bytes, application);
            });
        }
        WriteTable(writer, "attributeTypeRanges", metadata.attributeTypeRanges ?? [], (item, index) =>
        {
            writer.WriteNumber("token", item.token);
            writer.WriteNumber("start", item.start);
            writer.WriteNumber("count", item.count);
        });
        WriteNumbers(writer, "referencedAssemblyIndices", metadata.referencedAssemblies.Select(value => (long)value));
        WriteNumbers(writer, "genericConstraintTypeIndices", metadata.constraintIndices.Select(value => (long)value.Value));
        WriteNumbers(writer, "interfaceTypeIndices", metadata.interfaceIndices.Select(value => (long)value.Value));
        WriteNumbers(writer, "exportedTypeIndices", (metadata.exportedTypes ?? []).Select(value => (long)value));
        WriteNumbers(writer, "attributeTypeIndices", (metadata.attributeTypes ?? []).Select(value => (long)value));
        writer.WriteStartArray("genericMethodPointers");
        foreach (var pointer in binary.GenericMethodPointers)
            writer.WriteNumberValue(pointer);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTable<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T> table, Action<T, int> write)
    {
        writer.WriteStartArray(name);
        for (var index = 0; index < table.Count; index++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", index);
            write(table[index], index);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIndex(Utf8JsonWriter writer, string name, int index, int count, bool nullable = false)
    {
        if ((index < 0 && !(nullable && index == -1)) || index >= count)
            throw new InvalidOperationException($"原始索引 {name}={index} 超出表长度 {count}。");
        writer.WriteNumber(name, index);
    }

    private static void WriteNumbers(Utf8JsonWriter writer, string name, IEnumerable<long> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
