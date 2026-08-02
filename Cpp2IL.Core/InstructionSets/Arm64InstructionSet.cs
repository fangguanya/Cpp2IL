using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.InstructionSets;

public class Arm64InstructionSet : Cpp2IlInstructionSet
{
    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var binary = context.AppContext.Binary;

        // 旧反汇编器仅保留为显式兼容入口，但文件切片同样必须遵守虚拟地址到文件偏移的映射契约。
        if (context is not ConcreteGenericMethodAnalysisContext &&
            Arm64MethodBodyReader.TryReadManagedMethodBody(binary, context.UnderlyingPointer, out var body))
            return body;

        var instructions = Arm64Utils.GetArm64MethodBodyAtVirtualAddress(binary, context.UnderlyingPointer);

        return new BinarySlice(instructions.SelectMany(i => i.Bytes).ToArray());
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        return [];
    }

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        return [];
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new Arm64KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context)
    {
        var sb = new StringBuilder();

        var instructions = Arm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext.Binary, context.UnderlyingPointer);

        var first = true;
        foreach (var instruction in instructions)
        {
            if (!first)
                sb.AppendLine();

            first = false;
            sb.Append("0x").Append(instruction.Address.ToString("X")).Append(" ").Append(instruction.Mnemonic).Append(" ").Append(instruction.Operand);
        }

        return sb.ToString();
    }
}
