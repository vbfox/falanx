namespace Falanx.Proto.Codec.Json
open System
open Fleece
open Fleece.Newtonsoft
open System.Collections.Generic
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Quotations.Patterns
open Falanx.Machinery
open System.Runtime.CompilerServices
open ProviderImplementation.ProvidedTypes.UncheckedQuotations

[<CLIMutable>]
type Result2 =
    { mutable url : string option 
      mutable title : int option }
      
[<CLIMutable>]
type Result3 =
    { mutable url : string option 
      mutable title : int option
      mutable snippets : ResizeArray<string> }
      
module Quotations =
    let rec traverseForCall q = [
      match q with
      | Patterns.Call _ as call -> yield call
      | Quotations.ExprShape.ShapeLambda(v, body)  -> yield! traverseForCall body
      | Quotations.ExprShape.ShapeCombination(comb, args) -> 
          for ex in args do yield! traverseForCall ex
      | Quotations.ExprShape.ShapeVar _ -> () ]

module temp =
    open FSharpPlus
    open System.Reflection
    open Newtonsoft.Json.Linq
    
    let cleanUpTypeName (str:string) =
        let sb = Text.StringBuilder(str)
        sb.Replace("System.", "")
          .Replace("Microsoft.FSharp.Core.", "")
          .Replace("Collections.Generic.","")
          .Replace("Falanx.JsonCodec.", "")
          .Replace("Newtonsoft.Json.Linq.", "") 
          .Replace("`2", "")
          .Replace("`1", "")
          .Replace("FSharp", "")
          .Replace("Int32", "int") |> ignore
        sb.ToString()
            
    let simpleTypeFormatter (a:System.IO.TextWriter) (b:Type) =
        b.ToString()
        |> cleanUpTypeName
        |> a.Write
        
    let sprintfsimpleTypeFormatter () (b:Type) =
        b.ToString()
        |> cleanUpTypeName

    let x<'T> : 'T = Unchecked.defaultof<'T>
    let knownNamespaces = ["System"; "System.Collections.Generic"; "Fleece.Newtonsoft"; "Microsoft.FSharp.Core"; "Newtonsoft.Json.Linq"] |> Set.ofList
    let qs = ProviderImplementation.ProvidedTypes.QuotationSimplifier(true)                          
     
    let typeName (m : Type) =
        Microsoft.FSharp.Compiler.PrettyNaming.DemangleGenericTypeName  m.Name
        
    let rec generic (m : Type) = 
        if m.IsGenericType then   
            sprintf "%s%s" (typeName m) (m.GetGenericArguments() |> genericArgs)
        else
            typeName m
            
    and genericArgs (args : Type []) = 
        let x = args |> Array.map generic |> String.concat ", "
        sprintf "[%s]" x
    
    let rec fsSig (t : Type) = 
        if Reflection.FSharpType.IsFunction t then 
            let a,b = Reflection.FSharpType.GetFunctionElements t
            sprintf "%s -> %s" (fsSig a) (fsSig b)
        elif Reflection.FSharpType.IsTuple t then 
            let types = Reflection.FSharpType.GetTupleElements t
            let str = types |> Array.map fsSig |> String.concat ", " 
            sprintf "(%i, %s)" types.Length str
        else    
            generic t
    
    let createLambdaRecord recordType =
        //Lambda (u, Lambda (t, NewRecord (Result2, u, t))
        let recordFields = Reflection.FSharpType.GetRecordFields(recordType)
        let recordVars =
            recordFields
            |> List.ofArray
            |> List.map(fun pi -> Var(pi.Name, pi.PropertyType))

        let thing = 
            List.foldBack (fun v acc -> Expr.Lambda(v,acc)) recordVars (Expr.NewRecord(recordType, recordVars |> List.map (Expr.Var) ))
        thing
              
    let  getFunctionReturnType typ =
        let rec loop typ = 
            if FSharp.Reflection.FSharpType.IsFunction typ then
                let domain, range = FSharp.Reflection.FSharpType.GetFunctionElements typ
                loop range
            else typ  
        let result = loop typ
        result
        
    let  getFunctionReturnType2 typ =            
        let rec loop typ = 
            if FSharp.Reflection.FSharpType.IsFunction typ then
                let domain, range = FSharp.Reflection.FSharpType.GetFunctionElements typ
                range
            else typ
        let result = loop typ
        result
        
    let rec getLambdaElements typ = [
        let returnOrLoop t = [
            if Reflection.FSharpType.IsFunction t then 
                yield! getLambdaElements t
            else yield t ]
        let domain, rest = Reflection.FSharpType.GetFunctionElements(typ)
        yield! returnOrLoop domain
        yield! returnOrLoop rest
        ]
        
    let makeFunctionTypeFromElements (xs: Type list) =
        let newFunction = xs |> List.reduceBack (fun a b -> Reflection.FSharpType.MakeFunctionType(a, b) )
        newFunction
        
    
    let callMapping (lambda:Expr) =
    
        let replaceLambdaArgs (mi: MethodInfo) (lambda:Type) =
            let lambdaReturn = getFunctionReturnType lambda
            mi.MakeGenericMethod([|lambda
                                   typeof<IReadOnlyDictionary<string,JsonValue>>
                                   typeof<string>
                                   lambdaReturn
                                   typeof<string>
                                   typeof<JToken>|])

        //mapping f: 'a -> ('b -> Result<'a,'c>) * ('d -> IReadOnlyDictionary<'e,'f>)
        
        //As an expression it looks like:
        //Call (None, mapping, [Lambda (u, NewRecord (Result, u))])

        //in generic types or a C# point of view its defined:
        //Tuple<FSharpFunc<b, FSharpResult<a, c>>, FSharpFunc<d, IReadOnlyDictionary<e, f>>> mapping<a, b, c, d, e, f>(a f)

        //for a Record3 we would have a type signature like this:
        //mapping<Option<String> -> Option<String> -> Result3, IReadOnlyDictionary<String, JToken>, String, Result3, String, JToken>
        
        let mappingMethodInfo = Expr.methoddefof <@ withFields<_,IReadOnlyDictionary<string,JsonValue>,string,_,string,JToken> @>
        
        let lambdaType = lambda.Type
        
        let mappingMethodInfoFull = replaceLambdaArgs mappingMethodInfo lambdaType
        let mappingMiFullArgs = mappingMethodInfoFull.GetGenericArguments()
        
        //usage would normally be a lambda piped into mapping:
        //fun f : Option<String> -> Option<String> -> Result2 
        //|> mapping<Option<String> -> Option<String> -> Result2, IReadOnlyDictionary<String, JToken>, String, Result2, String, JToken> 
        
        //FSharpFunc`2[FSharpFunc`2[Option`1[String],FSharpFunc`2[Option`1[String],Result2]],Tuple`2[FSharpFunc`2[IReadOnlyDictionary`2[String,JToken],FSharpResult`2[FSharpFunc`2[FSharpOption`1[String],FSharpFunc`2[FSharpOption`1[String],Result2]],String]],FSharpFunc`2[IReadOnlyDictionary`2[String,NJToken]]]]

        //after having f applied to mapping:
        //(IReadOnlyDictionary<string,JsonValue> -> Result<(string option -> string option -> Result2),string>) * (Result2 -> IReadOnlyDictionary<string,JToken>
        
        //generic signature:
        //System.Tuple`2[FSharpFunc`2[IReadOnlyDictionary`2[String, JToken],FSharpResult`2[FSharpFunc`2[FSharpOption`1[String], FSharpFunc`2[FSharpOption`1[String],Result2]],String]],FSharpFunc`2[Result3,IReadOnlyDictionary`2[JToken]]]

        let f = Var("f", lambdaType)
        let call = Expr.Call(mappingMethodInfoFull, [Expr.Var(f)])
        let lambda = Expr.Lambda(f, call)
        lambda        
        
    let callPipeRight (arg:Expr) (func:Expr) =
        let methodInfoGeneric = Expr.methoddefof<@ (|>) @>
        let funcTypeReturn = getFunctionReturnType func.Type
        let methodInfoTyped = methodInfoGeneric.MakeGenericMethod([|arg.Type; funcTypeReturn|])
        let expr = Expr.Call(methodInfoTyped, [arg; func])
        expr
        

    //this needs to be done at the Expt -> AST level to remove the extra lambda call  
//    let callPipeRight2 (arg:Expr) (func:Expr) =
//        let methodInfoGeneric = Expr.methoddefof<@ (|>) @>
//        let funcTypeReturn = getFunctionReturnType func.Type
//
//        let methodInfoTyped = methodInfoGeneric.MakeGenericMethod([|arg.Type; funcTypeReturn|])
//        let expr = 
//            match func with 
//            | Lambda(_, Call(Some obj,minfo,args)) ->  Expr.Call(methodInfoTyped, [arg; Expr.Call(obj,minfo,args |> List.truncate (args.Length - 1))])
//            | Lambda(_, Call(None,minfo,args)) ->  Expr.CallUnchecked(methodInfoTyped, [arg; Expr.CallUnchecked(minfo,args |> List.truncate (args.Length - 1))])
//            | _ -> Expr.Call(methodInfoTyped, [arg; func])
//        expr

        
    let callJfieldopt (recordType: Type) propertyInfo (fieldType: Type ) (nextFieldType: Type option) =
        //
        // fun u t -> { url = u; title= t }
        // |> mapping<Option<String> -> Option<int> -> Result2, IReadOnlyDictionary<String, JToken>, String, Result2, String, JToken>
        // |> jfieldopt<Result2, string, Option<int> -> Result2> "url"   (fun x -> x.url)
        // |> jfieldopt<Result2, int   , Result2>                "title" (fun x -> x.title)
        let remainingTypeExpression = 
            match nextFieldType with 
            | Some nextFieldType ->
                Reflection.FSharpType.MakeFunctionType(typedefof<Option<_>>.MakeGenericType(nextFieldType), recordType)
            | None -> recordType
        
        let jfieldoptMethodInfo = Expr.methoddefof <@ jfieldOpt<_,string,_> x x x @>
        //                          Record    ; fieldType; nextFieldType -> Record
        let jFieldTypeArguments = [|recordType; fieldType; remainingTypeExpression|]
        let jfieldoptMethodInfoTyped = jfieldoptMethodInfo.MakeGenericMethod jFieldTypeArguments
        let formattedjfieldoptMethodInfoTyped = sprintf "%s" (jfieldoptMethodInfoTyped.ToString() |> cleanUpTypeName)
        
        let fieldName = Expr.Value "url"
        
        let xvar = Var("x", recordType)
        
        let getter = Expr.Lambda(xvar, Expr.PropertyGet( Expr.Var xvar, propertyInfo) )

        // IReadOnlyDictionary<String, JToken> -> Result<Option<fieldType> -> Option<nextFieldType> -> Record, String>
        let domain = typeof<IReadOnlyDictionary<String, JToken>>
        let range =
            let currentField = typedefof<Option<_>>.MakeGenericType(fieldType)
            
            let functionType =
                match nextFieldType with 
                | Some nextFieldType ->
                    let nextField = typedefof<Option<_>>.MakeGenericType(nextFieldType)
                    makeFunctionTypeFromElements [currentField; nextField; recordType]
                | None ->
                    Reflection.FSharpType.MakeFunctionType(currentField, recordType)
            
            //IReadOnlyDictionary<String,JToken> -> Result<Option<String> -> Option<Int32>   -> Result2, String>
            //IReadOnlyDictionary<String,JToken> -> Result<Option<Int32>                     -> Result2, String>
            typedefof<Result<_,_>>.MakeGenericType([|functionType; typeof<string>|])
            
        let decoderType = Reflection.FSharpType.MakeFunctionType(domain, range)
        let formattedDecoderType = sprintf "%a" sprintfsimpleTypeFormatter decoderType
        //decoder is:
        //IReadOnlyDictionary<String,JToken> -> Result< Result2 -> Option<String> -> Option<Int32>, String>
        
        //decoder should be:
        //IReadOnlyDictionary<String,JToken> -> Result< Option<String> -> Option<Int32> -> Result2, String>
        //IReadOnlyDictionary<String,JToken> -> Result<Result2, String>
        let decoder = Var("decode", decoderType)
        
        // Record -> IReadOnlyDictionary<String, JToken>
        let encoderType = Reflection.FSharpType.MakeFunctionType(recordType, typeof<IReadOnlyDictionary<String, JToken>>)
        let formattedEncoderType = sprintf "%a" sprintfsimpleTypeFormatter encoderType
        let encoder = Var("encode", encoderType)
        
        // decoder * encoder
        // ( IReadOnlyDictionary<String, JToken> -> Result<Option<fieldType> -> Option<nextFieldType> -> Record, String>) * (Record -> IReadOnlyDictionary<String, JToken> )
        let codecType = Reflection.FSharpType.MakeTupleType [|decoderType; encoderType|]
        let formattedCodecType = sprintf "%a" sprintfsimpleTypeFormatter codecType
        let codec = Var("codec", codecType)
        
        Expr.Lambda(codec,
            Expr.Let(decoder, Expr.TupleGet(Expr.Var codec, 0),
                Expr.Let(encoder, Expr.TupleGet(Expr.Var codec, 1),
                    Expr.Call(jfieldoptMethodInfoTyped, [fieldName; getter; Expr.Var decoder; Expr.Var encoder]))))
                    
                    
    type TypeChainCache<'a> = 
        | Lookup of ConditionalWeakTable<Type,TypeChainCache<'a>>
        | Value of 'a
        member x.GetOrAdd(tl : Type list, f) = 
            match tl with 
            | h :: t -> 
                match x with 
                | Lookup d -> 
                    let v = 
                        let scc,v = d.TryGetValue(h)
                        if scc then
                            v
                        else
                            let v = 
                                match t with 
                                | [] -> f() |> Value
                                | _ -> ConditionalWeakTable<Type,TypeChainCache<'a>>() |> Lookup
                            d.Add(h,v)
                            v
                    v.GetOrAdd(t,f)
                | Value v -> failwith "Bad type list length"
            | [] -> 
                match x with 
                | Lookup d -> failwith "Bad type list length"
                | Value v -> v
        static member Create() = ConditionalWeakTable<Type,TypeChainCache<'a>>() |> Lookup
    type TypeTemplate<'a,'b>() =
        //static let cache = TypeChainCache.Create()
        static let cache = System.Collections.Generic.Dictionary<string,TypeChainCache<_>>()
        static member bleh ([<ReflectedDefinition(true)>] f : Expr<'a -> 'b>) = printfn "%A" f
        static member create ([<ReflectedDefinition(false)>] f : Expr<'a -> 'b>) : string -> Type list -> ('a -> 'b)  = 
            fun cacheName -> 
                let cache = 
                    let scc,v = cache.TryGetValue cacheName
                    if scc then 
                        v
                    else
                        let v = TypeChainCache.Create()
                        cache.Add(cacheName,v)
                        v
                let f() =
                    let v = 
                        let rec extractCall e = 
                            match e with
                            | Patterns.Lambda(_,body) -> extractCall body
                            | Patterns.Call(_o,minfo,_args) -> minfo
                            | _ -> failwithf "Expression not of expected form %A" e
                        let rec replaceMethod minfo e = 
                            match e with
                            | Patterns.Lambda(v,body) -> Expr.Lambda(v, replaceMethod minfo body)
                            | Patterns.Call(Some o,_,args) -> Expr.Call(o,minfo,args)
                            | Patterns.Call(None,_,args) -> Expr.Call(minfo,args)
                            | _ -> failwithf "Expression not of expected form %A" e
                        match f with
                        | Patterns.Lambda (_,e) -> 
                            let minfo = extractCall e
                            let makeMethod =
                                if minfo.IsGenericMethod then
                                    let minfodef = minfo.GetGenericMethodDefinition()
                                    fun types -> 
                                        minfodef.MakeGenericMethod(types |> Seq.toArray)
                                else
                                    fun _ -> minfo
                            fun types -> 
                                let method = makeMethod types
                                let lambda = replaceMethod method f
                                FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.EvaluateQuotation lambda :?> ('a -> 'b)
                        | _ -> failwithf "Expression not of expected form %A" f
                    v
                fun types -> 
                    cache.GetOrAdd(types, fun () -> f() types)
                    
    let inline callJfieldopt3< ^a,^b,^c when (OfJson or ^b) : (static member OfJson : ^b*OfJson -> (JsonValue -> ^b ParseResult)) 
                                        and  (ToJson or ^b) : (static member ToJson : ^b*ToJson -> JsonValue) > propertyInfo =
        let getter = 
            let x = Var("x",typeof< ^a>)
            <@ %%(Expr.Lambda(x, Expr.PropertyGet(Expr.Var x,propertyInfo))) : ^a -> ^b option@>
        <@@
            fun codec ->
                let decode = fst codec
                let encode = snd codec
                Fleece.Newtonsoft.jfieldOpt "url" %getter ((decode,encode) : SplitCodec<_,_ -> ^c,_>)
        @@>

    let callJfieldoptNew (recordType: Type) propertyInfo (fieldType: Type ) (nextFieldType: Type option) =
        let typeC =
            match nextFieldType with 
            | Some t -> Reflection.FSharpType.MakeFunctionType(typedefof<Option<_>>.MakeGenericType(t), recordType)
            | None -> recordType
        TypeTemplate.create callJfieldopt3<_,string,_> "callJfieldopt3" [recordType; fieldType; typeC] propertyInfo
     


        
    let printerOfDoom() =
    
        let rec traverseQuotation f q = 
          let q = defaultArg (f q) q
          match q with     
          | ExprShape.ShapeLambda(v, body)  -> Expr.Lambda(v, traverseQuotation f body)
          | ExprShape.ShapeCombination(comb, args) -> ExprShape.RebuildShapeCombination(comb, List.map (traverseQuotation f) args )          
          | ExprShape.ShapeVar _ -> q
      
        let r =
            <@
                fun u t s -> { url = u; title= t; snippets = s }
                |> withFields
                |> jfieldOpt "url" (fun x -> x.url)
                |> jfieldOpt "title" (fun x -> x.title)
                |> jfield "snippets"  (fun x -> x.snippets)
                
            @>


        
        let result = 
            r |> traverseQuotation (fun e -> match e with
                                             | Call(_,mi,args) when mi.IsGenericMethod -> 
                                                                        printfn "Call: %s" mi.Name
                                                                        printfn "    Args: %A" args
                                                                        printfn "    Type: %a" simpleTypeFormatter e.Type
                                                                        printfn "    MI_RType: %a" simpleTypeFormatter mi.ReturnType
                                                                        mi.GetGenericArguments()
                                                                        |> Array.iteri (fun i a -> printfn "    %i: %a" i simpleTypeFormatter a)
                                                                        printfn ""
                                                                        None
                                             | Let(v,mi,_) ->
                                                 printfn "Let:\n    Var: %s\n    Type: %a\n" v.Name simpleTypeFormatter v.Type
                                                 None
                                             | Lambda(v, expr) ->
                                                printfn "Lambda:"
                                                printfn "    Var: %s\n    Type: %a" v.Name simpleTypeFormatter v.Type
                                                printfn "    expr: %A" expr
                                                printfn "    Type: %a" simpleTypeFormatter expr.Type
                                                
                                                None
                                             | _ -> None)
                                                                        
        

        ()   
        //printfn "%A" (loop r)

    let tryCode() =
        printerOfDoom()

        let recordType = typeof<Result2>
        let lambdaRecord = createLambdaRecord recordType
        let mapping = callMapping lambdaRecord
        let pipeLambdaToMapping =  callPipeRight lambdaRecord mapping
        
        let recordFields = Reflection.FSharpType.GetRecordFields(recordType) |> Array.toList
        
        
        let createRecordJFieldOpts recordFields =
            let final = recordFields |> List.last
            let pairs = recordFields |> List.pairwise
            let optionType (o: Type) =
                o.GetGenericArguments().[0]
            
            let jFieldOptions =
                pairs
                |> List.map (fun (first, second) -> callJfieldopt recordType first (optionType first.PropertyType) (Some (optionType second.PropertyType)))
            let finalJField = callJfieldopt recordType final (optionType final.PropertyType) None
            
            [ yield! jFieldOptions
              yield finalJField ]
                
            
            
        let jFieldOpts = createRecordJFieldOpts recordFields
        let allPipedFunctions = [yield lambdaRecord; yield mapping; yield! jFieldOpts]
        let foldedFunctions =
            allPipedFunctions |> List.reduce callPipeRight
                         

//        let firstJfieldOpt = callJfieldopt recordType (Expr.propertyof<@ (x:Result2).url @>) typeof<string> (Some typeof<int>)
//        let secondJFieldOpt = callJfieldopt recordType (Expr.propertyof<@ (x:Result2).title @>) typeof<int> None
//        
//        let pipeLambdaToMappingToFirstJFieldOpt = callPipeRight pipeLambdaToMapping firstJfieldOpt
//        let pipeLambdaToMappingToFirstJFieldOptToSecondJFieldOpt = callPipeRight pipeLambdaToMappingToFirstJFieldOpt secondJFieldOpt
//        
//        let qs = ProviderImplementation.ProvidedTypes.QuotationSimplifier(true)
//        let simplified = qs.Simplify pipeLambdaToMappingToFirstJFieldOptToSecondJFieldOpt
        
        let ctast, ctpt = Quotations.ToAst( foldedFunctions, knownNamespaces = knownNamespaces )
        let code = Fantomas.CodeFormatter.FormatAST(ctpt, "test", None, Fantomas.FormatConfig.FormatConfig.Default)
        //FsAst.PrintAstInfo.printAstInfo "/Users/dave.thomas/codec.fs"
        code                              
           
module test =


    let test() =

        //let original = {url = Some "John"}
        //let serialized = sprintf "%s" (string (toJson original))
        //let deserialized = parseJson<Result> serialized
        //original, serialized, deserialized
        ()
