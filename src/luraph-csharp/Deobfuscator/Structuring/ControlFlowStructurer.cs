using LuraphDeobfuscator.Deobfuscator.AST;
using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;

namespace LuraphDeobfuscator.Deobfuscator.Structuring;
public class ControlFlowStructurer
{
    private readonly List<BasicBlock> _blocks;
    private readonly List<NaturalLoop> _loops;
    private readonly Dictionary<int, NaturalLoop> _loopByHeader;
    private readonly Dictionary<int, BasicBlock> _blockByStartIndex;
    private readonly HashSet<int> _emitted = [];

    public ControlFlowStructurer(List<BasicBlock> blocks, List<NaturalLoop> loops)
    {
        _blocks = blocks;
        _loops = loops;
        _loopByHeader = loops.ToDictionary(l => l.Header.Id);
        _blockByStartIndex = new Dictionary<int, BasicBlock>(blocks.Count);
        foreach (var b in blocks)
            _blockByStartIndex[b.StartIndex] = b;
    }
    public ASTBlock Structure()
    {
        _emitted.Clear();
        var ast = new ASTBlock();

        if (_blocks.Count == 0) return ast;

        var entry = _blocks[0];
        if (IsSelfLoop(entry))
        {
            _emitted.Add(entry.Id);
            entry = _blocks.Count > 1 ? _blocks[1] : null!;
        }

        if (entry != null)
            EmitRegion(ast, entry, stopAt: null);
        foreach (var block in _blocks.OrderBy(b => b.Id))
        {
            if (_emitted.Contains(block.Id)) continue;
            if (block.IsUnreachable) { _emitted.Add(block.Id); continue; }
            if (IsSelfLoop(block)) { _emitted.Add(block.Id); continue; }
            EmitRegion(block: block, target: ast, stopAt: null);
        }

        return ast;
    }
    private static bool IsSelfLoop(BasicBlock block)
    {
        return block.Terminator is Jump j
            && j.Target == block.StartIndex
            && block.Instructions.Count == 0;
    }
    private void EmitRegion(ASTBlock target, BasicBlock? block, BasicBlock? stopAt, NaturalLoop? loop = null)
    {
        while (block != null && block != stopAt && !_emitted.Contains(block.Id))
        {
            if (IsSelfLoop(block))
            {
                _emitted.Add(block.Id);
                block = NextLinearBlock(block);
                continue;
            }
            if (loop != null && !loop.Body.Contains(block))
            {
                target.Statements.Add(new ASTBreak());
                return;
            }
            if (_loopByHeader.TryGetValue(block.Id, out var nestedLoop))
            {
                if (loop != null && block == loop.Header)
                    return;

                EmitLoop(target, nestedLoop, stopAt);
                block = FindLoopFollow(nestedLoop);
                continue;
            }
            _emitted.Add(block.Id);
            EmitInstructions(target, block);
            var term = block.Terminator;
            switch (term)
            {
                case null:
                    block = block.Successors.Count > 0 ? block.Successors[0] : null;
                    break;

                case Jump:
                    {
                        var jumpNext = block.Successors.Count > 0
                            ? block.Successors[0] : null;
                        if (jumpNext != null && loop != null
                            && !loop.Body.Contains(jumpNext))
                        {
                            target.Statements.Add(new ASTBreak());
                            return;
                        }
                        block = jumpNext;
                        break;
                    }

                case CondJump or TestJump:
                    EmitConditional(target, block, stopAt, loop);
                    return;

                case Return:
                case TailCall:
                    var retStmt = StatementBuilder.FromInstruction(term);
                    if (retStmt != null) target.Statements.Add(retStmt);
                    return;

                case ForPrep:
                    block = block.Successors.Count > 0 ? block.Successors[0] : null;
                    break;

                case ForLoop fl:
                    {
                        var flBody = ResolveBlock(fl.BodyTarget);
                        if (flBody != null
                            && _loopByHeader.TryGetValue(flBody.Id, out var flLoop))
                        {
                            int baseReg = fl.Base.Reg;
                            var body = new ASTBlock();
                            _emitted.Add(flBody.Id);
                            EmitInstructions(body, flBody);
                            var headerSucc = flBody.Successors
                                .FirstOrDefault(s => flLoop.Body.Contains(s)
                                                  && !_emitted.Contains(s.Id));
                            if (headerSucc != null)
                                EmitRegion(body, headerSucc, stopAt: null, loop: flLoop);

                            target.Statements.Add(new ASTNumericFor
                            {
                                Variable = $"L_{baseReg + 3}",
                                Init = new ASTRegisterRef(baseReg),
                                Limit = new ASTRegisterRef(baseReg + 1),
                                Step = new ASTRegisterRef(baseReg + 2),
                                Body = body
                            });
                            block = ResolveBlock(fl.ExitTarget);
                        }
                        else
                        {
                            block = block.Successors.Count > 0
                                ? block.Successors[^1] : null;
                        }
                        break;
                    }

                case TForLoop tfl:
                    {
                        var tflBody = ResolveBlock(tfl.BodyTarget);
                        if (tflBody != null
                            && _loopByHeader.TryGetValue(tflBody.Id, out var tflLoop))
                        {
                            int baseReg = tfl.Base.Reg;
                            int numVars = tfl.NumVars;
                            if (numVars <= 0)
                                numVars = InferGenericForVarCount(baseReg, tflLoop);
                            var body = new ASTBlock();

                            _emitted.Add(tflBody.Id);
                            EmitInstructions(body, tflBody);

                            var headerSucc = tflBody.Successors
                                .FirstOrDefault(s => tflLoop.Body.Contains(s)
                                                  && !_emitted.Contains(s.Id));
                            if (headerSucc != null)
                                EmitRegion(body, headerSucc, stopAt: null, loop: tflLoop);

                            var vars = new List<string>();
                            for (int i = 0; i < numVars; i++)
                                vars.Add($"L_{baseReg + 3 + i}");

                            target.Statements.Add(new ASTGenericFor
                            {
                                Variables = vars,
                                Iterator = new ASTRegisterRef(baseReg),
                                State = new ASTRegisterRef(baseReg + 1),
                                Control = new ASTRegisterRef(baseReg + 2),
                                Body = body
                            });
                            block = ResolveBlock(tfl.ExitTarget);
                        }
                        else
                        {
                            block = block.Successors.Count > 0
                                ? block.Successors[^1] : null;
                        }
                        break;
                    }

                default:
                    var stmtT = StatementBuilder.FromInstruction(term);
                    if (stmtT != null) target.Statements.Add(stmtT);
                    block = GetFallthrough(block);
                    break;
            }
        }
    }

    private void EmitLoop(ASTBlock target, NaturalLoop loop, BasicBlock? outerStopAt)
    {
        if (loop.Body.Count == 1 && IsSelfLoop(loop.Header))
        {
            _emitted.Add(loop.Header.Id);
            return;
        }

        var kind = loop.Kind;

        switch (kind)
        {
            case LoopKind.NumericFor:
                EmitNumericFor(target, loop);
                break;
            case LoopKind.GenericFor:
                EmitGenericFor(target, loop);
                break;
            case LoopKind.RepeatUntil:
                EmitRepeatUntil(target, loop);
                break;
            default:
                EmitWhileLoop(target, loop);
                break;
        }
    }

    private void EmitWhileLoop(ASTBlock target, NaturalLoop loop)
    {
        var header = loop.Header;
        var term = header.Terminator;
        ASTExpression condition;
        BasicBlock? bodyEntry;

        if (term is CondJump or TestJump)
        {
            var (trueTarget, falseTarget) = GetBranchTargets(term);
            var trueBlock = ResolveBlock(trueTarget);
            var falseBlock = ResolveBlock(falseTarget);

            if (trueBlock != null && loop.Body.Contains(trueBlock))
            {
                condition = ExpressionBuilder.FromCondition(term);
                bodyEntry = trueBlock;
            }
            else if (falseBlock != null && loop.Body.Contains(falseBlock))
            {
                condition = ExpressionBuilder.NegateCondition(term);
                bodyEntry = falseBlock;
            }
            else
            {
                condition = new ASTLiteral("true");
                bodyEntry = header.Successors.FirstOrDefault(s => loop.Body.Contains(s));
            }
        }
        else
        {
            condition = new ASTLiteral("true");
            bodyEntry = header.Successors.FirstOrDefault(s =>
                s != header && loop.Body.Contains(s));
        }
        _emitted.Add(header.Id);
        EmitInstructions(target, header);
        var body = new ASTBlock();
        if (bodyEntry != null)
            EmitRegion(body, bodyEntry, stopAt: null, loop: loop);

        target.Statements.Add(new ASTWhile
        {
            Condition = condition,
            Body = body
        });
    }

    private void EmitRepeatUntil(ASTBlock target, NaturalLoop loop)
    {
        var header = loop.Header;
        var latch = loop.Latch;
        var latchTerm = latch.Terminator;

        _emitted.Add(header.Id);

        var body = new ASTBlock();
        EmitInstructions(body, header);
        var bodyEntry = header.Successors
            .FirstOrDefault(s => loop.Body.Contains(s) && s != latch);
        if (bodyEntry != null)
            EmitRegion(body, bodyEntry, stopAt: latch, loop: loop);
        if (!_emitted.Contains(latch.Id))
        {
            _emitted.Add(latch.Id);
            EmitInstructions(body, latch);
        }
        ASTExpression condition;
        if (latchTerm is CondJump or TestJump)
        {
            var (trueTarget, _) = GetBranchTargets(latchTerm);
            var trueBlock = ResolveBlock(trueTarget);
            if (trueBlock != null && trueBlock == header)
                condition = ExpressionBuilder.NegateCondition(latchTerm);
            else
                condition = ExpressionBuilder.FromCondition(latchTerm);
        }
        else
        {
            condition = new ASTLiteral("false");
        }

        target.Statements.Add(new ASTRepeatUntil
        {
            Condition = condition,
            Body = body
        });
    }

    private void EmitNumericFor(ASTBlock target, NaturalLoop loop)
    {
        var header = loop.Header;
        _emitted.Add(header.Id);
        ForLoop? forLoop = null;
        BasicBlock? forLoopBlock = null;

        if (header.Terminator is ForLoop headerFl)
        {
            forLoop = headerFl;
            forLoopBlock = header;
        }
        else if (loop.Latch.Terminator is ForLoop latchFl)
        {
            forLoop = latchFl;
            forLoopBlock = loop.Latch;
        }
        else
        {
            foreach (var b in loop.Body.OrderBy(b => b.Id))
            {
                if (b.Terminator is ForLoop fl)
                {
                    forLoop = fl;
                    forLoopBlock = b;
                    break;
                }
            }
        }

        if (forLoop == null)
        {
            EmitWhileLoop(target, loop);
            return;
        }

        int baseReg = forLoop.Base.Reg;
        var varName = $"L_{baseReg + 3}";
        var init = new ASTRegisterRef(baseReg);
        var limit = new ASTRegisterRef(baseReg + 1);
        var step = new ASTRegisterRef(baseReg + 2);
        var body = new ASTBlock();
        var bodyEntry = ResolveBlock(forLoop.BodyTarget);

        if (forLoopBlock != null)
            _emitted.Add(forLoopBlock.Id);

        if (bodyEntry != null)
            EmitRegion(body, bodyEntry, stopAt: forLoopBlock, loop: loop);

        target.Statements.Add(new ASTNumericFor
        {
            Variable = varName,
            Init = init,
            Limit = limit,
            Step = step,
            Body = body
        });
    }

    private void EmitGenericFor(ASTBlock target, NaturalLoop loop)
    {
        var header = loop.Header;
        _emitted.Add(header.Id);
        TForLoop? tfor = null;
        BasicBlock? tforBlock = null;

        if (header.Terminator is TForLoop headerTf)
        {
            tfor = headerTf;
            tforBlock = header;
        }
        else if (loop.Latch.Terminator is TForLoop latchTf)
        {
            tfor = latchTf;
            tforBlock = loop.Latch;
        }
        else
        {
            foreach (var b in loop.Body.OrderBy(b => b.Id))
            {
                if (b.Terminator is TForLoop tf)
                {
                    tfor = tf;
                    tforBlock = b;
                    break;
                }
            }
        }

        if (tfor == null)
        {
            EmitWhileLoop(target, loop);
            return;
        }

        int baseReg = tfor.Base.Reg;
        int numVars = tfor.NumVars;
        if (numVars <= 0)
            numVars = InferGenericForVarCount(baseReg, loop);
        var iterator = new ASTRegisterRef(baseReg);
        var state = new ASTRegisterRef(baseReg + 1);
        var control = new ASTRegisterRef(baseReg + 2);
        var vars = new List<string>();
        for (int i = 0; i < numVars; i++)
            vars.Add($"L_{baseReg + 3 + i}");
        var body = new ASTBlock();

        if (tforBlock != null)
            _emitted.Add(tforBlock.Id);

        var bodyEntry = ResolveBlock(tfor.BodyTarget);
        if (bodyEntry != null)
            EmitRegion(body, bodyEntry, stopAt: tforBlock, loop: loop);

        target.Statements.Add(new ASTGenericFor
        {
            Variables = vars,
            Iterator = iterator,
            State = state,
            Control = control,
            Body = body
        });
    }
    private int InferGenericForVarCount(int baseReg, NaturalLoop loop)
    {
        int maxVar = 0;
        int firstVar = baseReg + 3;
        int limit = baseReg + 10;

        foreach (var block in loop.Body)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (int reg in GetReferencedRegisters(instr))
                {
                    if (reg >= firstVar && reg <= limit)
                        maxVar = Math.Max(maxVar, reg - firstVar + 1);
                }
            }
            if (block.Terminator != null)
            {
                foreach (int reg in GetReferencedRegisters(block.Terminator))
                {
                    if (reg >= firstVar && reg <= limit)
                        maxVar = Math.Max(maxVar, reg - firstVar + 1);
                }
            }
        }

        return Math.Max(maxVar, 2);
    }
    private static IEnumerable<int> GetReferencedRegisters(IRInstr instr)
    {
        switch (instr)
        {
            case Move m:
                yield return m.Dst.Reg;
                if (m.Src is RegOp rs) yield return rs.Reg;
                break;
            case GetTable gt:
                yield return gt.Dst.Reg;
                if (gt.Table is RegOp gtb) yield return gtb.Reg;
                if (gt.Key is RegOp gk) yield return gk.Reg;
                break;
            case SetTable st:
                if (st.Table is RegOp stb) yield return stb.Reg;
                if (st.Key is RegOp sk) yield return sk.Reg;
                if (st.Value is RegOp sv) yield return sv.Reg;
                break;
            case Call c:
                yield return c.Func.Reg;
                break;
            case BinOp bo:
                yield return bo.Dst.Reg;
                if (bo.Left is RegOp bl) yield return bl.Reg;
                if (bo.Right is RegOp br) yield return br.Reg;
                break;
            case UnaryOp uo:
                yield return uo.Dst.Reg;
                if (uo.Src is RegOp uop) yield return uop.Reg;
                break;
            case Self s:
                yield return s.Dst.Reg;
                if (s.Object is RegOp sob) yield return sob.Reg;
                if (s.Method is RegOp skey) yield return skey.Reg;
                break;
            case GetGlobal gg:
                yield return gg.Dst.Reg;
                break;
            case SetGlobal sg:
                if (sg.Name is RegOp sgr) yield return sgr.Reg;
                if (sg.Value is RegOp sgv) yield return sgv.Reg;
                break;
            case GetUpval gu:
                yield return gu.Dst.Reg;
                break;
            case SetUpval su:
                if (su.Value is RegOp suv) yield return suv.Reg;
                break;
            case Concat cc:
                yield return cc.Dst.Reg;
                if (cc.Left is RegOp cl) yield return cl.Reg;
                if (cc.Right is RegOp cr) yield return cr.Reg;
                break;
            case NewTable nt:
                yield return nt.Dst.Reg;
                break;
            case MakeClosure mc:
                yield return mc.Dst.Reg;
                break;
            case CondJump cj:
                if (cj.Left is RegOp cjl) yield return cjl.Reg;
                if (cj.Right is RegOp cjr) yield return cjr.Reg;
                break;
            case TestJump tj:
                if (tj.Value is RegOp tjv) yield return tjv.Reg;
                break;
        }
    }
    private void EmitConditional(ASTBlock target, BasicBlock block, BasicBlock? stopAt, NaturalLoop? loop = null)
    {
        var term = block.Terminator!;
        var (trueTarget, falseTarget) = GetBranchTargets(term);

        var trueBlock = ResolveBlock(trueTarget);
        var falseBlock = ResolveBlock(falseTarget);
        if (loop != null)
        {
            bool trueIsBackEdge = trueBlock != null && trueBlock == loop.Header;
            bool falseIsBackEdge = falseBlock != null && falseBlock == loop.Header;
            bool trueExits = !trueIsBackEdge
                && (trueBlock == null || !loop.Body.Contains(trueBlock));
            bool falseExits = !falseIsBackEdge
                && (falseBlock == null || !loop.Body.Contains(falseBlock));
            if (trueExits && falseExits)
            {
                target.Statements.Add(new ASTBreak());
                return;
            }
            if (trueExits)
            {
                var cond = ExpressionBuilder.FromCondition(term);
                target.Statements.Add(new ASTIf
                {
                    Condition = cond,
                    Then = new ASTBlock { Statements = { new ASTBreak() } }
                });
                if (falseBlock != null && !_emitted.Contains(falseBlock.Id)
                    && falseBlock != loop.Header)
                    EmitRegion(target, falseBlock, stopAt, loop);
                return;
            }
            if (falseExits)
            {
                var cond = ExpressionBuilder.NegateCondition(term);
                target.Statements.Add(new ASTIf
                {
                    Condition = cond,
                    Then = new ASTBlock { Statements = { new ASTBreak() } }
                });
                if (trueBlock != null && !_emitted.Contains(trueBlock.Id)
                    && trueBlock != loop.Header)
                    EmitRegion(target, trueBlock, stopAt, loop);
                return;
            }
            if (trueIsBackEdge)
            {
                if (falseBlock != null && !_emitted.Contains(falseBlock.Id))
                {
                    var cond = ExpressionBuilder.NegateCondition(term);
                    var ifBody = new ASTBlock();
                    EmitRegion(ifBody, falseBlock, stopAt, loop);
                    if (ifBody.Statements.Count > 0)
                        target.Statements.Add(new ASTIf
                        { Condition = cond, Then = ifBody });
                }
                return;
            }
            if (falseIsBackEdge)
            {
                if (trueBlock != null && !_emitted.Contains(trueBlock.Id))
                {
                    var cond = ExpressionBuilder.FromCondition(term);
                    var ifBody = new ASTBlock();
                    EmitRegion(ifBody, trueBlock, stopAt, loop);
                    if (ifBody.Statements.Count > 0)
                        target.Statements.Add(new ASTIf
                        { Condition = cond, Then = ifBody });
                }
                return;
            }
        }
        var merge = FindMergeBlock(trueBlock, falseBlock, stopAt);
        if (loop != null && merge != null && !loop.Body.Contains(merge))
            merge = stopAt;

        var condition = ExpressionBuilder.FromCondition(term);

        var thenBody = new ASTBlock();
        if (trueBlock != null && trueBlock != merge)
            EmitRegion(thenBody, trueBlock, merge, loop);

        ASTBlock? elseBody = null;
        if (falseBlock != null && falseBlock != merge && !_emitted.Contains(falseBlock.Id))
        {
            elseBody = new ASTBlock();
            EmitRegion(elseBody, falseBlock, merge, loop);
        }

        target.Statements.Add(new ASTIf
        {
            Condition = condition,
            Then = thenBody,
            Else = (elseBody?.Statements.Count > 0) ? elseBody : null
        });
        if (merge != null && merge != stopAt)
            EmitRegion(target, merge, stopAt, loop);
    }

    private void EmitInstructions(ASTBlock target, BasicBlock block)
    {
        foreach (var instr in block.Instructions)
        {
            var stmt = StatementBuilder.FromInstruction(instr);
            if (stmt != null)
                target.Statements.Add(stmt);
        }
    }

    private BasicBlock? FindLoopFollow(NaturalLoop loop)
    {
        if (loop.ExitBlocks.Count > 0)
            return loop.ExitBlocks.OrderBy(b => b.StartIndex).First();
        foreach (var succ in loop.Header.Successors)
        {
            if (!loop.Body.Contains(succ))
                return succ;
        }

        return null;
    }

    private BasicBlock? FindMergeBlock(BasicBlock? trueBlock, BasicBlock? falseBlock, BasicBlock? stopAt)
    {
        if (trueBlock == null && falseBlock == null) return stopAt;
        if (trueBlock == null) return falseBlock;
        if (falseBlock == null) return stopAt;
        var trueReachable = CollectReachable(trueBlock, stopAt);
        var falseReachable = CollectReachable(falseBlock, stopAt);
        var shared = trueReachable.Intersect(falseReachable).ToList();
        if (shared.Count > 0)
        {
            BasicBlock? best = null;
            foreach (var b in _blocks)
            {
                if (shared.Contains(b.Id))
                {
                    if (best == null || b.StartIndex < best.StartIndex)
                        best = b;
                }
            }
            return best ?? stopAt;
        }

        return stopAt;
    }

    private HashSet<int> CollectReachable(BasicBlock start, BasicBlock? stopAt)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<BasicBlock>();
        foreach (var succ in start.Successors)
        {
            if (stopAt != null && succ == stopAt)
            {
                visited.Add(succ.Id);
                continue;
            }
            if (visited.Add(succ.Id))
                queue.Enqueue(succ);
        }

        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            if (stopAt != null && b == stopAt) continue;

            foreach (var succ in b.Successors)
            {
                if (visited.Add(succ.Id))
                    queue.Enqueue(succ);
            }
        }

        return visited;
    }

    private (int trueTarget, int falseTarget) GetBranchTargets(IRInstr term)
    {
        return term switch
        {
            CondJump cj => (cj.TrueTarget, cj.FalseTarget),
            TestJump tj => (tj.TrueTarget, tj.FalseTarget),
            _ => (-1, -1)
        };
    }

    private BasicBlock? ResolveBlock(int instrIndex)
    {
        if (instrIndex < 0) return null;
        return _blockByStartIndex.GetValueOrDefault(instrIndex);
    }
    private BasicBlock? GetFallthrough(BasicBlock block)
    {
        if (block.Successors.Count == 0) return null;
        if (block.Successors.Count == 1) return block.Successors[0];
        BasicBlock? best = null;
        foreach (var s in block.Successors)
        {
            if (s.StartIndex > block.StartIndex)
            {
                if (best == null || s.StartIndex < best.StartIndex)
                    best = s;
            }
        }
        return best ?? block.Successors[0];
    }
    private BasicBlock? NextLinearBlock(BasicBlock block)
    {
        int idx = _blocks.FindIndex(b => b.Id == block.Id);
        return idx >= 0 && idx + 1 < _blocks.Count ? _blocks[idx + 1] : null;
    }
}
