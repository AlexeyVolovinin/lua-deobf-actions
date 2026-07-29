using LuraphDeobfuscator.Deobfuscator.AST;

namespace LuraphDeobfuscator.Deobfuscator.Passes;
public static class ExpressionFolder
{
    public static void Fold(ASTBlock root) => FoldBlock(root, isTopLevel: true, stripDispatch: true);

    private static void FoldBlock(ASTBlock block, bool isTopLevel, bool stripDispatch = false)
    {
        FlattenBlocks(block);
        if (stripDispatch)
            StripDispatchCalls(block);
        RemoveDeadNilAssignments(block);
        var map = new Dictionary<int, ASTExpression>();
        var substituted = new HashSet<int>();
        for (int i = 0; i < block.Statements.Count; i++)
        {
            SubstituteInStatement(block.Statements[i], map, substituted);
            TrackDefinition(block.Statements[i], map);
        }
        FoldMethodCalls(block);
        RemoveDeadDefinitions(block, isTopLevel, substituted);
        foreach (var stmt in block.Statements)
            RecurseNested(stmt, stripDispatch);
        UnwrapWhileTrueBreak(block);
        StripAntiTamperDeadCode(block);
    }
    private static void StripDispatchCalls(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            var stmt = block.Statements[i];
            if (stmt is ASTAssign { Target: ASTRegisterRef { Register: 0 }, Value: ASTCall { Function: ASTRegisterRef { Register: 0 } } })
            {
                block.Statements.RemoveAt(i);
                continue;
            }
            if (stmt is ASTExpressionStatement { Expression: ASTCall { Function: ASTRegisterRef { Register: 0 } } })
            {
                block.Statements.RemoveAt(i);
                continue;
            }
        }
    }
    private static void RemoveDeadNilAssignments(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is not ASTAssign
                { Target: ASTRegisterRef { Register: var reg }, Value: ASTLiteral { Value: "nil" } })
                continue;
            bool canRemove = false;
            for (int j = i + 1; j < block.Statements.Count; j++)
            {
                var next = block.Statements[j];
                if (next is ASTAssign { Target: ASTRegisterRef { Register: var nextReg } } nextAssign
                    && nextReg == reg)
                {
                    if (nextAssign.Value is ASTIndex { Table: ASTRegisterRef { Register: var tblReg } }
                        && tblReg == reg)
                    {
                        canRemove = true;
                    }
                    else if (!ContainsRegRead(nextAssign.Value, reg))
                    {
                        canRemove = true;
                    }
                    break;
                }
                if (ContainsRegisterRead(next, reg))
                    break;
                if (next is ASTIf or ASTWhile or ASTRepeatUntil or ASTNumericFor
                    or ASTGenericFor or ASTDoBlock or ASTReturn)
                    break;
            }

            if (canRemove)
                block.Statements.RemoveAt(i);
        }
    }
    private static void FoldMethodCalls(ASTBlock block)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case ASTAssign assign:
                    if (assign.Value is ASTCall call1) TryFoldMethodCall(call1);
                    break;
                case ASTMultiAssign multi:
                    foreach (var v in multi.Values)
                        if (v is ASTCall mc) TryFoldMethodCall(mc);
                    break;
                case ASTExpressionStatement es:
                    if (es.Expression is ASTCall call2) TryFoldMethodCall(call2);
                    break;
                case ASTReturn ret:
                    foreach (var v in ret.Values)
                        if (v is ASTCall call3) TryFoldMethodCall(call3);
                    break;
            }
        }
    }

    private static void TryFoldMethodCall(ASTCall call)
    {
        if (call.IsMethodCall) return;
        if (call.Function is not ASTIndex { IsDot: true, Key: ASTLiteral keyLit } idx) return;
        if (call.Arguments.Count < 1) return;
        if (!AreStructurallyEqual(idx.Table, call.Arguments[0])) return;
        string methodName = keyLit.Value;
        if (methodName.StartsWith('"') && methodName.EndsWith('"'))
            methodName = methodName[1..^1];

        call.IsMethodCall = true;
        call.MethodName = methodName;
        call.Function = idx.Table;
        call.Arguments.RemoveAt(0);
    }
    private static bool AreStructurallyEqual(ASTExpression a, ASTExpression b)
    {
        return (a, b) switch
        {
            (ASTRegisterRef ra, ASTRegisterRef rb) => ra.Register == rb.Register,
            (ASTUpvalueRef ua, ASTUpvalueRef ub) => ua.Index == ub.Index,
            (ASTGlobalRef ga, ASTGlobalRef gb) => ga.Name == gb.Name,
            (ASTLiteral la, ASTLiteral lb) => la.Value == lb.Value,
            (ASTNot na, ASTNot nb) => AreStructurallyEqual(na.Operand, nb.Operand),
            (ASTUnaryOp uoa, ASTUnaryOp uob) => uoa.Operator == uob.Operator
                && AreStructurallyEqual(uoa.Operand, uob.Operand),
            (ASTBinaryOp ba, ASTBinaryOp bb) => ba.Operator == bb.Operator
                && AreStructurallyEqual(ba.Left, bb.Left)
                && AreStructurallyEqual(ba.Right, bb.Right),
            (ASTIndex ia, ASTIndex ib) => ia.IsDot == ib.IsDot &&
                                          AreStructurallyEqual(ia.Table, ib.Table) &&
                                          AreStructurallyEqual(ia.Key, ib.Key),
            _ => false
        };
    }

    private static void FlattenBlocks(ASTBlock block)
    {
        bool hasBlocks = false;
        foreach (var s in block.Statements)
            if (s is ASTBlock) { hasBlocks = true; break; }
        if (!hasBlocks) return;

        var newStmts = new List<ASTNode>(block.Statements.Count);
        foreach (var s in block.Statements)
        {
            if (s is ASTBlock inner)
                newStmts.AddRange(inner.Statements);
            else
                newStmts.Add(s);
        }
        block.Statements = newStmts;
    }

    private static void TrackDefinition(ASTNode stmt, Dictionary<int, ASTExpression> map)
    {
        switch (stmt)
        {
            case ASTAssign { Target: ASTRegisterRef reg } assign:
                if (IsFoldable(assign.Value) && !ContainsRegRead(assign.Value, reg.Register))
                    map[reg.Register] = assign.Value;
                else
                    map.Remove(reg.Register);
                break;

            case ASTMultiAssign multi:
                foreach (var t in multi.Targets)
                    if (t is ASTRegisterRef r)
                        map.Remove(r.Register);
                break;
            case ASTIf:
            case ASTWhile:
            case ASTRepeatUntil:
            case ASTNumericFor:
            case ASTGenericFor:
            case ASTDoBlock:
                map.Clear();
                break;
        }
    }
    private static void RemoveDeadDefinitions(ASTBlock block, bool isTopLevel, HashSet<int> substituted)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is not ASTAssign { Target: ASTRegisterRef reg } assign)
                continue;
            if (!IsFoldable(assign.Value))
                continue;
            bool isRead = false;
            bool isRedefined = false;
            for (int j = i + 1; j < block.Statements.Count; j++)
            {
                if (ContainsRegisterRead(block.Statements[j], reg.Register))
                {
                    isRead = true;
                    break;
                }
                if (IsRedefinition(block.Statements[j], reg.Register))
                {
                    isRedefined = true;
                    break;
                }
            }
            if (!isRead && (substituted.Contains(reg.Register) || isRedefined || isTopLevel))
                block.Statements.RemoveAt(i);
        }
    }

    private static void RecurseNested(ASTNode stmt, bool stripDispatch)
    {
        switch (stmt)
        {
            case ASTIf ifs:
                FoldBlock(ifs.Then, isTopLevel: false, stripDispatch: stripDispatch);
                if (ifs.Else != null) FoldBlock(ifs.Else, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTWhile w:
                FoldBlock(w.Body, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTRepeatUntil r:
                FoldBlock(r.Body, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTNumericFor nf:
                FoldBlock(nf.Body, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTGenericFor gf:
                FoldBlock(gf.Body, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTDoBlock db:
                FoldBlock(db.Body, isTopLevel: false, stripDispatch: stripDispatch);
                break;
            case ASTAssign { Value: ASTFunction func }:
                FoldBlock(func.Body, isTopLevel: true, stripDispatch: true);
                break;
            case ASTMultiAssign multi:
                foreach (var v in multi.Values)
                    if (v is ASTFunction f)
                        FoldBlock(f.Body, isTopLevel: true, stripDispatch: false);
                break;
            case ASTReturn ret:
                foreach (var v in ret.Values)
                    if (v is ASTFunction f)
                        FoldBlock(f.Body, isTopLevel: true, stripDispatch: false);
                break;
        }
    }

    private static void SubstituteInStatement(ASTNode stmt, Dictionary<int, ASTExpression> map, HashSet<int>? substituted = null)
    {
        if (map.Count == 0) return;

        switch (stmt)
        {
            case ASTAssign assign:
                assign.Value = SubstExpr(assign.Value, map, substituted);
                if (assign.Target is ASTIndex idx)
                {
                    idx.Table = SubstExpr(idx.Table, map, substituted);
                    idx.Key = SubstExpr(idx.Key, map, substituted);
                }
                break;

            case ASTMultiAssign multi:
                for (int i = 0; i < multi.Values.Count; i++)
                    multi.Values[i] = SubstExpr(multi.Values[i], map, substituted);
                break;

            case ASTExpressionStatement es:
                es.Expression = SubstExpr(es.Expression, map, substituted);
                break;

            case ASTReturn ret:
                for (int i = 0; i < ret.Values.Count; i++)
                    ret.Values[i] = SubstExpr(ret.Values[i], map, substituted);
                break;

            case ASTIf ifs:
                ifs.Condition = SubstExpr(ifs.Condition, map, substituted);
                break;

            case ASTWhile w:
                w.Condition = SubstExpr(w.Condition, map, substituted);
                break;

            case ASTRepeatUntil r:
                r.Condition = SubstExpr(r.Condition, map, substituted);
                break;

            case ASTNumericFor nf:
                nf.Init = SubstExpr(nf.Init, map, substituted);
                nf.Limit = SubstExpr(nf.Limit, map, substituted);
                nf.Step = SubstExpr(nf.Step, map, substituted);
                break;

            case ASTGenericFor gf:
                gf.Iterator = SubstExpr(gf.Iterator, map, substituted);
                gf.State = SubstExpr(gf.State, map, substituted);
                gf.Control = SubstExpr(gf.Control, map, substituted);
                break;
        }
    }

    private static ASTExpression SubstExpr(ASTExpression expr, Dictionary<int, ASTExpression> map, HashSet<int>? substituted = null)
    {
        switch (expr)
        {
            case ASTRegisterRef reg when map.TryGetValue(reg.Register, out var sub):
                substituted?.Add(reg.Register);
                return CloneExpr(sub);

            case ASTBinaryOp bin:
                bin.Left = SubstExpr(bin.Left, map, substituted);
                bin.Right = SubstExpr(bin.Right, map, substituted);
                return bin;

            case ASTUnaryOp un:
                un.Operand = SubstExpr(un.Operand, map, substituted);
                return un;

            case ASTNot not:
                not.Operand = SubstExpr(not.Operand, map, substituted);
                return not;

            case ASTCall call:
                call.Function = SubstExpr(call.Function, map, substituted);
                for (int i = 0; i < call.Arguments.Count; i++)
                    call.Arguments[i] = SubstExpr(call.Arguments[i], map, substituted);
                return call;

            case ASTIndex idx:
                idx.Table = SubstExpr(idx.Table, map, substituted);
                idx.Key = SubstExpr(idx.Key, map, substituted);
                return idx;

            case ASTConcat cat:
                for (int i = 0; i < cat.Parts.Count; i++)
                    cat.Parts[i] = SubstExpr(cat.Parts[i], map, substituted);
                return cat;

            default:
                return expr;
        }
    }
    private static bool IsFoldable(ASTExpression expr) => expr switch
    {
        ASTLiteral lit => lit.Value != "nil",
        ASTGlobalRef => true,
        ASTUpvalueRef => true,
        ASTRegisterRef => true,
        ASTIndex idx => IsFoldable(idx.Table) && IsFoldable(idx.Key),
        _ => false
    };

    private static bool ContainsRegisterRead(ASTNode node, int reg)
    {
        switch (node)
        {
            case ASTAssign assign:
                if (ContainsRegRead(assign.Value, reg)) return true;
                if (assign.Target is ASTIndex idx)
                    return ContainsRegRead(idx.Table, reg) || ContainsRegRead(idx.Key, reg);
                return false;

            case ASTMultiAssign multi:
                return multi.Values.Any(v => ContainsRegRead(v, reg));

            case ASTExpressionStatement es:
                return ContainsRegRead(es.Expression, reg);

            case ASTReturn ret:
                return ret.Values.Any(v => ContainsRegRead(v, reg));

            case ASTIf ifs:
                return ContainsRegRead(ifs.Condition, reg) ||
                       ContainsRegReadInBlock(ifs.Then, reg) ||
                       (ifs.Else != null && ContainsRegReadInBlock(ifs.Else, reg));

            case ASTWhile w:
                return ContainsRegRead(w.Condition, reg) || ContainsRegReadInBlock(w.Body, reg);

            case ASTRepeatUntil r:
                return ContainsRegRead(r.Condition, reg) || ContainsRegReadInBlock(r.Body, reg);

            case ASTNumericFor nf:
                return ContainsRegRead(nf.Init, reg) || ContainsRegRead(nf.Limit, reg) ||
                       ContainsRegRead(nf.Step, reg) || ContainsRegReadInBlock(nf.Body, reg);

            case ASTGenericFor gf:
                return ContainsRegRead(gf.Iterator, reg) || ContainsRegRead(gf.State, reg) ||
                       ContainsRegRead(gf.Control, reg) || ContainsRegReadInBlock(gf.Body, reg);

            case ASTDoBlock db:
                return ContainsRegReadInBlock(db.Body, reg);

            case ASTBlock blk:
                return ContainsRegReadInBlock(blk, reg);

            default:
                return false;
        }
    }

    private static bool ContainsRegReadInBlock(ASTBlock block, int reg)
    {
        foreach (var stmt in block.Statements)
            if (ContainsRegisterRead(stmt, reg))
                return true;
        return false;
    }

    private static bool ContainsRegRead(ASTExpression expr, int reg)
    {
        switch (expr)
        {
            case ASTRegisterRef r:
                return r.Register == reg;
            case ASTBinaryOp bin:
                return ContainsRegRead(bin.Left, reg) || ContainsRegRead(bin.Right, reg);
            case ASTUnaryOp un:
                return ContainsRegRead(un.Operand, reg);
            case ASTNot not:
                return ContainsRegRead(not.Operand, reg);
            case ASTCall call:
                return ContainsRegRead(call.Function, reg) ||
                       call.Arguments.Any(a => ContainsRegRead(a, reg));
            case ASTIndex idx:
                return ContainsRegRead(idx.Table, reg) || ContainsRegRead(idx.Key, reg);
            case ASTConcat cat:
                return cat.Parts.Any(p => ContainsRegRead(p, reg));
            case ASTTableConstructor tc:
                return tc.Fields.Any(f =>
                    (f.Key != null && ContainsRegRead(f.Key, reg)) ||
                    ContainsRegRead(f.Value, reg));
            case ASTFunction:
                return false;
            default:
                return false;
        }
    }

    private static bool IsRedefinition(ASTNode stmt, int reg)
    {
        if (stmt is ASTAssign { Target: ASTRegisterRef r } && r.Register == reg)
            return true;
        if (stmt is ASTMultiAssign multi)
            return multi.Targets.Any(t => t is ASTRegisterRef r && r.Register == reg);
        return false;
    }

    private static ASTExpression CloneExpr(ASTExpression expr) => expr switch
    {
        ASTRegisterRef reg => new ASTRegisterRef(reg.Register) { Name = reg.Name },
        ASTUpvalueRef up => new ASTUpvalueRef(up.Index) { Name = up.Name },
        ASTLiteral lit => new ASTLiteral(lit.Value),
        ASTGlobalRef gr => new ASTGlobalRef { Name = gr.Name },
        ASTBinaryOp bin => new ASTBinaryOp
        {
            Left = CloneExpr(bin.Left),
            Operator = bin.Operator,
            Right = CloneExpr(bin.Right)
        },
        ASTUnaryOp un => new ASTUnaryOp
        {
            Operator = un.Operator,
            Operand = CloneExpr(un.Operand)
        },
        ASTNot not => new ASTNot { Operand = CloneExpr(not.Operand) },
        ASTIndex idx => new ASTIndex
        {
            Table = CloneExpr(idx.Table),
            Key = CloneExpr(idx.Key),
            IsDot = idx.IsDot
        },
        ASTCall call => new ASTCall
        {
            Function = CloneExpr(call.Function),
            Arguments = call.Arguments.Select(CloneExpr).ToList(),
            ReturnCount = call.ReturnCount,
            IsMethodCall = call.IsMethodCall,
            MethodName = call.MethodName
        },
        ASTConcat cat => new ASTConcat
        {
            Parts = cat.Parts.Select(CloneExpr).ToList()
        },
        ASTVararg => new ASTVararg(),
        _ => expr
    };
    private static void UnwrapWhileTrueBreak(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTWhile w
                && w.Condition is ASTLiteral { Value: "true" }
                && w.Body.Statements.Count > 0
                && w.Body.Statements[^1] is ASTBreak)
            {
                bool hasNestedBreak = false;
                for (int j = 0; j < w.Body.Statements.Count - 1; j++)
                {
                    if (ContainsBreak(w.Body.Statements[j]))
                    {
                        hasNestedBreak = true;
                        break;
                    }
                }
                if (hasNestedBreak) continue;
                block.Statements.RemoveAt(i);
                var bodyStmts = w.Body.Statements.Take(w.Body.Statements.Count - 1).ToList();
                block.Statements.InsertRange(i, bodyStmts);
            }
        }
    }

    private static bool ContainsBreak(ASTNode node) => node switch
    {
        ASTBreak => true,
        ASTIf ifn => ContainsBreakInBlock(ifn.Then)
                   || (ifn.Else != null && ContainsBreakInBlock(ifn.Else)),
        ASTWhile or ASTRepeatUntil or ASTNumericFor or ASTGenericFor => false,
        ASTDoBlock db => ContainsBreakInBlock(db.Body),
        _ => false
    };

    private static bool ContainsBreakInBlock(ASTBlock block) =>
        block.Statements.Any(ContainsBreak);
    private static bool StripEmptyIfBlocks(ASTBlock block)
    {
        bool modified = false;
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            var stmt = block.Statements[i];
            if (stmt is ASTIf ifn
                && ifn.Then.Statements.Count == 0
                && ifn.Else == null)
            {
                if (IsSideEffectFree(ifn.Condition))
                {
                    block.Statements.RemoveAt(i);
                    modified = true;
                }
                continue;
            }
            if (stmt is ASTIf ifn2 && ifn2.Else == null
                && IsSideEffectFree(ifn2.Condition)
                && IsBlockAllDeadCode(ifn2.Then))
            {
                block.Statements.RemoveAt(i);
                modified = true;
                continue;
            }
            if (stmt is ASTIf outerIf
                && outerIf.Condition is ASTNot { Operand: var outerCond })
            {
                modified |= StripContradictoryIfs(outerIf.Then, outerCond);
            }
            if (stmt is ASTIf outerIf2
                && outerIf2.Condition is not ASTNot
                && IsSideEffectFree(outerIf2.Condition))
            {
                modified |= StripContradictoryNotIfs(outerIf2.Then, outerIf2.Condition);
            }
            if (stmt is ASTWhile w
                && w.Body.Statements.Count == 0
                && IsSideEffectFree(w.Condition))
            {
                block.Statements.RemoveAt(i);
                modified = true;
                continue;
            }
            if (stmt is ASTWhile w2
                && IsSideEffectFree(w2.Condition)
                && IsBlockAllDeadCode(w2.Body))
            {
                block.Statements.RemoveAt(i);
                modified = true;
                continue;
            }
            if (stmt is ASTRepeatUntil r
                && r.Body.Statements.Count == 0
                && IsSideEffectFree(r.Condition))
            {
                block.Statements.RemoveAt(i);
                modified = true;
                continue;
            }
        }
        return modified;
    }
    private static bool StripContradictoryIfs(ASTBlock block, ASTExpression falsyCond)
    {
        bool modified = false;
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTIf inner
                && AreStructurallyEqual(inner.Condition, falsyCond))
            {
                modified = true;
                if (inner.Else != null)
                {
                    block.Statements.RemoveAt(i);
                    for (int j = 0; j < inner.Else.Statements.Count; j++)
                        block.Statements.Insert(i + j, inner.Else.Statements[j]);
                }
                else
                {
                    block.Statements.RemoveAt(i);
                }
            }
        }
        return modified;
    }
    private static bool StripContradictoryNotIfs(ASTBlock block, ASTExpression truthyCond)
    {
        bool modified = false;
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTIf inner
                && inner.Condition is ASTNot { Operand: var innerCond }
                && AreStructurallyEqual(innerCond, truthyCond))
            {
                modified = true;
                if (inner.Else != null)
                {
                    block.Statements.RemoveAt(i);
                    for (int j = 0; j < inner.Else.Statements.Count; j++)
                        block.Statements.Insert(i + j, inner.Else.Statements[j]);
                }
                else
                {
                    block.Statements.RemoveAt(i);
                }
            }
        }
        return modified;
    }
    private static bool IsBlockAllDeadCode(ASTBlock block)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case ASTAssign { Value: ASTLiteral { Value: "nil" } }:
                    continue;
                case ASTAssign { Value: ASTLiteral }:
                    continue;
                case ASTAssign { Value: ASTRegisterRef }:
                    continue;
                case ASTAssign { Value: ASTBinaryOp bin } when IsSideEffectFree(bin):
                    continue;
                case ASTAssign { Value: ASTUnaryOp un } when IsSideEffectFree(un):
                    continue;
                case ASTIf ifStmt when IsSideEffectFree(ifStmt.Condition)
                    && IsBlockAllDeadCode(ifStmt.Then)
                    && (ifStmt.Else == null || IsBlockAllDeadCode(ifStmt.Else)):
                    continue;
                case ASTWhile w when IsSideEffectFree(w.Condition) && IsBlockAllDeadCode(w.Body):
                    continue;
                case ASTBreak:
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }
    private static bool IsSideEffectFree(ASTExpression expr) => expr switch
    {
        ASTRegisterRef => true,
        ASTUpvalueRef => true,
        ASTLiteral => true,
        ASTBinaryOp bin => IsSideEffectFree(bin.Left) && IsSideEffectFree(bin.Right),
        ASTUnaryOp un => IsSideEffectFree(un.Operand),
        ASTNot n => IsSideEffectFree(n.Operand),
        ASTIndex idx => IsSideEffectFree(idx.Table) && IsSideEffectFree(idx.Key),
        _ => false
    };
    private static void StripAntiTamperDeadCode(ASTBlock block)
    {
        bool modified;
        do
        {
            modified = false;
            int prevCount = block.Statements.Count;

            StripHighIndexTableWrites(block);
            StripAntiTamperArithmetic(block);
            StripNonZeroDispatchCalls(block);
            StripDeadNilRegisters(block);
            modified |= StripEmptyIfBlocks(block);
            StripDeadLocalNilDeclarations(block);

            modified |= block.Statements.Count != prevCount;
        }
        while (modified);
    }
    private static void StripHighIndexTableWrites(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTAssign { Target: ASTIndex idx }
                && idx.Table is ASTRegisterRef
                && idx.Key is ASTLiteral { Value: var keyStr }
                && int.TryParse(keyStr, out _))
            {
                block.Statements.RemoveAt(i);
            }
        }
    }
    private static void StripAntiTamperArithmetic(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is not ASTAssign { Target: ASTRegisterRef target, Value: var val })
                continue;

            if (val is ASTBinaryOp bin && (bin.Operator is "+" or "-"))
            {
                int reg = target.Register;
                if (bin.Left is ASTRegisterRef lr && lr.Register == reg
                    && bin.Right is ASTLiteral { Value: var rv }
                    && long.TryParse(rv, out var lv) && Math.Abs(lv) >= 100)
                {
                    block.Statements.RemoveAt(i);
                    continue;
                }
                if (bin.Right is ASTRegisterRef rr && rr.Register == reg
                    && bin.Left is ASTLiteral { Value: var lval }
                    && long.TryParse(lval, out var llv) && Math.Abs(llv) >= 100)
                {
                    block.Statements.RemoveAt(i);
                    continue;
                }
                if (bin.Left is ASTLiteral { Value: var litVal }
                    && long.TryParse(litVal, out var litLv) && Math.Abs(litLv) >= 100
                    && bin.Right is ASTRegisterRef
                    && !IsRegUsedAfter(block, i, reg))
                {
                    block.Statements.RemoveAt(i);
                    continue;
                }
                if (bin.Right is ASTLiteral { Value: var rLitVal }
                    && long.TryParse(rLitVal, out var rLitLv) && Math.Abs(rLitLv) >= 100
                    && bin.Left is ASTRegisterRef leftRef && leftRef.Register != reg
                    && !IsRegUsedAfter(block, i, reg))
                {
                    block.Statements.RemoveAt(i);
                    continue;
                }
            }
            if (val is ASTBinaryOp mulBin && mulBin.Operator == "*"
                && mulBin.Left is ASTRegisterRef mulLr && mulLr.Register == target.Register
                && mulBin.Right is ASTLiteral { Value: var mulRv }
                && long.TryParse(mulRv, out var mulLv) && Math.Abs(mulLv) >= 100)
            {
                block.Statements.RemoveAt(i);
                continue;
            }
            if (val is ASTBinaryOp cmpBin
                && cmpBin.Operator is ">=" or "<=" or ">" or "<" or "==" or "~="
                && cmpBin.Left is ASTLiteral && cmpBin.Right is ASTLiteral)
            {
                block.Statements.RemoveAt(i);
                continue;
            }
        }
    }
    private static void StripNonZeroDispatchCalls(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTAssign
                {
                    Target: ASTRegisterRef { Register: var reg }, Value: ASTCall { Function: ASTRegisterRef { Register: var callReg }, Arguments: var args }
                }
                && reg == callReg
                && reg != 0
                && args.Count <= 3
                && args.All(a => a is ASTRegisterRef or ASTVararg)
                && !IsRegUsedAfter(block, i, reg))
            {
                block.Statements.RemoveAt(i);
            }
        }
    }
    private static bool IsRegUsedAfter(ASTBlock block, int afterIdx, int reg)
    {
        for (int j = afterIdx + 1; j < block.Statements.Count; j++)
        {
            var s = block.Statements[j];
            if (s is ASTAssign { Target: ASTRegisterRef tr } && tr.Register == reg)
            {
                return ContainsRegRead(((ASTAssign)s).Value, reg);
            }
            if (ContainsRegisterRead(s, reg))
                return true;
            if (s is ASTIf or ASTWhile or ASTRepeatUntil or ASTNumericFor
                or ASTGenericFor or ASTDoBlock or ASTReturn)
                return true;
        }
        return false;
    }
    private static void StripDeadNilRegisters(ASTBlock block)
    {
        var nilledRegs = new HashSet<int>();
        foreach (var stmt in block.Statements)
        {
            if (stmt is ASTAssign { Target: ASTRegisterRef reg, Value: ASTLiteral { Value: "nil" } })
                nilledRegs.Add(reg.Register);
        }

        if (nilledRegs.Count == 0) return;
        var usedMeaningfully = new HashSet<int>();
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case ASTAssign { Target: ASTRegisterRef reg, Value: ASTLiteral { Value: "nil" } }
                    when nilledRegs.Contains(reg.Register):
                    break;
                default:
                    foreach (int r in CollectReferencedRegisters(stmt))
                        if (nilledRegs.Contains(r))
                            usedMeaningfully.Add(r);
                    break;
            }
        }

        var deadRegs = new HashSet<int>(nilledRegs.Except(usedMeaningfully));
        if (deadRegs.Count == 0) return;

        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTAssign { Target: ASTRegisterRef reg2, Value: ASTLiteral { Value: "nil" } }
                && deadRegs.Contains(reg2.Register))
            {
                block.Statements.RemoveAt(i);
            }
        }
    }
    private static void StripDeadLocalNilDeclarations(ASTBlock block)
    {
        for (int i = block.Statements.Count - 1; i >= 0; i--)
        {
            if (block.Statements[i] is ASTAssign { IsLocal: true, Target: ASTRegisterRef { Register: var reg }, Value: ASTLiteral { Value: "nil" } })
            {
                if (!IsRegUsedAfter(block, i, reg))
                    block.Statements.RemoveAt(i);
            }
        }
    }
    private static IEnumerable<int> CollectReferencedRegisters(ASTNode node)
    {
        switch (node)
        {
            case ASTRegisterRef reg:
                yield return reg.Register;
                break;
            case ASTAssign a:
                foreach (var r in CollectReferencedRegisters(a.Target)) yield return r;
                foreach (var r in CollectReferencedRegisters(a.Value)) yield return r;
                break;
            case ASTExpressionStatement es:
                foreach (var r in CollectReferencedRegisters(es.Expression)) yield return r;
                break;
            case ASTIndex idx:
                foreach (var r in CollectReferencedRegisters(idx.Table)) yield return r;
                foreach (var r in CollectReferencedRegisters(idx.Key)) yield return r;
                break;
            case ASTBinaryOp bin:
                foreach (var r in CollectReferencedRegisters(bin.Left)) yield return r;
                foreach (var r in CollectReferencedRegisters(bin.Right)) yield return r;
                break;
            case ASTUnaryOp un:
                foreach (var r in CollectReferencedRegisters(un.Operand)) yield return r;
                break;
            case ASTCall call:
                foreach (var r in CollectReferencedRegisters(call.Function)) yield return r;
                foreach (var arg in call.Arguments)
                    foreach (var r in CollectReferencedRegisters(arg)) yield return r;
                break;
            case ASTReturn ret:
                foreach (var expr in ret.Values)
                    foreach (var r in CollectReferencedRegisters(expr)) yield return r;
                break;
            case ASTIf ifn:
                foreach (var r in CollectReferencedRegisters(ifn.Condition)) yield return r;
                foreach (var s in ifn.Then.Statements)
                    foreach (var r in CollectReferencedRegisters(s)) yield return r;
                if (ifn.Else != null)
                    foreach (var s in ifn.Else.Statements)
                        foreach (var r in CollectReferencedRegisters(s)) yield return r;
                break;
            case ASTWhile w:
                foreach (var r in CollectReferencedRegisters(w.Condition)) yield return r;
                foreach (var s in w.Body.Statements)
                    foreach (var r in CollectReferencedRegisters(s)) yield return r;
                break;
        }
    }
}