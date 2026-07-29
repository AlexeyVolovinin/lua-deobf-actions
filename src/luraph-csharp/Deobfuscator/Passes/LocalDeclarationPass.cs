using LuraphDeobfuscator.Deobfuscator.AST;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class LocalDeclarationPass
{
    public static void Apply(ASTBlock root, int entryNumParams)
    {
        var declared = new HashSet<int>();
        for (int i = 0; i < entryNumParams; i++)
            declared.Add(i);

        MarkBlock(root, declared);
    }

    private static void MarkBlock(ASTBlock block, HashSet<int> declared)
    {
        foreach (var stmt in block.Statements)
            MarkStatement(stmt, declared);
    }

    private static void MarkStatement(ASTNode stmt, HashSet<int> declared)
    {
        switch (stmt)
        {
            case ASTAssign assign:
                if (assign.Target is ASTRegisterRef reg && declared.Add(reg.Register))
                    assign.IsLocal = true;
                if (assign.Value is ASTFunction func)
                    MarkFunction(func);
                break;

            case ASTMultiAssign multi:
                {
                    var newRegs = new List<ASTRegisterRef>();
                    int totalRegTargets = 0;
                    foreach (var t in multi.Targets)
                    {
                        if (t is ASTRegisterRef r)
                        {
                            totalRegTargets++;
                            if (!declared.Contains(r.Register))
                                newRegs.Add(r);
                        }
                    }

                    if (newRegs.Count > 0 && newRegs.Count == totalRegTargets)
                    {
                        foreach (var r in newRegs) declared.Add(r.Register);
                        multi.IsLocal = true;
                    }
                    else if (newRegs.Count > 0)
                    {
                        foreach (var r in newRegs)
                            declared.Add(r.Register);
                    }

                    foreach (var v in multi.Values)
                        if (v is ASTFunction f)
                            MarkFunction(f);
                    break;
                }

            case ASTIf ifs:
                var thenDeclared = new HashSet<int>(declared);
                MarkBlock(ifs.Then, thenDeclared);
                if (ifs.Else != null)
                {
                    var elseDeclared = new HashSet<int>(declared);
                    MarkBlock(ifs.Else, elseDeclared);
                    foreach (var r in thenDeclared)
                        if (elseDeclared.Contains(r))
                            declared.Add(r);
                }
                else
                {
                    declared.UnionWith(thenDeclared);
                }
                break;

            case ASTWhile w:
                var whileDeclared = new HashSet<int>(declared);
                MarkBlock(w.Body, whileDeclared);
                declared.UnionWith(whileDeclared);
                break;

            case ASTRepeatUntil r:
                var repeatDeclared = new HashSet<int>(declared);
                MarkBlock(r.Body, repeatDeclared);
                declared.UnionWith(repeatDeclared);
                break;

            case ASTNumericFor nf:
                var nfDeclared = new HashSet<int>(declared);
                MarkBlock(nf.Body, nfDeclared);
                declared.UnionWith(nfDeclared);
                break;

            case ASTGenericFor gf:
                var gfDeclared = new HashSet<int>(declared);
                MarkBlock(gf.Body, gfDeclared);
                declared.UnionWith(gfDeclared);
                break;

            case ASTDoBlock db:
                MarkBlock(db.Body, declared);
                break;

            case ASTReturn ret:
                foreach (var v in ret.Values)
                    if (v is ASTFunction f)
                        MarkFunction(f);
                break;

            case ASTExpressionStatement es:
                WalkForFunctions(es.Expression);
                break;

            case ASTBlock blk:
                MarkBlock(blk, declared);
                break;
        }
    }

    private static void MarkFunction(ASTFunction func)
    {
        var innerDeclared = new HashSet<int>();
        for (int i = 0; i < func.Parameters.Count; i++)
            innerDeclared.Add(i);
        MarkBlock(func.Body, innerDeclared);
    }

    private static void WalkForFunctions(ASTExpression expr)
    {
        switch (expr)
        {
            case ASTFunction f:
                MarkFunction(f);
                break;
            case ASTCall call:
                WalkForFunctions(call.Function);
                foreach (var arg in call.Arguments)
                    WalkForFunctions(arg);
                break;
        }
    }
}
