namespace LuraphDeobfuscator.Deobfuscator.IR;
public enum BinOpKind
{
    Add, Sub, Mul, Div, Mod, Pow, IDiv, BXor, BAnd, BOr
}
public enum UnaryOpKind
{
    Negate, Not, Len, BNot
}
public enum CmpKind
{
    Eq, NotEq, Lt, Le, Gt, Ge
}
public static class IREnumExtensions
{
    public static string Symbol(this BinOpKind kind) => kind switch
    {
        BinOpKind.Add => "+",
        BinOpKind.Sub => "-",
        BinOpKind.Mul => "*",
        BinOpKind.Div => "/",
        BinOpKind.Mod => "%",
        BinOpKind.Pow => "^",
        BinOpKind.IDiv => "//",
        BinOpKind.BXor => "~",
        BinOpKind.BAnd => "&",
        BinOpKind.BOr => "|",
        _ => "?"
    };
    public static string Symbol(this UnaryOpKind kind) => kind switch
    {
        UnaryOpKind.Negate => "-",
        UnaryOpKind.Not => "not ",
        UnaryOpKind.Len => "#",
        UnaryOpKind.BNot => "~",
        _ => "?"
    };
    public static string Symbol(this CmpKind kind) => kind switch
    {
        CmpKind.Eq => "==",
        CmpKind.NotEq => "~=",
        CmpKind.Lt => "<",
        CmpKind.Le => "<=",
        CmpKind.Gt => ">",
        CmpKind.Ge => ">=",
        _ => "?"
    };
    public static CmpKind Invert(this CmpKind kind) => kind switch
    {
        CmpKind.Eq => CmpKind.NotEq,
        CmpKind.NotEq => CmpKind.Eq,
        CmpKind.Lt => CmpKind.Ge,
        CmpKind.Le => CmpKind.Gt,
        CmpKind.Gt => CmpKind.Le,
        CmpKind.Ge => CmpKind.Lt,
        _ => kind
    };
    public static CmpKind Swap(this CmpKind kind) => kind switch
    {
        CmpKind.Lt => CmpKind.Gt,
        CmpKind.Le => CmpKind.Ge,
        CmpKind.Gt => CmpKind.Lt,
        CmpKind.Ge => CmpKind.Le,
        _ => kind
    };
}
