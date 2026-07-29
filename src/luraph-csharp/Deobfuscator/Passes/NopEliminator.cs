using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class NopEliminator
{
    public static int Run(List<BasicBlock> blocks)
    {
        int removed = 0;
        foreach (var block in blocks)
        {
            int before = block.Instructions.Count;
            block.Instructions.RemoveAll(i => i is Nop);
            removed += before - block.Instructions.Count;
        }
        return removed;
    }
}
