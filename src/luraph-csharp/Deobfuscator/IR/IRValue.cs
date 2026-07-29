namespace LuraphDeobfuscator.Deobfuscator.IR;
public class IRValue
{
    public IRValueKind Kind { get; set; }
    public int Register { get; set; } = -1;
    public int UpvalueIndex { get; set; } = -1;
    public object? ConstantValue { get; set; }
    public int ProtoIndex { get; set; } = -1;

    public static IRValue Reg(int index) => new()
    {
        Kind = IRValueKind.Register,
        Register = index
    };

    public static IRValue Const(object? value) => new()
    {
        Kind = IRValueKind.Constant,
        ConstantValue = value
    };

    public static IRValue Upval(int index) => new()
    {
        Kind = IRValueKind.Upvalue,
        UpvalueIndex = index
    };

    public static IRValue Proto(int index) => new()
    {
        Kind = IRValueKind.Proto,
        ProtoIndex = index
    };

    public static IRValue Nil() => new()
    {
        Kind = IRValueKind.Constant,
        ConstantValue = null
    };

    public static IRValue Bool(bool v) => new()
    {
        Kind = IRValueKind.Constant,
        ConstantValue = v
    };

    public static IRValue Number(double v) => new()
    {
        Kind = IRValueKind.Constant,
        ConstantValue = v
    };

    public bool IsRegister => Kind == IRValueKind.Register;
    public bool IsConstant => Kind == IRValueKind.Constant;
    public bool IsUpvalue => Kind == IRValueKind.Upvalue;
    public int Index => Kind switch
    {
        IRValueKind.Register => Register,
        IRValueKind.Upvalue => UpvalueIndex,
        IRValueKind.Proto => ProtoIndex,
        _ => -1
    };

    public override string ToString()
    {
        return Kind switch
        {
            IRValueKind.Register => $"R({Register})",
            IRValueKind.Constant when ConstantValue is string s => $"\"{Escape(s)}\"",
            IRValueKind.Constant when ConstantValue is bool b => b ? "true" : "false",
            IRValueKind.Constant when ConstantValue is null => "nil",
            IRValueKind.Constant => $"{ConstantValue}",
            IRValueKind.Upvalue => $"U({UpvalueIndex})",
            IRValueKind.Proto => $"Proto({ProtoIndex})",
            _ => "???"
        };
    }

    private static string Escape(string s)
    {
        if (s.Length > 40) s = s[..37] + "...";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}

public enum IRValueKind
{
    Register, Constant, Upvalue, Proto,
}
