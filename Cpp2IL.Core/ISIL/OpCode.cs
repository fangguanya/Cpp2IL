using Cpp2IL.Core.Analysis;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// If changing this, also update <see cref="Instruction"/>
/// </summary>
public enum OpCode
{
    /// <summary>Invalid instruction, op 1 is the debug string</summary>
    Invalid,

    /// <summary>Not implemented instruction, op 1 is the debug string</summary>
    NotImplemented,

    /// <summary>
    /// Interrupt, kept for stack analysis
    /// </summary>
    Interrupt,

    /// <summary>
    /// No operation
    /// </summary>
    Nop,

    /// <summary>Moves op 2 into op 1</summary>
    Move,

    /// <summary>Moves the result of phi function into op 1, other operands are inputs</summary>
    Phi,

    /// <summary>Calls a method @ op 1, moves the result into op 2, and the rest are params</summary>
    Call,

    /// <summary>Calls a method @ op 1, the rest are params</summary>
    CallVoid,

    /// <summary>Calls a method @ op 1, the rest are params</summary>
    IndirectCall,

    /// <summary>Returns from the method, op 1 is the value to return (optional)</summary>
    Return,

    /// <summary>Jumps to op 1</summary>
    Jump,

    /// <summary>Jumps to op 1</summary>
    IndirectJump,

    /// <summary><c>If op 2 is true, jumps to op 1</summary>
    ConditionalJump,

    /// <summary>op 2 为真时选 op 3，否则选 op 4，并把结果写入 op 1</summary>
    ConditionalSelect,

    /// <summary>Adds op 1 to stack pointer</summary>
    ShiftStack,

    /// <summary>Adds op 2 and op 3, and moves the result into op 1</summary>
    Add,

    /// <summary>Subtracts op 3 from op 2, and moves the result into op 1</summary>
    Subtract,

    /// <summary>Multiplies op 2 by op 3, and moves the result into op 1</summary>
    Multiply,

    /// <summary>Divides op 2 by op 3, and moves the result into op 1</summary>
    Divide,

    /// <summary>Divides op 2 by op 3, and moves the remainder into op 1</summary>
    Modulo,

    /// <summary>Shifts the bits of op 2 left by op 3, and moves the result into op 1</summary>
    ShiftLeft,

    /// <summary>Shifts the bits of op 2 right by op 3, and moves the result into op 1</summary>
    ShiftRight,

    /// <summary>将操作数2按无符号位模式逻辑右移操作数3位，并把结果写入操作数1</summary>
    ShiftRightUnsigned,

    /// <summary>Bitwise AND on op 2 and op 3, moves the result into op 1</summary>
    And,

    /// <summary>Bitwise OR on op 2 and op 3, moves the result into op 1</summary>
    Or,

    /// <summary>Bitwise XOR on op 2 and op 3, moves the result into op 1</summary>
    Xor,

    /// <summary>Logical not on op 2, moves the result into op 1</summary>
    Not,

    /// <summary>Negates op 2, moves the result into op 1</summary>
    Negate,

    /// <summary>按 op 3 浮点位宽计算 op 2 的绝对值并写入 op 1</summary>
    AbsoluteNumber,

    /// <summary>按 op 4 浮点位宽计算 op 2 与 op 3 之差的绝对值并写入 op 1</summary>
    AbsoluteDifference,

    /// <summary>按 IEEE-754 maximumNumber 语义选择 op 2/op 3 并写入 op 1；op 4 为浮点位宽</summary>
    MaximumNumber,

    /// <summary>按 op 3 指定的目标位宽转换浮点精度，并把 op 2 写入 op 1</summary>
    ConvertFloatingPointPrecision,

    /// <summary>把 op 2 向零舍入为 op 3 指定位宽的有符号整数，并写入 op 1</summary>
    ConvertFloatToSignedInteger,

    /// <summary>把 op 2 的有符号整数按 op 3 目标浮点位宽和 op 4 源整数位宽转换，并写入 op 1</summary>
    ConvertSignedIntegerToFloat,

    /// <summary>把 op 2 的有符号整数从 op 4 源位宽转换到 op 3 目标位宽，并写入 op 1</summary>
    ConvertSignedIntegerWidth,

    /// <summary>把 op 2 的整数位模式按 op 3 指定位宽原样解释为浮点值，并写入 op 1</summary>
    ReinterpretIntegerBitsAsFloat,

    /// <summary>把 op 2 的浮点位模式按 op 3 指定位宽原样解释为整数值，并写入 op 1</summary>
    ReinterpretFloatBitsAsInteger,

    /// <summary>把 op 2 的低位元素复制为 op 3 个 op 4 位向量通道，并写入 op 1</summary>
    VectorDuplicate,

    /// <summary>把 op 2 的无符号16位向量通道拓宽为32位，并写入 op 1；op 3 为通道数</summary>
    VectorWidenUnsignedInt16ToInt32,

    /// <summary>把 op 2 的向量通道左移 op 3 位并写入 op 1；op 4/5 为通道数和元素位宽</summary>
    VectorShiftLeft,

    /// <summary>逐通道判断 op 2 是否小于零并把掩码写入 op 1；op 3/4 为通道数和元素位宽</summary>
    VectorCompareLessThanZero,

    /// <summary>以 op 2 为掩码在 op 3 与 op 4 之间逐位选择并写入 op 1；op 5 为向量位宽</summary>
    VectorBitwiseSelect,

    /// <summary>把 op 2 的浮点向量乘以 op 3 的指定元素；op 4/5/6 为元素索引、通道数和元素位宽</summary>
    VectorMultiplyByElement,

    /// <summary>
    /// 把op 2的四个16位累计低位与op 3对op 4..7四个常量的逐通道比较合并；
    /// op 8的位掩码指定使用“标量大于常量”的反向通道，最终全通道成立时把true写入op 1。
    /// </summary>
    VectorAllLanesPredicate,

    /// <summary>从op 2打包的低64位向量中提取op 3指定的无符号16位通道并写入op 1</summary>
    VectorExtractUnsignedInt16,

    /// <summary>把 op 2 向正无穷舍入，并按 op 3 指定的浮点位宽写入 op 1</summary>
    RoundFloatTowardPositiveInfinity,

    /// <summary>把 op 2 向负无穷舍入，并按 op 3 指定的浮点位宽写入 op 1</summary>
    RoundFloatTowardNegativeInfinity,

    /// <summary>Moves 1 into op 1, if op 2 and op 3 are equal</summary>
    CheckEqual,

    /// <summary>Moves 1 into op 1, if op 2 is greater than op 3</summary>
    CheckGreater,

    /// <summary>Moves 1 into op 1, if op 2 is less than op 3</summary>
    CheckLess,

    /// <summary>Moves 1 into op 1, if op 2 and op 3 are not equal</summary>
    CheckNotEqual,

    /// <summary>Moves 1 into op 1, if op 2 is greater than or equal to op 3</summary>
    CheckGreaterOrEqual,

    /// <summary>Moves 1 into op 1, if op 2 is less than or equal to op 3</summary>
    CheckLessOrEqual,

    /// <summary>将 op 2 与 op 3 按无符号值比较；op 2 大于 op 3 时，把 1 写入 op 1</summary>
    CheckGreaterUnsigned,

    /// <summary>将 op 2 与 op 3 按无符号值比较；op 2 小于 op 3 时，把 1 写入 op 1</summary>
    CheckLessUnsigned,

    /// <summary>将 op 2 与 op 3 按无符号值比较；op 2 大于等于 op 3 时，把 1 写入 op 1</summary>
    CheckGreaterOrEqualUnsigned,

    /// <summary>将 op 2 与 op 3 按无符号值比较；op 2 小于等于 op 3 时，把 1 写入 op 1</summary>
    CheckLessOrEqualUnsigned,

    /// <summary>
    /// Allocates a new, uninitialized instance of the type described by op 2 and moves it into op 1.
    /// </summary>
    Newobj,

    /// <summary>把 op 2 的值类型值装箱为 op 3 指定的值类型，并把对象写入 op 1。</summary>
    Box,

    /// <summary>把 op 2 的对象按 op 3 指定的值类型拆箱，并把值写入 op 1。</summary>
    Unbox,

    /// <summary>把 op 2 的对象强制转换为 op 3 指定的引用类型，并把结果写入 op 1。</summary>
    CastClass,

    /// <summary>测试 op 2 是否兼容 op 3 指定的引用类型；成功时写入原对象，失败时写入 null。</summary>
    IsInst,

    /// <summary>
    /// Allocates a new array of the type described by op 2, with the length in op 3, into op 1.
    /// </summary>
    NewArr,

    /// <summary>
    /// Throws a new instance of the exception type described by op 1.
    /// </summary>
    Throw
}
