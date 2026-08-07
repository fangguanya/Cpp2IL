using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class RuntimeMetadataSlotResolverTests
{
    [Test]
    [Category("基本功能")]
    public void 调用实参直接解引用槽指针时可回溯绝对地址()
    {
        var slotPointer = Local("slotPointer");
        var definition = new Instruction(
            0,
            OpCode.Move,
            slotPointer,
            new MemoryOperand(addend: 0x5A3B030));
        var definitions = new Dictionary<LocalVariable, Instruction> { [slotPointer] = definition };
        var argument = new MemoryOperand(slotPointer);

        var matched = RuntimeMetadataSlotResolver.TryFindSlotOrigin(
            argument,
            definitions,
            out var address,
            out var origin);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(address, Is.EqualTo(0x5A3B030));
            Assert.That(origin, Is.SameAs(slotPointer));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已证明的元数据槽加载在退SSA前精确删除()
    {
        var slot = Local("slot");
        var load = new Instruction(0, OpCode.Move, slot, new MemoryOperand(addend: 0x5A3B030));
        var definitions = new Dictionary<LocalVariable, Instruction> { [slot] = load };

        var changed = RuntimeMetadataSlotResolver.RemoveProvenLoads(definitions, [slot]);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(load.Operands, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 绝对地址加载可识别为运行时元数据槽值()
    {
        var value = Local("value");
        var definition = new Instruction(0, OpCode.Move, value, new MemoryOperand(addend: 0x5A3B030));
        var definitions = new Dictionary<LocalVariable, Instruction> { [value] = definition };

        var matched = RuntimeMetadataSlotResolver.TryFindSlotOrigin(
            value,
            definitions,
            out var address,
            out var origin);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(address, Is.EqualTo(0x5A3B030));
            Assert.That(origin, Is.SameAs(value));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 混合Phi中的元数据槽输入替换为业务输入()
    {
        var slot = Local("slot");
        var business = Local("business");
        var result = Local("result");
        var phi = new Instruction(0, OpCode.Phi, result, slot, business);

        var changed = RuntimeMetadataSlotResolver.RepairMixedPhis([phi], [slot]);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(phi.Operands[1], Is.SameAs(business));
            Assert.That(phi.Operands[2], Is.SameAs(business));
        });
    }

    [Test]
    [Category("边界值")]
    public void 全部来自元数据槽的Phi保持边数并继续传播标记()
    {
        var first = Local("first");
        var second = Local("second");
        var result = Local("result");
        var phi = new Instruction(0, OpCode.Phi, result, first, second);

        var changed = RuntimeMetadataSlotResolver.RepairMixedPhis([phi], [first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(phi.Operands.Count, Is.EqualTo(3));
            Assert.That(phi.Operands[1], Is.SameAs(first));
            Assert.That(phi.Operands[2], Is.SameAs(second));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非双重内存加载链不得冒充元数据槽()
    {
        var value = Local("value");
        var definition = new Instruction(0, OpCode.Move, value, new Immediate(7));
        var definitions = new Dictionary<LocalVariable, Instruction> { [value] = definition };

        Assert.That(
            RuntimeMetadataSlotResolver.TryFindSlotOrigin(value, definitions, out _, out _),
            Is.False);
    }

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
