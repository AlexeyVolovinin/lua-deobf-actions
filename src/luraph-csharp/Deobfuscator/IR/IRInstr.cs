using System.Linq;

namespace LuraphDeobfuscator.Deobfuscator.IR;
public abstract record IRInstr(int Index)
{
    public abstract string Disasm();
    public string Format() => $"[{Index,4}] {GetType().Name,-14} {Disasm()}";
}
public sealed record Move(int Index, RegOp Dst, Operand Src) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Src}";
}
public sealed record LoadVararg(int Index, RegOp Dst, int Count) : IRInstr(Index)
{
    public override string Disasm() =>
        Count < 0 ? $"{Dst}... = ..." : $"{Dst}..v{Dst.Reg + Count - 1} = ...";
}
public sealed record BinOp(int Index, BinOpKind Kind, RegOp Dst, Operand Left, Operand Right) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Left} {Kind.Symbol()} {Right}";
}
public sealed record UnaryOp(int Index, UnaryOpKind Kind, RegOp Dst, Operand Src) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Kind.Symbol()}{Src}";
}
public sealed record Concat(int Index, RegOp Dst, Operand Left, Operand Right) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Left} .. {Right}";
}
public sealed record NewTable(int Index, RegOp Dst, int ArraySize = 0, int HashSize = 0) : IRInstr(Index)
{
    public override string Disasm() => ArraySize > 0 || HashSize > 0
        ? $"{Dst} = {{}} (array={ArraySize}, hash={HashSize})"
        : $"{Dst} = {{}}";
}
public sealed record GetTable(int Index, RegOp Dst, Operand Table, Operand Key) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Table}[{Key}]";
}
public sealed record SetTable(int Index, Operand Table, Operand Key, Operand Value) : IRInstr(Index)
{
    public override string Disasm() => $"{Table}[{Key}] = {Value}";
}
public sealed record SetList(int Index, RegOp Table, int Offset, int Count) : IRInstr(Index)
{
    public override string Disasm() => $"{Table}[{Offset}..] = stack({Count})";
}
public sealed record GetGlobal(int Index, RegOp Dst, Operand Name) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = _G[{Name}]";
}
public sealed record SetGlobal(int Index, Operand Name, Operand Value) : IRInstr(Index)
{
    public override string Disasm() => $"_G[{Name}] = {Value}";
}
public sealed record GetUpval(int Index, RegOp Dst, UpvalOp Upval) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = {Upval}";
}
public sealed record SetUpval(int Index, UpvalOp Upval, Operand Value) : IRInstr(Index)
{
    public override string Disasm() => $"{Upval} = {Value}";
}
public sealed record Self(int Index, RegOp Dst, Operand Object, Operand Method) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst}, v{Dst.Reg + 1} = self({Object}, {Method})";
}
public sealed record Call(int Index, RegOp Func, int ArgCount, int RetCount) : IRInstr(Index)
{
    public override string Disasm()
    {
        var a = ArgCount < 0 ? "var" : ArgCount.ToString();
        var r = RetCount < 0 ? "var" : RetCount.ToString();
        return $"call {Func}({a} args, {r} rets)";
    }
}
public sealed record TailCall(int Index, RegOp Func, int ArgCount) : IRInstr(Index)
{
    public override string Disasm()
    {
        var a = ArgCount < 0 ? "var" : ArgCount.ToString();
        return $"tailcall {Func}({a} args)";
    }
}
public sealed record Return(int Index, int BaseReg, int Count) : IRInstr(Index)
{
    public override string Disasm() => Count switch
    {
        0 => "return",
        -1 => $"return v{BaseReg}..top",
        1 => $"return v{BaseReg}",
        _ => $"return v{BaseReg}..v{BaseReg + Count - 1}"
    };
}
public sealed record Jump(int Index, int Target) : IRInstr(Index)
{
    public override string Disasm() => $"goto [{Target}]";
}
public sealed record CondJump(int Index, CmpKind Cmp, Operand Left, Operand Right, int TrueTarget, int FalseTarget) : IRInstr(Index)
{
    public override string Disasm() =>
        $"if {Left} {Cmp.Symbol()} {Right} goto [{TrueTarget}] else [{FalseTarget}]";
}
public sealed record TestJump(int Index, Operand Value, bool Invert, int TrueTarget, int FalseTarget) : IRInstr(Index)
{
    public override string Disasm()
    {
        var cond = Invert ? $"not {Value}" : $"{Value}";
        return $"if {cond} goto [{TrueTarget}] else [{FalseTarget}]";
    }
}
public sealed record CmpBool(int Index, CmpKind Cmp, RegOp Dst, Operand Left, Operand Right) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = ({Left} {Cmp.Symbol()} {Right})";
}
public sealed record ForPrep(int Index, RegOp Base, int LoopTarget) : IRInstr(Index)
{
    public override string Disasm() => $"forprep {Base} -> [{LoopTarget}]";
}
public sealed record ForLoop(int Index, RegOp Base, int BodyTarget, int ExitTarget) : IRInstr(Index)
{
    public override string Disasm() =>
        $"forloop {Base} body=[{BodyTarget}] exit=[{ExitTarget}]";
}
public sealed record TForLoop(int Index, RegOp Base, int NumVars, int BodyTarget, int ExitTarget) : IRInstr(Index)
{
    public override string Disasm() =>
        $"tforloop {Base} vars={NumVars} body=[{BodyTarget}] exit=[{ExitTarget}]";
}
public sealed record IterPrep(int Index, RegOp Base, int Target) : IRInstr(Index)
{
    public override string Disasm() => $"iterprep {Base} -> [{Target}]";
}
public sealed record MakeClosure(int Index, RegOp Dst, int ProtoIndex) : IRInstr(Index)
{
    public override string Disasm() => $"{Dst} = closure(P{ProtoIndex})";
}
public sealed record CloseUpvals(int Index, RegOp From) : IRInstr(Index)
{
    public override string Disasm() => $"close >= {From}";
}
public sealed record Phi(int Index, RegOp Dst) : IRInstr(Index)
{
    public List<(int BlockId, Operand Value)> Sources { get; } = [];

    public override string Disasm()
    {
        var srcs = string.Join(", ", Sources.Select(s => $"B{s.BlockId}:{s.Value}"));
        return $"{Dst} = phi({srcs})";
    }
}
public sealed record Nop(int Index, string? Reason = null) : IRInstr(Index)
{
    public override string Disasm() => Reason != null ? $"nop ; {Reason}" : "nop";
}
