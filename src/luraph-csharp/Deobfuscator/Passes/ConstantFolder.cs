using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class ConstantFolder
{
    public static int Run(List<BasicBlock> blocks)
    {
        int folded = 0;
        foreach (var block in blocks)
        {
            if (block.IsUnreachable) continue;

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var replacement = TryFold(block.Instructions[i]);
                if (replacement != null)
                {
                    block.Instructions[i] = replacement;
                    folded++;
                }
            }
        }
        return folded;
    }

    private static IRInstr? TryFold(IRInstr instr)
    {
        switch (instr)
        {
            case BinOp { Left: ConstOp left, Right: ConstOp right } bin:
                {
                    var result = EvalBinOp(bin.Kind, left.Value, right.Value);
                    if (result != null)
                        return new Move(bin.Index, bin.Dst, new ConstOp(result));
                    break;
                }
            case UnaryOp { Src: ConstOp src } unary:
                {
                    var result = EvalUnaryOp(unary.Kind, src.Value);
                    if (result != null)
                        return new Move(unary.Index, unary.Dst, new ConstOp(result));
                    break;
                }
            case Concat { Left: ConstOp left, Right: ConstOp right } cat
                when left.Value is string ls && right.Value is string rs:
                {
                    return new Move(cat.Index, cat.Dst, new ConstOp(ls + rs));
                }
        }
        return null;
    }

    private static object? EvalBinOp(BinOpKind kind, object? left, object? right)
    {
        double? l = ToDouble(left);
        double? r = ToDouble(right);
        if (l == null || r == null) return null;

        return kind switch
        {
            BinOpKind.Add => (object)(l.Value + r.Value),
            BinOpKind.Sub => l.Value - r.Value,
            BinOpKind.Mul => l.Value * r.Value,
            BinOpKind.Div when r.Value != 0 => l.Value / r.Value,
            BinOpKind.Mod when r.Value != 0 => l.Value % r.Value,
            BinOpKind.Pow => Math.Pow(l.Value, r.Value),
            BinOpKind.IDiv when r.Value != 0 => Math.Floor(l.Value / r.Value),
            BinOpKind.BXor => (object)(long)((long)l.Value ^ (long)r.Value),
            BinOpKind.BAnd => (long)((long)l.Value & (long)r.Value),
            BinOpKind.BOr => (long)((long)l.Value | (long)r.Value),
            _ => null
        };
    }

    private static object? EvalUnaryOp(UnaryOpKind kind, object? operand)
    {
        return kind switch
        {
            UnaryOpKind.Negate when ToDouble(operand) is double d => (object)(-d),
            UnaryOpKind.Not => operand is null or false ? (object)true : false,
            UnaryOpKind.Len when operand is string s => (object)(double)s.Length,
            UnaryOpKind.BNot when ToDouble(operand) is double d => (object)(long)(~(long)d),
            _ => null
        };
    }

    private static double? ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => null,
    };
}
