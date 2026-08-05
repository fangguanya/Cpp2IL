using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

/// <summary>
/// 为分析管线提供唯一的跨指令集调用约定入口，避免各恢复器重复判断架构。
/// </summary>
public static class CallingConventionResolver
{
    public static bool HasRawArgumentLayout(Instruction call, ApplicationAnalysisContext app)
        => IsArm64(app)
            ? Arm64CallingConventionResolver.HasRawArgumentLayout(call)
            : X64CallingConventionResolver.HasRawArgumentLayout(call, app);

    public static void RemapRawArguments(Instruction call, MethodAnalysisContext resolved)
    {
        if (IsArm64(resolved.AppContext))
            Arm64CallingConventionResolver.RemapRawArguments(call, resolved);
        else
            X64CallingConventionResolver.RemapRawArguments(call, resolved);
    }

    public static bool ReturnsViaHiddenBuffer(MethodAnalysisContext method)
        => IsArm64(method.AppContext)
            ? Arm64CallingConventionResolver.ReturnsViaHiddenBuffer(method)
            : X64CallingConventionResolver.ReturnsViaHiddenBuffer(method);

    public static Register? HiddenReturnBufferRegister(MethodAnalysisContext method)
        => IsArm64(method.AppContext)
            ? Arm64CallingConventionResolver.HiddenReturnBufferRegister(method)
            : X64CallingConventionResolver.HiddenReturnBufferRegister(method);

    public static Register ReturnRegister(MethodAnalysisContext method)
        => IsArm64(method.AppContext)
            ? Arm64CallingConventionResolver.ReturnRegister(method)
            : X64CallingConventionResolver.ReturnRegister(method);

    private static bool IsArm64(ApplicationAnalysisContext app)
        => app.InstructionSet is NewArmV8InstructionSet;
}
