using LuraphDeobfuscator.Deobfuscator.AST;
using LuraphDeobfuscator.Deobfuscator.VM;

namespace LuraphDeobfuscator.Deobfuscator.Structuring;
public static class ProtoLinker
{
    public static void Link(Dictionary<int, ASTBlock> allASTs, List<LuraphProto> prototypes)
    {
        foreach (var (_, ast) in allASTs)
        {
            LinkBlock(ast, allASTs, prototypes);
        }
    }
    public static ASTBlock BuildFullOutput(int entryIndex, Dictionary<int, ASTBlock> allASTs, List<LuraphProto> prototypes)
    {
        var referenced = new HashSet<int>();
        foreach (var (_, ast) in allASTs)
            CollectClosureRefs(ast, referenced);
        Link(allASTs, prototypes);
        var output = new ASTBlock();

        if (allASTs.TryGetValue(entryIndex, out var entryAST))
        {
            foreach (var stmt in entryAST.Statements)
                output.Statements.Add(stmt);
        }
        for (int i = 0; i < prototypes.Count; i++)
        {
            if (i == entryIndex) continue;
            if (referenced.Contains(i)) continue;
            if (!allASTs.TryGetValue(i, out var protoAST)) continue;
            if (protoAST.Statements.Count == 0) continue;
            if (IsTrivialPrototype(protoAST)) continue;
            var proto = prototypes[i];
            var funcNode = MakeFunction(proto, protoAST);

            output.Statements.Add(new ASTAssign
            {
                IsLocal = true,
                Target = new ASTGlobalRef { Name = $"proto_{i}" },
                Value = funcNode
            });
        }

        return output;
    }

    private static bool IsTrivialPrototype(ASTBlock block)
    {
        if (block.Statements.Count == 0) return true;
        if (block.Statements.Count == 1)
        {
            if (block.Statements[0] is ASTReturn ret && ret.Values.Count == 0)
                return true;
        }
        return false;
    }

    private static void LinkBlock(ASTBlock block, Dictionary<int, ASTBlock> allASTs, List<LuraphProto> prototypes)
    {
        for (int i = 0; i < block.Statements.Count; i++)
        {
            var stmt = block.Statements[i];

            switch (stmt)
            {
                case ASTAssign assign:
                    assign.Value = (ASTExpression)TryReplaceClosure(assign.Value, allASTs, prototypes);
                    LinkInNode(assign.Value, allASTs, prototypes);
                    break;

                case ASTMultiAssign multi:
                    for (int j = 0; j < multi.Values.Count; j++)
                    {
                        multi.Values[j] = (ASTExpression)TryReplaceClosure(multi.Values[j], allASTs, prototypes);
                        LinkInNode(multi.Values[j], allASTs, prototypes);
                    }
                    break;

                case ASTIf ifStmt:
                    LinkBlock(ifStmt.Then, allASTs, prototypes);
                    if (ifStmt.Else != null)
                        LinkBlock(ifStmt.Else, allASTs, prototypes);
                    break;

                case ASTWhile whileStmt:
                    LinkBlock(whileStmt.Body, allASTs, prototypes);
                    break;

                case ASTRepeatUntil repeat:
                    LinkBlock(repeat.Body, allASTs, prototypes);
                    break;

                case ASTNumericFor numFor:
                    LinkBlock(numFor.Body, allASTs, prototypes);
                    break;

                case ASTGenericFor genFor:
                    LinkBlock(genFor.Body, allASTs, prototypes);
                    break;

                case ASTDoBlock doBlock:
                    LinkBlock(doBlock.Body, allASTs, prototypes);
                    break;

                case ASTBlock nested:
                    LinkBlock(nested, allASTs, prototypes);
                    break;

                case ASTReturn ret:
                    for (int j = 0; j < ret.Values.Count; j++)
                    {
                        ret.Values[j] = (ASTExpression)TryReplaceClosure(ret.Values[j], allASTs, prototypes);
                        LinkInNode(ret.Values[j], allASTs, prototypes);
                    }
                    break;

                case ASTExpressionStatement exprStmt:
                    exprStmt.Expression = (ASTExpression)TryReplaceClosure(exprStmt.Expression, allASTs, prototypes);
                    LinkInNode(exprStmt.Expression, allASTs, prototypes);
                    break;
            }
        }
    }

    private static void LinkInNode(ASTNode node, Dictionary<int, ASTBlock> allASTs, List<LuraphProto> prototypes)
    {
        switch (node)
        {
            case ASTCall call:
                call.Function = (ASTExpression)TryReplaceClosure(call.Function, allASTs, prototypes);
                for (int i = 0; i < call.Arguments.Count; i++)
                    call.Arguments[i] = (ASTExpression)TryReplaceClosure(call.Arguments[i], allASTs, prototypes);
                break;

            case ASTBinaryOp bin:
                bin.Left = (ASTExpression)TryReplaceClosure(bin.Left, allASTs, prototypes);
                bin.Right = (ASTExpression)TryReplaceClosure(bin.Right, allASTs, prototypes);
                break;

            case ASTUnaryOp un:
                un.Operand = (ASTExpression)TryReplaceClosure(un.Operand, allASTs, prototypes);
                break;

            case ASTNot not:
                not.Operand = (ASTExpression)TryReplaceClosure(not.Operand, allASTs, prototypes);
                break;

            case ASTIndex idx:
                idx.Table = (ASTExpression)TryReplaceClosure(idx.Table, allASTs, prototypes);
                idx.Key = (ASTExpression)TryReplaceClosure(idx.Key, allASTs, prototypes);
                break;

            case ASTFunction func:
                LinkBlock(func.Body, allASTs, prototypes);
                break;
            case ASTTableConstructor tc:
                for (int i = 0; i < tc.Fields.Count; i++)
                {
                    var field = tc.Fields[i];
                    if (field.Key != null)
                    {
                        field.Key = (ASTExpression)TryReplaceClosure(field.Key, allASTs, prototypes);
                        LinkInNode(field.Key, allASTs, prototypes);
                    }
                    field.Value = (ASTExpression)TryReplaceClosure(field.Value, allASTs, prototypes);
                    LinkInNode(field.Value, allASTs, prototypes);
                }
                break;

            case ASTConcat cc:
                for (int i = 0; i < cc.Parts.Count; i++)
                {
                    cc.Parts[i] = (ASTExpression)TryReplaceClosure(cc.Parts[i], allASTs, prototypes);
                    LinkInNode(cc.Parts[i], allASTs, prototypes);
                }
                break;
        }
    }
    private static ASTNode TryReplaceClosure(ASTNode node, Dictionary<int, ASTBlock> allASTs, List<LuraphProto> prototypes)
    {
        if (node is ASTLiteral lit && TryParseClosureRef(lit.Value, out int protoIdx))
        {
            if (allASTs.TryGetValue(protoIdx, out var body) &&
                protoIdx < prototypes.Count)
            {
                var proto = prototypes[protoIdx];
                return MakeFunction(proto, body);
            }
        }
        return node;
    }

    private static bool TryParseClosureRef(string value, out int index)
    {
        index = 0;
        if (value.StartsWith("<closure:") && value.EndsWith(">"))
        {
            return int.TryParse(value.AsSpan(9, value.Length - 10), out index);
        }
        if (value.StartsWith("<proto:") && value.EndsWith(">"))
        {
            return int.TryParse(value.AsSpan(7, value.Length - 8), out index);
        }
        return false;
    }

    private static ASTFunction MakeFunction(LuraphProto proto, ASTBlock body)
    {
        var parameters = new List<string>();
        for (int i = 0; i < proto.NumParams; i++)
            parameters.Add($"L_ARG_{i}");

        return new ASTFunction
        {
            Parameters = parameters,
            IsVararg = ContainsVararg(body),
            Body = body
        };
    }
    private static bool ContainsVararg(ASTNode node)
    {
        switch (node)
        {
            case ASTVararg:
                return true;
            case ASTBlock block:
                foreach (var stmt in block.Statements)
                    if (ContainsVararg(stmt)) return true;
                return false;
            case ASTAssign a:
                return ContainsVararg(a.Target) || ContainsVararg(a.Value);
            case ASTMultiAssign m:
                foreach (var t in m.Targets) if (ContainsVararg(t)) return true;
                foreach (var v in m.Values) if (ContainsVararg(v)) return true;
                return false;
            case ASTIf i:
                return ContainsVararg(i.Condition) || ContainsVararg(i.Then)
                    || (i.Else != null && ContainsVararg(i.Else));
            case ASTWhile w:
                return ContainsVararg(w.Condition) || ContainsVararg(w.Body);
            case ASTRepeatUntil r:
                return ContainsVararg(r.Condition) || ContainsVararg(r.Body);
            case ASTNumericFor nf:
                return ContainsVararg(nf.Init) || ContainsVararg(nf.Limit)
                    || ContainsVararg(nf.Step) || ContainsVararg(nf.Body);
            case ASTGenericFor gf:
                return ContainsVararg(gf.Iterator) || ContainsVararg(gf.State)
                    || ContainsVararg(gf.Control) || ContainsVararg(gf.Body);
            case ASTDoBlock db:
                return ContainsVararg(db.Body);
            case ASTReturn ret:
                foreach (var v in ret.Values) if (ContainsVararg(v)) return true;
                return false;
            case ASTExpressionStatement es:
                return ContainsVararg(es.Expression);
            case ASTCall call:
                if (ContainsVararg(call.Function)) return true;
                foreach (var a in call.Arguments) if (ContainsVararg(a)) return true;
                return false;
            case ASTBinaryOp bin:
                return ContainsVararg(bin.Left) || ContainsVararg(bin.Right);
            case ASTUnaryOp un:
                return ContainsVararg(un.Operand);
            case ASTNot not:
                return ContainsVararg(not.Operand);
            case ASTIndex idx:
                return ContainsVararg(idx.Table) || ContainsVararg(idx.Key);
            case ASTConcat cc:
                foreach (var p in cc.Parts) if (ContainsVararg(p)) return true;
                return false;
            case ASTTableConstructor tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null && ContainsVararg(f.Key)) return true;
                    if (ContainsVararg(f.Value)) return true;
                }
                return false;
            case ASTFunction func:
                return false;
            default:
                return false;
        }
    }

    private static void CollectClosureRefs(ASTNode node, HashSet<int> refs)
    {
        switch (node)
        {
            case ASTBlock block:
                foreach (var stmt in block.Statements)
                    CollectClosureRefs(stmt, refs);
                break;

            case ASTAssign assign:
                CollectClosureRefs(assign.Value, refs);
                break;

            case ASTMultiAssign multi:
                foreach (var v in multi.Values)
                    CollectClosureRefs(v, refs);
                break;

            case ASTIf ifStmt:
                CollectClosureRefs(ifStmt.Then, refs);
                if (ifStmt.Else != null)
                    CollectClosureRefs(ifStmt.Else, refs);
                break;

            case ASTWhile w:
                CollectClosureRefs(w.Body, refs);
                break;

            case ASTRepeatUntil r:
                CollectClosureRefs(r.Body, refs);
                break;

            case ASTNumericFor nf:
                CollectClosureRefs(nf.Body, refs);
                break;

            case ASTGenericFor gf:
                CollectClosureRefs(gf.Body, refs);
                break;

            case ASTDoBlock db:
                CollectClosureRefs(db.Body, refs);
                break;

            case ASTReturn ret:
                foreach (var v in ret.Values)
                    CollectClosureRefs(v, refs);
                break;

            case ASTExpressionStatement es:
                CollectClosureRefs(es.Expression, refs);
                break;

            case ASTFunction func:
                CollectClosureRefs(func.Body, refs);
                break;

            case ASTLiteral lit:
                if (TryParseClosureRef(lit.Value, out int idx))
                    refs.Add(idx);
                break;

            case ASTCall call:
                CollectClosureRefs(call.Function, refs);
                foreach (var arg in call.Arguments)
                    CollectClosureRefs(arg, refs);
                break;

            case ASTBinaryOp bin:
                CollectClosureRefs(bin.Left, refs);
                CollectClosureRefs(bin.Right, refs);
                break;

            case ASTUnaryOp un:
                CollectClosureRefs(un.Operand, refs);
                break;

            case ASTNot not:
                CollectClosureRefs(not.Operand, refs);
                break;

            case ASTIndex idxNode:
                CollectClosureRefs(idxNode.Table, refs);
                CollectClosureRefs(idxNode.Key, refs);
                break;
            case ASTTableConstructor tc:
                foreach (var field in tc.Fields)
                {
                    if (field.Key != null)
                        CollectClosureRefs(field.Key, refs);
                    CollectClosureRefs(field.Value, refs);
                }
                break;

            case ASTConcat cc:
                foreach (var part in cc.Parts)
                    CollectClosureRefs(part, refs);
                break;
        }
    }
}
