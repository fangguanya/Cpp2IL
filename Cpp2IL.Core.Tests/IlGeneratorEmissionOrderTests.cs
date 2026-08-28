using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class IlGeneratorEmissionOrderTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 追加到块表尾部的临界边桥先于消费者发射()
    {
        var entry = 新建块(0, BlockType.Entry);
        var exit = 新建块(1, BlockType.Exit);
        var branch = 新建块(2, BlockType.TwoWay);
        var ordinaryPath = 新建块(3, BlockType.OneWay);
        var consumer = 新建块(4, BlockType.Return);
        var edgeBridge = 新建块(5, BlockType.OneWay);
        var unreachable = 新建块(6, BlockType.Return);

        连接(entry, branch);
        // 先登记普通路径、后登记桥路径，使 RPO 把桥紧邻条件前驱发射。
        连接(branch, ordinaryPath);
        连接(branch, edgeBridge);
        连接(ordinaryPath, consumer);
        连接(edgeBridge, consumer);
        连接(consumer, exit);

        var graph = 创建图(
            entry,
            exit,
            [entry, exit, consumer, ordinaryPath, branch, unreachable, edgeBridge]);
        var order = IlGenerator.GetReachableBlockEmissionOrder(graph).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(order[0], Is.SameAs(entry));
            Assert.That(order.IndexOf(edgeBridge), Is.EqualTo(order.IndexOf(branch) + 1));
            Assert.That(order.IndexOf(edgeBridge), Is.LessThan(order.IndexOf(consumer)));
            Assert.That(order, Does.Not.Contain(unreachable));
        });
    }

    [Test]
    [Category("边界值")]
    public void 循环退出桥排在回边体之后且消费者之前()
    {
        var entry = 新建块(0, BlockType.Entry);
        var exit = 新建块(1, BlockType.Exit);
        var loopHead = 新建块(2, BlockType.TwoWay);
        var loopBody = 新建块(3, BlockType.OneWay);
        var exitBridge = 新建块(4, BlockType.OneWay);
        var consumer = 新建块(5, BlockType.Return);

        连接(entry, loopHead);
        // 先遍历退出边、再遍历循环体，反向后序会保持“循环头、循环体、退出桥、消费者”。
        连接(loopHead, exitBridge);
        连接(loopHead, loopBody);
        连接(loopBody, loopHead);
        连接(exitBridge, consumer);
        连接(consumer, exit);

        var graph = 创建图(
            entry,
            exit,
            [entry, exit, consumer, loopBody, loopHead, exitBridge]);
        var order = IlGenerator.GetReachableBlockEmissionOrder(graph);

        Assert.That(
            order,
            Is.EqualTo(new[] { entry, loopHead, loopBody, exitBridge, consumer, exit }));
    }

    [Test]
    [Category("异常输入")]
    public void 可达边指向未登记块时立即报告图损坏()
    {
        var entry = 新建块(0, BlockType.Entry);
        var exit = 新建块(1, BlockType.Exit);
        var dangling = 新建块(99, BlockType.OneWay);
        连接(entry, dangling);

        var graph = 创建图(entry, exit, [entry, exit]);
        var exception = Assert.Throws<DecompilerException>(
            () => IlGenerator.GetReachableBlockEmissionOrder(graph));

        Assert.That(exception!.Message, Does.Contain("from=0，to=99"));
    }

    [Test]
    [Category("基本功能")]
    public void GenerateIl按可达顺序修复分支标签并跳过不可达块()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemInt32 = app.SystemTypes.SystemInt32Type;
        var result = new LocalVariable("result", new Register(null, "W1"), systemInt32);
        var unreachableResult = new LocalVariable("unreachableResult", new Register(null, "W2"), systemInt32);

        var entry = 新建块(0, BlockType.Entry);
        var exit = 新建块(1, BlockType.Exit);
        var branch = 新建块(2, BlockType.TwoWay);
        var ordinaryPath = 新建块(3, BlockType.OneWay);
        var consumer = 新建块(4, BlockType.Return);
        var edgeBridge = 新建块(5, BlockType.OneWay);
        var unreachable = 新建块(6, BlockType.Return);

        var conditionalJump = new Instruction(0, OpCode.ConditionalJump, edgeBridge, new Immediate(1));
        var bridgeMove = new Instruction(1, OpCode.Move, result, new Immediate(22));
        var consumerReturn = new Instruction(5, OpCode.Return, result);
        branch.Instructions = [conditionalJump];
        ordinaryPath.Instructions =
        [
            new Instruction(2, OpCode.Move, result, new Immediate(11)),
            new Instruction(3, OpCode.Jump, consumer),
        ];
        edgeBridge.Instructions =
        [
            bridgeMove,
            new Instruction(4, OpCode.Jump, consumer),
        ];
        consumer.Instructions = [consumerReturn];
        unreachable.Instructions =
        [
            new Instruction(6, OpCode.Move, unreachableResult, new Immediate(99)),
            new Instruction(7, OpCode.Return, unreachableResult),
        ];

        连接(entry, branch);
        连接(branch, ordinaryPath);
        连接(branch, edgeBridge);
        连接(ordinaryPath, consumer);
        连接(edgeBridge, consumer);
        连接(consumer, exit);

        var graph = 创建图(
            entry,
            exit,
            [entry, exit, consumer, ordinaryPath, branch, unreachable, edgeBridge]);
        var context = new InjectedMethodAnalysisContext(
            app.SystemTypes.SystemObjectType,
            "CriticalEdgeDiamond",
            systemInt32,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [])
        {
            ControlFlowGraph = graph,
            Locals = [result],
            ParameterLocals = [],
            AnalysisWarnings = [],
        };

        var module = new ModuleDefinition(
            "CriticalEdgeDiamondTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemInt32,
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "CriticalEdgeDiamondType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "CriticalEdgeDiamond",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);
        var validation = CilStackValidator.Validate(
            method.CilMethodBody!,
            "CriticalEdgeDiamondType::CriticalEdgeDiamond");
        var emitted = method.CilMethodBody!.Instructions.ToList();
        var bridgeValue = emitted.Single(instruction =>
            instruction.OpCode == CilOpCodes.Ldc_I4 && Equals(instruction.Operand, 22));
        var ordinaryValue = emitted.Single(instruction =>
            instruction.OpCode == CilOpCodes.Ldc_I4 && Equals(instruction.Operand, 11));
        var consumerLoad = emitted.Single(instruction => instruction.OpCode == CilOpCodes.Ldloc);
        var trueBranch = emitted.Single(instruction => instruction.OpCode == CilOpCodes.Brtrue);
        var falseBranch = emitted[emitted.IndexOf(trueBranch) + 1];

        Assert.Multiple(() =>
        {
            Assert.That(conditionalJump.Operands[0], Is.SameAs(bridgeMove));
            Assert.That(trueBranch.Operand, Is.TypeOf<CilInstructionLabel>());
            Assert.That(((CilInstructionLabel)trueBranch.Operand!).Instruction, Is.SameAs(bridgeValue));
            Assert.That(falseBranch.OpCode, Is.EqualTo(CilOpCodes.Br));
            Assert.That(((CilInstructionLabel)falseBranch.Operand!).Instruction, Is.SameAs(ordinaryValue));
            Assert.That(emitted.IndexOf(bridgeValue), Is.LessThan(emitted.IndexOf(consumerLoad)));
            Assert.That(emitted.Any(instruction => Equals(instruction.Operand, 99)), Is.False);
            Assert.That(method.CilMethodBody.LocalVariables, Has.Count.EqualTo(1));
            Assert.That(validation.MaxStack, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// 创建只携带顺序测试所需身份与类型的基本块。
    /// </summary>
    private static Block 新建块(int id, BlockType type) => new()
    {
        ID = id,
        BlockType = type,
    };

    /// <summary>
    /// 同时维护前驱与后继，保证测试图满足生产 CFG 的双向边约束。
    /// </summary>
    private static void 连接(Block source, Block target)
    {
        source.Successors.Add(target);
        target.Predecessors.Add(source);
    }

    /// <summary>
    /// 用显式块顺序覆盖构造器的默认图，模拟分析阶段在列表尾部追加桥块。
    /// </summary>
    private static ISILControlFlowGraph 创建图(
        Block entry,
        Block exit,
        List<Block> blocks)
    {
        var graph = new ISILControlFlowGraph([])
        {
            EntryBlock = entry,
            ExitBlock = exit,
            Blocks = blocks,
        };
        return graph;
    }

    /// <summary>
    /// 绑定测试使用的托管系统类型，使 ISIL 局部能够映射到同一模块的 CIL 类型签名。
    /// </summary>
    private static void 绑定AsmResolver系统类型(
        ModuleDefinition module,
        TypeAnalysisContext context,
        string name,
        TypeAttributes attributes)
    {
        var definition = new TypeDefinition("System", name, attributes);
        module.TopLevelTypes.Add(definition);
        context.PutExtraData("AsmResolverType", definition);
    }
}
