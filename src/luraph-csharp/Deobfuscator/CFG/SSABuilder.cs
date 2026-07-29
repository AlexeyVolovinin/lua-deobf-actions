using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.CFG;
public static class SSABuilder
{
    private static readonly Dictionary<(int instrIndex, int reg, bool isDef), string> _nameHints = [];
    private static readonly object _nameHintsLock = new();
    public static void Transform(List<BasicBlock> blocks)
    {
        if (blocks.Count == 0) return;
        InsertPhisAtMerges(blocks);
    }
    public static void BuildNameHints(List<BasicBlock> blocks, int numParams)
    {
        lock (_nameHintsLock)
        {
            _nameHints.Clear();

            if (blocks.Count == 0)
                return;

            var ordered = blocks
                .Where(b => !b.IsUnreachable)
                .OrderBy(b => b.StartIndex)
                .ToList();
            if (ordered.Count == 0)
                return;
            var outVersions = ordered.ToDictionary(b => b.Id, _ => new Dictionary<int, int>());

            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;

                foreach (var block in ordered)
                {
                    var current = MergePredecessorVersions(block, outVersions);
                    var next = new Dictionary<int, int>(current);

                    foreach (var instr in block.AllInstructions)
                    {
                        foreach (var def in GetAllDefinedRegisters(instr))
                        {
                            int version = next.TryGetValue(def, out var cur) ? cur + 1 : 1;
                            next[def] = version;
                        }
                    }

                    if (!VersionMapsEqual(next, outVersions[block.Id]))
                    {
                        outVersions[block.Id] = next;
                        changed = true;
                    }
                }

                if (!changed)
                    break;
            }
            foreach (var block in ordered)
            {
                var current = MergePredecessorVersions(block, outVersions);

                foreach (var instr in block.AllInstructions)
                {
                    if (instr.Index < 0)
                    {
                        foreach (var def in GetAllDefinedRegisters(instr))
                        {
                            int version = current.TryGetValue(def, out var cur) ? cur + 1 : 1;
                            current[def] = version;
                        }
                        continue;
                    }

                    foreach (var use in GetUsedRegisters(instr))
                    {
                        int version = current.TryGetValue(use, out var cur) ? cur : 0;
                        _nameHints[(instr.Index, use, false)] = FormatSsaName(use, version, numParams);
                    }

                    foreach (var def in GetAllDefinedRegisters(instr))
                    {
                        int version = current.TryGetValue(def, out var cur) ? cur + 1 : 1;
                        current[def] = version;
                        _nameHints[(instr.Index, def, true)] = FormatSsaName(def, version, numParams);
                    }
                }
            }
        }
    }
    public static string? TryGetRegisterName(int instrIndex, int reg, bool isDefinition)
    {
        lock (_nameHintsLock)
        {
            if (_nameHints.TryGetValue((instrIndex, reg, isDefinition), out var exact))
                return exact;
            if (_nameHints.TryGetValue((instrIndex, reg, !isDefinition), out var other))
                return other;

            return null;
        }
    }
    private static void InsertPhisAtMerges(List<BasicBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.IsUnreachable) continue;
            if (block.Predecessors.Count < 2) continue;
            var defsReachingHere = new HashSet<int>();
            foreach (var pred in block.Predecessors)
            {
                foreach (var instr in pred.AllInstructions)
                {
                    foreach (var def in GetAllDefinedRegisters(instr))
                        defsReachingHere.Add(def);
                }
            }
            int inserted = 0;
            foreach (var reg in defsReachingHere.OrderBy(r => r))
            {
                var phi = new Phi(-1, new RegOp(reg));
                foreach (var pred in block.Predecessors)
                    phi.Sources.Add((pred.Id, new RegOp(reg)));

                block.Instructions.Insert(inserted, phi);
                inserted++;
            }
        }
    }
    public static int? GetDefinedRegister(IRInstr instr) => instr switch
    {
        Move m => m.Dst.Reg,
        LoadVararg lv => lv.Dst.Reg,
        BinOp b => b.Dst.Reg,
        UnaryOp u => u.Dst.Reg,
        Concat c => c.Dst.Reg,
        NewTable nt => nt.Dst.Reg,
        GetTable gt => gt.Dst.Reg,
        GetGlobal gg => gg.Dst.Reg,
        GetUpval gu => gu.Dst.Reg,
        Self s => s.Dst.Reg,
        CmpBool cb => cb.Dst.Reg,
        MakeClosure mc => mc.Dst.Reg,
        Phi p => p.Dst.Reg,
        Call cl => cl.Func.Reg,
        _ => null,
    };
    public static List<int> GetAllDefinedRegisters(IRInstr instr)
    {
        var regs = new List<int>();
        switch (instr)
        {
            case Call cl:
                int retCount = cl.RetCount;
                if (retCount < 0) retCount = 1;
                for (int i = 0; i < retCount; i++)
                    regs.Add(cl.Func.Reg + i);
                break;
            case Self s:
                regs.Add(s.Dst.Reg);
                regs.Add(s.Dst.Reg + 1);
                break;
            case LoadVararg lv:
                regs.Add(lv.Dst.Reg);
                if (lv.Count > 1)
                    for (int i = 1; i < lv.Count; i++)
                        regs.Add(lv.Dst.Reg + i);
                break;
            default:
                var primary = GetDefinedRegister(instr);
                if (primary != null) regs.Add(primary.Value);
                break;
        }
        return regs;
    }
    public static List<int> GetUsedRegisters(IRInstr instr)
    {
        var regs = new List<int>();

        void AddOperand(Operand? op)
        {
            if (op is RegOp r) regs.Add(r.Reg);
        }

        switch (instr)
        {
            case Move m:
                AddOperand(m.Src);
                break;
            case BinOp b:
                AddOperand(b.Left);
                AddOperand(b.Right);
                break;
            case UnaryOp u:
                AddOperand(u.Src);
                break;
            case Concat c:
                AddOperand(c.Left);
                AddOperand(c.Right);
                break;
            case GetTable gt:
                AddOperand(gt.Table);
                AddOperand(gt.Key);
                break;
            case SetTable st:
                AddOperand(st.Table);
                AddOperand(st.Key);
                AddOperand(st.Value);
                break;
            case SetGlobal sg:
                AddOperand(sg.Name);
                AddOperand(sg.Value);
                break;
            case GetGlobal gg:
                AddOperand(gg.Name);
                break;
            case SetUpval su:
                AddOperand(su.Value);
                break;
            case Self s:
                AddOperand(s.Object);
                AddOperand(s.Method);
                break;
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
            case CondJump cj:
                AddOperand(cj.Left);
                AddOperand(cj.Right);
                break;
            case TestJump tj:
                AddOperand(tj.Value);
                break;
            case CmpBool cb:
                AddOperand(cb.Left);
                AddOperand(cb.Right);
                break;
            case SetList sl:
                regs.Add(sl.Table.Reg);
                break;
            case Phi p:
                foreach (var (_, val) in p.Sources)
                    AddOperand(val);
                break;
        }

        return regs;
    }
    public static int CountPhis(List<BasicBlock> blocks)
    {
        int count = 0;
        foreach (var block in blocks)
            foreach (var instr in block.Instructions)
                if (instr is Phi) count++;
        return count;
    }
    public static int CountDefinitions(List<BasicBlock> blocks)
    {
        int count = 0;
        foreach (var block in blocks)
            foreach (var instr in block.AllInstructions)
                if (GetDefinedRegister(instr) != null) count++;
        return count;
    }
    public static HashSet<int> LiveIn(BasicBlock block)
    {
        var defined = new HashSet<int>();
        var liveIn = new HashSet<int>();

        foreach (var instr in block.AllInstructions)
        {
            foreach (var use in GetUsedRegisters(instr))
            {
                if (!defined.Contains(use))
                    liveIn.Add(use);
            }

            var def = GetDefinedRegister(instr);
            if (def != null) defined.Add(def.Value);
        }

        return liveIn;
    }

    private static Dictionary<int, int> MergePredecessorVersions(BasicBlock block, Dictionary<int, Dictionary<int, int>> outVersions)
    {
        var merged = new Dictionary<int, int>();

        foreach (var pred in block.Predecessors)
        {
            if (!outVersions.TryGetValue(pred.Id, out var predMap))
                continue;

            foreach (var (reg, ver) in predMap)
            {
                if (!merged.TryGetValue(reg, out var current) || ver > current)
                    merged[reg] = ver;
            }
        }

        return merged;
    }

    private static bool VersionMapsEqual(Dictionary<int, int> a, Dictionary<int, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || bv != v)
                return false;
        return true;
    }

    private static string FormatSsaName(int reg, int version, int numParams)
    {
        string baseName = reg < numParams ? $"L_ARG_{reg}" : $"L_{reg}";
        return version > 0 ? $"{baseName}_{version}" : baseName;
    }
}
