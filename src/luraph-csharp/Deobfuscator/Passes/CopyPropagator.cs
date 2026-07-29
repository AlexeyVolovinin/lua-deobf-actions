using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class CopyPropagator
{
    public static int Run(List<BasicBlock> blocks)
    {
        int propagated = 0;

        foreach (var block in blocks)
        {
            if (block.IsUnreachable) continue;
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                if (block.Instructions[i] is not Move { Src: RegOp srcReg } move)
                    continue;

                int dstReg = move.Dst.Reg;
                int src = srcReg.Reg;
                if (dstReg == src) continue;
                bool srcRedefined = false;
                bool dstRedefined = false;
                int dstRedefIdx = block.Instructions.Count;

                for (int j = i + 1; j < block.Instructions.Count; j++)
                {
                    var def = SSABuilder.GetDefinedRegister(block.Instructions[j]);
                    if (def == src)
                    {
                        srcRedefined = true;
                        break;
                    }
                    if (def == dstReg && j > i)
                    {
                        dstRedefined = true;
                        dstRedefIdx = j;
                        break;
                    }
                }

                if (srcRedefined) continue;
                int limit = dstRedefined ? dstRedefIdx : block.Instructions.Count;
                bool anyReplaced = false;
                for (int j = i + 1; j < limit; j++)
                {
                    var replaced = ReplaceRegInInstr(block.Instructions[j], dstReg, src);
                    if (replaced != null)
                    {
                        block.Instructions[j] = replaced;
                        anyReplaced = true;
                    }
                }
                if (!dstRedefined && block.Terminator != null)
                {
                    var replaced = ReplaceRegInInstr(block.Terminator, dstReg, src);
                    if (replaced != null)
                    {
                        block.Terminator = replaced;
                        anyReplaced = true;
                    }
                }

                if (anyReplaced) propagated++;
            }
        }

        return propagated;
    }
    private static IRInstr? ReplaceRegInInstr(IRInstr instr, int oldReg, int newReg)
    {
        Operand Sub(Operand op) => op is RegOp r && r.Reg == oldReg ? new RegOp(newReg) : op;
        bool any = false;

        Operand Track(Operand op)
        {
            var result = Sub(op);
            if (result != op) any = true;
            return result;
        }

        switch (instr)
        {
            case BinOp b:
                var bl = Track(b.Left); var br = Track(b.Right);
                return any ? b with { Left = bl, Right = br } : null;
            case UnaryOp u:
                var us = Track(u.Src);
                return any ? u with { Src = us } : null;
            case Concat c:
                var cl = Track(c.Left); var cr = Track(c.Right);
                return any ? c with { Left = cl, Right = cr } : null;
            case GetTable gt:
                var gtt = Track(gt.Table); var gtk = Track(gt.Key);
                return any ? gt with { Table = gtt, Key = gtk } : null;
            case SetTable st:
                var stt = Track(st.Table); var stk = Track(st.Key); var stv = Track(st.Value);
                return any ? st with { Table = stt, Key = stk, Value = stv } : null;
            case SetGlobal sg:
                var sgn = Track(sg.Name); var sgv = Track(sg.Value);
                return any ? sg with { Name = sgn, Value = sgv } : null;
            case GetGlobal gg:
                var ggn = Track(gg.Name);
                return any ? gg with { Name = ggn } : null;
            case SetUpval su:
                var suv = Track(su.Value);
                return any ? su with { Value = suv } : null;
            case Self s:
                var so = Track(s.Object); var sm = Track(s.Method);
                return any ? s with { Object = so, Method = sm } : null;
            case CondJump cj:
                var cjl = Track(cj.Left); var cjr = Track(cj.Right);
                return any ? cj with { Left = cjl, Right = cjr } : null;
            case TestJump tj:
                var tjv = Track(tj.Value);
                return any ? tj with { Value = tjv } : null;
            case CmpBool cb:
                var cbl = Track(cb.Left); var cbr = Track(cb.Right);
                return any ? cb with { Left = cbl, Right = cbr } : null;
            case Move m:
                var ms = Track(m.Src);
                return any ? m with { Src = ms } : null;
            case Call call:
                {
                    var newFunc = call.Func;
                    if (call.Func.Reg == oldReg) { newFunc = new RegOp(newReg); any = true; }
                    return any ? call with { Func = newFunc } : null;
                }
            case Return ret:
                return null;
            default:
                return null;
        }
    }
}
