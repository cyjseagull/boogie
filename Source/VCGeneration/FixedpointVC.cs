﻿//-----------------------------------------------------------------------------
//
// Copyright (C) 2012 Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics.Contracts;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;


using Term = Microsoft.Boogie.VCExprAST.VCExpr;
using FuncDecl = Microsoft.Boogie.VCExprAST.VCExprOp;
using Sort = Microsoft.Boogie.Type;
using Microsoft.Boogie.ExprExtensions;


namespace Microsoft.Boogie
{
    public class FixedpointVC : VC.VCGen
    {

        public class AnnotationInfo
        {
            public enum AnnotationType { LoopInvariant, ProcedureSummary };
            public string filename;
            public int lineno;
            public string[] argnames;
            public AnnotationType type;
        };

        static bool NoLabels = false;
        
        // options
        bool largeblock = false;

        public bool SetOption(string option, string value)
        {
            if (option == "LargeBlock")
            {
                largeblock = true;
                return true;
            }
            return false;
        }

        Context ctx;
        RPFP rpfp;
        Program program;
        Microsoft.Boogie.ProverContext boogieContext;
        Microsoft.Boogie.VCExpressionGenerator gen;
        public readonly static string recordProcName = "boogie_si_record"; // TODO: this really needed?
        private Dictionary<string, StratifiedInliningInfo> implName2StratifiedInliningInfo
            = new Dictionary<string, StratifiedInliningInfo>();
        Checker checker;
        // Microsoft.Boogie.Z3.Z3InstanceOptions options = new Microsoft.Boogie.Z3.Z3InstanceOptions(); // TODO: what?
        LineariserOptions linOptions;
        Dictionary<FuncDecl, StratifiedInliningInfo> relationToProc = new Dictionary<FuncDecl, StratifiedInliningInfo>();
        Dictionary<string, Term> labels = new Dictionary<string, Term> ();
        List<Term> DualityVCs = new List<Term>();
        Dictionary<string, bool> summaries = new Dictionary<string, bool>();
        string main_proc_name = "main";
        
        

        public enum Mode { Corral, OldCorral, Boogie};
        public enum AnnotationStyle { Flat, Procedure, Call };

        Mode mode;
        AnnotationStyle style;

        public FixedpointVC( Program _program, string/*?*/ logFilePath, bool appendLogFile)
            : base(_program, logFilePath, appendLogFile) 
        {
            switch (CommandLineOptions.Clo.FixedPointMode)
            {
                case CommandLineOptions.FixedPointInferenceMode.Corral:
                    mode = Mode.Corral;
                    style = AnnotationStyle.Procedure;
                    break;
                case CommandLineOptions.FixedPointInferenceMode.OldCorral:
                    mode = Mode.OldCorral;
                    style = AnnotationStyle.Procedure;
                    break;
                case CommandLineOptions.FixedPointInferenceMode.Flat:
                    mode = Mode.Boogie;
                    style = AnnotationStyle.Flat;
                    break;
                case CommandLineOptions.FixedPointInferenceMode.Procedure:
                    mode = Mode.Boogie;
                    style = AnnotationStyle.Procedure;
                    break;
                case CommandLineOptions.FixedPointInferenceMode.Call:
                    mode = Mode.Boogie;
                    style = AnnotationStyle.Call;
                    break;
            }
            ctx = new Context(); // TODO is this right?
            rpfp = new RPFP(RPFP.CreateLogicSolver(ctx));
            program = _program;
            gen = ctx;
            checker = new Checker(this, program, logFilePath, appendLogFile, 0, null);
            boogieContext = checker.TheoremProver.Context;
            linOptions = null; //  new Microsoft.Boogie.Z3.Z3LineariserOptions(false, options, new List<VCExprVar>());
        }

        Dictionary<string, AnnotationInfo> annotationInfo = new Dictionary<string, AnnotationInfo>();
        
        public void AnnotateLoops(Implementation impl, ProverContext ctxt)
        {
            Contract.Requires(impl != null);

            CurrentLocalVariables = impl.LocVars;
            variable2SequenceNumber = new Hashtable/*Variable -> int*/();
            incarnationOriginMap = new Dictionary<Incarnation, Absy>();

            ResetPredecessors(impl.Blocks);

            #region Create the graph by adding the source node and each edge
            GraphUtil.Graph<Block> g = Program.GraphFromImpl(impl);
            #endregion

            //Graph<Block> g = program.ProcessLoops(impl);

            g.ComputeLoops(); // this is the call that does all of the processing
            if (!g.Reducible)
            {
                throw new System.Exception("Irreducible flow graphs are unsupported.");
            }

            #region add a symbolic annoation to every loop head
            foreach (Block header in cce.NonNull(g.Headers))
                AnnotateBlock(impl, ctxt, header);
            #endregion
        }

        private void AnnotateCallSites(Implementation impl, ProverContext ctxt, Dictionary<string, bool> impls){
            foreach (var b in impl.Blocks)
            {
                foreach (var cmd in b.Cmds)
                {
                    if (cmd is CallCmd)
                    {
                        string name = (cmd as CallCmd).callee;
                        if(impls.ContainsKey(name))
                            goto annotate;
                    }
                }
                continue;
            annotate:
                AnnotateBlock(impl, ctxt, b);
            }
        }


        private void AnnotateBlock(Implementation impl, ProverContext ctxt, Block header)
        {
            Contract.Assert(header != null);
            
            string name = impl.Name + "_" + header.Label + "_invar";
            if (annotationInfo.ContainsKey(name))
                return;

            // collect the variables needed in the invariant
            ExprSeq exprs = new ExprSeq();
            VariableSeq vars = new VariableSeq();
            List<string> names = new List<string>();

            if (style == AnnotationStyle.Flat)
            {
                // in flat mode, all live globals should be in live set
#if false
                foreach (Variable v in program.GlobalVariables())
                {
                    vars.Add(v);
                    names.Add(v.ToString());
                    exprs.Add(new IdentifierExpr(Token.NoToken, v));
                }
#endif
                foreach (Variable v in /* impl.LocVars */ header.liveVarsBefore)
                {
                    vars.Add(v);
                    names.Add(v.ToString()); 
                    exprs.Add(new IdentifierExpr(Token.NoToken, v));
                }
            }
            else
            {
                foreach (Variable v in program.GlobalVariables())
                {
                    vars.Add(v);
                    names.Add("@old_" + v.ToString());
                    exprs.Add(new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                }
                foreach (IdentifierExpr ie in impl.Proc.Modifies)
                {
                    if (ie.Decl == null)
                        continue;
                    vars.Add(ie.Decl);
                    names.Add(ie.Decl.ToString());
                    exprs.Add(ie);
                }
                foreach (Variable v in impl.Proc.InParams)
                {
                    Contract.Assert(v != null);
                    vars.Add(v);
                    names.Add("@old_" + v.ToString());
                    exprs.Add(new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                }
                foreach (Variable v in impl.LocVars)
                {
                    vars.Add(v);
                    names.Add(v.ToString()); 
                    exprs.Add(new IdentifierExpr(Token.NoToken, v));
                }
            }
            
            TypedIdent ti = new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Bool);
            Contract.Assert(ti != null);
            Formal returnVar = new Formal(Token.NoToken, ti, false);
            Contract.Assert(returnVar != null);
            var function = new Function(Token.NoToken, name, vars, returnVar);
            ctxt.DeclareFunction(function, "");

            Expr invarExpr = new NAryExpr(Token.NoToken, new FunctionCall(function), exprs);
            var invarAssertion = new AssertCmd(Token.NoToken, invarExpr);
            CmdSeq newCmds = new CmdSeq();
            newCmds.Add(invarAssertion);

            // make a record in annotationInfo;
            var info = new AnnotationInfo();
            info.filename = header.tok.filename;
            info.lineno = header.Line;
            info.argnames = names.ToArray();
            info.type = AnnotationInfo.AnnotationType.LoopInvariant;
            annotationInfo.Add(name, info);
            // get file and line info from havoc, if there is...
            if (header.Cmds.Length > 0)
            {
                PredicateCmd bif = header.Cmds[0] as PredicateCmd;
                if (bif != null)
                {
                    string foo = QKeyValue.FindStringAttribute(bif.Attributes, "sourcefile");
                    if (foo != null)
                        info.filename = foo;
                    int bar = QKeyValue.FindIntAttribute(bif.Attributes, "sourceline", -1);
                    if (bar != -1)
                        info.lineno = bar;
                }
            }
            var thing = header;
            foreach (Cmd c in header.Cmds)
            {
                newCmds.Add(c);
            }
            header.Cmds = newCmds;
        }

#if true

        public void AnnotateProcEnsures(Procedure proc, Implementation impl, ProverContext ctxt)
        {
            Contract.Requires(impl != null);

            CurrentLocalVariables = impl.LocVars;

            // collect the variables needed in the invariant
            ExprSeq exprs = new ExprSeq();
            VariableSeq vars = new VariableSeq();
            List<string> names = new List<string>();

                foreach (Variable v in program.GlobalVariables())
                {
                    vars.Add(v);
                    exprs.Add(new OldExpr(Token.NoToken,new IdentifierExpr(Token.NoToken, v)));
                    names.Add(v.Name);
                }
                foreach (IdentifierExpr ie in proc.Modifies)
                        {
                            if (ie.Decl == null)
                                continue;
                            vars.Add(ie.Decl);
                            exprs.Add(ie);
                            names.Add(ie.Decl.Name + "_out");
                        }
                foreach (Variable v in proc.InParams)
                {
                            Contract.Assert(v != null);
                            vars.Add(v);
                            exprs.Add(new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                            names.Add(v.Name);
                }
                foreach (Variable v in proc.OutParams)
                {
                            Contract.Assert(v != null);
                            vars.Add(v);
                            exprs.Add(new IdentifierExpr(Token.NoToken, v));
                            names.Add(v.Name);
                }
                string name = impl.Name + "_summary";
                summaries.Add(name, true);
                TypedIdent ti = new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Bool);
                Contract.Assert(ti != null);
                Formal returnVar = new Formal(Token.NoToken, ti, false);
                Contract.Assert(returnVar != null);
                var function = new Function(Token.NoToken, name, vars, returnVar);
                ctxt.DeclareFunction(function, "");

                Expr invarExpr = new NAryExpr(Token.NoToken, new FunctionCall(function), exprs);
                
            proc.Ensures.Add(new Ensures(Token.NoToken, false, invarExpr, "", null));
            
            var info = new AnnotationInfo();
            info.filename = proc.tok.filename;
            info.lineno = proc.Line;
            info.argnames = names.ToArray();
            info.type = AnnotationInfo.AnnotationType.ProcedureSummary;
            annotationInfo.Add(name, info);
        }
#endif

        void InlineAll()
        {
            foreach (var d in program.TopLevelDeclarations)
            {
                var impl = d as Implementation;
                if (impl != null)
                {
                    impl.OriginalBlocks = impl.Blocks;
                    impl.OriginalLocVars = impl.LocVars;
                    if(impl.Name != main_proc_name)
                      if(impl.FindExprAttribute("inline") == null)
                        impl.AddAttribute("inline", Expr.Literal(100));
                }
            }
            foreach (var d in program.TopLevelDeclarations)
            {
                var impl = d as Implementation;
                if (impl != null && !impl.SkipVerification)
                {
                    Inliner.ProcessImplementation(program, impl);
                }
            }
            foreach (var d in program.TopLevelDeclarations)
            {
                var impl = d as Implementation;
                if (impl != null)
                {
                    impl.OriginalBlocks = null;
                    impl.OriginalLocVars = null;
                }
            }
        }

        public class LazyInliningInfo
        {
            [ContractInvariantMethod]
            void ObjectInvariant()
            {
                Contract.Invariant(impl != null);
                Contract.Invariant(function != null);
                Contract.Invariant(controlFlowVariable != null);
                Contract.Invariant(assertExpr != null);
                Contract.Invariant(cce.NonNullElements(interfaceVars));
                Contract.Invariant(incarnationOriginMap == null || cce.NonNullDictionaryAndValues(incarnationOriginMap));
            }

            public Implementation impl;
            public int uniqueId;
            public Function function;
            public Variable controlFlowVariable;
            public List<Variable> interfaceVars;
            public List<List<Variable>> interfaceVarCopies;
            public Expr assertExpr;
            public VCExpr vcexpr;
            public List<VCExprVar> privateVars;
            public Dictionary<Incarnation, Absy> incarnationOriginMap;
            public Hashtable /*Variable->Expr*/ exitIncarnationMap;
            public Hashtable /*GotoCmd->returnCmd*/ gotoCmdOrigins;
            public Hashtable/*<int, Absy!>*/ label2absy;
            public VC.ModelViewInfo mvInfo;

            public Dictionary<Block, VCExprVar> reachVars;
            public List<VCExprLetBinding> reachVarBindings;
            public Variable inputErrorVariable;
            public Variable outputErrorVariable;



            public LazyInliningInfo(Implementation impl, Program program, ProverContext ctxt, int uniqueId, GlobalVariable errorVariable)
            {
                Contract.Requires(impl != null);
                Contract.Requires(program != null);
                Procedure proc = cce.NonNull(impl.Proc);

                this.impl = impl;
                this.uniqueId = uniqueId;
                this.controlFlowVariable = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, "cfc", Microsoft.Boogie.Type.Int));
                impl.LocVars.Add(controlFlowVariable);

                List<Variable> interfaceVars = new List<Variable>();
                Expr assertExpr = new LiteralExpr(Token.NoToken, true);
                Contract.Assert(assertExpr != null);
                foreach (Variable v in program.GlobalVariables())
                {
                    Contract.Assert(v != null);
                    interfaceVars.Add(v);
                    if (v.Name == "error")
                        inputErrorVariable = v;
                }
                // InParams must be obtained from impl and not proc
                foreach (Variable v in impl.InParams)
                {
                    Contract.Assert(v != null);
                    interfaceVars.Add(v);
                }
                // OutParams must be obtained from impl and not proc
                foreach (Variable v in impl.OutParams)
                {
                    Contract.Assert(v != null);
                    Constant c = new Constant(Token.NoToken,
                                              new TypedIdent(Token.NoToken, impl.Name + "_" + v.Name, v.TypedIdent.Type));
                    interfaceVars.Add(c);
                    Expr eqExpr = Expr.Eq(new IdentifierExpr(Token.NoToken, c), new IdentifierExpr(Token.NoToken, v));
                    assertExpr = Expr.And(assertExpr, eqExpr);
                }
                if (errorVariable != null)
                {
                    proc.Modifies.Add(new IdentifierExpr(Token.NoToken, errorVariable));
                }
                foreach (IdentifierExpr e in proc.Modifies)
                {
                    Contract.Assert(e != null);
                    if (e.Decl == null)
                        continue;
                    Variable v = e.Decl;
                    Constant c = new Constant(Token.NoToken, new TypedIdent(Token.NoToken, impl.Name + "_" + v.Name, v.TypedIdent.Type));
                    interfaceVars.Add(c);
                    if (v.Name == "error")
                    {
                        outputErrorVariable = c;
                        continue;
                    }
                    Expr eqExpr = Expr.Eq(new IdentifierExpr(Token.NoToken, c), new IdentifierExpr(Token.NoToken, v));
                    assertExpr = Expr.And(assertExpr, eqExpr);
                }

                this.interfaceVars = interfaceVars;
                this.assertExpr = Expr.Not(assertExpr);
                VariableSeq functionInterfaceVars = new VariableSeq();
                foreach (Variable v in interfaceVars)
                {
                    Contract.Assert(v != null);
                    functionInterfaceVars.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, v.Name, v.TypedIdent.Type), true));
                }
                TypedIdent ti = new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Bool);
                Contract.Assert(ti != null);
                Formal returnVar = new Formal(Token.NoToken, ti, false);
                Contract.Assert(returnVar != null);
                this.function = new Function(Token.NoToken, proc.Name, functionInterfaceVars, returnVar);
                ctxt.DeclareFunction(this.function, "");

                interfaceVarCopies = new List<List<Variable>>();
                int temp = 0;
                for (int i = 0; i < /* CommandLineOptions.Clo.ProcedureCopyBound */ 0; i++)
                {
                    interfaceVarCopies.Add(new List<Variable>());
                    foreach (Variable v in interfaceVars)
                    {
                        Constant constant = new Constant(Token.NoToken, new TypedIdent(Token.NoToken, v.Name + temp++, v.TypedIdent.Type));
                        interfaceVarCopies[i].Add(constant);
                        //program.TopLevelDeclarations.Add(constant);
                    }
                }
            }
        }

        public class StratifiedInliningInfo : LazyInliningInfo
        {
            [ContractInvariantMethod]
            void ObjectInvariant()
            {
                Contract.Invariant(cce.NonNullElements(privateVars));
                Contract.Invariant(cce.NonNullElements(interfaceExprVars));
                Contract.Invariant(cce.NonNullElements(interfaceExprVars));
            }

            // public StratifiedVCGenBase vcgen;
            //public Implementation impl;
            //public Program program;
            //public ProverContext ctxt;
            //public int uniqueid;
            //public Function function;
            //public Variable controlFlowVariable;
            //public Expr assertExpr;
            //public VCExpr vcexpr;
            //public List<VCExprVar> interfaceExprVars;
            //public List<VCExprVar> privateExprVars;
            //public Hashtable/*<int, Absy!>*/ label2absy;
            //public VC.ModelViewInfo mvInfo;
            //public Dictionary<Block, List<CallSite>> callSites;
            //public Dictionary<Block, List<CallSite>> recordProcCallSites;
            //public IEnumerable<Block> sortedBlocks;
            //public bool initialized { get; private set; }


            public List<VCExprVar> interfaceExprVars; 
            public List<VCExprVar> privateVars;
            public VCExpr funcExpr;
            public VCExpr falseExpr;
            public RPFP.Transformer F;
            public RPFP.Node node;
            public RPFP.Edge edge;
            public bool isMain = false;
            public Dictionary<Absy, string> label2absyInv;
            public ProverContext ctxt;
            public Hashtable/*<Block, LetVariable!>*/ blockVariables = new Hashtable/*<Block, LetVariable!!>*/();
            public List<VCExprLetBinding> bindings = new List<VCExprLetBinding>();
                
            public StratifiedInliningInfo(Implementation _impl, Program _program, ProverContext _ctxt, int _uniqueid)
            : base(_impl,_program,_ctxt,_uniqueid,null){
                Contract.Requires(_impl != null);
                Contract.Requires(_program != null);
                privateVars = new List<VCExprVar>();
                interfaceExprVars = new List<VCExprVar>();
                ctxt = _ctxt;
            }

        }

        protected override void addExitAssert(string implName, Block exitBlock)
        {
            if (implName2StratifiedInliningInfo != null
                && implName2StratifiedInliningInfo.ContainsKey(implName)
                && !implName2StratifiedInliningInfo[implName].isMain)
            {
                if (mode == Mode.Boogie) return;
                Expr assertExpr = implName2StratifiedInliningInfo[implName].assertExpr;
                Contract.Assert(assertExpr != null);
                exitBlock.Cmds.Add(new AssertCmd(Token.NoToken, assertExpr));
            }
        }

#if false
        protected override void storeIncarnationMaps(string implName, Hashtable exitIncarnationMap)
        {
            if (implName2StratifiedInliningInfo != null && implName2StratifiedInliningInfo.ContainsKey(implName))
            {
                StratifiedInliningInfo info = implName2StratifiedInliningInfo[implName];
                Contract.Assert(info != null);
                info.exitIncarnationMap = exitIncarnationMap;
                info.incarnationOriginMap = this.incarnationOriginMap;
            }
        }
#endif

        public void GenerateVCsForStratifiedInlining()
        {
            Contract.Requires(program != null);
            foreach (Declaration decl in program.TopLevelDeclarations)
            {
                Contract.Assert(decl != null);
                Implementation impl = decl as Implementation;
                if (impl == null)
                    continue;
                Contract.Assert(!impl.Name.StartsWith(recordProcName), "Not allowed to have an implementation for this guy");

                Procedure proc = cce.NonNull(impl.Proc);

                {
                    StratifiedInliningInfo info = new StratifiedInliningInfo(impl, program, boogieContext, QuantifierExpr.GetNextSkolemId());
                    implName2StratifiedInliningInfo[impl.Name] = info;
                    // We don't need controlFlowVariable for stratified Inlining
                    //impl.LocVars.Add(info.controlFlowVariable);
                    ExprSeq exprs = new ExprSeq();

                    if (mode != Mode.Boogie && QKeyValue.FindBoolAttribute(impl.Attributes, "entrypoint"))
                    {
                        proc.Ensures.Add(new Ensures(Token.NoToken, true, Microsoft.Boogie.Expr.False, "", null));
                        info.assertExpr = Microsoft.Boogie.Expr.False;
                        // info.isMain = true;
                    }
                    else if (mode == Mode.Corral || proc.FindExprAttribute("inline") != null || proc is LoopProcedure)
                    {
                        foreach (Variable v in program.GlobalVariables())
                        {
                            Contract.Assert(v != null);
                            exprs.Add(new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
                        }
                        foreach (Variable v in proc.InParams)
                        {
                            Contract.Assert(v != null);
                            exprs.Add(new IdentifierExpr(Token.NoToken, v));
                        }
                        foreach (Variable v in proc.OutParams)
                        {
                            Contract.Assert(v != null);
                            exprs.Add(new IdentifierExpr(Token.NoToken, v));
                        }
                        foreach (IdentifierExpr ie in proc.Modifies)
                        {
                            Contract.Assert(ie != null);
                            if (ie.Decl == null)
                                continue;
                            exprs.Add(ie);
                        }
                        Expr freePostExpr = new NAryExpr(Token.NoToken, new FunctionCall(info.function), exprs);
#if false
                        if(mode == Mode.Corral || mode == Mode.OldCorral)
                            proc.Ensures.Add(new Ensures(Token.NoToken, true, freePostExpr, "", new QKeyValue(Token.NoToken, "si_fcall", new List<object>(), null)));
#endif
                    }
                    else // not marked "inline" must be main
                    {
                        Expr freePostExpr = new NAryExpr(Token.NoToken, new FunctionCall(info.function), exprs);
                        info.isMain = true;
                    }
                }
            }

            if (mode == Mode.Boogie) return;

            foreach (var decl in program.TopLevelDeclarations)
            {
                var proc = decl as Procedure;
                if (proc == null) continue;
                if (!proc.Name.StartsWith(recordProcName)) continue;
                Contract.Assert(proc.InParams.Length == 1);

                // Make a new function
                TypedIdent ti = new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Bool);
                Contract.Assert(ti != null);
                Formal returnVar = new Formal(Token.NoToken, ti, false);
                Contract.Assert(returnVar != null);

                // Get record type
                var argtype = proc.InParams[0].TypedIdent.Type;

                var ins = new VariableSeq();
                ins.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "x", argtype), true));

                var recordFunc = new Function(Token.NoToken, proc.Name, ins, returnVar);
                boogieContext.DeclareFunction(recordFunc, "");

                var exprs = new ExprSeq();
                exprs.Add(new IdentifierExpr(Token.NoToken, proc.InParams[0]));

                Expr freePostExpr = new NAryExpr(Token.NoToken, new FunctionCall(recordFunc), exprs);
                proc.Ensures.Add(new Ensures(true, freePostExpr));
            }
        }

        private Term ExtractSmallerVCsRec(Dictionary<Term, Term> memo, Term t, List<Term> small)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                if (f.GetKind() == DeclKind.Implies){
                    var lhs = t.GetAppArgs()[0];
                    if(lhs.GetKind() == TermKind.App){
                        var r = lhs.GetAppDecl();
                        if (r.GetKind() == DeclKind.And)
                        {
                            Term q = t.GetAppArgs()[1];
                            var lhsargs = lhs.GetAppArgs();
                            for (int i = lhsargs.Length-1; i >= 0; --i)
                            {
                                q = ctx.MkImplies(lhsargs[i], q);
                            }
                            res = ExtractSmallerVCsRec(memo, q, small);
                            goto done;
                        }
                        if (r.GetKind() == DeclKind.Label)
                        {
                            var arg = lhs; 
                            arg = lhs.GetAppArgs()[0];
                            if (!(arg.GetKind() == TermKind.App && arg.GetAppDecl().GetKind() == DeclKind.Uninterpreted))
                                goto normal;
                            if (!(annotationInfo.ContainsKey(arg.GetAppDecl().GetDeclName()) && annotationInfo[arg.GetAppDecl().GetDeclName()].type == AnnotationInfo.AnnotationType.LoopInvariant))
                                goto normal;
                            var sm = ctx.MkImplies(lhs, ExtractSmallerVCsRec(memo, t.GetAppArgs()[1], small));
                            small.Add(sm);
                            res = ctx.MkTrue();
                            goto done;
                        }
                        if (r.GetKind() == DeclKind.Uninterpreted)
                        {
                            var arg = lhs;
                            if (!(annotationInfo.ContainsKey(arg.GetAppDecl().GetDeclName()) && annotationInfo[arg.GetAppDecl().GetDeclName()].type == AnnotationInfo.AnnotationType.LoopInvariant))
                                goto normal;
                            var sm = ctx.MkImplies(lhs,ExtractSmallerVCsRec(memo,t.GetAppArgs()[1],small));
                            small.Add(sm);
                            res = ctx.MkTrue();
                            goto done;
                        }
                    }
                normal:
                    res = ctx.MkImplies(lhs,ExtractSmallerVCsRec(memo,t.GetAppArgs()[1],small));
                }
                else if (f.GetKind() == DeclKind.And)
                {
                    res = ctx.MkApp(f,t.GetAppArgs().Select(x => ExtractSmallerVCsRec(memo, x, small)).ToArray());
                }
                else
                    res = t;
            }
            else
                res = t;
            done:
            memo.Add(t, res);
            return res;
        }

        private void ExtractSmallerVCs(Term t, List<Term> small){
            Dictionary<Term, Term> memo = new Dictionary<Term, Term>();
            Term top = ExtractSmallerVCsRec(memo, t, small);
            small.Add(top);
        }

        private Dictionary<FuncDecl, int> goalNumbering = new Dictionary<FuncDecl, int>();

        private Term NormalizeGoal(Term goal, FuncDecl label)
        {
            var f = goal.GetAppDecl();
            var args = goal.GetAppArgs();
            int number;
            if (!goalNumbering.TryGetValue(f, out number))
            {
                number = goalNumbering.Count;
                goalNumbering.Add(f, number);
            }
            Term[] tvars = new Term[args.Length];
            Term[] eqns = new Term[args.Length];
            AnnotationInfo info = null;
            annotationInfo.TryGetValue(f.GetDeclName(), out info);
            for (int i = 0; i < args.Length; i++)
            {
                string pname = (info == null) ? i.ToString() : info.argnames[i];
                tvars[i] = ctx.MkConst("@a" + number.ToString() + "_" + pname, args[i].GetSort());
                eqns[i] = ctx.MkEq(tvars[i], args[i]);
            }
            return ctx.MkImplies(ctx.MkAnd(eqns), ctx.MkApp(label, ctx.MkApp(f, tvars)));
        }

        private Term MergeGoalsRec(Dictionary<Term, Term> memo, Term t)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                var args = t.GetAppArgs();
                if (f.GetKind() == DeclKind.Implies)
                {
                    res = ctx.MkImplies(args[0], MergeGoalsRec(memo, args[1]));
                    goto done;
                }
                else if (f.GetKind() == DeclKind.And)
                {
                    args = args.Select(x => MergeGoalsRec(memo, x)).ToArray();
                    res = ctx.MkApp(f, args);
                    goto done;
                }
                else if (f.GetKind() == DeclKind.Label)
                {
                    var arg = t.GetAppArgs()[0];
                    var r = arg.GetAppDecl();
                    if (r.GetKind() == DeclKind.Uninterpreted)
                    {
                        res = NormalizeGoal(arg, f);
                        goto done;
                    }
                }
            }
            res = t;
        done:
            memo.Add(t, res);
            return res;
        }

        private Term MergeGoals(Term t)
        {
            Dictionary<Term, Term> memo = new Dictionary<Term, Term>();
            return MergeGoalsRec(memo, t);
        }

        private Term CollectGoalsRec(Dictionary<Term, Term> memo, Term t, List<Term> goals, List<Term> cruft)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                if (f.GetKind() == DeclKind.Implies)
                {
                    CollectGoalsRec(memo, t.GetAppArgs()[1], goals, cruft);
                    goto done;
                }
                else if (f.GetKind() == DeclKind.And)
                {
                    foreach (var arg in t.GetAppArgs())
                    {
                        CollectGoalsRec(memo, arg, goals, cruft);
                    }
                    goto done;
                }
                else if (f.GetKind() == DeclKind.Label)
                {
                    var arg = t.GetAppArgs()[0];
                    var r = arg.GetAppDecl();
                    if (r.GetKind() == DeclKind.Uninterpreted)
                    {
                        if (memo.TryGetValue(arg, out res))
                            goto done;
                        if (!annotationInfo.ContainsKey(r.GetDeclName()) && !arg.GetAppDecl().GetDeclName().StartsWith("_solve_"))
                            goto done;
                        goals.Add(arg);
                        memo.Add(arg, arg);
                        goto done;
                    }
                    else
                        return CollectGoalsRec(memo, arg, goals, cruft);
                }
                else if (f.GetKind() == DeclKind.Uninterpreted)
                {
                    string name = f.GetDeclName();
                    if (name.StartsWith("_solve_"))
                    {
                        if (memo.TryGetValue(t, out res))
                            goto done;
                        goals.Add(t);
                        memo.Add(t, t);
                        return t;
                    }
                }
            }
            // else the goal must be cruft
            cruft.Add(t);
        done:
            res = t; // just to return something
            memo.Add(t, res);
            return res;
        }

        private void CollectGoals(Term t, List<Term> goals, List<Term> cruft)
        {
            Dictionary<Term, Term> memo = new Dictionary<Term, Term>();
            CollectGoalsRec(memo, t.GetAppArgs()[1], goals, cruft);
        }

        private Term SubstRec(Dictionary<Term, Term> memo, Term t)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                var args = t.GetAppArgs().Select(x => SubstRec(memo, x)).ToArray();
                res = ctx.MkApp(f, args);
            }
            else res = t;
            memo.Add(t, res);
            return res;
        }

        private Term SubstRecGoals(Dictionary<Term, Term> memo, Term t)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                var args = t.GetAppArgs();
                if (f.GetKind() == DeclKind.Implies){
                    res = SubstRecGoals(memo, args[1]);
                    if (res != ctx.MkTrue())
                      res = ctx.MkImplies(args[0],res);
                    goto done;
                }
                else if (f.GetKind() == DeclKind.And)
                {
                    args = args.Select(x => SubstRecGoals(memo, x)).ToArray();
                    args = args.Where(x => x != ctx.MkTrue()).ToArray();
                    res = ctx.MkAnd(args);
                    goto done;
                }
                else if (f.GetKind() == DeclKind.Label)
                {
                    var arg = t.GetAppArgs()[0];
                    var r = arg.GetAppDecl();
                    if (r.GetKind() == DeclKind.Uninterpreted)
                    {
                        if (memo.TryGetValue(arg, out res))
                        {
                            if(res != ctx.MkTrue())
                                res = ctx.MkApp(f, res);
                            goto done;
                        }
                    }
                    else
                    {
                        res = ctx.MkApp(f, SubstRecGoals(memo, arg));
                        goto done;
                    }

                }
                // what's left could be cruft!
                if (memo.TryGetValue(t, out res))
                {
                    goto done;
                }
            }
            res = t;
          done:
            memo.Add(t, res);
            return res;
        }
        
        private void FactorVCs(Term t, List<Term> vcs)
        {
            List<Term> small = new List<Term>();
            ExtractSmallerVCs(t, small);
            foreach (var smm in small)
            {
                List<Term> goals = new List<Term>();
                List<Term> cruft = new List<Term>();
                var sm = largeblock ? MergeGoals(smm) : smm;
                CollectGoals(sm, goals,cruft);
                foreach (var goal in goals)
                {
                    Dictionary<Term, Term> memo = new Dictionary<Term, Term>();
                    foreach (var othergoal in goals)
                        memo.Add(othergoal, othergoal.Equals(goal) ? ctx.MkFalse() : ctx.MkTrue());
                    foreach (var thing in cruft)
                        memo.Add(thing, ctx.MkTrue());
                    var vc = SubstRecGoals(memo, sm);
                    vc = ctx.MkImplies(ctx.MkNot(vc), goal);
                    vcs.Add(vc);
                }
                {
                    Dictionary<Term, Term> memo = new Dictionary<Term, Term>();
                    foreach (var othergoal in goals)
                        memo.Add(othergoal, ctx.MkTrue());
                    var vc = SubstRecGoals(memo, sm);
                    if (vc != ctx.MkTrue())
                    {
                        vc = ctx.MkImplies(ctx.MkNot(vc), ctx.MkFalse());
                        vcs.Add(vc);
                    }
                }
            }
        }

        

        private void GenerateVCForStratifiedInlining(Program program, StratifiedInliningInfo info, Checker checker)
        {
            Contract.Requires(program != null);
            Contract.Requires(info != null);
            Contract.Requires(checker != null);
            Contract.Requires(info.impl != null);
            Contract.Requires(info.impl.Proc != null);


            
            Implementation impl = info.impl;
            if (mode == Mode.Boogie && style == AnnotationStyle.Flat && impl.Name != main_proc_name)
                return;
            Contract.Assert(impl != null);
            ConvertCFG2DAG(impl);
            VC.ModelViewInfo mvInfo;
            PassifyImpl(impl, out mvInfo);
            Hashtable/*<int, Absy!>*/ label2absy = null;
            VCExpressionGenerator gen = checker.VCExprGen;
            Contract.Assert(gen != null);
            VCExpr vcexpr;
            if(NoLabels){
                int assertionCount = 0;
                VCExpr startCorrect = null; /* VC.VCGen.LetVC(cce.NonNull(impl.Blocks[0]), null, null, info.blockVariables, info.bindings,
                    info.ctxt, out assertionCount); */
                vcexpr = gen.Let(info.bindings, startCorrect);
            }
            else vcexpr = GenerateVC(impl, null /* info.controlFlowVariable */, out label2absy, info.ctxt);
            if(mode != Mode.Boogie)
                vcexpr = gen.Not(vcexpr);
            Contract.Assert(vcexpr != null);
            info.label2absy = label2absy;
            info.mvInfo = mvInfo;
            List<VCExpr> interfaceExprs = new List<VCExpr>();

            if (true || !info.isMain)
            {
                Boogie2VCExprTranslator translator = checker.TheoremProver.Context.BoogieExprTranslator;
                Contract.Assert(translator != null);
                info.privateVars = new List<VCExprVar>();
                foreach (Variable v in impl.LocVars)
                {
                    Contract.Assert(v != null);
                    info.privateVars.Add(translator.LookupVariable(v));
                }
                foreach (Variable v in impl.OutParams)
                {
                    Contract.Assert(v != null);
                    info.privateVars.Add(translator.LookupVariable(v));
                }

                info.interfaceExprVars = new List<VCExprVar>();

                foreach (Variable v in info.interfaceVars)
                {
                    Contract.Assert(v != null);
                    VCExprVar ev = translator.LookupVariable(v);
                    Contract.Assert(ev != null);
                    info.interfaceExprVars.Add(ev);
                    interfaceExprs.Add(ev);
                }
            }

            Function function = cce.NonNull(info.function);
            Contract.Assert(function != null);
            info.funcExpr = gen.Function(function, interfaceExprs);
            info.vcexpr = vcexpr;

            if (mode == Mode.Boogie)
            {
                Term z3vc = boogieContext.VCExprToTerm(vcexpr, linOptions);
                FactorVCs(z3vc, DualityVCs);
            }
            else
            {
                // Index the procedures by relational variable
                FuncDecl R = boogieContext.VCExprToTerm(info.funcExpr, linOptions).GetAppDecl();
                relationToProc.Add(R, info);
                info.node = rpfp.CreateNode(boogieContext.VCExprToTerm(info.funcExpr, linOptions));
                if (info.isMain || QKeyValue.FindBoolAttribute(impl.Attributes, "entrypoint"))
                    info.node.Bound.Formula = ctx.MkFalse();
            }
        }

        // This returns a new FuncDel with same sort as top-level function
        // of term t, but with numeric suffix appended to name.

        private FuncDecl SuffixFuncDecl(Term t, int n)
        {
            var name = t.GetAppDecl().GetDeclName() + "_" + n.ToString();
            return ctx.MkFuncDecl(name, t.GetAppDecl());
        }

        // Collect the relational paremeters

        private Term CollectParamsRec(Dictionary<Term, Term> memo, Term t, List<FuncDecl> parms, List<RPFP.Node> nodes)
        {
            Term res;
            if (memo.TryGetValue(t, out res))
                return res;
            var kind = t.GetKind();
            if (kind == TermKind.App)
            {
                var f = t.GetAppDecl();
                StratifiedInliningInfo info;
                if (relationToProc.TryGetValue(f, out info))
                {
                    f = SuffixFuncDecl(t, parms.Count);
                    parms.Add(f);
                    nodes.Add(info.node);
                }
                var args = t.GetAppArgs();
                args = args.Select(x => CollectParamsRec(memo, x, parms, nodes)).ToArray();
                res = ctx.MkApp(f, args);
            } // TODO: handle quantifiers
            else res = t;
            memo.Add(t, res);
            return res;
        }

        public void GetTransformer(StratifiedInliningInfo info)
        {
            Term vcTerm = boogieContext.VCExprToTerm(info.vcexpr, linOptions);
            Term[] paramTerms = info.interfaceExprVars.Select(x => boogieContext.VCExprToTerm(x, linOptions)).ToArray();
            var relParams = new List<FuncDecl>();
            var nodeParams = new List<RPFP.Node>();
            var memo = new Dictionary<Term, Term>();
            vcTerm = CollectParamsRec(memo, vcTerm, relParams, nodeParams);
            // var ops = new Util.ContextOps(ctx);
            // var foo = ops.simplify_lhs(vcTerm);
            // vcTerm = foo.Item1;
            info.F = rpfp.CreateTransformer(relParams.ToArray(), paramTerms, vcTerm);
            info.edge = rpfp.CreateEdge(info.node, info.F, nodeParams.ToArray());
            // TODO labels[info.edge.number] = foo.Item2;
        }

        public RPFP.Node GetNodeOfImpl(Implementation/*!*/ impl)
        {
            return implName2StratifiedInliningInfo[impl.Name].node;
        }

        public class CyclicLiveVariableAnalysis : Microsoft.Boogie.LiveVariableAnalysis
        {
            public static void ComputeLiveVariables(Implementation impl)
            {

                bool some_change = true;
                List<Block> sortedNodes = new List<Block>();
                foreach (var block in impl.Blocks)
                {
                    sortedNodes.Add(block);
                }
                sortedNodes.Reverse();
             
                while (some_change)
                {
                    some_change = false;
                    foreach (Block/*!*/ block in sortedNodes)
                    {
                        Contract.Assert(block != null);
                        HashSet<Variable/*!*/>/*!*/ liveVarsAfter = new HashSet<Variable/*!*/>();
                        if (block.TransferCmd is GotoCmd)
                        {
                            GotoCmd gotoCmd = (GotoCmd)block.TransferCmd;
                            if (gotoCmd.labelTargets != null)
                            {
                                foreach (Block/*!*/ succ in gotoCmd.labelTargets)
                                {
                                    Contract.Assert(succ != null);
                                    if (succ.liveVarsBefore != null)
                                        liveVarsAfter.UnionWith(succ.liveVarsBefore);
                                }
                            }
                        }

                        CmdSeq cmds = block.Cmds;
                        int len = cmds.Length;
                        for (int i = len - 1; i >= 0; i--)
                        {
                            if (cmds[i] is CallCmd)
                            {
                                Procedure/*!*/ proc = cce.NonNull(cce.NonNull((CallCmd/*!*/)cmds[i]).Proc);
                                if (InterProcGenKill.HasSummary(proc.Name))
                                {
                                    liveVarsAfter =
                                      InterProcGenKill.PropagateLiveVarsAcrossCall(cce.NonNull((CallCmd/*!*/)cmds[i]), liveVarsAfter);
                                    continue;
                                }
                            }
                            Propagate(cmds[i], liveVarsAfter);
                        }

                        if (block.liveVarsBefore == null)
                            block.liveVarsBefore = new HashSet<Variable>();
                        if (!liveVarsAfter.IsSubsetOf(block.liveVarsBefore))
                        {
                            block.liveVarsBefore = liveVarsAfter;
                            some_change = true;
                        }
                    }
                }
            }
        }

        public void Generate()
        {


            // Run live variable analysis (TODO: should this be here?)
#if false
            if (CommandLineOptions.Clo.LiveVariableAnalysis == 2)
            {
                Microsoft.Boogie.InterProcGenKill.ComputeLiveVars(impl, program);
            }
#endif

            #region In Boogie mode, annotate the program
            if (mode == Mode.Boogie)
            {
                // find the name of the main procedure
                main_proc_name = "main"; // default in case no entry point defined
                foreach (var d in program.TopLevelDeclarations)
                {
                    var impl = d as Implementation;
                    if (impl != null)
                    {
                        if (QKeyValue.FindBoolAttribute(impl.Attributes, "entrypoint"))
                            main_proc_name = impl.Proc.Name;
                    }
                }
                if (style == AnnotationStyle.Flat)
                {
                    InlineAll();
                    Microsoft.Boogie.BlockCoalescer.CoalesceBlocks(program);
                    foreach (var d in program.TopLevelDeclarations)
                    {
                        var impl = d as Implementation;
                        if (impl != null && main_proc_name == impl.Proc.Name)
                        {
                            Microsoft.Boogie.LiveVariableAnalysis.ClearLiveVariables(impl);
                            CyclicLiveVariableAnalysis.ComputeLiveVariables(impl);
                            AnnotateLoops(impl, boogieContext);
                        }
                    }
                }
                else
                {

                    if (style == AnnotationStyle.Procedure || style == AnnotationStyle.Call)
                    {
                        foreach (var proc in program.TopLevelDeclarations)
                        {
                            var impl = proc as Implementation;
                            if (impl != null)
                                AnnotateProcEnsures(impl.Proc, impl, boogieContext);
                        }
                        if (style == AnnotationStyle.Call)
                        {

                        }
                    }

                    // must do this after annotating procedures, else calls
                    // will be prematurely desugared
                    
                    foreach (var d in program.TopLevelDeclarations)
                    {
                        var impl = d as Implementation;
                        if (impl != null)
                        {
                            Microsoft.Boogie.LiveVariableAnalysis.ClearLiveVariables(impl);
                            CyclicLiveVariableAnalysis.ComputeLiveVariables(impl);
                        }
                    }


                    if (style == AnnotationStyle.Flat || style == AnnotationStyle.Call)
                    {
                        foreach (var proc in program.TopLevelDeclarations)
                        {
                            var impl = proc as Implementation;
                            if (impl != null)
                                AnnotateLoops(impl, boogieContext);
                        }
                    }
                    if (style == AnnotationStyle.Call)
                    {
                        Dictionary<string, bool> impls = new Dictionary<string, bool>();
                        foreach (var proc in program.TopLevelDeclarations)
                        {
                            var impl = proc as Implementation;
                            if (impl != null)
                                impls.Add(impl.Proc.Name, true);
                        }
                        foreach (var proc in program.TopLevelDeclarations)
                        {
                            var impl = proc as Implementation;
                            if (impl != null)
                                AnnotateCallSites(impl, boogieContext, impls);
                        }
                    }
                    if (style == AnnotationStyle.Flat)
                        InlineAll();
                }
            }
            #endregion

            /* Generate the VC's */
            GenerateVCsForStratifiedInlining();
 
            /* Generate the background axioms */
            Term background = ctx.MkTrue(); // TODO boogieContext.VCExprToTerm(boogieContext.Axioms, linOptions);
            rpfp.AssertAxiom(background);

            int save_option = CommandLineOptions.Clo.StratifiedInlining; // need this to get funcall labels
            CommandLineOptions.Clo.StratifiedInlining = 1;

            /* Create the nodes, indexing procedures by their relational symbols. */
            foreach (StratifiedInliningInfo info in implName2StratifiedInliningInfo.Values)
                GenerateVCForStratifiedInlining(program, info, checker);

            CommandLineOptions.Clo.StratifiedInlining = save_option;

            if (mode == Mode.Boogie)
            {
                // var ops = new Util.ContextOps(ctx);
                var vcs = DualityVCs;
                DualityVCs = new List<Term>();
                foreach (var vc in vcs)
                {
                    // var foo = ops.simplify_lhs(vc.GetAppArgs()[0]);
                    var foo = vc.GetAppArgs()[0];
                    if (!foo.IsFalse())
                        DualityVCs.Add(ctx.MkImplies(foo, vc.GetAppArgs()[1]));
                }

                rpfp.FromClauses(DualityVCs.ToArray());
                // TODO rpfp.HornClauses = style == AnnotationStyle.Flat;
            }
            else
            {
                /* Generate the edges. */
                foreach (StratifiedInliningInfo info in implName2StratifiedInliningInfo.Values)
                    GetTransformer(info);
            }

            // save some information for debugging purposes
            // TODO rpfp.ls.SetAnnotationInfo(annotationInfo);
        }

        private class ErrorHandler : ProverInterface.ErrorHandler
        {
            //TODO: anything we need to handle?
        }

        /** Check the RPFP, and return a counterexample if there is one. */

        public RPFP.LBool Check(ref RPFP.Node cexroot)
        {
            var start = DateTime.Now;

            ErrorHandler handler = new ErrorHandler();
            RPFP.Node cex;
            ProverInterface.Outcome outcome = checker.TheoremProver.CheckRPFP("name", rpfp, handler, out cex);
            cexroot = cex;
          
            Console.WriteLine("solve: {0}s", (DateTime.Now - start).TotalSeconds);
            
            switch(outcome)
            {
                case ProverInterface.Outcome.Valid:
                   return RPFP.LBool.False;
                case ProverInterface.Outcome.Invalid:
                   return RPFP.LBool.True;
                default:
                   return RPFP.LBool.Undef;
            }
        }

        public override VC.VCGen.Outcome VerifyImplementation(Implementation impl, VerifierCallback collector)
        {
            Procedure proc = impl.Proc;
            
            // we verify all the impls at once, so we need to execute only once
            // TODO: make sure needToCheck is true only once
            bool needToCheck = false; 
            if (mode == Mode.OldCorral)
                needToCheck = proc.FindExprAttribute("inline") == null && !(proc is LoopProcedure);
            else if (mode == Mode.Corral)
                needToCheck = proc.FindExprAttribute("entrypoint") != null && !(proc is LoopProcedure);
            else
                needToCheck = impl.Name == main_proc_name;

            if (needToCheck)
            {
                var start = DateTime.Now;
                Generate(); 
                Console.WriteLine("generate: {0}s", (DateTime.Now - start).TotalSeconds);

                Console.WriteLine("Verifying {0}...", impl.Name);

                RPFP.Node cexroot = null;
                // start = DateTime.Now;
                var checkres = Check(ref cexroot);
                Console.WriteLine("check: {0}s", (DateTime.Now - start).TotalSeconds);
                switch (checkres)
                {
                    case RPFP.LBool.True:
                        Console.WriteLine("Counterexample found.\n");
                        // start = DateTime.Now;
                        Counterexample cex = CreateBoogieCounterExample(cexroot.owner, cexroot, impl);
                        // cexroot.owner.DisposeDualModel();
                        // cex.Print(0);  // TODO: just for testing
                        collector.OnCounterexample(cex, "assertion failure");
                        Console.WriteLine("cex: {0}s", (DateTime.Now - start).TotalSeconds);
                        return VC.ConditionGeneration.Outcome.Errors;
                    case RPFP.LBool.False:
                        Console.WriteLine("Procedure is correct.");
                        return Outcome.Correct;
                    case RPFP.LBool.Undef:
                        Console.WriteLine("Inconclusive result.");
                        return Outcome.ReachedBound;
                }
            }
            
            return Outcome.Inconclusive;
        }

        public void FindLabelsRec(HashSet<Term> memo, Term t, Dictionary<string, Term> res)
        {
            if (memo.Contains(t))
                return;
            if (t.IsLabel())
            {
                string l = t.LabelName();
                if (!res.ContainsKey(l))
                    res.Add(l, t.GetAppArgs()[0]);
            }
            if (t.GetKind() == TermKind.App)
            {
                var args = t.GetAppArgs();
                foreach (var a in args)
                    FindLabelsRec(memo, a, res);
            } // TODO: handle quantifiers
            
            memo.Add(t);
        }

        public void FindLabels()
        {
            labels = new Dictionary<string, Term>();
            foreach(var e in rpfp.edges){
                int id = e.number;
                HashSet<Term> memo = new HashSet<Term>();
                FindLabelsRec(memo, e.F.Formula, labels);
            }
        }

        public string CodeLabel(Absy code, StratifiedInliningInfo info, string prefix)
        {
            if (info.label2absyInv == null)
            {
                info.label2absyInv = new Dictionary<Absy, string>();
                foreach (int foo in info.label2absy.Keys)
                {
                    Absy bar = info.label2absy[foo] as Absy;
                    string lbl = foo.ToString();
                    info.label2absyInv.Add(bar, lbl);
                }
            }
            if (info.label2absyInv.ContainsKey(code))
            {
                string label = info.label2absyInv[code];
                return prefix+label;
            }
            return null;
        }

        public Term CodeLabeledExpr(RPFP rpfp, RPFP.Node root, Absy code, StratifiedInliningInfo info, string prefix)
        {
            string label = CodeLabel(code, info, prefix);
            
            if (label != null)
            {
                var res = labels[label];
                return res;
            }
            else return null;
        }

        public class LabelNotFound : Exception { };
        
        public bool CodeLabelTrue(RPFP rpfp, RPFP.Node root, Absy code, StratifiedInliningInfo info, string prefix)
        {
            string label = CodeLabel(code, info, prefix);
            
            if (label == null)
                throw new LabelNotFound();
            return root.Outgoing.labels.Contains(label);
        }

        public bool CodeLabelFalse(RPFP rpfp, RPFP.Node root, Absy code, StratifiedInliningInfo info, string prefix)
        {
            return CodeLabelTrue(rpfp, root, code, info, prefix);
        }

        public Counterexample CreateBoogieCounterExample(RPFP rpfp, RPFP.Node root, Implementation mainImpl)
        {
            FindLabels();
            var orderedStateIds = new List<Tuple<int, int>>();
            Counterexample newCounterexample =
              GenerateTrace(rpfp, root, orderedStateIds, mainImpl,true);
            return newCounterexample;
        }

        private Counterexample GenerateTrace(RPFP rpfp, RPFP.Node root,
                                                 List<Tuple<int, int>> orderedStateIds, Implementation procImpl, bool toplevel)
        {
            Contract.Requires(procImpl != null);

            Contract.Assert(!rpfp.Empty(root));


            var info = implName2StratifiedInliningInfo[procImpl.Name]; 
            Block entryBlock = cce.NonNull(procImpl.Blocks[0]);
            Contract.Assert(entryBlock != null);
            if (!CodeLabelFalse(rpfp, root, entryBlock, info, "+"))
                Contract.Assert(false);
            BlockSeq trace = new BlockSeq(); 
            trace.Add(entryBlock);

            var calleeCounterexamples = new Dictionary<TraceLocation, CalleeCounterexampleInfo>();
            Counterexample newCounterexample =
                GenerateTraceRec(rpfp, root, orderedStateIds, entryBlock, trace, calleeCounterexamples, info, toplevel);

            return newCounterexample;
        }

        // TODO: this is a bit cheesy. Rather than finding the argument position
        // of a relational term in a transformer by linear search, better to index this
        // somewhere, but where?
        private int TransformerArgPosition(RPFP rpfp, RPFP.Node root, Term expr)
        {
            FuncDecl rel = expr.GetAppDecl();
            string relname = rel.GetDeclName();
            var rps = root.Outgoing.F.RelParams;
            for (int i = 0; i < rps.Length; i++)
            {
                string thisname = rps[i].GetDeclName();
                if (thisname == relname)
                    return i;
            }
            return -1;
        }

        private bool EvalToFalse(RPFP rpfp, RPFP.Node root, Term expr,StratifiedInliningInfo info){
            Term res = rpfp.Eval(root.Outgoing,expr);
            return res.Equals(ctx.MkTrue());
        }
        
        private Counterexample GenerateTraceRec(
                              RPFP rpfp, RPFP.Node root,
                              List<Tuple<int, int>> orderedStateIds,
                              Block/*!*/ b, BlockSeq/*!*/ trace,
                              Dictionary<TraceLocation/*!*/, CalleeCounterexampleInfo/*!*/>/*!*/ calleeCounterexamples,
                              StratifiedInliningInfo info,
                              bool toplevel)
        {
            Contract.Requires(b != null);
            Contract.Requires(trace != null);
            Contract.Requires(cce.NonNullDictionaryAndValues(calleeCounterexamples));
            // After translation, all potential errors come from asserts.
            while (true)
            {
                CmdSeq cmds = b.Cmds;
                TransferCmd transferCmd = cce.NonNull(b.TransferCmd);
                for (int i = 0; i < cmds.Length; i++)
                {
                    Cmd cmd = cce.NonNull(cmds[i]);

                    // Skip if 'cmd' not contained in the trace or not an assert
                    if (cmd is AssertCmd)
                    {
                        bool is_failed_assertion = false;
                        if (NoLabels)
                            is_failed_assertion = true; // we assume only assertions on 
                        else
                            is_failed_assertion = CodeLabelTrue(rpfp, root, cmd, info, "@");

                        Counterexample newCounterexample =
                            AssertCmdToCounterexample((AssertCmd)cmd, transferCmd, trace, new Microsoft.Boogie.Model(), info.mvInfo,
                            boogieContext);
                        newCounterexample.AddCalleeCounterexample(calleeCounterexamples);
                        return newCounterexample;
                    }

                    // Counterexample generation for inlined procedures
                    AssumeCmd assumeCmd = cmd as AssumeCmd;
                    if (assumeCmd == null)
                        continue;
                    NAryExpr naryExpr = assumeCmd.Expr as NAryExpr;
                    if (naryExpr == null)
                        continue;
                    string calleeName = naryExpr.Fun.FunctionName;
                    Contract.Assert(calleeName != null);

                    // what is this crap???
                    BinaryOperator binOp = naryExpr.Fun as BinaryOperator;
                    if (binOp != null && binOp.Op == BinaryOperator.Opcode.And)
                    {
                        Expr expr = naryExpr.Args[0];
                        NAryExpr mvStateExpr = expr as NAryExpr;
                        if (mvStateExpr != null && mvStateExpr.Fun.FunctionName == VC.ModelViewInfo.MVState_FunctionDef.Name)
                        {
                            LiteralExpr x = mvStateExpr.Args[1] as LiteralExpr;
                            // Debug.Assert(x != null);
                            int foo = x.asBigNum.ToInt;
                            orderedStateIds.Add(new Tuple<int, int>(root.number, foo));
                        }
                    }

                    if (calleeName.EndsWith("_summary"))
                        calleeName = calleeName.Substring(0, calleeName.Length - 8);

                    if (!implName2StratifiedInliningInfo.ContainsKey(calleeName) && !calleeName.EndsWith("_summary"))
                        continue;

                    {
                        Term code = CodeLabeledExpr(rpfp, root, cmd, info, "+si_fcall_");
                        int pos = TransformerArgPosition(rpfp,root,code);
                        if(pos >= 0)
                        {
                            RPFP.Node callee = root.Outgoing.Children[pos];
                            orderedStateIds.Add(new Tuple<int, int>(callee.number, VC.StratifiedVCGen.StratifiedInliningErrorReporter.CALL));
                            calleeCounterexamples[new TraceLocation(trace.Length - 1, i)] =
                                new CalleeCounterexampleInfo(
                                    cce.NonNull(GenerateTrace(rpfp, callee, orderedStateIds, 
                                                implName2StratifiedInliningInfo[calleeName].impl,false)),
                                    new List<object>());
                            orderedStateIds.Add(new Tuple<int, int>(root.number, VC.StratifiedVCGen.StratifiedInliningErrorReporter.RETURN));
                        }
                    }
                }

                GotoCmd gotoCmd = transferCmd as GotoCmd;
                if (gotoCmd != null)
                {
                    b = null;
                    foreach (Block bb in cce.NonNull(gotoCmd.labelTargets))
                    {
                        Contract.Assert(bb != null);
                        if (CodeLabelFalse(rpfp,root,bb,info,"+"))
                        {
                            trace.Add(bb);
                            b = bb;
                            break;
                        }
                    }
                    if (b != null) continue;
                }
                return null;
            }

            
        }

    }


}