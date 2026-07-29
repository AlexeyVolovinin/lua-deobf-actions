namespace LuraphDeobfuscator.Deobfuscator.IR;
public abstract record Operand
{
    public virtual int RegIndex => -1;
}
public sealed record RegOp(int Reg) : Operand
{
    public override int RegIndex => Reg;
    public override string ToString() => $"L_{Reg}";
}
public sealed record ConstOp(object? Value) : Operand
{
    public override string ToString() => Value switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        string s => $"\"{EscapeTruncated(s)}\"",
        double d => FormatNumber(d),
        long l => l.ToString(),
        int i => i.ToString(),
        _ => Value.ToString() ?? "???"
    };

    private static string EscapeTruncated(string s)
    {
        if (s.Length > 40) s = s[..37] + "...";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private static string FormatNumber(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString();
        return d.ToString("G");
    }
}
public sealed record UpvalOp(int Index) : Operand
{
    public override string ToString() => $"UPVAL_{Index}";
}
