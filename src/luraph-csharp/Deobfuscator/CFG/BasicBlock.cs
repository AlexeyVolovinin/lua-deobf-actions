using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.CFG;
public class BasicBlock
{
    public int Id { get; set; }
    public string Label => $"B{Id}";
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
    public List<IRInstr> Instructions { get; set; } = [];
    public IRInstr? Terminator { get; set; }
    public IEnumerable<IRInstr> AllInstructions => Terminator != null ? Instructions.Append(Terminator) : Instructions;
    public List<BasicBlock> Successors { get; set; } = [];
    public List<BasicBlock> Predecessors { get; set; } = [];
    public bool IsLoopHeader { get; set; }
    public int LoopDepth { get; set; }
    public HashSet<BasicBlock>? LoopBody { get; set; }
    public BasicBlock? ImmediateDominator { get; set; }
    public List<BasicBlock> DominatedChildren { get; set; } = [];
    public HashSet<BasicBlock> DominanceFrontier { get; set; } = [];
    public bool IsExit => Terminator is Return or TailCall;
    public bool IsUnreachable { get; set; }

    public override string ToString()
    {
        var succs = string.Join(", ", Successors.Select(s => s.Label));
        return $"{Label} [{StartIndex}-{EndIndex}] ({Instructions.Count} instrs) -> {succs}";
    }
}
