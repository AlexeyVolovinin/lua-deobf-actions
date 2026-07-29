using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.CFG;
public static class LoopAnalyzer
{
    public static List<NaturalLoop> DetectLoops(List<BasicBlock> blocks)
    {
        var loops = new List<NaturalLoop>();
        foreach (var block in blocks)
        {
            if (block.IsUnreachable) continue;

            foreach (var succ in block.Successors)
            {
                if (DominatorTree.Dominates(succ, block))
                {
                    var body = ComputeLoopBody(succ, block);
                    var loop = new NaturalLoop
                    {
                        Header = succ,
                        Latch = block,
                        Body = body
                    };
                    loops.Add(loop);

                    succ.IsLoopHeader = true;
                    succ.LoopBody = body;
                }
            }
        }
        var merged = MergeLoops(loops);
        ComputeLoopDepth(merged, blocks);
        foreach (var loop in merged)
        {
            loop.ExitBlocks = [];
            foreach (var b in loop.Body)
            {
                foreach (var succ in b.Successors)
                {
                    if (!loop.Body.Contains(succ))
                    {
                        loop.ExitBlocks.Add(succ);
                    }
                }
            }
        }

        return merged;
    }
    public static LoopKind ClassifyLoop(NaturalLoop loop)
    {
        var header = loop.Header;
        var terminator = header.Terminator;

        if (terminator == null)
            return LoopKind.While;
        if (terminator is ForLoop) return LoopKind.NumericFor;
        if (terminator is TForLoop) return LoopKind.GenericFor;
        var latchTerm = loop.Latch.Terminator;
        if (latchTerm is ForLoop) return LoopKind.NumericFor;
        if (latchTerm is TForLoop) return LoopKind.GenericFor;

        return ClassifyByLatchCondition(loop);
    }
    private static HashSet<BasicBlock> ComputeLoopBody(BasicBlock header, BasicBlock latch)
    {
        var body = new HashSet<BasicBlock> { header };

        if (header == latch)
            return body;

        var worklist = new Stack<BasicBlock>();
        body.Add(latch);
        worklist.Push(latch);

        while (worklist.Count > 0)
        {
            var node = worklist.Pop();
            foreach (var pred in node.Predecessors)
            {
                if (body.Add(pred) && DominatorTree.Dominates(header, pred))
                    worklist.Push(pred);
            }
        }

        return body;
    }

    private static List<NaturalLoop> MergeLoops(List<NaturalLoop> loops)
    {
        var byHeader = new Dictionary<int, NaturalLoop>();

        foreach (var loop in loops)
        {
            if (byHeader.TryGetValue(loop.Header.Id, out var existing))
            {
                existing.Body.UnionWith(loop.Body);
                if (loop.Body.Count > existing.Body.Count)
                    existing.Latch = loop.Latch;
            }
            else
            {
                byHeader[loop.Header.Id] = loop;
            }
        }

        return byHeader.Values.ToList();
    }

    private static void ComputeLoopDepth(List<NaturalLoop> loops, List<BasicBlock> blocks)
    {
        foreach (var b in blocks)
            b.LoopDepth = 0;
        var sorted = loops.OrderBy(l => l.Body.Count).ToList();

        foreach (var loop in sorted)
        {
            foreach (var b in loop.Body)
                b.LoopDepth++;
        }
    }

    private static LoopKind ClassifyByLatchCondition(NaturalLoop loop)
    {
        var latchTerm = loop.Latch.Terminator;
        if (latchTerm == null)
            return LoopKind.While;
        if (latchTerm is CondJump or TestJump)
        {
            foreach (var succ in loop.Latch.Successors)
            {
                if (succ == loop.Header)
                    return LoopKind.RepeatUntil;
            }
        }

        return LoopKind.While;
    }
}
public class NaturalLoop
{
    public required BasicBlock Header { get; set; }
    public required BasicBlock Latch { get; set; }
    public required HashSet<BasicBlock> Body { get; set; }
    public HashSet<BasicBlock> ExitBlocks { get; set; } = [];
    public LoopKind Kind => LoopAnalyzer.ClassifyLoop(this);

    public override string ToString()
        => $"Loop @ {Header.Label}, {Body.Count} blocks, kind={Kind}";
}
public enum LoopKind
{
    While, RepeatUntil, NumericFor, GenericFor
}
