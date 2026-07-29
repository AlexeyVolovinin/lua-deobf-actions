using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.CFG;
public static class CFGBuilder
{
    public static List<BasicBlock> Build(List<IRInstr> instructions)
    {
        if (instructions.Count == 0)
            return [];
        var leaders = new HashSet<int> { 0 };
        var indexToPos = new Dictionary<int, int>(instructions.Count);
        for (int i = 0; i < instructions.Count; i++)
            indexToPos[instructions[i].Index] = i;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            switch (instr)
            {
                case Jump j:
                    AddLeader(leaders, indexToPos, j.Target);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case CondJump cj:
                    AddLeader(leaders, indexToPos, cj.TrueTarget);
                    AddLeader(leaders, indexToPos, cj.FalseTarget);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case TestJump tj:
                    AddLeader(leaders, indexToPos, tj.TrueTarget);
                    AddLeader(leaders, indexToPos, tj.FalseTarget);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case ForPrep fp:
                    AddLeader(leaders, indexToPos, fp.LoopTarget);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case ForLoop fl:
                    AddLeader(leaders, indexToPos, fl.BodyTarget);
                    AddLeader(leaders, indexToPos, fl.ExitTarget);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case TForLoop tfl:
                    AddLeader(leaders, indexToPos, tfl.BodyTarget);
                    AddLeader(leaders, indexToPos, tfl.ExitTarget);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case IterPrep ip:
                    AddLeader(leaders, indexToPos, ip.Target);
                    AddNextLeader(leaders, instructions, i);
                    break;

                case Return:
                case TailCall:
                    AddNextLeader(leaders, instructions, i);
                    break;
            }
        }
        var sortedLeaders = leaders
            .Where(l => l >= 0 && l < instructions.Count)
            .Order()
            .ToList();

        var blocks = new List<BasicBlock>(sortedLeaders.Count);
        var indexToBlock = new Dictionary<int, BasicBlock>();

        for (int li = 0; li < sortedLeaders.Count; li++)
        {
            int start = sortedLeaders[li];
            int end = (li + 1 < sortedLeaders.Count)
                ? sortedLeaders[li + 1] - 1
                : instructions.Count - 1;

            if (start >= instructions.Count) continue;
            end = Math.Min(end, instructions.Count - 1);
            if (end < start) continue;
            var blockInstrs = new List<IRInstr>();
            IRInstr? terminator = null;

            for (int k = start; k <= end; k++)
            {
                var instr = instructions[k];
                if (k == end && IsTerminator(instr))
                    terminator = instr;
                else
                    blockInstrs.Add(instr);
            }

            var block = new BasicBlock
            {
                Id = blocks.Count,
                StartIndex = instructions[start].Index,
                EndIndex = instructions[end].Index,
                Instructions = blockInstrs,
                Terminator = terminator,
            };

            blocks.Add(block);
            indexToBlock[instructions[start].Index] = block;
        }
        for (int bi = 0; bi < blocks.Count; bi++)
        {
            var block = blocks[bi];
            var term = block.Terminator;

            if (term == null)
            {
                if (bi + 1 < blocks.Count)
                    AddEdge(block, blocks[bi + 1]);
                continue;
            }

            switch (term)
            {
                case Jump j:
                    AddEdge(block, indexToBlock, j.Target);
                    break;

                case CondJump cj:
                    AddEdge(block, indexToBlock, cj.TrueTarget);
                    AddEdge(block, indexToBlock, cj.FalseTarget);
                    break;

                case TestJump tj:
                    AddEdge(block, indexToBlock, tj.TrueTarget);
                    AddEdge(block, indexToBlock, tj.FalseTarget);
                    break;

                case ForPrep fp:
                    AddEdge(block, indexToBlock, fp.LoopTarget);
                    break;

                case ForLoop fl:
                    AddEdge(block, indexToBlock, fl.BodyTarget);
                    AddEdge(block, indexToBlock, fl.ExitTarget);
                    break;

                case TForLoop tfl:
                    AddEdge(block, indexToBlock, tfl.BodyTarget);
                    AddEdge(block, indexToBlock, tfl.ExitTarget);
                    break;

                case IterPrep ip:
                    AddEdge(block, indexToBlock, ip.Target);
                    break;

                case Return:
                case TailCall:
                    break;

                default:
                    if (bi + 1 < blocks.Count)
                        AddEdge(block, blocks[bi + 1]);
                    break;
            }
        }
        MarkReachable(blocks);

        return blocks;
    }
    public static bool IsTerminator(IRInstr instr) => instr is
        Jump or CondJump or TestJump or
        ForPrep or ForLoop or TForLoop or IterPrep or
        Return or TailCall;
    public static BasicBlock? FindBlock(List<BasicBlock> blocks, int instrIndex)
    {
        int lo = 0, hi = blocks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var b = blocks[mid];
            if (instrIndex < b.StartIndex)
                hi = mid - 1;
            else if (instrIndex > b.EndIndex)
                lo = mid + 1;
            else
                return b;
        }
        return null;
    }
    private static void AddLeader(HashSet<int> leaders, Dictionary<int, int> indexToPos, int targetIndex)
    {
        if (indexToPos.TryGetValue(targetIndex, out int pos))
            leaders.Add(pos);
    }

    private static void AddNextLeader(HashSet<int> leaders, List<IRInstr> instructions, int currentPos)
    {
        if (currentPos + 1 < instructions.Count)
            leaders.Add(currentPos + 1);
    }

    private static void AddEdge(BasicBlock from, BasicBlock to)
    {
        if (!from.Successors.Contains(to))
            from.Successors.Add(to);
        if (!to.Predecessors.Contains(from))
            to.Predecessors.Add(from);
    }

    private static void AddEdge(BasicBlock from, Dictionary<int, BasicBlock> map, int targetIndex)
    {
        if (targetIndex >= 0 && map.TryGetValue(targetIndex, out var to))
            AddEdge(from, to);
    }

    private static void MarkReachable(List<BasicBlock> blocks)
    {
        if (blocks.Count == 0) return;

        var visited = new HashSet<int>();
        var stack = new Stack<BasicBlock>();
        stack.Push(blocks[0]);
        visited.Add(blocks[0].Id);

        while (stack.Count > 0)
        {
            var b = stack.Pop();
            foreach (var succ in b.Successors)
            {
                if (visited.Add(succ.Id))
                    stack.Push(succ);
            }
        }

        foreach (var block in blocks)
            block.IsUnreachable = !visited.Contains(block.Id);
    }
}
