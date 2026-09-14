using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitVectorArrayLoad(Instruction instruction, ArrayAccess array,
        RecoveredVectorCilState.Storage destination, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        // 数组声明身份和原生完整访问宽度共同决定连续元素，禁止把引用槽转换为整数。
        if (instruction.MemoryAccessWidthBits != destination.Shape.TotalWidthBits
            || !ManagedArraySpanRecoveryHelper.TryDescribe(instruction, context.AppContext.Binary.PointerSizeBytes, out var span))
            throw new UnresolvedCilSemanticException(method.FullName, "VECTOR_ARRAY_LAYOUT", $"数组元素、索引或完整跨度缺少精确布局：{instruction}");

        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var module = method.DeclaringModule!;
        var index = new CilLocalVariable(span.NativeIndex ? module.CorLibTypeFactory.IntPtr : module.CorLibTypeFactory.Int32);
        var bits = new CilLocalVariable(module.CorLibTypeFactory.UInt64);
        body.LocalVariables.Add(index);
        body.LocalVariables.Add(bits);
        if (array.Index is Immediate constant)
            instructions.Add(CilOpCodes.Ldc_I4, checked((int)constant.Value));
        else
            LoadLocal((LocalVariable)array.Index, method, locals);
        instructions.Add(CilOpCodes.Stloc, index);
        instructions.Add(CilOpCodes.Ldc_I8, 0L);
        instructions.Add(CilOpCodes.Stloc, destination.Low);
        if (destination.High != null)
        {
            instructions.Add(CilOpCodes.Ldc_I8, 0L);
            instructions.Add(CilOpCodes.Stloc, destination.High);
        }
        var elementDescriptor = module.DefaultImporter!.ImportTypeSignature(span.ElementType.ToTypeSignature(module)).ToTypeDefOrRef();
        // 索引仅求值一次，原生宽索引不截断；每个元素按其真实类型读取后复用字段位段合并器。
        for (var slot = 0; slot < span.Count; slot++)
        {
            LoadLocal(array.Array, method, locals);
            instructions.Add(CilOpCodes.Ldloc, index);
            if (slot != 0)
            {
                instructions.Add(CilOpCodes.Ldc_I4, slot);
                instructions.Add(CilOpCodes.Add);
            }
            instructions.Add(CilOpCodes.Ldelem, elementDescriptor);
            EmitScalarValueAsVectorBits(span.Kind, span.ElementBytes, method);
            instructions.Add(CilOpCodes.Stloc, bits);
            MergeVectorBits(destination, bits, slot * span.ElementBytes * 8, span.ElementBytes * 8, method);
        }
    }
}
