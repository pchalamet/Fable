[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fabel.FSharp2Fabel

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fabel.AST
open Fabel.FSharp2Fabel.Util

let rec private transformExpr com ctx fsExpr =
    match fsExpr with
    (** ## Erased *)
    | BasicPatterns.Coerce(_targetType, Transform com ctx inpExpr) -> inpExpr
    | BasicPatterns.NewDelegate(_delegateType, Transform com ctx delegateBodyExpr) -> delegateBodyExpr
    // TypeLambda is a local generic lambda
    // e.g, member x.Test() = let typeLambda x = x in typeLambda 1, typeLambda "A"
    | BasicPatterns.TypeLambda (_genArgs, Transform com ctx lambda) -> lambda

    | BasicPatterns.ILAsm (_asmCode, _typeArgs, argExprs) ->
        printfn "ILAsm detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        match argExprs with
        | [] -> Fabel.Value Fabel.Null |> makeExpr com ctx fsExpr
        | [Transform com ctx expr] -> expr
        | exprs -> Fabel.Sequential (List.map (transformExpr com ctx) exprs)
                    |> makeExpr com ctx fsExpr

    (** ## Flow control *)
    | BasicPatterns.FastIntegerForLoop(Transform com ctx start, Transform com ctx limit, body, isUp) ->
        match body with
        | BasicPatterns.Lambda (BindIdent com ctx (newContext, ident), body) ->
            Fabel.For (ident, start, limit, com.Transform newContext body, isUp)
            |> Fabel.Loop |> makeExpr com ctx fsExpr
        | _ -> failwithf "Unexpected loop in %A: %A" fsExpr.Range fsExpr

    | BasicPatterns.WhileLoop(Transform com ctx guardExpr, Transform com ctx bodyExpr) ->
        Fabel.While (guardExpr, bodyExpr)
        |> Fabel.Loop |> makeExpr com ctx fsExpr

    // This must appear before BasicPatterns.Let
    | ForOf (BindIdent com ctx (newContext, ident), Transform com ctx value, body) ->
        Fabel.ForOf (ident, value, transformExpr com newContext body)
        |> Fabel.Loop |> makeExpr com ctx fsExpr

    (** Values *)
    | BasicPatterns.Const(value, _typ) ->
        makeExpr com ctx fsExpr (makeConst value)

    | BasicPatterns.BaseValue _type ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.Super)

    | BasicPatterns.ThisValue _type ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.This)

    | BasicPatterns.Value thisVar when thisVar.IsMemberThisValue ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.This)

    | BasicPatterns.Value v ->
        if not v.IsModuleValueOrMember then
            let (GetIdent com ctx ident) = v
            upcast ident
        else
            // ctx.parentEntities
            // |> List.exists (fun x -> x.IsEffectivelySameAs v.EnclosingEntity)
            // |> function
            // | true -> upcast makeSanitizedIdent ctx Fabel.UnknownType v.DisplayName
            // | false ->
            let typeRef =
                makeTypeFromDef com v.EnclosingEntity
                |> Fabel.TypeRef |> Fabel.Value
            Fabel.Get (Fabel.Expr typeRef, makeLiteral v.DisplayName)
            |> makeExpr com ctx fsExpr

    | BasicPatterns.DefaultValue (FabelType com typ) ->
        let valueKind =
            match typ with
            | Fabel.PrimitiveType Fabel.Boolean -> Fabel.BoolConst false
            | Fabel.PrimitiveType (Fabel.Number _) -> Fabel.IntConst 0
            | _ -> Fabel.Null
        makeExpr com ctx fsExpr (Fabel.Value valueKind)

    (** ## Assignments *)
    // TODO: Possible optimization if binding to another ident (let x = y), just replace it in the ctx
    | BasicPatterns.Let((BindIdent com ctx (newContext, ident) as var,
                            Transform com ctx value), body) ->
        let body = transformExpr com newContext body
        let assignment = Fabel.VarDeclaration (ident, value, var.IsMutable) |> makeExpr com ctx fsExpr
        match body.Kind with
        // Check if this this is just a wrapper to a call as it happens in pipelines
        // e.g., let x = 5 in fun y -> methodCall x y
        | Fabel.Lambda (lambdaArgs,
                        (ExprKind (Fabel.Apply (eBody, ReplaceArgs [ident,value] args, isCons)) as e),
                        Fabel.Immediate, false) ->
            Fabel.Lambda (lambdaArgs,
                Fabel.Expr (Fabel.Apply (eBody, args, isCons), e.Type, ?range=e.Range),
                Fabel.Immediate, false) |> makeExpr com ctx fsExpr
        | _ -> makeSequential [assignment; body]

    | BasicPatterns.LetRec(recursiveBindings, body) ->
        let newContext, idents =
            recursiveBindings
            |> List.foldBack (fun (var, _) (accContext, accIdents) ->
                let (BindIdent com accContext (newContext, ident)) = var
                newContext, (ident::accIdents)) <| (ctx, [])
        let assignments =
            recursiveBindings
            |> List.map2 (fun ident (var, Transform com ctx binding) ->
                Fabel.VarDeclaration (ident, binding, var.IsMutable)
                |> makeExpr com ctx fsExpr) idents
        assignments @ [transformExpr com newContext body] 
        |> makeSequential

    (** ## Applications *)
    | BasicPatterns.TraitCall (_sourceTypes, traitName, _typeArgs, _typeInstantiation, argExprs) ->
        printfn "TraitCall detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        makeGetApply (transformExpr com ctx argExprs.Head) traitName
                     (List.map (transformExpr com ctx) argExprs.Tail)
        |> makeExpr com ctx fsExpr

    // TODO: Check `inline` annotation?
    // TODO: Watch for restParam attribute
    | BasicPatterns.Call(callee, meth, _typeArgs1, _typeArgs2, args) ->
        makeCall com ctx fsExpr callee meth args

    | BasicPatterns.Application(Transform com ctx expr, _typeArgs, args) ->
        makeApply ctx expr (List.map (transformExpr com ctx) args)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.IfThenElse (Transform com ctx guardExpr, Transform com ctx thenExpr, Transform com ctx elseExpr) ->
        Fabel.IfThenElse (guardExpr, thenExpr, elseExpr)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.TryFinally (BasicPatterns.TryWith(body, _, _, catchVar, catchBody),finalBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) (Some finalBody)

    | BasicPatterns.TryFinally (body, finalBody) ->
        makeTryCatch com ctx fsExpr body None (Some finalBody)

    | BasicPatterns.TryWith (body, _, _, catchVar, catchBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) None

    | BasicPatterns.Sequential (Transform com ctx first, Transform com ctx second) ->
        makeSequential [first; second]

    (** ## Lambdas *)
    | BasicPatterns.Lambda (var, body) ->
        makeLambda com ctx (Some fsExpr.Range) [var] body

    (** ## Getters and Setters *)
    | BasicPatterns.ILFieldGet (callee, typ, fieldName) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Check if it's FSharpException
    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldGet (callee, FabelType com calleeType, FieldName fieldName) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Get (callee, makeLiteral fieldName)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.TupleGet (_tupleType, tupleElemIndex, Transform com ctx tupleExpr) ->
        Fabel.Get (tupleExpr, makeLiteral tupleElemIndex)
        |> makeExpr com ctx fsExpr

    // Single field: Item; Multiple fields: Item1, Item2...
    | BasicPatterns.UnionCaseGet (Transform com ctx unionExpr, FabelType com unionType, unionCase, FieldName fieldName) ->
        match unionType with
        | ErasedUnion | OptionUnion -> unionExpr
        | ListUnion -> failwith "TODO"
        | OtherType ->
            Fabel.Get (unionExpr, makeLiteral fieldName)
            |> makeExpr com ctx fsExpr

    | BasicPatterns.ILFieldSet (callee, typ, fieldName, value) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldSet (callee, FabelType com calleeType, FieldName fieldName, Transform com ctx value) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Set (callee, Some (makeLiteral fieldName), value)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.UnionCaseTag (Transform com ctx unionExpr, _unionType) ->
        Fabel.Get (unionExpr, makeLiteral "Tag")
        |> makeExpr com ctx fsExpr

    // We don't need to check if this an erased union, as union case values are only set
    // in constructors, which are ignored for erased unions
    | BasicPatterns.UnionCaseSet (Transform com ctx unionExpr, _type, _case, FieldName caseField, Transform com ctx valueExpr) ->
        Fabel.Set (unionExpr, Some (makeLiteral caseField), valueExpr)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.ValueSet (GetIdent com ctx valToSet, Transform com ctx valueExpr) ->
        Fabel.Set (valToSet, None, valueExpr)
        |> makeExpr com ctx fsExpr

    (** Instantiation *)
    | BasicPatterns.NewArray(FabelType com typ, argExprs) ->
        match typ with
        | Fabel.PrimitiveType (Fabel.TypedArray numberKind) -> failwith "TODO: NewArray args"
        | _ -> Fabel.Value (Fabel.ArrayConst (argExprs |> List.map (transformExpr com ctx)))
        |> makeExpr com ctx fsExpr

    | BasicPatterns.NewTuple(_, argExprs) ->
        Fabel.Value (Fabel.ArrayConst (argExprs |> List.map (transformExpr com ctx)))
        |> makeExpr com ctx fsExpr

    | BasicPatterns.ObjectExpr(_objType, _baseCallExpr, _overrides, interfaceImplementations) ->
        failwith "TODO"

    // TODO: Check for erased constructors with property assignment (Call + Sequential)
    | BasicPatterns.NewObject(meth, _typeArgs, args) ->
        makeCall com ctx fsExpr None meth args

    // TODO: Check if it's FSharpException
    // TODO: Create constructors for Record and Union types
    | BasicPatterns.NewRecord(FabelType com recordType, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        Fabel.Apply (makeTypeRef recordType, argExprs, true)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.NewUnionCase(FabelType com unionType, unionCase, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        match unionType with
        | ErasedUnion | OptionUnion ->
            match argExprs with
            | [] -> Fabel.Value Fabel.Null |> makeExpr com ctx fsExpr
            | [expr] -> expr
            | _ -> failwithf "Erased Union Cases must have one single field: %A" unionType
        | ListUnion ->
            match unionCase.Name with
            | "Cons" -> Fabel.Apply (Fabel.Value (Fabel.CoreModule "List") |> Fabel.Expr,
                            (makeLiteral "Cons")::argExprs, true)
            | _ -> Fabel.Value Fabel.Null
            |> makeExpr com ctx fsExpr
        | OtherType ->
            // Include Tag name in args
            let argExprs = (makeLiteral unionCase.Name)::argExprs
            Fabel.Apply (makeTypeRef unionType, argExprs, true)
            |> makeExpr com ctx fsExpr

    (** ## Type test *)
    | BasicPatterns.TypeTest (FabelType com typ as fsTyp, Transform com ctx expr) ->
        makeTypeTest typ expr |> makeExpr com ctx fsExpr

    | BasicPatterns.UnionCaseTest (Transform com ctx unionExpr, FabelType com unionType, unionCase) ->
        match unionType with
        | ErasedUnion ->
            if unionCase.UnionCaseFields.Count <> 1 then
                failwithf "Erased Union Cases must have one single field: %A" unionType
            else
                let typ = makeType com unionCase.UnionCaseFields.[0].FieldType
                makeTypeTest typ unionExpr
        | OptionUnion | ListUnion ->
            let opKind =
                if (unionCase.Name = "None" || unionCase.Name = "Empty")
                then BinaryEqual
                else BinaryUnequal
            makeBinOp opKind unionExpr (Fabel.Value Fabel.Null |> Fabel.Expr)
        | OtherType ->
            let left = Fabel.Get (unionExpr, makeLiteral "Tag") |> Fabel.Expr
            let right = makeLiteral unionCase.Name
            makeBinOp BinaryEqualStrict left right
        |> makeExpr com ctx fsExpr

    (** Pattern Matching *)
    | BasicPatterns.DecisionTreeSuccess (decIndex, decBindings) ->
        match Map.tryFind decIndex ctx.decisionTargets with
        | None -> failwith "Missing decision target"
        // If we get a reference to a function, call it
        | Some (TargetRef targetRef) ->
            Fabel.Apply (targetRef, (decBindings |> List.map (transformExpr com ctx)), false)
            |> makeExpr com ctx fsExpr
        // If we get an implementation without bindings, just transform it
        | Some (TargetImpl ([], Transform com ctx decBody)) -> decBody
        // If we have bindings, create the assignments
        | Some (TargetImpl (decVars, decBody)) ->
            let newContext, assignments =
                List.foldBack2 (fun var (Transform com ctx binding) (accContext, accAssignments) ->
                    let (BindIdent com accContext (newContext, ident)) = var
                    let assignment = Fabel.Expr (Fabel.VarDeclaration (ident, binding, var.IsMutable))
                    newContext, (assignment::accAssignments)) decVars decBindings (ctx, [])
            assignments @ [transformExpr com newContext decBody]
            |> makeSequential

    | BasicPatterns.DecisionTree(decisionExpr, decisionTargets) ->
        let rec getTargetRefsCount map = function
            | BasicPatterns.IfThenElse (_, thenExpr, elseExpr) ->
                let map = getTargetRefsCount map thenExpr
                getTargetRefsCount map elseExpr
            | BasicPatterns.DecisionTreeSuccess (idx, _) ->
                match (Map.tryFind idx map) with
                | Some refCount -> Map.remove idx map |> Map.add idx (refCount + 1)
                | None -> Map.add idx 1 map
            | _ as e ->
                failwithf "Unexpected DecisionTree branch in %A: %A" e.Range e
        let targetRefsCount = getTargetRefsCount (Map.empty<int,int>) decisionExpr
        // Convert targets referred more than once into functions
        // and just pass the F# implementation for the others
        let assignments =
            targetRefsCount
            |> Map.filter (fun k v -> v > 1)
            |> Map.fold (fun acc k v ->
                let decTargetVars, decTargetExpr = decisionTargets.[k]
                let lambda = makeLambda com ctx None decTargetVars decTargetExpr
                let ident = makeSanitizedIdent ctx lambda.Type (sprintf "target%i" k)
                Map.add k (ident, lambda) acc) (Map.empty<_,_>)
        let decisionTargets =
            targetRefsCount |> Map.map (fun k v ->
                match v with
                | 1 -> TargetImpl decisionTargets.[k]
                | _ -> TargetRef (fst assignments.[k]))
        let newContext = { ctx with decisionTargets = decisionTargets }
        if assignments.Count = 0 then
            transformExpr com newContext decisionExpr
        else
            let assignments =
                assignments
                |> Seq.map (fun pair -> pair.Value)
                |> Seq.map (fun (ident, lambda) -> Fabel.VarDeclaration (ident, lambda, false))
                |> Seq.map Fabel.Expr
                |> Seq.toList
            Fabel.Sequential (assignments @ [transformExpr com newContext decisionExpr])
            |> makeExpr com ctx fsExpr

    (** Not implemented *)
    | BasicPatterns.Quote _ // (quotedExpr)
    | BasicPatterns.AddressOf _ // (lvalueExpr)
    | BasicPatterns.AddressSet _ // (lvalueExpr, rvalueExpr)
    | _ -> failwithf "Cannot compile expression in %A: %A" fsExpr.Range fsExpr

type private DeclInfo() =
    let mutable child: Fabel.Entity option = None
    member val decls = ResizeArray<Fabel.Declaration>()
    member val childDecls = ResizeArray<Fabel.Declaration>()
    member val extMods = ResizeArray<Fabel.ExternalEntity>()
    // The F# compiler considers class methods as children of the enclosing module, correct that
    member self.AddMethod (meth: FSharpMemberOrFunctionOrValue, methDecl: Fabel.Declaration) =
        let methParentFullName =
            sanitizeEntityName meth.EnclosingEntity.FullName
        match child with
        | Some x when x.FullName = methParentFullName ->
            self.childDecls.Add methDecl
        | _ -> self.decls.Add methDecl
    member self.ClearChild () =
        if child.IsSome then
            Fabel.EntityDeclaration (child.Value, List.ofSeq self.childDecls)
            |> self.decls.Add
        child <- None
        self.childDecls.Clear ()
    member self.AddChild (newChild, childDecls, childExtMods) =
        self.ClearChild ()
        child <- Some newChild
        self.childDecls.AddRange childDecls
        self.extMods.AddRange childExtMods
    
let private transformMemberDecl
    (com: IFabelCompiler) ctx (declInfo: DeclInfo) (meth: FSharpMemberOrFunctionOrValue)
    (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
    let memberKind =
        let name = meth.DisplayName
        // TODO: Another way to check module values?
        // TODO: Mutable module values
        if meth.EnclosingEntity.IsFSharpModule then
            match meth.XmlDocSig.[0] with
            | 'P' -> Fabel.Getter name
            | _ -> Fabel.Method name
        else
            // TODO: Check overloads
            if meth.IsImplicitConstructor then Fabel.Constructor
            elif meth.IsPropertyGetterMethod then Fabel.Getter name
            elif meth.IsPropertySetterMethod then Fabel.Setter name
            else Fabel.Method name
    let ctx, args =
        let args = if meth.IsInstanceMember then Seq.skip 1 args |> Seq.toList else args
        match args with
        | [] -> ctx, []
        | [[singleArg]] ->
            makeType com singleArg.FullType |> function
            | Fabel.PrimitiveType Fabel.Unit -> ctx, []
            | _ -> let (BindIdent com ctx (ctx, arg)) = singleArg in ctx, [arg]
        | _ ->
            List.foldBack (fun tupledArg (accContext, accArgs) ->
                match tupledArg with
                | [] -> failwith "Unexpected empty tupled in curried arguments"
                | [nonTupledArg] ->
                    let (BindIdent com accContext (newContext, arg)) = nonTupledArg
                    newContext, arg::accArgs
                | _ ->
                    // The F# compiler "untuples" the args in methods
                    let newContext, untupledArg = makeLambdaArgs com ctx tupledArg
                    newContext, untupledArg@accArgs
            ) args (ctx, []) // TODO: Reset Context?
    let entMember = 
        Fabel.Member(memberKind,
            Fabel.LambdaExpr (args, transformExpr com ctx body, Fabel.Immediate, hasRestParams meth),
            meth.Attributes |> Seq.choose (makeDecorator com) |> Seq.toList,
            meth.Accessibility.IsPublic, not meth.IsInstanceMember)
        |> Fabel.MemberDeclaration
    declInfo.AddMethod (meth, entMember)
    declInfo
   
let rec private transformEntityDecl
    (com: IFabelCompiler) ctx (declInfo: DeclInfo) ent subDecls =
    match ent with
    | WithAttribute "Global" _ ->
        Fabel.GlobalModule ent.FullName
        |> declInfo.extMods.Add
        declInfo
    | WithAttribute "Import" args ->
        match args with
        | [:? string as modName] ->
            Fabel.ImportModule(ent.FullName, modName)
            |> declInfo.extMods.Add
            declInfo
        | _ -> failwith "Import attributes must have a single string argument"
    | WithAttribute "Erase" _ | AbstractEntity _ ->
        declInfo // Ignore 
    | _ ->
        let ctx = { ctx with parentEntities = ent::ctx.parentEntities }
        let childDecls, childExtMods = transformDeclarations com ctx subDecls
        declInfo.AddChild (com.GetEntity ent, childDecls, childExtMods)
        declInfo

and private transformDeclarations (com: IFabelCompiler) ctx decls =
    let declInfo =
        decls |> List.fold (fun (declInfo: DeclInfo) decl ->
            match decl with
            | FSharpImplementationFileDeclaration.Entity (e, sub) ->
                transformEntityDecl com ctx declInfo e sub
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (meth, args, body) ->
                transformMemberDecl com ctx declInfo meth args body
            | FSharpImplementationFileDeclaration.InitAction (Transform com ctx expr) ->
                declInfo.decls.Add(Fabel.ActionDeclaration expr); declInfo
        ) (DeclInfo())
    declInfo.ClearChild ()
    List.ofSeq declInfo.decls, List.ofSeq declInfo.extMods
        
let transformFiles (com: ICompiler) (fsProj: FSharpCheckProjectResults) =
    let emptyContext parent = {
        scope = []
        decisionTargets = Map.empty<_,_>
        parentEntities = match parent with Some p -> [p] | None -> [] 
    }
    let rec getRootDecls rootEnt = function
        | [FSharpImplementationFileDeclaration.Entity (e, subDecls)]
            when e.IsNamespace || e.IsFSharpModule ->
            getRootDecls (Some e) subDecls
        | _ as decls -> rootEnt, decls
    let entities =
        System.Collections.Concurrent.ConcurrentDictionary<string, Fabel.Entity>()
    let fileNames =
        fsProj.AssemblyContents.ImplementationFiles
        |> Seq.map (fun x -> x.FileName) |> Set.ofSeq
    let com =
        { new IFabelCompiler with
            member fcom.Transform ctx fsExpr =
                transformExpr fcom ctx fsExpr
            member fcom.GetInternalFile tdef =
                let file = tdef.DeclarationLocation.FileName
                if Set.contains file fileNames then Some file else None
            member fcom.GetEntity tdef =
                entities.GetOrAdd (tdef.FullName, fun _ -> makeEntity fcom tdef)
        interface ICompiler with
            member __.Options = com.Options }    
    fsProj.AssemblyContents.ImplementationFiles
    |> List.map (fun file ->
        let rootEnt, rootDecls = getRootDecls None file.Declarations
        let rootDecls, extDecls = transformDeclarations com (emptyContext rootEnt) rootDecls
        match rootDecls with
        | [] -> Fabel.File(file.FileName, None, extDecls)
        | _ ->
            match rootEnt with
            | Some rootEnt -> makeEntity com rootEnt
            | None -> Fabel.Entity.CreateRootModule file.FileName
            |> fun rootEnt -> Some(Fabel.EntityDeclaration(rootEnt, rootDecls))
            |> fun rootDecl -> Fabel.File(file.FileName, rootDecl, extDecls))
