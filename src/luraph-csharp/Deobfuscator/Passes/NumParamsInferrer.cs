using LuraphDeobfuscator.Deobfuscator.IR;
using LuraphDeobfuscator.Deobfuscator.VM;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class NumParamsInferrer
{
    public static void InferAll(Dictionary<int, List<IRInstr>> allIR, List<LuraphProto> prototypes)
    {
        var protoArgCounts = new Dictionary<int, int>();

        foreach (var (parentIdx, ir) in allIR)
        {
            var regToProto = new Dictionary<int, int>();
            var regAlias = new Dictionary<int, int>();

            foreach (var instr in ir)
            {
                switch (instr)
                {
                    case MakeClosure mc:
                        regToProto[mc.Dst.Reg] = mc.ProtoIndex;
                        regAlias.Remove(mc.Dst.Reg);
                        break;

                    case Move mv when mv.Src is RegOp src:
                        regAlias[mv.Dst.Reg] = src.Reg;
                        if (regToProto.TryGetValue(src.Reg, out int pi))
                            regToProto[mv.Dst.Reg] = pi;
                        break;

                    case Call cl:
                        {
                            int funcReg = cl.Func.Reg;
                            int argCount = cl.ArgCount;
                            if (argCount < 0) argCount = 0;
                            if (TryResolveProto(funcReg, regToProto, regAlias, out int targetProto))
                            {
                                if (!protoArgCounts.TryGetValue(targetProto, out int cur) || argCount > cur)
                                    protoArgCounts[targetProto] = argCount;
                            }
                            regToProto.Remove(funcReg);
                            regAlias.Remove(funcReg);
                            break;
                        }

                    case TailCall tc:
                        {
                            int funcReg = tc.Func.Reg;
                            int argCount = tc.ArgCount;
                            if (argCount < 0) argCount = 0;

                            if (TryResolveProto(funcReg, regToProto, regAlias, out int targetProto))
                            {
                                if (!protoArgCounts.TryGetValue(targetProto, out int cur) || argCount > cur)
                                    protoArgCounts[targetProto] = argCount;
                            }
                            break;
                        }

                    default:
                        foreach (int reg in GetDefinedRegs(instr))
                        {
                            regToProto.Remove(reg);
                            regAlias.Remove(reg);
                        }
                        break;
                }
            }
        }
        for (int i = 0; i < prototypes.Count; i++)
        {
            var proto = prototypes[i];
            if (proto.NumParams != 0) continue;

            if (protoArgCounts.TryGetValue(i, out int args) && args > 0)
            {
                proto.NumParams = args;
            }
            else if (allIR.TryGetValue(i, out var ir) && ir.Count > 0)
            {
                proto.NumParams = InferFallback(ir);
            }
        }
    }
    private static bool TryResolveProto(int reg, Dictionary<int, int> regToProto, Dictionary<int, int> regAlias, out int protoIndex)
    {
        if (regToProto.TryGetValue(reg, out protoIndex))
            return true;
        int cur = reg;
        for (int depth = 0; depth < 10; depth++)
        {
            if (!regAlias.TryGetValue(cur, out int src))
                break;
            if (regToProto.TryGetValue(src, out protoIndex))
                return true;
            cur = src;
        }

        protoIndex = -1;
        return false;
    }
    private static int InferFallback(List<IRInstr> ir)
    {
        var defined = new HashSet<int>();
        var used = new HashSet<int>();

        foreach (var instr in ir)
        {
            if (instr is Nop) continue;
            foreach (int reg in GetUsedRegs(instr)) used.Add(reg);
            foreach (int reg in GetDefinedRegs(instr)) defined.Add(reg);
        }

        var neverDefined = new HashSet<int>(used);
        neverDefined.ExceptWith(defined);

        if (neverDefined.Count == 0) return 0;
        int numParams = 0;
        for (int r = 0; ; r++)
        {
            if (neverDefined.Contains(r))
                numParams = r + 1;
            else
                break;
        }
        return numParams;
    }

    private static List<int> GetDefinedRegs(IRInstr instr)
    {
        var regs = new List<int>();
        switch (instr)
        {
            case Move m: regs.Add(m.Dst.Reg); break;
            case LoadVararg lv:
                regs.Add(lv.Dst.Reg);
                if (lv.Count > 1)
                    for (int i = 1; i < lv.Count; i++)
                        regs.Add(lv.Dst.Reg + i);
                break;
            case BinOp b: regs.Add(b.Dst.Reg); break;
            case UnaryOp u: regs.Add(u.Dst.Reg); break;
            case Concat c: regs.Add(c.Dst.Reg); break;
            case NewTable nt: regs.Add(nt.Dst.Reg); break;
            case GetTable gt: regs.Add(gt.Dst.Reg); break;
            case GetGlobal gg: regs.Add(gg.Dst.Reg); break;
            case GetUpval gu: regs.Add(gu.Dst.Reg); break;
            case Self s:
                regs.Add(s.Dst.Reg);
                regs.Add(s.Dst.Reg + 1);
                break;
            case CmpBool cb: regs.Add(cb.Dst.Reg); break;
            case MakeClosure mc: regs.Add(mc.Dst.Reg); break;
            case Call cl:
                int retCount = cl.RetCount;
                if (retCount < 0) retCount = 1;
                for (int i = 0; i < retCount; i++)
                    regs.Add(cl.Func.Reg + i);
                break;
            case ForLoop fl: regs.Add(fl.Base.Reg + 3); break;
            case TForLoop tfl:
                for (int i = 0; i < tfl.NumVars; i++)
                    regs.Add(tfl.Base.Reg + 3 + i);
                break;
        }
        return regs;
    }

    private static List<int> GetUsedRegs(IRInstr instr)
    {
        var regs = new List<int>();

        void Add(Operand? op)
        {
            if (op is RegOp r) regs.Add(r.Reg);
        }

        switch (instr)
        {
            case Move m: Add(m.Src); break;
            case BinOp b: Add(b.Left); Add(b.Right); break;
            case UnaryOp u: Add(u.Src); break;
            case Concat c: Add(c.Left); Add(c.Right); break;
            case GetTable gt: Add(gt.Table); Add(gt.Key); break;
            case SetTable st: Add(st.Table); Add(st.Key); Add(st.Value); break;
            case SetGlobal sg: Add(sg.Name); Add(sg.Value); break;
            case GetGlobal gg: Add(gg.Name); break;
            case SetUpval su: Add(su.Value); break;
            case Self s: Add(s.Object); Add(s.Method); break;
            case Call cl:
                regs.Add(cl.Func.Reg);
                if (cl.ArgCount > 0)
                    for (int i = 0; i < cl.ArgCount; i++)
                        regs.Add(cl.Func.Reg + 1 + i);
                break;
            case TailCall tc:
                regs.Add(tc.Func.Reg);
                if (tc.ArgCount > 0)
                    for (int i = 0; i < tc.ArgCount; i++)
                        regs.Add(tc.Func.Reg + 1 + i);
                break;
            case Return ret:
                if (ret.Count > 0)
                    for (int i = 0; i < ret.Count; i++)
                        regs.Add(ret.BaseReg + i);
                else if (ret.Count == -1)
                    regs.Add(ret.BaseReg);
                break;
            case CondJump cj: Add(cj.Left); Add(cj.Right); break;
            case TestJump tj: Add(tj.Value); break;
            case CmpBool cb: Add(cb.Left); Add(cb.Right); break;
            case SetList sl: regs.Add(sl.Table.Reg); break;
            case ForPrep fp:
                regs.Add(fp.Base.Reg);
                regs.Add(fp.Base.Reg + 1);
                regs.Add(fp.Base.Reg + 2);
                break;
            case ForLoop fl:
                regs.Add(fl.Base.Reg);
                regs.Add(fl.Base.Reg + 1);
                regs.Add(fl.Base.Reg + 2);
                break;
            case TForLoop tfl:
                regs.Add(tfl.Base.Reg);
                regs.Add(tfl.Base.Reg + 1);
                regs.Add(tfl.Base.Reg + 2);
                break;
        }

        return regs;
    }
}
