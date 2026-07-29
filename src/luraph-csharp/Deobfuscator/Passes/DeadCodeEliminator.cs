using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class DeadCodeEliminator
{
    public static int Run(List<BasicBlock> blocks)
    {
        var usedRegs = new HashSet<int>();
        foreach (var block in blocks)
        {
            if (block.IsUnreachable) continue;
            foreach (var instr in block.AllInstructions)
            {
                foreach (int reg in SSABuilder.GetUsedRegisters(instr))
                    usedRegs.Add(reg);
            }
        }

        int removed = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;

            foreach (var block in blocks)
            {
                if (block.IsUnreachable) continue;

                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    var instr = block.Instructions[i];
                    if (!CanEliminate(instr)) continue;
                    var defRegs = SSABuilder.GetAllDefinedRegisters(instr);
                    if (defRegs.Count > 0 && defRegs.All(r => !usedRegs.Contains(r)))
                    {
                        block.Instructions.RemoveAt(i);
                        removed++;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                usedRegs.Clear();
                foreach (var block in blocks)
                {
                    if (block.IsUnreachable) continue;
                    foreach (var instr in block.AllInstructions)
                    {
                        foreach (int reg in SSABuilder.GetUsedRegisters(instr))
                            usedRegs.Add(reg);
                    }
                }
            }
        }

        return removed;
    }
    private static bool CanEliminate(IRInstr instr) => instr switch
    {
        Move => true,
        BinOp => true,
        UnaryOp => true,
        Concat => true,
        CmpBool => true,
        NewTable => true,
        GetTable => false,
        GetGlobal => false,
        GetUpval => true,
        Phi => true,
        Call => false,
        TailCall => false,
        Return => false,
        SetTable => false,
        SetGlobal => false,
        SetUpval => false,
        SetList => false,
        Jump => false,
        CondJump => false,
        TestJump => false,
        ForPrep => false,
        ForLoop => false,
        TForLoop => false,
        IterPrep => false,
        MakeClosure => false,
        CloseUpvals => false,
        Self => false,
        LoadVararg => false,
        _ => false,
    };
}
