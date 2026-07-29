using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class UnreachableBlockEliminator
{
    public static int Run(List<BasicBlock> blocks)
    {
        int removed = blocks.RemoveAll(b => b.IsUnreachable);
        var alive = new HashSet<int>(blocks.Select(b => b.Id));
        foreach (var block in blocks)
        {
            block.Predecessors.RemoveAll(b => !alive.Contains(b.Id));
            block.Successors.RemoveAll(b => !alive.Contains(b.Id));
        }

        return removed;
    }
}
