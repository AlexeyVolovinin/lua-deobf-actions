using LuraphDeobfuscator.Deobfuscator.IR;
using LuraphDeobfuscator.Deobfuscator.CFG;

namespace LuraphDeobfuscator.Deobfuscator.AST;
public static class StatementBuilder
{
    public static ASTNode? FromInstruction(IRInstr instr)
    {
        switch (instr)
        {
            case Move m:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, m.Dst.Reg, true),
                    Value = ExpressionBuilder.FromOperand(m.Src, instr.Index, false)
                };

            case LoadVararg lv:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, lv.Dst.Reg, true),
                    Value = new ASTVararg()
                };
            case BinOp b:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, b.Dst.Reg, true),
                    Value = new ASTBinaryOp
                    {
                        Left = ExpressionBuilder.FromOperand(b.Left, instr.Index, false),
                        Operator = b.Kind.Symbol(),
                        Right = ExpressionBuilder.FromOperand(b.Right, instr.Index, false)
                    }
                };

            case UnaryOp u:
                ASTExpression unaryExpr = u.Kind
                switch
                {
                    UnaryOpKind.Not => new ASTNot
                    {
                        Operand = ExpressionBuilder.FromOperand(u.Src, instr.Index, false)
                    },
                    UnaryOpKind.Len => new ASTUnaryOp
                    {
                        Operator = "#",
                        Operand = ExpressionBuilder.FromOperand(u.Src, instr.Index, false)
                    },
                    _ => new ASTUnaryOp
                    {
                        Operator = u.Kind.Symbol(),
                        Operand = ExpressionBuilder.FromOperand(u.Src, instr.Index, false)
                    }
                };
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, u.Dst.Reg, true),
                    Value = unaryExpr
                };

            case Concat c:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, c.Dst.Reg, true),
                    Value = new ASTConcat
                    {
                        Parts = [
                            ExpressionBuilder.FromOperand(c.Left, instr.Index, false),
                        ExpressionBuilder.FromOperand(c.Right, instr.Index, false)
                        ]
                    }
                };
            case NewTable nt:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, nt.Dst.Reg, true),
                    Value = new ASTTableConstructor()
                };

            case GetTable gt:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, gt.Dst.Reg, true),
                    Value = new ASTIndex
                    {
                        Table = ExpressionBuilder.FromOperand(gt.Table, instr.Index, false),
                        Key = ExpressionBuilder.FromOperand(gt.Key, instr.Index, false),
                        IsDot = ExpressionBuilder.IsIdentifierKey(gt.Key)
                    }
                };

            case SetTable st:
                {
                    var tableExpr = ExpressionBuilder.FromOperand(st.Table, instr.Index, false);
                    var keyExpr = ExpressionBuilder.FromOperand(st.Key, instr.Index, false);
                    var valueExpr = ExpressionBuilder.FromOperand(st.Value, instr.Index, false);

                    if (!IsAssignableIndexBase(tableExpr))
                    {
                        return new ASTExpressionStatement
                        {
                            Expression = new ASTLiteral($"--[[ invalid settable target: {st.Disasm()} ]]")
                        };
                    }

                    return new ASTAssign
                    {
                        Target = new ASTIndex
                        {
                            Table = tableExpr,
                            Key = keyExpr,
                            IsDot = ExpressionBuilder.IsIdentifierKey(st.Key)
                        },
                        Value = valueExpr
                    };
                }
            case GetGlobal gg:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, gg.Dst.Reg, true),
                    Value = BuildGlobalRef(gg.Name)
                };

            case SetGlobal sg:
                return new ASTAssign
                {
                    Target = BuildGlobalRef(sg.Name),
                    Value = ExpressionBuilder.FromOperand(sg.Value, instr.Index, false)
                };
            case GetUpval gu:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, gu.Dst.Reg, true),
                    Value = new ASTUpvalueRef(gu.Upval.Index)
                };

            case SetUpval su:
                return new ASTAssign
                {
                    Target = new ASTUpvalueRef(su.Upval.Index),
                    Value = ExpressionBuilder.FromOperand(su.Value, instr.Index, false)
                };
            case Self s:
                return MakeSelf(s);
            case CmpBool cb:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, cb.Dst.Reg, true),
                    Value = new ASTBinaryOp
                    {
                        Left = ExpressionBuilder.FromOperand(cb.Left, instr.Index, false),
                        Operator = cb.Cmp.Symbol(),
                        Right = ExpressionBuilder.FromOperand(cb.Right, instr.Index, false)
                    }
                };
            case Call call:
                return MakeCall(call);

            case TailCall tc:
                return MakeTailCall(tc);
            case Return ret:
                return MakeReturn(ret);
            case MakeClosure mc:
                return new ASTAssign
                {
                    Target = RegRef(instr.Index, mc.Dst.Reg, true),
                    Value = new ASTLiteral($"<closure:{mc.ProtoIndex}>")
                };
            case SetList sl:
                return MakeSetList(sl);
            case Nop:
            case Phi:
            case CloseUpvals:
            case ForPrep:
            case ForLoop:
            case TForLoop:
            case IterPrep:
            case Jump:
            case CondJump:
            case TestJump:
                return null;

            default:
                return new ASTExpressionStatement
                {
                    Expression = new ASTLiteral($"--[[ unknown: {instr.Disasm()} ]]")
                };
        }
    }

    private static ASTExpression BuildGlobalRef(Operand name)
    {
        if (name is ConstOp
            {
                Value: string s
            })
        {
            if (ExpressionBuilder.IsLuaIdentifier(s))
                return new ASTGlobalRef
                {
                    Name = s
                };

            return new ASTIndex
            {
                Table = new ASTGlobalRef
                {
                    Name = "_G"
                },
                Key = ExpressionBuilder.FromConst(s),
                IsDot = false
            };
        }

        return new ASTIndex
        {
            Table = new ASTGlobalRef
            {
                Name = "_G"
            },
            Key = ExpressionBuilder.FromOperand(name),
            IsDot = false
        };
    }

    private static ASTRegisterRef RegRef(int instrIndex, int reg, bool isDefinition)
    {
        var rr = new ASTRegisterRef(reg);
        var hinted = SSABuilder.TryGetRegisterName(instrIndex, reg, isDefinition);
        if (!string.IsNullOrEmpty(hinted))
            rr.Name = hinted;
        return rr;
    }

    private static bool IsAssignableIndexBase(ASTExpression expr) => expr is
    ASTRegisterRef or ASTUpvalueRef or ASTGlobalRef or ASTIndex;

    private static ASTNode MakeSelf(Self s)
    {
        int a = s.Dst.Reg;
        var block = new ASTBlock();
        block.Statements.Add(new ASTAssign
        {
            Target = RegRef(s.Index, a + 1, true),
            Value = ExpressionBuilder.FromOperand(s.Object, s.Index, false)
        });
        var methodKey = ExpressionBuilder.FromOperand(s.Method, s.Index, false);
        block.Statements.Add(new ASTAssign
        {
            Target = RegRef(s.Index, a, true),
            Value = new ASTIndex
            {
                Table = ExpressionBuilder.FromOperand(s.Object, s.Index, false),
                Key = methodKey,
                IsDot = ExpressionBuilder.IsIdentifierKey(s.Method)
            }
        });

        return block;
    }

    private static ASTNode MakeCall(Call call)
    {
        int funcReg = call.Func.Reg;
        int argCount = call.ArgCount;
        int retCount = call.RetCount;

        var args = new List<ASTExpression>();

        if (argCount == -1)
        {
            args.Add(new ASTVararg());
        }
        else
        {
            for (int i = 0; i < argCount; i++)
                args.Add(RegRef(call.Index, funcReg + 1 + i, false));
        }

        var astCall = new ASTCall
        {
            Function = RegRef(call.Index, funcReg, false),
            Arguments = args,
            ReturnCount = retCount
        };
        if (retCount == 0)
            return new ASTExpressionStatement
            {
                Expression = astCall
            };
        if (retCount == -1 || retCount == 1)
            return new ASTAssign
            {
                Target = RegRef(call.Index, funcReg, true),
                Value = astCall
            };
        var targets = new List<ASTExpression>();
        for (int i = 0; i < retCount; i++)
            targets.Add(RegRef(call.Index, funcReg + i, true));

        return new ASTMultiAssign
        {
            Targets = targets,
            Values = [astCall]
        };
    }

    private static ASTNode MakeTailCall(TailCall tc)
    {
        int funcReg = tc.Func.Reg;
        int argCount = tc.ArgCount;

        var args = new List<ASTExpression>();
        if (argCount == -1)
            args.Add(new ASTVararg());
        else
            for (int i = 0; i < argCount; i++)
                args.Add(RegRef(tc.Index, funcReg + 1 + i, false));

        var astCall = new ASTCall
        {
            Function = RegRef(tc.Index, funcReg, false),
            Arguments = args,
            ReturnCount = -1
        };

        return new ASTReturn
        {
            Values = [astCall]
        };
    }

    private static ASTNode MakeReturn(Return ret)
    {
        int baseReg = ret.BaseReg;
        int count = ret.Count;

        var values = new List<ASTExpression>();

        if (count == -1)
            values.Add(new ASTVararg());
        else if (count > 0)
            for (int i = 0; i < count; i++)
                values.Add(RegRef(ret.Index, baseReg + i, false));

        return new ASTReturn
        {
            Values = values
        };
    }
    private static ASTNode MakeSetList(SetList sl)
    {
        int tableReg = sl.Table.Reg;
        int offset = sl.Offset;
        int count = sl.Count;

        var block = new ASTBlock();

        if (count <= 0)
        {
            block.Statements.Add(new ASTExpressionStatement
            {
                Expression = new ASTLiteral($"--[[ SETLIST {sl.Table}[{offset}..] = stack ]]")
            });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                block.Statements.Add(new ASTAssign
                {
                    Target = new ASTIndex
                    {
                        Table = RegRef(sl.Index, tableReg, false),
                        Key = new ASTLiteral((offset + i).ToString()),
                        IsDot = false
                    },
                    Value = RegRef(sl.Index, tableReg + 1 + i, false)
                });
            }
        }

        return block;
    }
}