using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.VM;
public class FragmentState
{
    public FragmentValue? G { get; set; }
    public FragmentValue? F { get; set; }
    public FragmentValue? Q { get; set; }
    public FragmentValue? O { get; set; }
    public FragmentValue? V { get; set; }

    public bool HasPending => G != null || Q != null;

    public void Reset()
    {
        G = null; F = null; Q = null; O = null; V = null;
    }
    public IRInstr? TryCommit(int idx)
    {
        if (G != null && F != null && Q != null)
        {
            IRInstr instr;

            if (G.IsRegisterFile)
                instr = new Move(idx, new RegOp(F.AsRegister()), Q.ToOperand());
            else if (G.IsEnvironment)
                instr = new SetGlobal(idx, F.ToOperand(), Q.ToOperand());
            else
                instr = new SetTable(idx, G.ToOperand(), F.ToOperand(), Q.ToOperand());

            Reset();
            return instr;
        }

        return null;
    }
}
public class FragmentValue
{
    public enum FragKind
    {
        Register, Constant, RegisterFile, Environment, Upvalue,
    }

    public FragKind Kind { get; set; }
    public int RegisterIndex { get; set; } = -1;
    public object? ConstantValue { get; set; }
    public int UpvalueIndex { get; set; } = -1;

    public bool IsRegisterFile => Kind == FragKind.RegisterFile;
    public bool IsEnvironment => Kind == FragKind.Environment;

    public static FragmentValue Reg(int idx) => new() { Kind = FragKind.Register, RegisterIndex = idx };
    public static FragmentValue Const(object? val) => new() { Kind = FragKind.Constant, ConstantValue = val };
    public static FragmentValue RegFile() => new() { Kind = FragKind.RegisterFile };
    public static FragmentValue Env() => new() { Kind = FragKind.Environment };
    public static FragmentValue Upval(int idx) => new() { Kind = FragKind.Upvalue, UpvalueIndex = idx };

    public int AsRegister() => RegisterIndex;

    public Operand ToOperand() => Kind switch
    {
        FragKind.Register => new RegOp(RegisterIndex),
        FragKind.Constant => new ConstOp(ConstantValue),
        FragKind.Upvalue => new UpvalOp(UpvalueIndex),
        _ => new ConstOp(null),
    };
}
