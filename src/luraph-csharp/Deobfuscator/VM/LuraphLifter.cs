using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.VM;
public class LuraphLifter
{
    private readonly FragmentState _fragments = new();
    private readonly List<IRInstr> _output = [];
    private readonly LuraphSettings? _settings;

    public LuraphLifter(LuraphSettings? settings = null)
    {
        _settings = settings;
    }
    public List<IRInstr> Lift(LuraphProto proto)
    {
        _output.Clear();
        _fragments.Reset();

        for (int idx = 1; idx <= proto.InstructionCount; idx++)
        {
            int vOp = proto.Opcodes[idx];
            int rawA = proto.RawA[idx], rawB = proto.RawB[idx], rawC = proto.RawC[idx];
            int regA = proto.RegA[idx], regB = proto.RegB[idx], regC = proto.RegC[idx];
            object? constA = proto.ConstsA[idx], constB = proto.ConstsB[idx], protoRef = proto.ProtoRefs[idx];

            LiftInstruction(vOp, idx, rawA, rawB, rawC, regA, regB, regC, constA, constB, protoRef, proto);
        }
        var commit = _fragments.TryCommit(_output.Count > 0 ? _output[^1].Index : 0);
        if (commit != null) _output.Add(commit);

        return new(_output);
    }

    private void Emit(IRInstr instr) => _output.Add(instr);
    private static int ArgDecode(int b) => b == 0 ? -1 : b - 1;
    private static int RetDecode(int c) => c == 0 ? -1 : c - 1;
    private static int RegIdx(OpSrc src, int regA, int regB, int regC) => src switch
    {
        OpSrc.RegA or OpSrc.OpA or OpSrc.JmpA or OpSrc.UpvalA => regA,
        OpSrc.RegB or OpSrc.OpB or OpSrc.JmpB or OpSrc.UpvalB => regB,
        OpSrc.RegC or OpSrc.OpC or OpSrc.JmpC or OpSrc.UpvalC => regC,
        _ => 0,
    };
    private static Operand ResolveOperand(OpSrc src, int regA, int regB, int regC, object? constA, object? constB, object? protoRef)
    {
        return src switch
        {
            OpSrc.RegA => new RegOp(regA),
            OpSrc.RegB => new RegOp(regB),
            OpSrc.RegC => new RegOp(regC),
            OpSrc.OpA => constA != null ? new ConstOp(constA) : new RegOp(regA),
            OpSrc.OpB => constB != null ? new ConstOp(constB) : new RegOp(regB),
            OpSrc.OpC => protoRef != null ? new ConstOp(protoRef) : new RegOp(regC),
            OpSrc.UpvalA => new UpvalOp(regA),
            OpSrc.UpvalB => new UpvalOp(regB),
            OpSrc.UpvalC => new UpvalOp(regC),
            OpSrc.Nil => new ConstOp(null),
            _ => new ConstOp(null),
        };
    }
    private static int ResolveJmp(OpSrc src, int idx, int regA, int regB, int regC, int[] modeA, int[] modeB, int[] modeC)
    {
        return src switch
        {
            OpSrc.JmpA => modeA[idx] == 0 ? regA + 1 : regA,
            OpSrc.JmpB => modeB[idx] == 0 ? regB + 1 : regB,
            OpSrc.JmpC => modeC[idx] == 0 ? regC + 1 : regC,
            _ => 0,
        };
    }
    private static int SlotValue(char? slot, int regA, int regB, int regC) => slot switch
    {
        'A' or 'a' => regA,
        'B' or 'b' => regB,
        'C' or 'c' => regC,
        _ => 0,
    };
    private void EmitFromEntry(OpcodeEntry e, int idx, int regA, int regB, int regC, object? constA, object? constB, object? protoRef, LuraphProto proto)
    {
        RegOp Reg(OpSrc s) => new(RegIdx(s, regA, regB, regC));
        Operand Op(OpSrc s) => ResolveOperand(s, regA, regB, regC, constA, constB, protoRef);
        int Jmp(OpSrc s) => ResolveJmp(s, idx, regA, regB, regC, proto.ModeA, proto.ModeB, proto.ModeC);
        int RIdx(OpSrc s) => RegIdx(s, regA, regB, regC);

        switch (e.Type)
        {
            case "Move":
                Emit(new Move(idx, Reg(e.Dst), Op(e.Src1)));
                break;

            case "NilRange":
                {
                    int a = RIdx(e.Src1), b = RIdx(e.Src2);
                    for (int r = Math.Min(a, b); r <= Math.Max(a, b); r++)
                        Emit(new Move(idx, new RegOp(r), new ConstOp(null)));
                    break;
                }

            case "BinOp":
                Emit(new BinOp(idx, Enum.Parse<BinOpKind>(e.Kind!), Reg(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "UnaryOp":
                Emit(new UnaryOp(idx, Enum.Parse<UnaryOpKind>(e.Kind!), Reg(e.Dst), Op(e.Src1)));
                break;

            case "GetGlobal":
                Emit(new GetGlobal(idx, Reg(e.Dst), Op(e.Src1)));
                break;

            case "SetGlobal":
                Emit(new SetGlobal(idx, Op(e.Src1), Op(e.Src2)));
                break;

            case "GetTable":
                Emit(new GetTable(idx, Reg(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "SetTable":
                Emit(new SetTable(idx, Op(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "NewTable":
                Emit(new NewTable(idx, Reg(e.Dst)));
                break;

            case "GetUpval":
                Emit(new GetUpval(idx, Reg(e.Dst), (UpvalOp)Op(e.Src1)));
                break;

            case "SetUpval":
                Emit(new SetUpval(idx, (UpvalOp)Op(e.Dst), Op(e.Src1)));
                break;

            case "CmpBool":
                Emit(new CmpBool(idx, Enum.Parse<CmpKind>(e.Kind!), Reg(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "CondJump":
                {
                    var kind = Enum.Parse<CmpKind>(e.Kind!);
                    if (e.Invert) kind = kind.Invert();
                    Emit(new CondJump(idx, kind, Op(e.Src1), Op(e.Src2), Jmp(e.Target), idx + 1));
                    break;
                }

            case "TestJump":
                Emit(new TestJump(idx, Op(e.Src1), e.TestCondition ?? true, Jmp(e.Target), idx + 1));
                break;

            case "Jump":
                Emit(new Jump(idx, Jmp(e.Target)));
                break;

            case "Call":
                {
                    int args = e.FixedArgs ?? ArgDecode(SlotValue(e.ArgSlot, regA, regB, regC));
                    int rets = e.FixedRets ?? RetDecode(SlotValue(e.RetSlot, regA, regB, regC));
                    Emit(new Call(idx, Reg(e.Dst), args, rets));
                    break;
                }

            case "TailCall":
                {
                    int args = e.FixedArgs ?? ArgDecode(SlotValue(e.ArgSlot, regA, regB, regC));
                    Emit(new TailCall(idx, Reg(e.Dst), args));
                    break;
                }

            case "Return":
                {
                    int start = e.Dst != OpSrc.None ? RIdx(e.Dst) : 0;
                    int count = e.FixedArgs ?? 0;
                    Emit(new Return(idx, start, count));
                    break;
                }

            case "ForPrep":
                Emit(new ForPrep(idx, Reg(e.Src1), Jmp(e.Target)));
                break;

            case "ForLoop":
                Emit(new ForLoop(idx, Reg(e.Src1), Jmp(e.Target), idx + 1));
                break;

            case "IterPrep":
                Emit(new IterPrep(idx, Reg(e.Src1), Jmp(e.Target)));
                break;

            case "TForLoop":
                Emit(new TForLoop(idx, Reg(e.Src1), 0, Jmp(e.Target), idx + 1));
                break;

            case "SetList":
                Emit(new SetList(idx, Reg(e.Src1), 0, RIdx(e.Src2)));
                break;

            case "CloseUpvals":
                Emit(new CloseUpvals(idx, Reg(e.Src1)));
                break;

            case "Closure":
                EmitClosureFromEntry(e, idx, regA, regB, regC, proto);
                break;

            case "Vararg":
                Emit(new LoadVararg(idx, Reg(e.Dst), RIdx(e.Src1)));
                break;

            case "Concat":
                Emit(new Concat(idx, Reg(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "Self":
                Emit(new Self(idx, Reg(e.Dst), Op(e.Src1), Op(e.Src2)));
                break;

            case "Nop":
                Emit(new Nop(idx, e.Comment ?? $"mapped nop"));
                break;

            default:
                Emit(new Nop(idx, $"unknown map type: {e.Type}"));
                break;
        }
    }
    private void EmitClosureFromEntry(OpcodeEntry e, int idx, int regA, int regB, int regC, LuraphProto proto)
    {
        int protoIdx = -1;
        if (proto.ClosureBackpatches.Exists(bp => bp.InstrIndex == idx))
            protoIdx = regC;
        else
        {
            if (regC >= 0 && regC < 10000) protoIdx = regC;
            else if (regB >= 0 && regB < 10000) protoIdx = regB;
            else if (regA > 0) protoIdx = regA - 1;
        }

        int destReg = RegIdx(e.Dst, regA, regB, regC);
        if (e.Dst == OpSrc.None)
        {
            destReg = regB;
            if (proto.ClosureBackpatches.Exists(bp => bp.InstrIndex == idx))
                destReg = regB;
            else if (regA != protoIdx && regA >= 0)
                destReg = regA;
        }

        Emit(new MakeClosure(idx, new RegOp(destReg), protoIdx));
    }
    private void HandleFragmentFromEntry(FragmentEntry fe, int idx, int regA, int regB, int regC, object? constA, object? constB, object? protoRef)
    {
        FragmentValue? Resolve(string? desc)
        {
            if (desc == null) return null;
            return desc switch
            {
                "RegFile" => FragmentValue.RegFile(),
                "Env" => FragmentValue.Env(),
                "RegA" => FragmentValue.Reg(regA),
                "RegB" => FragmentValue.Reg(regB),
                "RegC" => FragmentValue.Reg(regC),
                "OpA" => constA != null ? FragmentValue.Const(constA) : FragmentValue.Reg(regA),
                "OpB" => constB != null ? FragmentValue.Const(constB) : FragmentValue.Reg(regB),
                "OpC" => protoRef != null ? FragmentValue.Const(protoRef) : FragmentValue.Reg(regC),
                "UpvalA" => FragmentValue.Upval(regA),
                "UpvalB" => FragmentValue.Upval(regB),
                "UpvalC" => FragmentValue.Upval(regC),
                _ when desc.StartsWith("Const:") =>
                    FragmentValue.Const(int.Parse(desc[6..])),
                _ => null,
            };
        }

        if (fe.G != null) _fragments.G = Resolve(fe.G);
        if (fe.F != null) _fragments.F = Resolve(fe.F);
        if (fe.Q != null) _fragments.Q = Resolve(fe.Q);
        if (fe.O != null) _fragments.O = Resolve(fe.O);
        if (fe.V != null) _fragments.V = Resolve(fe.V);

        if (fe.Commit)
        {
            var committed = _fragments.TryCommit(idx);
            if (committed != null)
                Emit(committed);
            else
            {
                Emit(new Nop(idx, "fragment commit fallback (mapped)"));
                _fragments.Reset();
            }
        }
    }
    private void LiftInstruction(int vOp, int idx, int rawA, int rawB, int rawC, int regA, int regB, int regC, object? constA, object? constB, object? protoRef, LuraphProto proto)
    {
        if (_settings?.OpcodeMap != null &&
            _settings.OpcodeMap.TryGetValue(vOp, out var entry))
        {
            EmitFromEntry(entry, idx, regA, regB, regC, constA, constB, protoRef, proto);
            return;
        }

        if (_settings?.FragmentMap != null &&
            _settings.FragmentMap.TryGetValue(vOp, out var fragEntry))
        {
            HandleFragmentFromEntry(fragEntry, idx, regA, regB, regC, constA, constB, protoRef);
            return;
        }

        if (_settings?.NopOpcodes != null && _settings.NopOpcodes.Contains(vOp))
        {
            Emit(new Nop(idx, $"internal (vOp={vOp})"));
            return;
        }
        LiftInstructionHardcoded(vOp, idx, rawA, rawB, rawC, regA, regB, regC, constA, constB, protoRef, proto);
    }
    private void LiftInstructionHardcoded(int vOp, int idx, int rawA, int rawB, int rawC, int regA, int regB, int regC, object? constA, object? constB, object? protoRef, LuraphProto proto)
    {
        RegOp R(int r) => new(r);
        Operand OpA() => constA != null ? new ConstOp(constA) : (Operand)R(regA);
        Operand OpB() => constB != null ? new ConstOp(constB) : (Operand)R(regB);
        Operand OpC() => protoRef != null ? new ConstOp(protoRef) : (Operand)R(regC);
        int JmpB() => proto.ModeB[idx] == 0 ? regB + 1 : regB;
        int JmpC() => proto.ModeC[idx] == 0 ? regC + 1 : regC;
        int JmpA() => proto.ModeA[idx] == 0 ? regA + 1 : regA;
        void Bin(BinOpKind k, int d, Operand l, Operand r)
            => Emit(new BinOp(idx, k, R(d), l, r));

        void Cmp(CmpKind k, int d, Operand l, Operand r)
            => Emit(new CmpBool(idx, k, R(d), l, r));

        void CJmp(CmpKind k, Operand l, Operand r, int target, bool inv)
        {
            var ek = inv ? k.Invert() : k;
            Emit(new CondJump(idx, ek, l, r, target, idx + 1));
        }
        switch (vOp)
        {
            case 12: Emit(new Move(idx, R(regA), R(regB))); return;
            case 82: Emit(new Move(idx, R(regA), new ConstOp(null))); return;
            case 20:
            case 158:
                for (int r = Math.Min(regC, regB); r <= Math.Max(regC, regB); r++)
                    Emit(new Move(idx, R(r), new ConstOp(null)));
                return;
            case 95: Emit(new Move(idx, R(regB), OpC())); return;
            case 154: Emit(new Nop(idx, "LOADK_HELPER")); return;
            case 50: Emit(new GetGlobal(idx, R(regA), OpB())); return;
            case 111: Emit(new SetGlobal(idx, OpC(), R(regA))); return;
            case 84: Emit(new SetGlobal(idx, OpA(), OpB())); return;
            case 51: Emit(new GetTable(idx, R(regC), R(regA), R(regB))); return;
            case 136: Emit(new GetTable(idx, R(regC), R(regA), OpB())); return;
            case 48: Emit(new SetTable(idx, R(regA), R(regB), R(regC))); return;
            case 8: Emit(new SetTable(idx, R(regA), R(regC), OpB())); return;
            case 132: Emit(new SetTable(idx, R(regA), OpB(), OpC())); return;
            case 193: Emit(new SetTable(idx, R(regC), OpA(), R(regB))); return;
            case 27: Emit(new NewTable(idx, R(regA))); return;
            case 37: Emit(new NewTable(idx, R(regC))); return;
            case 10: Emit(new GetUpval(idx, R(regC), new UpvalOp(regB))); return;
            case 129: Emit(new GetUpval(idx, R(regB), new UpvalOp(regC))); return;
            case 105: Emit(new GetTable(idx, R(regA), new UpvalOp(regB), OpC())); return;
            case 78: Emit(new GetTable(idx, R(regA), new UpvalOp(regC), R(regB))); return;
            case 25: Emit(new GetTable(idx, R(regA), new UpvalOp(regC), OpB())); return;
            case 17: Emit(new GetTable(idx, R(regC), new UpvalOp(regB), R(regA))); return;
            case 151: Emit(new SetUpval(idx, new UpvalOp(regB), R(regA))); return;
            case 43: Emit(new SetUpval(idx, new UpvalOp(regA), OpC())); return;
            case 64:
            case 119:
                Emit(new SetTable(idx, new UpvalOp(regC), R(regB), R(regA))); return;
            case 172: Emit(new SetTable(idx, new UpvalOp(regA), OpC(), R(regB))); return;
            case 180: Emit(new SetTable(idx, new UpvalOp(regC), R(regA), OpB())); return;
            case 80: Bin(BinOpKind.Add, regB, R(regC), R(regA)); return;
            case 143: Bin(BinOpKind.Sub, regA, R(regB), R(regC)); return;
            case 39: Bin(BinOpKind.Mul, regB, R(regC), R(regA)); return;
            case 83: Bin(BinOpKind.Div, regC, R(regB), R(regA)); return;
            case 65: Bin(BinOpKind.Mod, regB, R(regC), R(regA)); return;
            case 133: Bin(BinOpKind.Add, regC, R(regA), OpB()); return;
            case 45: Bin(BinOpKind.Sub, regA, R(regC), OpB()); return;
            case 97: Bin(BinOpKind.Mul, regB, R(regA), OpC()); return;
            case 168: Bin(BinOpKind.Div, regB, R(regA), OpC()); return;
            case 3: Bin(BinOpKind.Mod, regC, R(regA), OpB()); return;
            case 94: Bin(BinOpKind.IDiv, regA, R(regB), OpC()); return;
            case 177: Bin(BinOpKind.Pow, regB, R(regC), OpA()); return;
            case 92: Bin(BinOpKind.Add, regA, OpC(), R(regB)); return;
            case 54: Bin(BinOpKind.Sub, regA, OpC(), R(regB)); return;
            case 117: Bin(BinOpKind.Mul, regB, OpC(), R(regA)); return;
            case 86: Bin(BinOpKind.Div, regC, OpA(), R(regB)); return;
            case 32: Bin(BinOpKind.Pow, regB, OpA(), R(regC)); return;
            case 19: Bin(BinOpKind.Add, regA, OpC(), OpB()); return;
            case 161: Bin(BinOpKind.Sub, regC, OpA(), OpB()); return;
            case 16: Bin(BinOpKind.Mod, regB, OpC(), OpA()); return;
            case 101: Bin(BinOpKind.BXor, regB, R(regA), OpC()); return;
            case 184: Bin(BinOpKind.BXor, regB, R(regC), R(regA)); return;
            case 153: Emit(new UnaryOp(idx, UnaryOpKind.Negate, R(regC), R(regA))); return;
            case 29: Emit(new UnaryOp(idx, UnaryOpKind.Not, R(regB), R(regC))); return;
            case 63: Emit(new UnaryOp(idx, UnaryOpKind.Len, R(regC), R(regA))); return;
            case 93: Emit(new Concat(idx, R(regB), R(regA), R(regC))); return;
            case 52: Emit(new Concat(idx, R(regA), OpB(), R(regC))); return;
            case 69: Emit(new Concat(idx, R(regB), R(regC), OpA())); return;
            case 62: Emit(new Self(idx, R(regC), R(regB), OpA())); return;
            case 144: Cmp(CmpKind.Eq, regA, R(regB), R(regC)); return;
            case 175: Cmp(CmpKind.Eq, regB, R(regA), OpC()); return;
            case 190: Cmp(CmpKind.Eq, regC, OpA(), R(regB)); return;
            case 21: Cmp(CmpKind.Eq, regA, OpC(), OpB()); return;
            case 4: Cmp(CmpKind.NotEq, regC, R(regB), R(regA)); return;
            case 113: Cmp(CmpKind.NotEq, regA, R(regC), OpB()); return;
            case 102: Cmp(CmpKind.NotEq, regA, OpB(), OpC()); return;
            case 147: Cmp(CmpKind.Lt, regC, R(regB), R(regA)); return;
            case 104: Cmp(CmpKind.Lt, regA, R(regB), OpC()); return;
            case 186: Cmp(CmpKind.Lt, regA, OpC(), R(regB)); return;
            case 77: Cmp(CmpKind.Le, regB, R(regA), R(regC)); return;
            case 0: Cmp(CmpKind.Le, regA, R(regB), OpC()); return;
            case 159: Cmp(CmpKind.Le, regA, OpC(), R(regB)); return;
            case 174: Cmp(CmpKind.Le, regC, OpA(), OpB()); return;
            case 35: Cmp(CmpKind.Gt, regB, R(regC), R(regA)); return;
            case 5: Cmp(CmpKind.Gt, regA, R(regC), OpB()); return;
            case 171: Cmp(CmpKind.Gt, regB, OpC(), R(regA)); return;
            case 138: Cmp(CmpKind.Gt, regB, OpA(), OpC()); return;
            case 40: Cmp(CmpKind.Ge, regA, R(regC), R(regB)); return;
            case 191: Cmp(CmpKind.Ge, regC, R(regA), OpB()); return;
            case 131: Cmp(CmpKind.Ge, regB, OpA(), OpC()); return;
            case 156: CJmp(CmpKind.Eq, R(regA), R(regB), JmpC(), false); return;
            case 28: CJmp(CmpKind.NotEq, R(regB), R(regC), JmpA(), false); return;
            case 7: CJmp(CmpKind.NotEq, R(regC), OpA(), JmpB(), false); return;
            case 130: CJmp(CmpKind.Eq, OpA(), R(regB), JmpC(), false); return;
            case 192:
            case 194:
                CJmp(CmpKind.Lt, R(regB), R(regA), JmpC(), false); return;
            case 125: CJmp(CmpKind.Le, R(regC), R(regA), JmpB(), true); return;
            case 44: CJmp(CmpKind.Lt, OpB(), R(regA), JmpC(), true); return;
            case 110: CJmp(CmpKind.Le, OpA(), R(regB), JmpC(), false); return;
            case 182: CJmp(CmpKind.Le, OpC(), R(regB), JmpA(), true); return;
            case 15: CJmp(CmpKind.Le, OpB(), R(regA), JmpC(), false); return;
            case 72: CJmp(CmpKind.Eq, R(regC), OpA(), JmpB(), false); return;
            case 108: CJmp(CmpKind.Le, OpC(), R(regA), JmpB(), true); return;
            case 188: CJmp(CmpKind.Le, OpC(), R(regA), JmpB(), false); return;
            case 121: Emit(new TestJump(idx, R(regA), true, JmpC(), idx + 1)); return;
            case 146:
            case 148:
                Emit(new TestJump(idx, R(regA), false, JmpB(), idx + 1)); return;
            case 187: Emit(new Jump(idx, JmpB())); return;
            case 2: Emit(new Call(idx, R(regB), ArgDecode(regC), RetDecode(regA))); return;
            case 33: Emit(new Call(idx, R(regC), 0, 0)); return;
            case 41: Emit(new Call(idx, R(regA), 1, 0)); return;
            case 56: Emit(new Call(idx, R(regA), 2, 0)); return;
            case 107: Emit(new Call(idx, R(regA), 2, 1)); return;
            case 114: Emit(new Call(idx, R(regC), 0, 1)); return;
            case 118: Emit(new Call(idx, R(regB), -1, 0)); return;
            case 124: Emit(new Call(idx, R(regB), -1, 1)); return;
            case 139: Emit(new Call(idx, R(regA), -1, 0)); return;
            case 141: Emit(new Call(idx, R(regA), -1, 1)); return;
            case 167: Emit(new Call(idx, R(regC), 1, 1)); return;
            case 66: Emit(new TailCall(idx, R(regB), -1)); return;
            case 23: Emit(new TailCall(idx, R(regC), 1)); return;
            case 128: Emit(new TailCall(idx, R(regA), -1)); return;
            case 178: Emit(new TailCall(idx, R(regC), 0)); return;
            case 170: Emit(new Return(idx, 0, 0)); return;
            case 75: Emit(new Return(idx, regA, 1)); return;
            case 152: Emit(new Return(idx, regA, -1)); return;
            case 85: Emit(new ForPrep(idx, R(regA), JmpB())); return;
            case 57:
            case 137:
                Emit(new ForLoop(idx, R(regA), JmpB(), idx + 1)); return;
            case 103: Emit(new IterPrep(idx, R(regA), JmpB())); return;
            case 96: Emit(new TForLoop(idx, R(regA), 0, JmpB(), idx + 1)); return;
            case 106:
            case 140:
                Emit(new SetList(idx, R(regA), 0, regC)); return;
            case 30: Emit(new CloseUpvals(idx, R(regA))); return;
            case 67:
            case 181:
                {
                    int protoIdx = -1;
                    if (proto.ClosureBackpatches.Exists(bp => bp.InstrIndex == idx))
                        protoIdx = regC;
                    else
                    {
                        if (regC >= 0 && regC < 10000) protoIdx = regC;
                        else if (regB >= 0 && regB < 10000) protoIdx = regB;
                        else if (regA > 0) protoIdx = regA - 1;
                    }

                    int destReg = regB;
                    if (proto.ClosureBackpatches.Exists(bp => bp.InstrIndex == idx))
                        destReg = regB;
                    else if (regA != protoIdx && regA >= 0)
                        destReg = regA;

                    Emit(new MakeClosure(idx, R(destReg), protoIdx));
                    return;
                }
            case 14:
            case 89:
            case 115:
            case 160:
                Emit(new LoadVararg(idx, R(regA), regC)); return;
            case 189: Emit(new Nop(idx, "internal_params")); return;
            case 53: Emit(new SetGlobal(idx, OpC(), R(regA))); return;
            case 60: Bin(BinOpKind.Sub, regA, R(regC), OpB()); return;
            case 70: Bin(BinOpKind.Mul, regB, R(regC), R(regA)); return;
            case 71: Emit(new SetUpval(idx, new UpvalOp(regB), R(regA))); return;
            case 91: Emit(new Self(idx, R(regB), R(regC), OpA())); return;
            case 157: Cmp(CmpKind.NotEq, regA, R(regC), OpB()); return;
            case 166: Emit(new SetTable(idx, R(regA), OpB(), OpC())); return;
            case 173: Bin(BinOpKind.Add, regA, OpC(), OpB()); return;
            case 1:
            case 6:
            case 9:
            case 24:
            case 42:
            case 47:
            case 36:
            case 46:
            case 55:
            case 61:
            case 68:
            case 73:
            case 76:
            case 79:
            case 88:
            case 100:
            case 109:
            case 112:
            case 120:
            case 122:
            case 123:
            case 127:
            case 149:
            case 162:
            case 165:
            case 183:
            case 185:
                HandleFragment(vOp, idx, regA, regB, regC, constA, constB, protoRef);
                return;
            case 13:
            case 34:
            case 74:
            case 81:
            case 99:
            case 150:
            case 164:
                Emit(new Nop(idx, $"internal (vOp={vOp})")); return;
            default:
                Emit(new Nop(idx, $"unmapped vOp={vOp}")); return;
        }
    }
    private void HandleFragment(int vOp, int idx, int regA, int regB, int regC, object? constA, object? constB, object? protoRef)
    {
        switch (vOp)
        {
            case 42: _fragments.G = FragmentValue.RegFile(); break;
            case 61: _fragments.G = FragmentValue.Reg(regC); break;
            case 112: _fragments.Q = FragmentValue.Env(); break;
            case 6: _fragments.F = FragmentValue.Reg(regC); break;
            case 24: _fragments.F = constA != null ? FragmentValue.Const(constA) : FragmentValue.Reg(regA); break;
            case 9: _fragments.G = FragmentValue.RegFile(); _fragments.F = FragmentValue.Reg(regA); break;
            case 127: _fragments.G = FragmentValue.RegFile(); _fragments.F = FragmentValue.Reg(regB); break;
            case 162: _fragments.F = FragmentValue.Const(-1); break;
            case 88: _fragments.F = constB != null ? FragmentValue.Const(constB) : FragmentValue.Reg(regB); break;
            case 1: _fragments.F = FragmentValue.Reg(regC); _fragments.Q = FragmentValue.RegFile(); break;
            case 47: _fragments.O = FragmentValue.Reg(regC); break;
            case 100: _fragments.O = FragmentValue.Reg(regA); break;
            case 109: _fragments.O = constB != null ? FragmentValue.Const(constB) : FragmentValue.Reg(regB); break;
            case 165: _fragments.V = FragmentValue.Reg(regA); break;
            case 36: _fragments.Q = protoRef != null ? FragmentValue.Const(protoRef) : FragmentValue.Reg(regB); break;
            case 68: _fragments.O = FragmentValue.RegFile(); break;
            case 73: _fragments.V = FragmentValue.Reg(regA); break;
            case 122: _fragments.Q = FragmentValue.RegFile(); break;
            case 185: _fragments.Q = FragmentValue.RegFile(); _fragments.O = FragmentValue.Reg(regA); break;
            case 46:
            case 55:
                {
                    if (_fragments.Q?.IsRegisterFile == true && _fragments.O?.Kind == FragmentValue.FragKind.Register)
                        _fragments.Q = FragmentValue.Reg(_fragments.O.AsRegister());

                    var committed = _fragments.TryCommit(idx);
                    if (committed != null)
                        Emit(committed);
                    else
                    {
                        Emit(new Nop(idx, $"fragment commit fallback (vOp={vOp})"));
                        _fragments.Reset();
                    }
                    break;
                }

            case 76:
            case 79:
            case 120:
            case 123:
            case 149:
            case 183:
                {
                    var committed = _fragments.TryCommit(idx);
                    if (committed != null)
                        Emit(committed);
                    else
                    {
                        Emit(new Nop(idx, $"fragment commit fallback (vOp={vOp})"));
                        _fragments.Reset();
                    }
                    break;
                }
        }
    }
    public static Dictionary<int, OpcodeEntry> BuildSample2OpcodeMap()
    {
        var m = new Dictionary<int, OpcodeEntry>();

        void Add(int op, OpcodeEntry e) => m[op] = e;
        Add(12, new OpcodeEntry { Type = "Move", Dst = OpSrc.RegA, Src1 = OpSrc.RegB });
        Add(82, new OpcodeEntry { Type = "Move", Dst = OpSrc.RegA, Src1 = OpSrc.Nil });
        Add(20, new OpcodeEntry { Type = "NilRange", Src1 = OpSrc.RegB, Src2 = OpSrc.RegC });
        Add(158, new OpcodeEntry { Type = "NilRange", Src1 = OpSrc.RegB, Src2 = OpSrc.RegC });
        Add(95, new OpcodeEntry { Type = "Move", Dst = OpSrc.RegB, Src1 = OpSrc.OpC });
        Add(154, new OpcodeEntry { Type = "Nop", Comment = "LOADK_HELPER" });
        Add(50, new OpcodeEntry { Type = "GetGlobal", Dst = OpSrc.RegA, Src1 = OpSrc.OpB });
        Add(111, new OpcodeEntry { Type = "SetGlobal", Src1 = OpSrc.OpC, Src2 = OpSrc.RegA });
        Add(84, new OpcodeEntry { Type = "SetGlobal", Src1 = OpSrc.OpA, Src2 = OpSrc.OpB });
        Add(51, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegC, Src1 = OpSrc.RegA, Src2 = OpSrc.RegB });
        Add(136, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegC, Src1 = OpSrc.RegA, Src2 = OpSrc.OpB });
        Add(48, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.RegC });
        Add(8, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(132, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.RegA, Src1 = OpSrc.OpB, Src2 = OpSrc.OpC });
        Add(193, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.RegC, Src1 = OpSrc.OpA, Src2 = OpSrc.RegB });
        Add(27, new OpcodeEntry { Type = "NewTable", Dst = OpSrc.RegA });
        Add(37, new OpcodeEntry { Type = "NewTable", Dst = OpSrc.RegC });
        Add(10, new OpcodeEntry { Type = "GetUpval", Dst = OpSrc.RegC, Src1 = OpSrc.UpvalB });
        Add(129, new OpcodeEntry { Type = "GetUpval", Dst = OpSrc.RegB, Src1 = OpSrc.UpvalC });
        Add(105, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegA, Src1 = OpSrc.UpvalB, Src2 = OpSrc.OpC });
        Add(78, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegA, Src1 = OpSrc.UpvalC, Src2 = OpSrc.RegB });
        Add(25, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegA, Src1 = OpSrc.UpvalC, Src2 = OpSrc.OpB });
        Add(17, new OpcodeEntry { Type = "GetTable", Dst = OpSrc.RegC, Src1 = OpSrc.UpvalB, Src2 = OpSrc.RegA });
        Add(151, new OpcodeEntry { Type = "SetUpval", Dst = OpSrc.UpvalB, Src1 = OpSrc.RegA });
        Add(43, new OpcodeEntry { Type = "SetUpval", Dst = OpSrc.UpvalA, Src1 = OpSrc.OpC });
        Add(64, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.UpvalC, Src1 = OpSrc.RegB, Src2 = OpSrc.RegA });
        Add(119, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.UpvalC, Src1 = OpSrc.RegB, Src2 = OpSrc.RegA });
        Add(172, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.UpvalA, Src1 = OpSrc.OpC, Src2 = OpSrc.RegB });
        Add(180, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.UpvalC, Src1 = OpSrc.RegA, Src2 = OpSrc.OpB });
        Add(80, new OpcodeEntry { Type = "BinOp", Kind = "Add", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(143, new OpcodeEntry { Type = "BinOp", Kind = "Sub", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.RegC });
        Add(39, new OpcodeEntry { Type = "BinOp", Kind = "Mul", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(83, new OpcodeEntry { Type = "BinOp", Kind = "Div", Dst = OpSrc.RegC, Src1 = OpSrc.RegB, Src2 = OpSrc.RegA });
        Add(65, new OpcodeEntry { Type = "BinOp", Kind = "Mod", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(133, new OpcodeEntry { Type = "BinOp", Kind = "Add", Dst = OpSrc.RegC, Src1 = OpSrc.RegA, Src2 = OpSrc.OpB });
        Add(45, new OpcodeEntry { Type = "BinOp", Kind = "Sub", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(97, new OpcodeEntry { Type = "BinOp", Kind = "Mul", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.OpC });
        Add(168, new OpcodeEntry { Type = "BinOp", Kind = "Div", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.OpC });
        Add(3, new OpcodeEntry { Type = "BinOp", Kind = "Mod", Dst = OpSrc.RegC, Src1 = OpSrc.RegA, Src2 = OpSrc.OpB });
        Add(94, new OpcodeEntry { Type = "BinOp", Kind = "IDiv", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.OpC });
        Add(177, new OpcodeEntry { Type = "BinOp", Kind = "Pow", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.OpA });
        Add(92, new OpcodeEntry { Type = "BinOp", Kind = "Add", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.RegB });
        Add(54, new OpcodeEntry { Type = "BinOp", Kind = "Sub", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.RegB });
        Add(117, new OpcodeEntry { Type = "BinOp", Kind = "Mul", Dst = OpSrc.RegB, Src1 = OpSrc.OpC, Src2 = OpSrc.RegA });
        Add(86, new OpcodeEntry { Type = "BinOp", Kind = "Div", Dst = OpSrc.RegC, Src1 = OpSrc.OpA, Src2 = OpSrc.RegB });
        Add(32, new OpcodeEntry { Type = "BinOp", Kind = "Pow", Dst = OpSrc.RegB, Src1 = OpSrc.OpA, Src2 = OpSrc.RegC });
        Add(19, new OpcodeEntry { Type = "BinOp", Kind = "Add", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.OpB });
        Add(161, new OpcodeEntry { Type = "BinOp", Kind = "Sub", Dst = OpSrc.RegC, Src1 = OpSrc.OpA, Src2 = OpSrc.OpB });
        Add(16, new OpcodeEntry { Type = "BinOp", Kind = "Mod", Dst = OpSrc.RegB, Src1 = OpSrc.OpC, Src2 = OpSrc.OpA });
        Add(101, new OpcodeEntry { Type = "BinOp", Kind = "BXor", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.OpC });
        Add(184, new OpcodeEntry { Type = "BinOp", Kind = "BXor", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(153, new OpcodeEntry { Type = "UnaryOp", Kind = "Negate", Dst = OpSrc.RegC, Src1 = OpSrc.RegA });
        Add(29, new OpcodeEntry { Type = "UnaryOp", Kind = "Not", Dst = OpSrc.RegB, Src1 = OpSrc.RegC });
        Add(63, new OpcodeEntry { Type = "UnaryOp", Kind = "Len", Dst = OpSrc.RegC, Src1 = OpSrc.RegA });
        Add(93, new OpcodeEntry { Type = "Concat", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.RegC });
        Add(52, new OpcodeEntry { Type = "Concat", Dst = OpSrc.RegA, Src1 = OpSrc.OpB, Src2 = OpSrc.RegC });
        Add(69, new OpcodeEntry { Type = "Concat", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.OpA });
        Add(62, new OpcodeEntry { Type = "Self", Dst = OpSrc.RegC, Src1 = OpSrc.RegB, Src2 = OpSrc.OpA });
        Add(144, new OpcodeEntry { Type = "CmpBool", Kind = "Eq", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.RegC });
        Add(175, new OpcodeEntry { Type = "CmpBool", Kind = "Eq", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.OpC });
        Add(190, new OpcodeEntry { Type = "CmpBool", Kind = "Eq", Dst = OpSrc.RegC, Src1 = OpSrc.OpA, Src2 = OpSrc.RegB });
        Add(21, new OpcodeEntry { Type = "CmpBool", Kind = "Eq", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.OpB });
        Add(4, new OpcodeEntry { Type = "CmpBool", Kind = "NotEq", Dst = OpSrc.RegC, Src1 = OpSrc.RegB, Src2 = OpSrc.RegA });
        Add(113, new OpcodeEntry { Type = "CmpBool", Kind = "NotEq", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(102, new OpcodeEntry { Type = "CmpBool", Kind = "NotEq", Dst = OpSrc.RegA, Src1 = OpSrc.OpB, Src2 = OpSrc.OpC });
        Add(147, new OpcodeEntry { Type = "CmpBool", Kind = "Lt", Dst = OpSrc.RegC, Src1 = OpSrc.RegB, Src2 = OpSrc.RegA });
        Add(104, new OpcodeEntry { Type = "CmpBool", Kind = "Lt", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.OpC });
        Add(186, new OpcodeEntry { Type = "CmpBool", Kind = "Lt", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.RegB });
        Add(77, new OpcodeEntry { Type = "CmpBool", Kind = "Le", Dst = OpSrc.RegB, Src1 = OpSrc.RegA, Src2 = OpSrc.RegC });
        Add(0, new OpcodeEntry { Type = "CmpBool", Kind = "Le", Dst = OpSrc.RegA, Src1 = OpSrc.RegB, Src2 = OpSrc.OpC });
        Add(159, new OpcodeEntry { Type = "CmpBool", Kind = "Le", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.RegB });
        Add(174, new OpcodeEntry { Type = "CmpBool", Kind = "Le", Dst = OpSrc.RegC, Src1 = OpSrc.OpA, Src2 = OpSrc.OpB });
        Add(35, new OpcodeEntry { Type = "CmpBool", Kind = "Gt", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(5, new OpcodeEntry { Type = "CmpBool", Kind = "Gt", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(171, new OpcodeEntry { Type = "CmpBool", Kind = "Gt", Dst = OpSrc.RegB, Src1 = OpSrc.OpC, Src2 = OpSrc.RegA });
        Add(138, new OpcodeEntry { Type = "CmpBool", Kind = "Gt", Dst = OpSrc.RegB, Src1 = OpSrc.OpA, Src2 = OpSrc.OpC });
        Add(40, new OpcodeEntry { Type = "CmpBool", Kind = "Ge", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.RegB });
        Add(191, new OpcodeEntry { Type = "CmpBool", Kind = "Ge", Dst = OpSrc.RegC, Src1 = OpSrc.RegA, Src2 = OpSrc.OpB });
        Add(131, new OpcodeEntry { Type = "CmpBool", Kind = "Ge", Dst = OpSrc.RegB, Src1 = OpSrc.OpA, Src2 = OpSrc.OpC });
        Add(156, new OpcodeEntry { Type = "CondJump", Kind = "Eq", Src1 = OpSrc.RegA, Src2 = OpSrc.RegB, Target = OpSrc.JmpC });
        Add(28, new OpcodeEntry { Type = "CondJump", Kind = "NotEq", Src1 = OpSrc.RegB, Src2 = OpSrc.RegC, Target = OpSrc.JmpA });
        Add(7, new OpcodeEntry { Type = "CondJump", Kind = "NotEq", Src1 = OpSrc.RegC, Src2 = OpSrc.OpA, Target = OpSrc.JmpB });
        Add(130, new OpcodeEntry { Type = "CondJump", Kind = "Eq", Src1 = OpSrc.OpA, Src2 = OpSrc.RegB, Target = OpSrc.JmpC });
        Add(192, new OpcodeEntry { Type = "CondJump", Kind = "Lt", Src1 = OpSrc.RegB, Src2 = OpSrc.RegA, Target = OpSrc.JmpC });
        Add(194, new OpcodeEntry { Type = "CondJump", Kind = "Lt", Src1 = OpSrc.RegB, Src2 = OpSrc.RegA, Target = OpSrc.JmpC });
        Add(125, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.RegC, Src2 = OpSrc.RegA, Target = OpSrc.JmpB, Invert = true });
        Add(44, new OpcodeEntry { Type = "CondJump", Kind = "Lt", Src1 = OpSrc.OpB, Src2 = OpSrc.RegA, Target = OpSrc.JmpC, Invert = true });
        Add(110, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.OpA, Src2 = OpSrc.RegB, Target = OpSrc.JmpC });
        Add(182, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.OpC, Src2 = OpSrc.RegB, Target = OpSrc.JmpA, Invert = true });
        Add(15, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.OpB, Src2 = OpSrc.RegA, Target = OpSrc.JmpC });
        Add(72, new OpcodeEntry { Type = "CondJump", Kind = "Eq", Src1 = OpSrc.RegC, Src2 = OpSrc.OpA, Target = OpSrc.JmpB });
        Add(108, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.OpC, Src2 = OpSrc.RegA, Target = OpSrc.JmpB, Invert = true });
        Add(188, new OpcodeEntry { Type = "CondJump", Kind = "Le", Src1 = OpSrc.OpC, Src2 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(121, new OpcodeEntry { Type = "TestJump", Src1 = OpSrc.RegA, Target = OpSrc.JmpC, TestCondition = true });
        Add(146, new OpcodeEntry { Type = "TestJump", Src1 = OpSrc.RegA, Target = OpSrc.JmpB, TestCondition = false });
        Add(148, new OpcodeEntry { Type = "TestJump", Src1 = OpSrc.RegA, Target = OpSrc.JmpB, TestCondition = false });
        Add(187, new OpcodeEntry { Type = "Jump", Target = OpSrc.JmpB });
        Add(2, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegB, ArgSlot = 'C', RetSlot = 'A' });
        Add(33, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegC, FixedArgs = 0, FixedRets = 0 });
        Add(41, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegA, FixedArgs = 1, FixedRets = 0 });
        Add(56, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegA, FixedArgs = 2, FixedRets = 0 });
        Add(107, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegA, FixedArgs = 2, FixedRets = 1 });
        Add(114, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegC, FixedArgs = 0, FixedRets = 1 });
        Add(118, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegB, FixedArgs = -1, FixedRets = 0 });
        Add(124, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegB, FixedArgs = -1, FixedRets = 1 });
        Add(139, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegA, FixedArgs = -1, FixedRets = 0 });
        Add(141, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegA, FixedArgs = -1, FixedRets = 1 });
        Add(167, new OpcodeEntry { Type = "Call", Dst = OpSrc.RegC, FixedArgs = 1, FixedRets = 1 });
        Add(66, new OpcodeEntry { Type = "TailCall", Dst = OpSrc.RegB, FixedArgs = -1 });
        Add(23, new OpcodeEntry { Type = "TailCall", Dst = OpSrc.RegC, FixedArgs = 1 });
        Add(128, new OpcodeEntry { Type = "TailCall", Dst = OpSrc.RegA, FixedArgs = -1 });
        Add(178, new OpcodeEntry { Type = "TailCall", Dst = OpSrc.RegC, FixedArgs = 0 });
        Add(170, new OpcodeEntry { Type = "Return", FixedArgs = 0 });
        Add(75, new OpcodeEntry { Type = "Return", Dst = OpSrc.RegA, FixedArgs = 1 });
        Add(152, new OpcodeEntry { Type = "Return", Dst = OpSrc.RegA, FixedArgs = -1 });
        Add(85, new OpcodeEntry { Type = "ForPrep", Src1 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(57, new OpcodeEntry { Type = "ForLoop", Src1 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(137, new OpcodeEntry { Type = "ForLoop", Src1 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(103, new OpcodeEntry { Type = "IterPrep", Src1 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(96, new OpcodeEntry { Type = "TForLoop", Src1 = OpSrc.RegA, Target = OpSrc.JmpB });
        Add(106, new OpcodeEntry { Type = "SetList", Src1 = OpSrc.RegA, Src2 = OpSrc.RegC });
        Add(140, new OpcodeEntry { Type = "SetList", Src1 = OpSrc.RegA, Src2 = OpSrc.RegC });
        Add(30, new OpcodeEntry { Type = "CloseUpvals", Src1 = OpSrc.RegA });
        Add(67, new OpcodeEntry { Type = "Closure", Dst = OpSrc.RegB });
        Add(181, new OpcodeEntry { Type = "Closure", Dst = OpSrc.RegB });
        Add(14, new OpcodeEntry { Type = "Vararg", Dst = OpSrc.RegA, Src1 = OpSrc.RegC });
        Add(89, new OpcodeEntry { Type = "Vararg", Dst = OpSrc.RegA, Src1 = OpSrc.RegC });
        Add(115, new OpcodeEntry { Type = "Vararg", Dst = OpSrc.RegA, Src1 = OpSrc.RegC });
        Add(160, new OpcodeEntry { Type = "Vararg", Dst = OpSrc.RegA, Src1 = OpSrc.RegC });
        Add(189, new OpcodeEntry { Type = "Nop", Comment = "internal_params" });
        Add(53, new OpcodeEntry { Type = "SetGlobal", Src1 = OpSrc.OpC, Src2 = OpSrc.RegA });
        Add(60, new OpcodeEntry { Type = "BinOp", Kind = "Sub", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(70, new OpcodeEntry { Type = "BinOp", Kind = "Mul", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.RegA });
        Add(71, new OpcodeEntry { Type = "SetUpval", Dst = OpSrc.UpvalB, Src1 = OpSrc.RegA });
        Add(91, new OpcodeEntry { Type = "Self", Dst = OpSrc.RegB, Src1 = OpSrc.RegC, Src2 = OpSrc.OpA });
        Add(157, new OpcodeEntry { Type = "CmpBool", Kind = "NotEq", Dst = OpSrc.RegA, Src1 = OpSrc.RegC, Src2 = OpSrc.OpB });
        Add(166, new OpcodeEntry { Type = "SetTable", Dst = OpSrc.RegA, Src1 = OpSrc.OpB, Src2 = OpSrc.OpC });
        Add(173, new OpcodeEntry { Type = "BinOp", Kind = "Add", Dst = OpSrc.RegA, Src1 = OpSrc.OpC, Src2 = OpSrc.OpB });
        foreach (var nop in new[] { 13, 34, 74, 81, 99, 150, 164 })
            Add(nop, new OpcodeEntry { Type = "Nop", Comment = "internal" });

        return m;
    }
    public static Dictionary<int, FragmentEntry> BuildSample2FragmentMap()
    {
        var m = new Dictionary<int, FragmentEntry>();
        m[42] = new FragmentEntry { G = "RegFile" };
        m[61] = new FragmentEntry { G = "RegC" };
        m[112] = new FragmentEntry { Q = "Env" };
        m[6] = new FragmentEntry { F = "RegC" };
        m[24] = new FragmentEntry { F = "OpA" };
        m[9] = new FragmentEntry { G = "RegFile", F = "RegA" };
        m[127] = new FragmentEntry { G = "RegFile", F = "RegB" };
        m[162] = new FragmentEntry { F = "Const:-1" };
        m[1] = new FragmentEntry { F = "RegC", Q = "RegFile" };
        m[47] = new FragmentEntry { O = "RegC" };
        m[36] = new FragmentEntry { Q = "OpC" };
        m[68] = new FragmentEntry { O = "RegFile" };
        m[73] = new FragmentEntry { V = "RegA" };
        m[88] = new FragmentEntry { F = "OpB" };
        m[100] = new FragmentEntry { O = "RegA" };
        m[109] = new FragmentEntry { O = "OpB" };
        m[122] = new FragmentEntry { Q = "RegFile" };
        m[165] = new FragmentEntry { V = "RegA" };
        m[185] = new FragmentEntry { Q = "RegFile", O = "RegA" };
        m[46] = new FragmentEntry { Commit = true };
        m[55] = new FragmentEntry { Commit = true };
        m[76] = new FragmentEntry { Commit = true };
        m[79] = new FragmentEntry { Commit = true };
        m[120] = new FragmentEntry { Commit = true };
        m[123] = new FragmentEntry { Commit = true };
        m[149] = new FragmentEntry { Commit = true };
        m[183] = new FragmentEntry { Commit = true };

        return m;
    }
    public static List<int> BuildSample2NopOpcodes() =>
        [13, 34, 74, 81, 99, 150, 164];
}
