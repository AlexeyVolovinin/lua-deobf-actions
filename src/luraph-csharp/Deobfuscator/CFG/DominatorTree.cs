namespace LuraphDeobfuscator.Deobfuscator.CFG;
public static class DominatorTree
{
    public static void Compute(List<BasicBlock> blocks)
    {
        if (blocks.Count == 0) return;

        var entry = blocks[0];
        var rpo = ReversePostOrder(entry, blocks.Count);
        var rpoIndex = new Dictionary<int, int>();
        for (int i = 0; i < rpo.Count; i++)
            rpoIndex[rpo[i].Id] = i;
        int maxId = 0;
        foreach (var b in blocks)
            if (b.Id > maxId) maxId = b.Id;
        var idom = new BasicBlock?[maxId + 1];
        idom[entry.Id] = entry;

        bool changed = true;
        while (changed)
        {
            changed = false;

            foreach (var b in rpo)
            {
                if (b == entry) continue;
                if (b.IsUnreachable) continue;
                BasicBlock? newIdom = null;
                foreach (var pred in b.Predecessors)
                {
                    if (idom[pred.Id] != null)
                    {
                        newIdom = pred;
                        break;
                    }
                }

                if (newIdom == null) continue;
                foreach (var pred in b.Predecessors)
                {
                    if (pred == newIdom) continue;
                    if (idom[pred.Id] != null)
                        newIdom = Intersect(pred, newIdom, idom, rpoIndex);
                }

                if (idom[b.Id] != newIdom)
                {
                    idom[b.Id] = newIdom;
                    changed = true;
                }
            }
        }
        foreach (var b in blocks)
        {
            b.ImmediateDominator = (idom[b.Id] != b) ? idom[b.Id] : null;
            b.DominatedChildren.Clear();
        }

        foreach (var b in blocks)
        {
            if (b.ImmediateDominator != null)
                b.ImmediateDominator.DominatedChildren.Add(b);
        }
        ComputeDominanceFrontier(blocks);
    }
    public static bool Dominates(BasicBlock a, BasicBlock b)
    {
        var current = b;
        while (current != null)
        {
            if (current == a) return true;
            current = current.ImmediateDominator;
        }
        return false;
    }

    private static BasicBlock Intersect(BasicBlock b1, BasicBlock b2, BasicBlock?[] idom, Dictionary<int, int> rpoIndex)
    {
        var finger1 = b1;
        var finger2 = b2;

        while (finger1 != finger2)
        {
            while (rpoIndex.GetValueOrDefault(finger1!.Id, int.MaxValue) >
                   rpoIndex.GetValueOrDefault(finger2!.Id, int.MaxValue))
            {
                finger1 = idom[finger1.Id]!;
            }
            while (rpoIndex.GetValueOrDefault(finger2!.Id, int.MaxValue) >
                   rpoIndex.GetValueOrDefault(finger1!.Id, int.MaxValue))
            {
                finger2 = idom[finger2.Id]!;
            }
        }

        return finger1!;
    }

    private static List<BasicBlock> ReversePostOrder(BasicBlock entry, int blockCount)
    {
        var visited = new HashSet<int>();
        var postOrder = new List<BasicBlock>(blockCount);
        var stack = new Stack<(BasicBlock Block, int ChildIndex)>();
        visited.Add(entry.Id);
        stack.Push((entry, 0));

        while (stack.Count > 0)
        {
            var (block, childIdx) = stack.Pop();

            if (childIdx < block.Successors.Count)
            {
                stack.Push((block, childIdx + 1));

                var succ = block.Successors[childIdx];
                if (visited.Add(succ.Id))
                    stack.Push((succ, 0));
            }
            else
            {
                postOrder.Add(block);
            }
        }

        postOrder.Reverse();
        return postOrder;
    }

    private static void ComputeDominanceFrontier(List<BasicBlock> blocks)
    {
        foreach (var b in blocks)
            b.DominanceFrontier.Clear();

        foreach (var b in blocks)
        {
            if (b.Predecessors.Count < 2) continue;

            foreach (var pred in b.Predecessors)
            {
                var runner = pred;
                while (runner != null && runner != b.ImmediateDominator)
                {
                    runner.DominanceFrontier.Add(b);
                    runner = runner.ImmediateDominator;
                }
            }
        }
    }
}
