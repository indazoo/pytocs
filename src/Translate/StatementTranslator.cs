﻿#region License
//  Copyright 2015 John Källén
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//      http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
#endregion

using Pytocs.CodeModel;
using Pytocs.Syntax;
using Pytocs.TypeInference;
using Pytocs.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pytocs.Translate
{
    public class StatementTranslator : IStatementVisitor
    {
        private CodeGenerator gen;
        private ExpTranslator xlat;
        private SymbolGenerator gensym;
        private ClassDef currentClass;
        private IEnumerable<CodeAttributeDeclaration> customAttrs;
        private Dictionary<Decorated, PropertyDefinition> properties;
        private HashSet<string> globals;
        private CodeConstructor classConstructor;

        public StatementTranslator(CodeGenerator gen, SymbolGenerator gensym, HashSet<string> globals)
        {
            this.gen = gen;
            this.gensym = gensym;
            this.xlat = new ExpTranslator(gen, gensym);
            this.properties = new Dictionary<Decorated, PropertyDefinition>();
            this.globals = globals;
        }

        public void VisitClass(ClassDef c)
        {
            var baseClasses = c.args.Select(a => GenerateBaseClassName(a)).ToList();
            var comments = ConvertFirstStringToComments(c.body.stmts);
            var gensym = new SymbolGenerator();
            var stmtXlt = new StatementTranslator(gen, gensym, new HashSet<string>());
            stmtXlt.currentClass = c;
            stmtXlt.properties = FindProperties(c.body.stmts);
            var csClass = gen.Class(c.name.Name, baseClasses, () => c.body.Accept(stmtXlt));
            csClass.Comments.AddRange(comments);
            if (customAttrs != null)
            {
                csClass.CustomAttributes.AddRange(customAttrs);
                customAttrs = null;
            }
        }

        public Dictionary<Decorated, PropertyDefinition> FindProperties(List<Statement> stmts)
        {
            var decs = stmts.OfType<Decorated>();
            var propdefs = new Dictionary<string, PropertyDefinition>();
            var result = new Dictionary<Decorated, PropertyDefinition>();
            foreach (var dec in decs)
            {
                foreach (var decoration in dec.Decorations)
                {
                    if (IsGetterDecorator(decoration))
                    {
                        var def = (FunctionDef)dec.Statement;
                        var propdef = EnsurePropertyDefinition(propdefs, def);
                        result[dec] = propdef;
                        propdef.Getter = dec;
                        propdef.GetterDecoration = decoration;
                    }
                    if (IsSetterDecorator(decoration))
                    {
                        var def = (FunctionDef)dec.Statement;
                        var propdef = EnsurePropertyDefinition(propdefs, def);
                        result[dec] = propdef;
                        propdef.Setter = dec;
                        propdef.SetterDecoration = decoration;
                    }
                }
            }
            return result;
        }

        private static PropertyDefinition EnsurePropertyDefinition(Dictionary<string, PropertyDefinition> propdefs, FunctionDef def)
        {
            if (!propdefs.TryGetValue(def.name.Name, out var propdef))
            {
                propdef = new PropertyDefinition(def.name.Name);
                propdefs.Add(def.name.Name, propdef);
            }
            return propdef;
        }

        private static bool IsGetterDecorator(Decorator decoration)
        {
            return decoration.className.segs.Count == 1 &&
                                    decoration.className.segs[0].Name == "property";
        }

        private static bool IsSetterDecorator(Decorator decorator)
        {
            if (decorator.className.segs.Count != 2)
                return false;
            return decorator.className.segs[1].Name == "setter";
        }
        public static IEnumerable<CodeCommentStatement> ConvertFirstStringToComments(List<Statement> statements)
        {
            var nothing = new CodeCommentStatement[0];
            int i = 0;
            for (; i < statements.Count; ++i)
            {
                if (statements[i] is SuiteStatement ste)
                {
                    if (!(ste.stmts[0] is CommentStatement))
                        break;
                }
            }
            if (i >= statements.Count)
                return nothing;
            var suiteStmt = statements[i] as SuiteStatement;
            if (suiteStmt == null)
                return nothing;
            var expStm = suiteStmt.stmts[0] as ExpStatement;
            if (expStm == null)
                return nothing;
            var str = expStm.Expression as Str;
            if (str == null)
                return nothing;
            statements.RemoveAt(i);
            return str.s.Replace("\r\n", "\n").Split('\r', '\n').Select(line => new CodeCommentStatement(" " + line));
        }

        public void VisitComment(CommentStatement c)
        {
            gen.Comment(c.comment);
        }

        public void VisitTry(TryStatement t)
        {
            var tryStmt = gen.Try(
                () => t.body.Accept(this),
                t.exHandlers.Select(eh => GenerateClause(eh)),
                () =>
                {
                    if (t.finallyHandler != null)
                        t.finallyHandler.Accept(this);
                });
        }

        private CodeCatchClause GenerateClause(ExceptHandler eh)
        {
            if (eh.type is Identifier ex)
            {
                return gen.CatchClause(
                    null,
                    new CodeTypeReference(ex.Name),
                    () => eh.body.Accept(this));
            }
            else
            {
                return gen.CatchClause(
                    null,
                    null,
                    () => eh.body.Accept(this));
            }
            throw new NotImplementedException();
        }

        private string GenerateBaseClassName(Exp exp)
        {
            return exp.ToString();
        }

        private static Dictionary<Op, CsAssignOp> assignOps = new Dictionary<Op, CsAssignOp>()
        {
            { Op.Eq, CsAssignOp.Assign },
            { Op.AugAdd, CsAssignOp.AugAdd },
        };

        public void VisitExec(ExecStatement e)
        {
            var args = new List<CodeExpression>();
            args.Add(e.code.Accept(xlat));
            if (e.globals != null)
            {
                args.Add(e.globals.Accept(xlat));
                if (e.locals != null)
                {
                    args.Add(e.locals.Accept(xlat));
                }
            }
            gen.SideEffect(
                gen.Appl(
                    new CodeVariableReferenceExpression("Python_Exec"),
                    args.ToArray()));
        }

        public void VisitExp(ExpStatement e)
        {
            if (e.Expression is AssignExp ass)
            {
                if (ass.Dst is Identifier idDst)
                {
                    gensym.EnsureLocalVariable(idDst.Name, new CodeTypeReference(typeof(object)), false);
                }

                if (ass.Dst is ExpList dstTuple)
                {
                    if (ass.Src is ExpList srcTuple)
                    {
                        EmitTupleToTupleAssignment(dstTuple.Expressions, srcTuple.Expressions);
                    }
                    else
                    {
                        var rhsTuple = ass.Src.Accept(xlat);
                        EmitTupleAssignment(dstTuple.Expressions, rhsTuple);
                    }
                    return;
                }
                var rhs = ass.Src.Accept(xlat);
                var lhs = ass.Dst.Accept(xlat);
                if (gen.CurrentMember != null)
                {
                    if (ass.op == Op.Assign)
                    {
                        gen.Assign(lhs, rhs);
                    }
                    else
                    {
                        gen.SideEffect(e.Expression.Accept(xlat));
                    }
                }
                else
                {
                    if (ass.Dst is Identifier id)
                    {
                        ClassTranslator_GenerateField(id, xlat, ass);
                    }
                    else
                    {
                        EnsureClassConstructor().Statements.Add(
                            new CodeAssignStatement(lhs, rhs));
                    }
                }
                return;
            }
            if (gen.CurrentMember != null)
            {
                var ex = e.Expression.Accept(xlat);
                gen.SideEffect(ex);
            }
            else
            {
                var ex = e.Expression.Accept(xlat);
                EnsureClassConstructor().Statements.Add(
                    new CodeExpressionStatement(e.Expression.Accept(xlat)));
            }
        }

        private void EmitTupleAssignment(List<Exp> lhs, CodeExpression rhs)
        {
            var tup = GenSymLocalTuple();
            gen.Assign(tup, rhs);
            EmitTupleFieldAssignments(lhs, tup);
        }

        private void EmitTupleToTupleAssignment(List<Exp> dstTuple, List<Exp> srcTuple)
        {
            //$TODO cycle detection
            foreach (var pyAss in dstTuple.Zip(srcTuple, (a, b) => new { Dst = a, Src = b }))
            {
                if (pyAss.Dst is Identifier id)
                {
                    gensym.EnsureLocalVariable(id.Name, gen.TypeRef("object"), false);
                }
                gen.Assign(pyAss.Dst.Accept(xlat), pyAss.Src.Accept(xlat));
            }
        }

        private void EmitTupleFieldAssignments(List<Exp> lhs, CodeVariableReferenceExpression tup)
        {
            int i = 0;
            foreach (Exp value in lhs)
            {
                ++i;
                if (value == null || value.Name == "_")
                    continue;
                var tupleField = gen.Access(tup, "Item" + i);
                if (value is Identifier id)
                {
                    gensym.EnsureLocalVariable(id.Name, new CodeTypeReference(typeof(object)), false);
                    gen.Assign(new CodeVariableReferenceExpression(id.Name), tupleField);
                }
                else
                {
                    var dst = value.Accept(xlat);
                    gen.Assign(dst, tupleField);
                }
            }
        }

        private CodeVariableReferenceExpression GenSymLocalTuple()
        {
            return gensym.GenSymLocal("_tup_", new CodeTypeReference(typeof(object)));
        }

        public CodeVariableReferenceExpression GenSymParameter(string prefix, CodeTypeReference type)
        {
            return gensym.GenSymAutomatic(prefix, type, true);
        }

        private CodeConstructor EnsureClassConstructor()
        {
            if (this.classConstructor == null)
            {
                this.classConstructor = new CodeConstructor
                {
                    Attributes = MemberAttributes.Static,
                };
                gen.CurrentType.Members.Add(classConstructor);
            }
            return this.classConstructor;
        }

        private void ClassTranslator_GenerateField(Identifier id, ExpTranslator xlat, AssignExp ass)
        {
            IEnumerable<Exp> slotNames = null;
            if (ass.Src is PyList srcList)
            {
                slotNames = srcList.elts;
            }
            else if (ass.Src is PyTuple srcTuple)
            {
                slotNames = srcTuple.values;
            }
            if (id.Name == "__slots__")
            {
                if (slotNames == null)
                {
                    // dynamically generated slots are hard.
                    gen.Comment(ass.ToString());
                }
                else
                {
                    foreach (var slotName in slotNames.OfType<Str>())
                    {
                        GenerateField(slotName.s, null);
                    }
                }
            }
            else
            {
                GenerateField(id.Name, ass.Src.Accept(xlat));
            }
        }

        protected virtual CodeMemberField GenerateField(string name, CodeExpression value)
        {
            var field = gen.Field(name);
            if (value != null)
            {
                field.InitExpression = value;
            }
            return field;
        }

        public void VisitFor(ForStatement f)
        {
            if (f.exprs is Identifier)
            {
                var exp = f.exprs.Accept(xlat);
                var v = f.tests.Accept(xlat);
                gen.Foreach(exp, v, () => f.Body.Accept(this));
                return;
            }
            else if (f.exprs is ExpList expList)
            {
                GenerateForTuple(f, expList.Expressions);
                return;
            }
            else if (f.exprs is PyTuple tuple)
            {
                GenerateForTuple(f, tuple.values);
                return;
            }
            else if (f.exprs is AttributeAccess attributeAccess)
            {
                GenerateForAttributeAccess(f, attributeAccess.Expression);
                return;
            }
            throw new NotImplementedException();
        }

        private void GenerateForAttributeAccess(ForStatement f, Exp id)
        {
            var localVar = GenSymLocalTuple();
            var exp = f.exprs.Accept(xlat);
            var v = f.tests.Accept(xlat);
            gen.Foreach(localVar, v, () =>
            {
                gen.Assign(exp, localVar);
                f.Body.Accept(this);
            });
        }

        private void GenerateForTuple(ForStatement f, List<Exp> ids)
        {
            var localVar = GenSymLocalTuple();
            var v = f.tests.Accept(xlat);
            gen.Foreach(localVar, v, () =>
            {
                EmitTupleFieldAssignments(ids, localVar);
                f.Body.Accept(this);
            });
        }

        public void VisitFuncdef(FunctionDef f)
        {
            MethodGenerator mgen;
            MemberAttributes attrs = 0;

            if (this.gen.CurrentMember != null)
            {
                var lgen = new LambdaBodyGenerator(f, f.parameters, true, gen);
                var def = lgen.GenerateLambdaVariable(f);
                var meth = lgen.Generate();
                def.InitExpression = gen.Lambda(
                    meth.Parameters.Select(p => new CodeVariableReferenceExpression(p.ParameterName)).ToArray(),
                    meth.Statements);
                gen.CurrentMemberStatements.Add(def);
                return;
            }
            if (this.currentClass != null)
            {
                // Inside a class; is this a instance method?
                bool hasSelf = f.parameters.Any(p => p.Id != null && p.Id.Name == "self");
                if (hasSelf)
                {
                    // Presence of 'self' says it _is_ an instance method.
                    var adjustedPs = f.parameters.Where(p => p.Id == null || p.Id.Name != "self").ToList();
                    var fnName = f.name.Name;
                    if (fnName == "__init__")
                    {
                        // Magic function __init__ is a ctor.
                        mgen = new ConstructorGenerator(f, adjustedPs, gen);
                    }
                    else
                    {
                        if (f.name.Name == "__str__")
                        {
                            attrs = MemberAttributes.Override;
                            fnName = "ToString";
                        }
                        mgen = new MethodGenerator(f, fnName, adjustedPs, false, gen);
                    }
                }
                else
                {
                    mgen = new MethodGenerator(f, f.name.Name, f.parameters, true, gen);
                }
            }
            else
            {
                mgen = new MethodGenerator(f, f.name.Name, f.parameters, true, gen);
            }
            CodeMemberMethod m = mgen.Generate();
            m.Attributes |= attrs;
            if (customAttrs != null)
            {
                m.CustomAttributes.AddRange(this.customAttrs);
                customAttrs = null;
            }
        }

        public void VisitIf(IfStatement i)
        {
            var ifStmt = gen.If(i.Test.Accept(xlat), () => Xlat(i.Then), () => Xlat(i.Else));
        }

        public void VisitFrom(FromStatement f)
        {
            foreach (var alias in f.AliasedNames)
            {
                if (f.DottedName != null)
                {
                    var total = f.DottedName.segs.Concat(alias.orig.segs)
                        .Select(s => gen.EscapeKeywordName(s.Name));
                    gen.Using(alias.alias.Name, string.Join(".", total));
                }
            }
        }

        public void VisitImport(ImportStatement i)
        {
            foreach (var name in i.names)
            {
                if (name.alias == null)
                {
                    gen.Using(name.orig.ToString());
                }
                else
                {
                    gen.Using(
                        name.alias.Name,
                        string.Join(
                            ".",
                            name.orig.segs.Select(s => gen.EscapeKeywordName(s.Name))));
                }
            }
        }

        public void Xlat(Statement stmt)
        {
            if (stmt != null)
            {
                stmt.Accept(this);
            }
        }

        public void VisitPass(PassStatement p)
        {
        }

        public void VisitPrint(PrintStatement p)
        {
            CodeExpression e = null;
            if (p.outputStream != null)
            {
                e = p.outputStream.Accept(xlat);
            }
            else
            {
                e = gen.TypeRefExpr("Console");
            }
            e = gen.MethodRef(
                e, "WriteLine");
            gen.SideEffect(
                gen.Appl(
                    e,
                    p.args.Select(a => xlat.VisitArgument(a)).ToArray()));
        }

        public void VisitReturn(ReturnStatement r)
        {
            if (r.Expression != null)
                gen.Return(r.Expression.Accept(xlat));
            else
                gen.Return();
        }

        public void VisitRaise(RaiseStatement r)
        {
            if (r.exToRaise != null)
            {
                gen.Throw(r.exToRaise.Accept(xlat));
            }
            else
            {
                gen.Throw();
            }
        }

        public void VisitSuite(SuiteStatement s)
        {
            if (s.stmts.Count == 1)
            {
                s.stmts[0].Accept(this);
            }
            else
            {
                foreach (var stmt in s.stmts)
                {
                    stmt.Accept(this);
                }
            }
        }

        public void VisitAssert(AssertStatement a)
        {
            foreach (var test in a.Tests)
            {
                GenerateAssert(test);
            }
        }

        private void GenerateAssert(Exp test)
        {
            gen.SideEffect(
                gen.Appl(
                    gen.MethodRef(
                        gen.TypeRefExpr("Debug"),
                        "Assert"),
                    test.Accept(xlat)));
            gen.EnsureImport("System.Diagnostics");
        }

        public void VisitBreak(BreakStatement b)
        {
            gen.Break();
        }

        public void VisitContinue(ContinueStatement c)
        {
            gen.Continue();
        }

        public void VisitDecorated(Decorated d)
        {
            var decorators = d.Decorations.ToList();
            if (this.properties.TryGetValue(d, out var propdef))
            {
                if (propdef.IsTranslated)
                    return;
                decorators.Remove(propdef.GetterDecoration);
                decorators.Remove(propdef.SetterDecoration);
                this.customAttrs = decorators.Select(dd => VisitDecorator(dd));
                var prop = gen.PropertyDef(
                    propdef.Name,
                    () => GeneratePropertyGetter(propdef.Getter),
                    () => GeneratePropertySetter(propdef.Setter));
                LocalVariableGenerator.Generate(null, prop.GetStatements, globals);
                LocalVariableGenerator.Generate(
                    new List<CodeParameterDeclarationExpression> {
                        new CodeParameterDeclarationExpression(prop.PropertyType, "value"),
                    },
                    prop.SetStatements,
                    globals);
                propdef.IsTranslated = true;
            }
            else
            {
                this.customAttrs = d.Decorations.Select(dd => VisitDecorator(dd));
                d.Statement.Accept(this);
            }
        }

        private void GeneratePropertyGetter(Decorated getter)
        {
            var def = (FunctionDef)getter.Statement;
            var mgen = new MethodGenerator(def, null, def.parameters, false, gen);
            var comments = ConvertFirstStringToComments(def.body.stmts);
            gen.CurrentMemberComments.AddRange(comments);
            mgen.Xlat(def.body);
        }

        private void GeneratePropertySetter(Decorated setter)
        {
            if (setter == null)
                return;
            var def = (FunctionDef)setter.Statement;
            var mgen = new MethodGenerator(def, null, def.parameters, false, gen);
            var comments = ConvertFirstStringToComments(def.body.stmts);
            gen.CurrentMemberComments.AddRange(comments);
            mgen.Xlat(def.body);
        }

        public CodeAttributeDeclaration VisitDecorator(Decorator d)
        {
            return gen.CustomAttr(
                gen.TypeRef(d.className.ToString()),
                d.arguments.Select(a => new CodeAttributeArgument
                {
                    Name = a.name?.ToString(),
                    Value = a.defval?.Accept(xlat),
                }).ToArray());
        }

        public void VisitDel(DelStatement d)
        {
            var exprList = d.Expressions.AsList()
                .Select(e => e.Accept(xlat))
                .ToList();
            if (exprList.Count == 1 &&
                exprList[0] is CodeArrayIndexerExpression aref &&
                aref.Indices.Length == 1)
            {
                // del foo[bar] is likely
                // foo.Remove(bar)
                gen.SideEffect(
                    gen.Appl(
                        gen.MethodRef(
                            aref.TargetObject,
                            "Remove"),
                        aref.Indices[0]));
                return;
            }
            var fn = new CodeVariableReferenceExpression("WONKO_del");
            foreach (var exp in exprList)
            {
                gen.SideEffect(gen.Appl(fn, exp));
            }
        }

        public void VisitGlobal(GlobalStatement g)
        {
            foreach (var name in g.names)
            {
                globals.Add(name.Name);
            }
        }

        public void VisitNonLocal(NonlocalStatement n)
        {
            gen.Comment("LOCAL " + string.Join(", ", n.names));
        }

        public void VisitWhile(WhileStatement w)
        {
            if (w.Else != null)
            {
                gen.If(
                    w.Test.Accept(xlat),
                    () => gen.DoWhile(
                        () => w.Body.Accept(this),
                        w.Test.Accept(xlat)),
                    () => w.Else.Accept(this));
            }
            else
            {
                gen.While(
                    w.Test.Accept(xlat),
                    () => w.Body.Accept(this));
            }
        }

        public void VisitWith(WithStatement w)
        {
            gen.Using(
                w.items.Select(wi => Translate(wi)),
                () => w.body.Accept(this));
        }

        private CodeStatement Translate(WithItem wi)
        {
            CodeExpression e1 = wi.t.Accept(xlat);
            CodeExpression e2 = wi.e?.Accept(xlat);
            if (e2 != null)
                return new CodeAssignStatement(e2, e1);
            else
                return new CodeExpressionStatement(e1);
        }

        public void VisitYield(YieldStatement y)
        {
            gen.Yield(y.Expression.Accept(xlat));
        }
    }

    public class PropertyDefinition
    {
        public string Name;
        public Decorated Getter;
        public Decorated Setter;
        public Decorator GetterDecoration;
        public Decorator SetterDecoration;
        public bool IsTranslated;

        public PropertyDefinition(string name)
        {
            this.Name = name;
        }
    }
}
