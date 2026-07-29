using System.Text;
using LuraphDeobfuscator.Deobfuscator.AST;

namespace LuraphDeobfuscator.Deobfuscator.Printer;
public class LuaPrinter : IASTVisitor
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private const string IndentStr = "    ";
    public static string Print(ASTNode root)
    {
        var printer = new LuaPrinter();
        root.Accept(printer);
        return printer._sb.ToString().TrimEnd();
    }

    public void VisitBlock(ASTBlock node)
    {
        foreach (var stmt in node.Statements)
        {
            if (stmt is ASTBlock nested)
            {
                VisitBlock(nested);
                continue;
            }

            stmt.Accept(this);
        }
    }

    public void VisitAssign(ASTAssign node)
    {
        WriteIndent();
        if (node.IsLocal)
            _sb.Append("local ");
        WriteExpr(node.Target);
        _sb.Append(" = ");
        WriteExpr(node.Value);
        _sb.AppendLine();
    }

    public void VisitMultiAssign(ASTMultiAssign node)
    {
        WriteIndent();
        if (node.IsLocal)
            _sb.Append("local ");

        for (int i = 0; i < node.Targets.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            WriteExpr(node.Targets[i]);
        }

        _sb.Append(" = ");

        for (int i = 0; i < node.Values.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            WriteExpr(node.Values[i]);
        }
        _sb.AppendLine();
    }

    public void VisitIf(ASTIf node)
    {
        if (node.Then.Statements.Count == 0 && node.Else != null && node.Else.Statements.Count > 0)
        {
            var inverted = new ASTIf
            {
                Condition = InvertCondition(node.Condition),
                Then = node.Else,
                Else = null
            };
            VisitIf(inverted);
            return;
        }

        WriteIndent();
        _sb.Append("if ");
        WriteExpr(node.Condition);
        _sb.AppendLine(" then");

        _indent++;
        VisitBlock(node.Then);
        _indent--;

        if (node.Else != null && node.Else.Statements.Count > 0)
        {
            if (node.Else.Statements.Count == 1 && node.Else.Statements[0] is ASTIf elseIf)
            {
                WriteIndent();
                _sb.Append("else");
                VisitElseIf(elseIf);
                return;
            }

            WriteIndent();
            _sb.AppendLine("else");
            _indent++;
            VisitBlock(node.Else);
            _indent--;
        }

        WriteIndent();
        _sb.AppendLine("end");
    }

    private void VisitElseIf(ASTIf node)
    {
        if (node.Then.Statements.Count == 0 && node.Else != null && node.Else.Statements.Count > 0)
        {
            var inverted = new ASTIf
            {
                Condition = InvertCondition(node.Condition),
                Then = node.Else,
                Else = null
            };
            VisitElseIf(inverted);
            return;
        }

        _sb.Append("if ");
        WriteExpr(node.Condition);
        _sb.AppendLine(" then");

        _indent++;
        VisitBlock(node.Then);
        _indent--;

        if (node.Else != null && node.Else.Statements.Count > 0)
        {
            if (node.Else.Statements.Count == 1 && node.Else.Statements[0] is ASTIf elseIf)
            {
                WriteIndent();
                _sb.Append("else");
                VisitElseIf(elseIf);
                return;
            }

            WriteIndent();
            _sb.AppendLine("else");
            _indent++;
            VisitBlock(node.Else);
            _indent--;
        }

        WriteIndent();
        _sb.AppendLine("end");
    }

    public void VisitWhile(ASTWhile node)
    {
        WriteIndent();
        _sb.Append("while ");
        WriteExpr(node.Condition);
        _sb.AppendLine(" do");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.AppendLine("end");
    }

    public void VisitRepeatUntil(ASTRepeatUntil node)
    {
        WriteIndent();
        _sb.AppendLine("repeat");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.Append("until ");
        WriteExpr(node.Condition);
        _sb.AppendLine();
    }

    public void VisitNumericFor(ASTNumericFor node)
    {
        WriteIndent();
        _sb.Append($"for {node.Variable} = ");
        WriteExpr(node.Init);
        _sb.Append(", ");
        WriteExpr(node.Limit);
        if (node.Step is not ASTLiteral { Value: "1" })
        {
            _sb.Append(", ");
            WriteExpr(node.Step);
        }
        _sb.AppendLine(" do");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.AppendLine("end");
    }

    public void VisitGenericFor(ASTGenericFor node)
    {
        WriteIndent();
        _sb.Append("for ");
        _sb.Append(string.Join(", ", node.Variables));
        _sb.Append(" in ");
        WriteExpr(node.Iterator);
        bool stateIsNil = node.State is ASTLiteral { Value: "nil" };
        bool controlIsNil = node.Control is ASTLiteral { Value: "nil" };
        if (!stateIsNil || !controlIsNil)
        {
            _sb.Append(", ");
            WriteExpr(node.State);
            if (!controlIsNil)
            {
                _sb.Append(", ");
                WriteExpr(node.Control);
            }
        }
        _sb.AppendLine(" do");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.AppendLine("end");
    }

    public void VisitReturn(ASTReturn node)
    {
        WriteIndent();
        if (node.Values.Count == 0)
        {
            _sb.AppendLine("return");
        }
        else
        {
            _sb.Append("return ");
            for (int i = 0; i < node.Values.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                WriteExpr(node.Values[i]);
            }
            _sb.AppendLine();
        }
    }

    public void VisitBreak(ASTBreak node)
    {
        WriteIndent();
        _sb.AppendLine("break");
    }

    public void VisitDoBlock(ASTDoBlock node)
    {
        WriteIndent();
        _sb.AppendLine("do");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.AppendLine("end");
    }

    public void VisitExpressionStatement(ASTExpressionStatement node)
    {
        WriteIndent();
        WriteExpr(node.Expression);
        _sb.AppendLine();
    }

    public void VisitRegisterRef(ASTRegisterRef node)
    {
        _sb.Append(node.Name ?? $"L_{node.Register}");
    }

    public void VisitUpvalueRef(ASTUpvalueRef node)
    {
        _sb.Append(node.Name ?? $"upval{node.Index}");
    }

    public void VisitLiteral(ASTLiteral node)
    {
        _sb.Append(node.Value);
    }

    public void VisitBinaryOp(ASTBinaryOp node)
    {
        if (node.Operator == "//")
        {
            _sb.Append("math.floor(");
            WriteExpr(node.Left);
            _sb.Append(" / ");
            WriteExpr(node.Right);
            _sb.Append(')');
            return;
        }

        int prec = GetPrecedence(node.Operator);
        bool needParens = NeedsParens(node, prec);

        if (needParens) _sb.Append('(');
        WriteExprWithPrec(node.Left, prec, isRight: false);
        _sb.Append($" {node.Operator} ");
        WriteExprWithPrec(node.Right, prec, isRight: true);
        if (needParens) _sb.Append(')');
    }

    public void VisitUnaryOp(ASTUnaryOp node)
    {
        var op = node.Operator;
        if (op == "not " || op == "not")
        {
            _sb.Append("not ");
        }
        else
        {
            _sb.Append(op);
        }
        WriteExprWithPrec(node.Operand, 12, isRight: false);
    }

    public void VisitCall(ASTCall node)
    {
        if (node.IsMethodCall && node.MethodName != null)
        {
            WriteExpr(node.Function);
            _sb.Append(':');
            _sb.Append(node.MethodName);
        }
        else
        {
            if (NeedsCalleeParens(node.Function))
            {
                _sb.Append('(');
                WriteExpr(node.Function);
                _sb.Append(')');
            }
            else
            {
                WriteExpr(node.Function);
            }
        }

        _sb.Append('(');
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            WriteExpr(node.Arguments[i]);
        }
        _sb.Append(')');
    }

    private static bool NeedsCalleeParens(ASTExpression expr) => expr is
        ASTLiteral or
        ASTBinaryOp or
        ASTUnaryOp or
        ASTNot or
        ASTConcat;

    public void VisitIndex(ASTIndex node)
    {
        WriteExpr(node.Table);
        if (node.IsDot && node.Key is ASTLiteral lit)
        {
            var key = lit.Value;
            if (key.StartsWith('"') && key.EndsWith('"'))
                key = key[1..^1];
            _sb.Append('.');
            _sb.Append(key);
        }
        else
        {
            _sb.Append('[');
            WriteExpr(node.Key);
            _sb.Append(']');
        }
    }

    public void VisitGlobalRef(ASTGlobalRef node)
    {
        _sb.Append(node.Name);
    }

    public void VisitTableConstructor(ASTTableConstructor node)
    {
        if (node.Fields.Count == 0)
        {
            _sb.Append("{}");
            return;
        }

        _sb.AppendLine("{");
        _indent++;

        foreach (var field in node.Fields)
        {
            WriteIndent();
            if (field.Key != null)
            {
                if (field.Key is ASTLiteral keyLit && IsIdentifier(keyLit.Value))
                {
                    _sb.Append(StripQuotes(keyLit.Value));
                    _sb.Append(" = ");
                }
                else
                {
                    _sb.Append('[');
                    WriteExpr(field.Key);
                    _sb.Append("] = ");
                }
            }
            WriteExpr(field.Value);
            _sb.AppendLine(",");
        }

        _indent--;
        WriteIndent();
        _sb.Append('}');
    }

    public void VisitFunction(ASTFunction node)
    {
        _sb.Append("function(");
        _sb.Append(string.Join(", ", node.Parameters));
        if (node.IsVararg)
        {
            if (node.Parameters.Count > 0) _sb.Append(", ");
            _sb.Append("...");
        }
        _sb.AppendLine(")");

        _indent++;
        VisitBlock(node.Body);
        _indent--;

        WriteIndent();
        _sb.Append("end");
    }

    public void VisitVararg(ASTVararg node)
    {
        _sb.Append("...");
    }

    public void VisitConcat(ASTConcat node)
    {
        for (int i = 0; i < node.Parts.Count; i++)
        {
            if (i > 0) _sb.Append(" .. ");
            WriteExpr(node.Parts[i]);
        }
    }

    public void VisitNot(ASTNot node)
    {
        _sb.Append("not ");
        WriteExpr(node.Operand);
    }

    private void WriteExpr(ASTExpression expr)
    {
        expr.Accept(this);
    }

    private void WriteExpr(ASTNode node)
    {
        node.Accept(this);
    }

    private void WriteIndent()
    {
        for (int i = 0; i < _indent; i++)
            _sb.Append(IndentStr);
    }

    private static bool IsIdentifier(string s)
    {
        var val = StripQuotes(s);
        if (string.IsNullOrEmpty(val)) return false;
        if (!char.IsLetter(val[0]) && val[0] != '_') return false;
        foreach (char c in val)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return !IsLuaKeyword(val);
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static bool IsLuaKeyword(string s) => s is
        "and" or "break" or "do" or "else" or "elseif" or "end" or
        "false" or "for" or "function" or "if" or "in" or "local" or
        "nil" or "not" or "or" or "repeat" or "return" or "then" or
        "true" or "until" or "while";
    private static ASTExpression InvertCondition(ASTExpression cond)
    {
        switch (cond)
        {
            case ASTNot not:
                return not.Operand;

            case ASTBinaryOp bin:
                var inv = bin.Operator switch
                {
                    "==" => "~=",
                    "~=" => "==",
                    "<" => ">=",
                    ">" => "<=",
                    "<=" => ">",
                    ">=" => "<",
                    _ => null
                };
                if (inv != null)
                    return new ASTBinaryOp { Left = bin.Left, Operator = inv, Right = bin.Right };
                return new ASTNot { Operand = cond };

            default:
                return new ASTNot { Operand = cond };
        }
    }
    private static int GetPrecedence(string op) => op switch
    {
        "or" => 1,
        "and" => 2,
        "<" or ">" or "<=" or ">=" or "~=" or "==" => 3,
        ".." => 4,
        "+" or "-" => 5,
        "*" or "/" or "%" => 6,
        "|" => 3,
        "~" => 3,
        "&" => 4,
        "<<" or ">>" => 4,
        "^" => 8,
        _ => 0,
    };
    private static bool IsRightAssociative(string op) => op is "^" or "..";
    private void WriteExprWithPrec(ASTExpression expr, int parentPrec, bool isRight)
    {
        if (expr is ASTBinaryOp child)
        {
            int childPrec = GetPrecedence(child.Operator);
            bool needsWrap = childPrec < parentPrec ||
                (childPrec == parentPrec && isRight != IsRightAssociative(child.Operator));
            if (needsWrap)
            {
                _sb.Append('(');
                WriteExpr(expr);
                _sb.Append(')');
            }
            else
            {
                WriteExpr(expr);
            }
        }
        else
        {
            WriteExpr(expr);
        }
    }
    private static bool NeedsParens(ASTBinaryOp node, int prec)
    {
        return false;
    }
}
