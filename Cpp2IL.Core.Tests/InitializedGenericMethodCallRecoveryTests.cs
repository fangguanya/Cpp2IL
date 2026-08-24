using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class InitializedGenericMethodCallRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 初始化方法槽把EnumerableToListGetItem调用收紧到具体元素类型()
    {
        var fixture = CreateFixture();
        var exactTargets = new Dictionary<ulong, MethodAnalysisContext>
        {
            [0x1000] = fixture.ExactElement,
            [0x1008] = fixture.ExactToList,
            [0x1010] = fixture.ExactGetItem,
        };

        var recovered = RuntimeMetadataSlotResolver.RecoverInitializedGenericCallTargets(
            [fixture.ElementCall, fixture.ToListCall, fixture.GetItemCall],
            exactTargets.Keys.ToArray(),
            address => exactTargets.GetValueOrDefault(address));

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(3));
            Assert.That(fixture.ElementCall.Operands[0], Is.SameAs(fixture.ExactElement));
            Assert.That(fixture.ToListCall.Operands[0], Is.SameAs(fixture.ExactToList));
            Assert.That(fixture.GetItemCall.Operands[0], Is.SameAs(fixture.ExactGetItem));
            Assert.That(fixture.ElementResult.Type, Is.SameAs(fixture.StringType));
            Assert.That(fixture.ListResult.Type!.FullName, Does.Contain("System.String"));
            Assert.That(fixture.ItemResult.Type, Is.SameAs(fixture.StringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已具体化调用与同一初始化槽保持原目标()
    {
        var fixture = CreateFixture();
        var exactCall = new Instruction(
            0,
            OpCode.Call,
            fixture.ExactElement,
            fixture.ElementResult,
            new LocalVariable("source", new Register(null, "X0"), fixture.ObjectType));

        var recovered = RuntimeMetadataSlotResolver.RecoverInitializedGenericCallTargets(
            [exactCall],
            [0x1000UL],
            _ => fixture.ExactElement);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(exactCall.Operands[0], Is.SameAs(fixture.ExactElement));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一基方法存在两个不同具体实参时保持共享目标()
    {
        var fixture = CreateFixture();
        var intElement = new ConcreteGenericMethodAnalysisContext(
            fixture.ExactElement.BaseMethodContext,
            [],
            [Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type]);
        var targets = new Dictionary<ulong, MethodAnalysisContext>
        {
            [0x1000] = fixture.ExactElement,
            [0x1008] = intElement,
        };
        var original = fixture.ElementCall.Operands[0];

        var recovered = RuntimeMetadataSlotResolver.RecoverInitializedGenericCallTargets(
            [fixture.ElementCall],
            targets.Keys.ToArray(),
            address => targets.GetValueOrDefault(address));

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.ElementCall.Operands[0], Is.SameAs(original));
            Assert.That(fixture.ElementResult.Type!.FullName, Does.Contain("System.Object"));
        });
    }

    private static Fixture CreateFixture()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var objectType = app.SystemTypes.SystemObjectType;
        var stringType = app.SystemTypes.SystemStringType;
        var enumerable = app.GetAssemblyByName("System.Core")!
            .GetTypeByFullName("System.Linq.Enumerable")!;
        var element = enumerable.Methods.Single(method =>
            method.Name == "Last"
            && method.Parameters.Count == 1
            && method.GenericParameters.Count == 1);
        var toList = enumerable.Methods.Single(method =>
            method.Name == "ToList"
            && method.Parameters.Count == 1
            && method.GenericParameters.Count == 1);
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var getItem = listDefinition.Methods.Single(method =>
            method.Name == "get_Item"
            && method.Parameters.Count == 1);

        var sharedElement = new ConcreteGenericMethodAnalysisContext(element, [], [objectType]);
        var sharedToList = new ConcreteGenericMethodAnalysisContext(toList, [], [objectType]);
        var sharedGetItem = new ConcreteGenericMethodAnalysisContext(getItem, [objectType], []);
        var exactElement = new ConcreteGenericMethodAnalysisContext(element, [], [stringType]);
        var exactToList = new ConcreteGenericMethodAnalysisContext(toList, [], [stringType]);
        var exactGetItem = new ConcreteGenericMethodAnalysisContext(getItem, [stringType], []);

        var source = new LocalVariable("source", new Register(null, "X0"), sharedElement.Parameters[0].ParameterType);
        var elementResult = new LocalVariable("elementResult", new Register(null, "X0", 2), sharedElement.ReturnType);
        var listResult = new LocalVariable("listResult", new Register(null, "X0", 3), sharedToList.ReturnType);
        var index = new LocalVariable("index", new Register(null, "X1"), app.SystemTypes.SystemInt32Type);
        var itemResult = new LocalVariable("itemResult", new Register(null, "X0", 4), objectType);

        return new Fixture(
            objectType,
            stringType,
            exactElement,
            exactToList,
            exactGetItem,
            elementResult,
            listResult,
            itemResult,
            new Instruction(0, OpCode.Call, sharedElement, elementResult, source),
            new Instruction(1, OpCode.Call, sharedToList, listResult, source),
            new Instruction(2, OpCode.Call, sharedGetItem, itemResult, listResult, index));
    }

    private sealed record Fixture(
        TypeAnalysisContext ObjectType,
        TypeAnalysisContext StringType,
        ConcreteGenericMethodAnalysisContext ExactElement,
        ConcreteGenericMethodAnalysisContext ExactToList,
        ConcreteGenericMethodAnalysisContext ExactGetItem,
        LocalVariable ElementResult,
        LocalVariable ListResult,
        LocalVariable ItemResult,
        Instruction ElementCall,
        Instruction ToListCall,
        Instruction GetItemCall);
}
