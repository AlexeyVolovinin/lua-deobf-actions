using LuraphDeobfuscator.Deobfuscator.IR;
using LuraphDeobfuscator.Deobfuscator.CFG;

namespace LuraphDeobfuscator.Deobfuscator.AST;
public static class ExpressionBuilder
{
    public static ASTExpression FromOperand(Operand op) => FromOperand(op, -1, false);
    public static ASTExpression FromOperand(Operand op, int instrIndex, bool isDefinition) => op
    switch
    {
        RegOp r => RegisterRef(r.Reg, instrIndex, isDefinition),
        UpvalOp u => new ASTUpvalueRef(u.Index),
        ConstOp c => FromConst(c.Value),
        _ => new ASTLiteral("nil")
    };
    public static ASTExpression FromConst(object? value) => value
    switch
    {
        null => new ASTLiteral("nil"),
        bool b => new ASTLiteral(b ? "true" : "false"),
        double d => new ASTLiteral(FormatNumber(d)),
        long l => new ASTLiteral(l.ToString()),
        int i => new ASTLiteral(i.ToString()),
        string s => new ASTLiteral(FormatString(s)),
        _ => new ASTLiteral(value.ToString() ?? "nil")
    };
    public static ASTExpression FromCondition(IRInstr terminator) => terminator
    switch
    {
        CondJump cj => new ASTBinaryOp
        {
            Left = FromOperand(cj.Left, terminator.Index, false),
            Operator = cj.Cmp.Symbol(),
            Right = FromOperand(cj.Right, terminator.Index, false)
        },
        TestJump tj => tj.Invert ?
        (ASTExpression)new ASTNot
        {
            Operand = FromOperand(tj.Value, terminator.Index, false)
        } :
        FromOperand(tj.Value, terminator.Index, false),
        _ => new ASTLiteral("true")
    };
    public static ASTExpression NegateCondition(IRInstr terminator) => terminator
    switch
    {
        CondJump cj => new ASTBinaryOp
        {
            Left = FromOperand(cj.Left, terminator.Index, false),
            Operator = cj.Cmp.Invert().Symbol(),
            Right = FromOperand(cj.Right, terminator.Index, false)
        },
        TestJump tj => tj.Invert ?
        FromOperand(tj.Value, terminator.Index, false) :
        (ASTExpression)new ASTNot
        {
            Operand = FromOperand(tj.Value, terminator.Index, false)
        },
        _ => new ASTLiteral("false")
    };

    private static ASTRegisterRef RegisterRef(int reg, int instrIndex, bool isDefinition)
    {
        var rr = new ASTRegisterRef(reg);
        var hinted = SSABuilder.TryGetRegisterName(instrIndex, reg, isDefinition);
        if (!string.IsNullOrEmpty(hinted))
            rr.Name = hinted;
        return rr;
    }

    public static string FormatNumber(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString();
        return d.ToString("G");
    }

    public static string FormatString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                default:
                    if (c < ' ' || c > '~')
                        sb.Append($"\\{(int)c}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
    public static bool IsIdentifierKey(Operand op)
    {
        if (op is not ConstOp
            {
                Value: string s
            }) return false;
        return IsLuaIdentifier(s);
    }

    public static bool IsLuaIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return !IsLuaKeyword(s);
    }

    private static bool IsLuaKeyword(string s) => s is "and"
    or "break"
    or "do"
    or "else"
    or "elseif"
    or "end"
    or "false"
    or "for"
    or "function"
    or "if"
    or "in"
    or "local"
    or "nil"
    or "not"
    or "or"
    or "repeat"
    or "return"
    or "then"
    or "true"
    or "until"
    or "while";
}