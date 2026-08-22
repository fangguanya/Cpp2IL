using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core;

public static class Il2CppClassUsefulOffsets
{
    public const int X86_INTERFACE_OFFSETS_OFFSET = 0x50;
    public const int X86_64_INTERFACE_OFFSETS_OFFSET = 0xB0;

    public static int GetVtableOffset(float metadataVersion, bool is32Bit) =>
        metadataVersion >= 24.2f
            ? is32Bit ? 0x999 /*TODO*/ : 0x138
            : is32Bit ? 0x999 /*TODO*/ : 0x128;

    public static readonly List<UsefulOffset> UsefulOffsets =
    [
        new("cctor_finished", 0x74, typeof(uint), true),
        new("flags1", 0xBB, typeof(byte), true),
        //new UsefulOffset("interface_offsets_count", 0x12A, typeof(ushort), true), //TODO
        // new UsefulOffset("rgctx_data", 0xC0, typeof(IntPtr), true), //TODO
        new("interfaceOffsets", X86_INTERFACE_OFFSETS_OFFSET, typeof(IntPtr), true),
        new("static_fields", 0x5C, typeof(IntPtr), true),
        //new UsefulOffset("vtable", 0x138, typeof(IntPtr), true), //TODO

        //64-bit offsets:
        new("elementType", 0x40, typeof(IntPtr), false),
        new("interfaceOffsets", X86_64_INTERFACE_OFFSETS_OFFSET, typeof(IntPtr), false),
        new("static_fields", 0xB8, typeof(IntPtr), false),
        new("rgctx_data", 0xC0, typeof(IntPtr), false),
        new("cctor_finished", 0xE0, typeof(uint), false),
        new("interface_offsets_count", 0x12A, typeof(ushort), false),
        new("flags1", 0x132, typeof(byte), false),
        new("flags2", 0x133, typeof(byte), false),
        new("vtable", 0x138, typeof(IntPtr), false)
    ];

    public static bool IsStaticFieldsPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "static_fields";
    }

    public static bool IsInterfaceOffsetsPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "interfaceOffsets";
    }

    public static bool IsInterfaceOffsetsCount(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "interface_offsets_count";
    }

    public static bool IsRGCTXDataPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "rgctx_data";
    }

    public static bool IsElementTypePtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "elementType";
    }

    public static bool IsPointerIntoVtable(uint offset, float metadataVersion, bool is32Bit)
    {
        return offset >= GetVtableOffset(metadataVersion, is32Bit);
    }

    /// <summary>
    /// 返回当前虚调用解析器采用的 <c>Il2CppClass::vtable</c> 起始偏移。
    /// 该值同时供目标方法半槽与 MethodInfo 半槽识别，禁止两条恢复路径各自维护常量。
    /// </summary>
    public static int GetVirtualInvokeDataVtableOffset(bool is32Bit) => is32Bit ? 0xC0 : 0x138;

    /// <summary>
    /// 判断类元数据内的偏移是否精确落在 <c>VirtualInvokeData.method</c> 指针半槽。
    /// 每个虚表项由方法地址和 MethodInfo 指针组成；只有第二个指针才是托管调用
    /// 完成绑定后可删除的原生隐参证据，方法地址半槽必须继续保留给间接调用解析。
    /// </summary>
    public static bool IsVirtualInvokeMethodInfoOffset(long offset, bool is32Bit)
    {
        var vtableOffset = GetVirtualInvokeDataVtableOffset(is32Bit);
        var pointerSize = is32Bit ? 4 : 8;
        var relativeOffset = offset - vtableOffset;

        return relativeOffset >= pointerSize
               && relativeOffset % (pointerSize * 2L) == pointerSize;
    }

    public static string? GetOffsetName(uint offset, bool is32Bit) =>
        UsefulOffsets.FirstOrDefault(o => o.is32Bit == is32Bit && o.offset == offset)?.name;

    public class UsefulOffset(string name, uint offset, Type type, bool is32Bit)
    {
        public string name = name;
        public uint offset = offset;
        public Type type = type;
        public bool is32Bit = is32Bit;
    }
}
