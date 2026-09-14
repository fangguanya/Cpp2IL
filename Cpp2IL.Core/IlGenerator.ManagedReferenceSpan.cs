using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private sealed class RecoveredManagedSpanCilState
    {
        private readonly IReadOnlyDictionary<Instruction, ManagedReferenceSpanRecovery.Access> accesses;
        private readonly Dictionary<LocalVariable, CilLocalVariable[]> snapshots = [];

        internal RecoveredManagedSpanCilState(ISILControlFlowGraph graph, int pointerSize, MethodDefinition method)
        {
            accesses = ManagedReferenceSpanRecovery.Describe(graph, pointerSize);
            foreach (var access in accesses.Values)
            {
                if (!access.IsLoad || access.IsWholeValue)
                    continue;
                var components = new CilLocalVariable[access.Components.Count];
                for (var index = 0; index < components.Length; index++)
                {
                    components[index] = new CilLocalVariable(access.Components[index].Type.ToTypeSignature(method.DeclaringModule!));
                    method.CilMethodBody!.LocalVariables.Add(components[index]);
                }
                snapshots.Add(access.Value, components);
            }
        }

        internal bool Contains(Instruction instruction) => accesses.ContainsKey(instruction);

        internal bool TryEmit(Instruction instruction, MethodAnalysisContext context, MethodDefinition method,
            Dictionary<LocalVariable, CilLocalVariable> locals)
        {
            if (!accesses.TryGetValue(instruction, out var access) || access.IsWholeValue)
                return false;
            var instructions = method.CilMethodBody!.Instructions;
            var components = snapshots[access.Value];
            if (access.Reference is ArrayAccess array)
            {
                var indexSnapshot = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.Int32);
                method.CilMethodBody.LocalVariables.Add(indexSnapshot);
                if (array.Index is Immediate immediate)
                    instructions.Add(CilOpCodes.Ldc_I4, checked((int)immediate.Value));
                else
                    LoadLocal((LocalVariable)array.Index, method, locals);
                instructions.Add(CilOpCodes.Stloc, indexSnapshot);
                // 按原生读取位置保存全部元素；中间调用即使修改数组或触发GC，也不改变后续写回的值。
                for (var index = 0; index < components.Length; index++)
                {
                    LoadLocal(array.Array, method, locals);
                    instructions.Add(CilOpCodes.Ldloc, indexSnapshot);
                    if (index != 0)
                    {
                        instructions.Add(CilOpCodes.Ldc_I4, index);
                        instructions.Add(CilOpCodes.Add);
                    }
                    instructions.Add(CilOpCodes.Ldelem_Ref);
                    instructions.Add(CilOpCodes.Stloc, components[index]);
                }
                return true;
            }
            var fieldReference = (FieldReference)access.Reference;
            // 全部字段读取先进入独立快照；跨调用和别名写入期间，GC 持续跟踪每个引用。
            for (var index = 0; index < components.Length; index++)
            {
                var segment = access.Components[index];
                var field = segment.Field!;
                var reference = LoadRecoveredFieldSpanReceiver(fieldReference,
                    new ManagedFieldSpanRecoveryHelper.Segment(field, segment.OffsetBytes, segment.SizeBytes,
                        segment.Kind, segment.ParentFields), context, method, locals, access.IsLoad);
                if (access.IsLoad)
                {
                    EmitRecoveredFieldOperation(reference, context, method, read: true);
                    instructions.Add(CilOpCodes.Stloc, components[index]);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldloc, components[index]);
                    EmitRecoveredFieldOperation(reference, context, method, read: false);
                }
            }
            return true;
        }
    }
}
