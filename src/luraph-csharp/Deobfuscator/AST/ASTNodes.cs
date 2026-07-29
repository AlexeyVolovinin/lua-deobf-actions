namespace LuraphDeobfuscator.Deobfuscator.AST;
public abstract class ASTNode
{
    public abstract void Accept(IASTVisitor visitor);
}
public abstract class ASTExpression : ASTNode { }
public class ASTBlock : ASTNode
{
    public List<ASTNode> Statements { get; set; } = [];

    public override void Accept(IASTVisitor visitor) => visitor.VisitBlock(this);
}
public class ASTAssign : ASTNode
{
    public required ASTExpression Target { get; set; }
    public required ASTExpression Value { get; set; }
    public bool IsLocal { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitAssign(this);
}
public class ASTMultiAssign : ASTNode
{
    public List<ASTExpression> Targets { get; set; } = [];
    public List<ASTExpression> Values { get; set; } = [];
    public bool IsLocal { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitMultiAssign(this);
}
public class ASTIf : ASTNode
{
    public required ASTExpression Condition { get; set; }
    public required ASTBlock Then { get; set; }
    public ASTBlock? Else { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitIf(this);
}
public class ASTWhile : ASTNode
{
    public required ASTExpression Condition { get; set; }
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitWhile(this);
}
public class ASTRepeatUntil : ASTNode
{
    public required ASTExpression Condition { get; set; }
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitRepeatUntil(this);
}
public class ASTNumericFor : ASTNode
{
    public required string Variable { get; set; }
    public required ASTExpression Init { get; set; }
    public required ASTExpression Limit { get; set; }
    public required ASTExpression Step { get; set; }
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitNumericFor(this);
}
public class ASTGenericFor : ASTNode
{
    public required List<string> Variables { get; set; }
    public required ASTExpression Iterator { get; set; }
    public required ASTExpression State { get; set; }
    public required ASTExpression Control { get; set; }
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitGenericFor(this);
}
public class ASTReturn : ASTNode
{
    public List<ASTExpression> Values { get; set; } = [];

    public override void Accept(IASTVisitor visitor) => visitor.VisitReturn(this);
}
public class ASTBreak : ASTNode
{
    public override void Accept(IASTVisitor visitor) => visitor.VisitBreak(this);
}
public class ASTDoBlock : ASTNode
{
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitDoBlock(this);
}
public class ASTExpressionStatement : ASTNode
{
    public required ASTExpression Expression { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitExpressionStatement(this);
}
public class ASTRegisterRef : ASTExpression
{
    public int Register { get; set; }
    public string? Name { get; set; }

    public ASTRegisterRef(int reg) { Register = reg; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitRegisterRef(this);
    public override string ToString() => Name ?? $"L_{Register}";
}
public class ASTUpvalueRef : ASTExpression
{
    public int Index { get; set; }
    public string? Name { get; set; }

    public ASTUpvalueRef(int index) { Index = index; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitUpvalueRef(this);
    public override string ToString() => Name ?? $"UPVAL_{Index}";
}
public class ASTLiteral : ASTExpression
{
    public string Value { get; set; }

    public ASTLiteral(string value) { Value = value; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitLiteral(this);
    public override string ToString() => Value;
}
public class ASTBinaryOp : ASTExpression
{
    public required ASTExpression Left { get; set; }
    public required string Operator { get; set; }
    public required ASTExpression Right { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitBinaryOp(this);
}
public class ASTUnaryOp : ASTExpression
{
    public required string Operator { get; set; }
    public required ASTExpression Operand { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitUnaryOp(this);
}
public class ASTCall : ASTExpression
{
    public required ASTExpression Function { get; set; }
    public List<ASTExpression> Arguments { get; set; } = [];
    public int ReturnCount { get; set; } = 1;
    public bool IsMethodCall { get; set; }
    public string? MethodName { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitCall(this);
}
public class ASTIndex : ASTExpression
{
    public required ASTExpression Table { get; set; }
    public required ASTExpression Key { get; set; }
    public bool IsDot { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitIndex(this);
}
public class ASTGlobalRef : ASTExpression
{
    public required string Name { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitGlobalRef(this);
    public override string ToString() => Name;
}
public class ASTTableConstructor : ASTExpression
{
    public List<ASTTableField> Fields { get; set; } = [];

    public override void Accept(IASTVisitor visitor) => visitor.VisitTableConstructor(this);
}
public class ASTTableField
{
    public ASTExpression? Key { get; set; }
    public required ASTExpression Value { get; set; }
}
public class ASTFunction : ASTExpression
{
    public List<string> Parameters { get; set; } = [];
    public bool IsVararg { get; set; }
    public required ASTBlock Body { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitFunction(this);
}
public class ASTVararg : ASTExpression
{
    public override void Accept(IASTVisitor visitor) => visitor.VisitVararg(this);
}
public class ASTConcat : ASTExpression
{
    public List<ASTExpression> Parts { get; set; } = [];

    public override void Accept(IASTVisitor visitor) => visitor.VisitConcat(this);
}
public class ASTNot : ASTExpression
{
    public required ASTExpression Operand { get; set; }

    public override void Accept(IASTVisitor visitor) => visitor.VisitNot(this);
}

public interface IASTVisitor
{
    void VisitBlock(ASTBlock node);
    void VisitAssign(ASTAssign node);
    void VisitMultiAssign(ASTMultiAssign node);
    void VisitIf(ASTIf node);
    void VisitWhile(ASTWhile node);
    void VisitRepeatUntil(ASTRepeatUntil node);
    void VisitNumericFor(ASTNumericFor node);
    void VisitGenericFor(ASTGenericFor node);
    void VisitReturn(ASTReturn node);
    void VisitBreak(ASTBreak node);
    void VisitDoBlock(ASTDoBlock node);
    void VisitExpressionStatement(ASTExpressionStatement node);
    void VisitRegisterRef(ASTRegisterRef node);
    void VisitUpvalueRef(ASTUpvalueRef node);
    void VisitLiteral(ASTLiteral node);
    void VisitBinaryOp(ASTBinaryOp node);
    void VisitUnaryOp(ASTUnaryOp node);
    void VisitCall(ASTCall node);
    void VisitIndex(ASTIndex node);
    void VisitGlobalRef(ASTGlobalRef node);
    void VisitTableConstructor(ASTTableConstructor node);
    void VisitFunction(ASTFunction node);
    void VisitVararg(ASTVararg node);
    void VisitConcat(ASTConcat node);
    void VisitNot(ASTNot node);
}
