namespace LuraphDeobfuscator.Deobfuscator.IR;
public enum IROp
{
    Assign, LoadNil, LoadBool, LoadConst, LoadVararg, Add, Sub, Mul, Div, Mod, Pow, IDiv, Negate, BXor, BAnd, BOr, Not, Eq, NotEq, Lt, Le, Gt, Ge, Concat, Len, NewTable, GetTable, SetTable, SetList, GetGlobal, SetGlobal, GetUpval, SetUpval, Self, Call, TailCall, Return, Jump, ConditionalJump, Test, ForPrep, ForLoop, TForLoop, IterPrep, Close, Closure, Nop,
}
public class IRInstruction
{
    public int OriginalIndex { get; set; }
    public IROp Op { get; set; }
    public int Dest { get; set; } = -1;
    public IRValue? OperandA { get; set; }
    public IRValue? OperandB { get; set; }
    public IRValue? OperandC { get; set; }
    public int JumpTarget { get; set; } = -1;
    public int ArgCount { get; set; } = 0;
    public int RetCount { get; set; } = 0;
    public bool Invert { get; set; }
    public bool StoresResult { get; set; }
    public int ProtoIndex { get; set; } = -1;
    public bool SkipNext { get; set; }
    public string? Comment { get; set; }

    public override string ToString()
    {
        var parts = new List<string> { $"[{OriginalIndex:D4}] {Op}" };

        if (Dest >= 0 && Op != IROp.Jump && Op != IROp.Nop)
            parts.Add($"R({Dest})");

        if (OperandA != null) parts.Add(OperandA.ToString()!);
        if (OperandB != null) parts.Add(OperandB.ToString()!);
        if (OperandC != null) parts.Add(OperandC.ToString()!);

        if (JumpTarget >= 0) parts.Add($"-> [{JumpTarget:D4}]");
        if (ArgCount != 0) parts.Add($"args={ArgCount}");
        if (RetCount != 0) parts.Add($"rets={RetCount}");
        if (Invert) parts.Add("INV");
        if (ProtoIndex >= 0) parts.Add($"proto={ProtoIndex}");
        if (Comment != null) parts.Add($"; {Comment}");

        return string.Join(" ", parts);
    }
}
