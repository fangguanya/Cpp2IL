using System.Reflection;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class Arm64CallingConventionResolverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void RawArgumentsCoverAllAapcs64RegistersInOrder()
    {
        Assert.That(
            Arm64CallingConventionResolver.RawArgumentRegisterNames,
            Is.EqualTo(new[]
            {
                "X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7",
                "V0", "V1", "V2", "V3", "V4", "V5", "V6", "V7"
            }));
    }

    [Test]
    [Category("边界值")]
    public void IndirectCallAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void IndirectJumpAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var jump = new Instruction(0, OpCode.IndirectJump, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(jump), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void MissingFinalFloatingRegisterIsRejected()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.RemoveAt(operands.Count - 1);
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void TrailingX8IndirectReturnCandidateIsAccepted()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.Add(new Register(null, "X8"));
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void TrailingOrdinaryRegisterIsRejectedAsReturnCandidate()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.Add(new Register(null, "X10"));
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void TwoSingleFieldsReturnThroughV0AndV1()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var aggregate = CreateValueType(
            "TwoSingleFields",
            appContext.SystemTypes.SystemSingleType,
            appContext.SystemTypes.SystemSingleType);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var registers = Arm64CallingConventionResolver.ReturnOperands(method)
            .Cast<Register>()
            .Select(register => register.Name)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registers, Is.EqualTo(new[] { "V0", "V1" }));
            Assert.That(Arm64CallingConventionResolver.ReturnsViaHiddenBuffer(method), Is.False);
        }
    }

    [Test]
    [Category("边界值")]
    public void FourDoubleFieldsUseAllFourHfaReturnRegisters()
    {
        var doubleType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemDoubleType;
        var aggregate = CreateValueType(
            "FourDoubleFields",
            doubleType,
            doubleType,
            doubleType,
            doubleType);
        var method = aggregate.InjectMethodContext(
            "ReturnAggregate",
            aggregate,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var registers = Arm64CallingConventionResolver.ReturnOperands(method)
            .Cast<Register>()
            .Select(register => register.Name)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registers, Is.EqualTo(new[] { "V0", "V1", "V2", "V3" }));
            Assert.That(Arm64CallingConventionResolver.ReturnsViaHiddenBuffer(method), Is.False);
        }
    }

    [Test]
    [Category("异常输入")]
    public void MixedOrFiveFieldAggregatesAreRejectedAsHfa()
    {
        var systemTypes = Cpp2IlApi.CurrentAppContext!.SystemTypes;
        var mixed = CreateValueType(
            "MixedFields",
            systemTypes.SystemSingleType,
            systemTypes.SystemInt32Type);
        var five = CreateValueType(
            "FiveSingleFields",
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType,
            systemTypes.SystemSingleType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(mixed, out _),
                Is.False);
            Assert.That(
                Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(five, out _),
                Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void MultiOperandReturnTreatsEveryComponentAsSource()
    {
        var first = new LocalVariable("first", new Register(null, "V0"));
        var second = new LocalVariable("second", new Register(null, "V1"));
        var instruction = new Instruction(0, OpCode.Return, first, second);

        Assert.That(instruction.Sources, Is.EqualTo(new IOperand[] { first, second }));
    }

    private static InjectedTypeAnalysisContext CreateValueType(
        string name,
        params TypeAnalysisContext[] fieldTypes)
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var valueType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.ValueType")!;
        var aggregate = appContext.InjectTypeIntoAllAssemblies(
                "Cpp2IL.Core.Tests",
                name,
                valueType,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
            .InjectedTypes[0];

        for (var index = 0; index < fieldTypes.Length; index++)
            aggregate.InjectFieldContext($"Component{index}", fieldTypes[index], FieldAttributes.Public);

        return aggregate;
    }
}
