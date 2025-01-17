(*    
    Copyright (C) 2022-2023 Raven Beutner

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*)

module ModelCheckingEntryPoint

open System
open System.IO

open FsOmegaLib.LTL

open TransitionSystemLib.TransitionSystem
open TransitionSystemLib.SymbolicSystem
open TransitionSystemLib.BooleanProgramSystem

open RunConfiguration
open HyperLTL
open HyperLTL.SymbolicHyperLTL
open ModelChecking

let private verify config (tslist : list<TransitionSystem<String>>) (hyperltl : HyperLTL<String>) (m : Mode) = 
    let res, t = ModelChecking.modelCheck config tslist hyperltl m

    if res then 
        config.Logger [ZERO; ONE] (sprintf "SAT\n")

        config.Logger [TWO; THREE; FOUR] $"=========================\nSAT\n=========================\n"
        config.Logger [TWO; THREE; FOUR] $"Time: %i{t.TotalTime}\n"
    else
        config.Logger [ZERO; ONE] (sprintf "UNSAT\n")

        config.Logger [TWO; THREE; FOUR] $"=========================\nUNSAT\n=========================\n"
        config.Logger [TWO; THREE; FOUR] $"Time: %i{t.TotalTime}\n"


let explictSystemVerification (config : Configuration) systemPaths propPath m  = 
    let sw: System.Diagnostics.Stopwatch = System.Diagnostics.Stopwatch()
    sw.Start()

    let hyperltlcontent =   
        try 
            File.ReadAllText propPath
        with 
        | _ -> raise <| AnalysisException $"Could not open/read file %s{propPath}"
                
    let tscontent = 
        systemPaths
        |> List.map (fun x -> 
            try 
                File.ReadAllText  x
            with 
            | _ -> raise <| AnalysisException $"Could not open/read file %s{x}"
        )

    config.Logger [TWO; THREE; FOUR] $"Read Input in: %i{sw.ElapsedMilliseconds}ms\n"

    sw.Restart()


    let hyperltl =
        match HyperLTL.Parser.parseNamedHyperLTL Util.ParserUtil.escapedStringParser hyperltlcontent with 
        | Result.Ok x ->
            NamedHyperLTL.toHyperLTL x
        | Result.Error err -> 
            raise <| AnalysisException $"The HyperLTL formula could not be parsed: %s{err}"
                
                
    if HyperLTL.isConsistent hyperltl |> not then
        raise <| AnalysisException "HyperLTL formula is not consistent. Aborting." 

    let tslist = 
        tscontent
        |> List.map (fun x -> 
            match TransitionSystemLib.TransitionSystem.Parser.parseTransitionSystem x with 
                | Result.Ok y -> y 
                | Result.Error msg -> 
                    raise <| AnalysisException $"The explicit-state system could not be parsed: %s{msg}"
        )

    config.Logger [TWO; THREE; FOUR] $"Parsed Input in: %i{sw.ElapsedMilliseconds}ms\n"
    
    let tslist =
        if tslist.Length > 1 then 
            if tslist.Length <> hyperltl.QuantifierPrefix.Length then 
                raise <| AnalysisException "The number of systems given does not match the property"
            tslist
        else  
            List.init (hyperltl.QuantifierPrefix.Length) (fun _ -> tslist.[0])
   
    
    tslist
    |> List.iteri (fun i ts ->
        match TransitionSystem.findError ts with 
        | None -> ()
        | Some msg -> 
            raise <| AnalysisException $"Found error in the %i{i}th system: %s{msg}"
        )
            
    hyperltl.LTLMatrix
    |> FsOmegaLib.LTL.LTL.allAtoms
    |> Set.iter (fun (x, i) ->
            if List.contains x tslist.[i].APs |> not then
                raise <| AnalysisException $"AP (%s{x}, %i{i}) is used in the HyperLTL formula, but AP %s{x} is not defined in the %i{i}th system"
            )
            
    verify config tslist hyperltl m

let nuSMVSystemVerification config systemPaths propPath m  = 
    let sw: System.Diagnostics.Stopwatch = System.Diagnostics.Stopwatch()
    sw.Start()

    let propContent = 
        try 
            File.ReadAllText propPath
        with 
            | _ -> 
                raise <| AnalysisException $"Could not open/read file %s{propPath}"

    let systemContents = 
        systemPaths 
        |> List.map (fun x -> 
                try 
                    File.ReadAllText  x
                with 
                    | _ -> 
                        raise <| AnalysisException $"Could not open/read file %s{x}"
            )

    config.Logger [TWO; THREE; FOUR] $"Read Input in: %i{sw.ElapsedMilliseconds}ms\n"

    sw.Restart()

    let plist = 
        systemContents
        |> List.map (fun s -> 
            match TransitionSystemLib.SymbolicSystem.Parser.parseSymbolicSystem s with 
                | Result.Ok x -> x 
                | Result.Error msg -> 
                    raise <| AnalysisException $"The NuSMV system could not be parsed: %s{msg}"
                    )

    let symbolicHyperLTL = 
        match HyperLTL.Parser.parseNamedSymbolicHyperltl propContent with
        | Result.Ok x ->
            HyperLTL.SymbolicHyperLTL.NamedSymbolicHyperLTL.toSymbolicHyperLTL x
        | Result.Error err -> 
            raise <| AnalysisException $"The HyperLTL formula could not be parsed. %s{err}"

    config.Logger [TWO; THREE; FOUR] $"Parsed Input in: %i{sw.ElapsedMilliseconds}ms\n"

    plist 
    |> List.iteri (fun i x -> 
        match SymbolicSystem.findError x with 
        | None -> ()
        | Some msg -> 
            raise <| AnalysisException $"Found error in the %i{i}th system: %s{msg}"
        )

    sw.Restart()
    
    let unfoldRelationPredicate (a : RelationalAtom)  = 
        match a with 
        | UnaryPred (e, n) -> 
            LTL.Atom ((e, n))
        | RelationalEq(e1, n1, e2, n2) -> 
            let t1 = e1 |> TransitionSystemLib.SymbolicSystem.Expression.inferType (fun x -> if plist.Length = 1 then plist.[0].VarTypes |> Map.ofList |> Map.find x else plist.[n1].VarTypes |> Map.ofList |> Map.find x)
            let t2 = e2 |> TransitionSystemLib.SymbolicSystem.Expression.inferType (fun x -> if plist.Length = 1 then plist.[0].VarTypes |> Map.ofList |> Map.find x else plist.[n2].VarTypes |> Map.ofList |> Map.find x)

            let t = 
                match TransitionSystemLib.SymbolicSystem.VariableType.joinTypes t1 t2 with 
                | Some x -> x 
                | None -> 
                    raise <| AnalysisException $"Error during unfolding: Could not unify types %A{t1} and %A{t1}."

            VariableType.allValues t
            |> List.map (fun v -> 
                LTL.And(
                    LTL.Atom((Expression.Eq(e1, v |> VariableValue.toConstant |> Expression.Const), n1)),
                    LTL.Atom((Expression.Eq(e2, v |> VariableValue.toConstant |> Expression.Const), n2))
                )
            )
            |> List.reduce (fun x y -> LTL.Or(x, y))

    let unfoldedHyperLTL = 
        {
            HyperLTL.QuantifierPrefix = symbolicHyperLTL.QuantifierPrefix
            HyperLTL.LTLMatrix = symbolicHyperLTL.LTLMatrix |> LTL.bind (fun x -> unfoldRelationPredicate x)
        }
        
    if HyperLTL.isConsistent unfoldedHyperLTL |> not then
        raise <| AnalysisException "HyperLTL formula is not consistent"
    
    let tsList = 
        if plist.Length = 1 then 
            let atomList = 
                unfoldedHyperLTL.LTLMatrix
                |> LTL.allAtoms
                |> Set.map fst 
                |> Set.toList
                
            atomList
            |> List.iter (fun (v : Expression) ->
                v 
                |> Expression.allVars
                |> Set.iter (fun x ->
                    if (Map.containsKey x plist.[0].VarTypeMap |> not) && (plist.[0].DefineMap.Keys.Contains x |> not) then
                        raise <| AnalysisException $"Variable %A{x} is used in an atomic proposition but not defined in the system"
                    )
            )
              
            let ts = 
                SymbolicSystem.convertSymbolicSystemToTransitionSystem plist.[0] atomList

            List.init (unfoldedHyperLTL.QuantifierPrefix.Length) (fun _ -> ts)
        else 
            if plist.Length <> unfoldedHyperLTL.QuantifierPrefix.Length then 
                raise <| AnalysisException "The number of systems given does not match the property"

            [0..unfoldedHyperLTL.QuantifierPrefix.Length-1]
            |> List.map (fun i -> 
                let atomList = 
                    unfoldedHyperLTL.LTLMatrix
                    |> LTL.allAtoms
                    |> Set.filter (fun (_, j) -> i = j) // Only those atoms that are rlevent
                    |> Set.map fst 
                    |> Set.toList
                    
                atomList
                |> List.iter (fun (v : Expression) ->
                    v
                    |> Expression.allVars
                    |> Set.iter (fun x ->
                            if plist.[i].VarTypeMap.ContainsKey x |> not && (plist.[i].DefineMap.ContainsKey x |> not) then
                                raise <| AnalysisException $"Variable %A{x} is used in an atomic proposition but defined in the %i{i}th system"
                        )
                )
                    
                SymbolicSystem.convertSymbolicSystemToTransitionSystem plist.[i] atomList
                )
                 
    config.Logger [TWO; THREE; FOUR] $"Compiled Program to explicit-state transition system in %i{sw.ElapsedMilliseconds}ms\n"
    config.Logger [TWO; THREE; FOUR] $"System Sizes: %A{tsList |> List.map (fun ts -> ts.States.Count)}\n"
    config.Logger [ONE] $"System Sizes: %A{tsList |> List.map (fun ts -> ts.States.Count)} (t: %i{sw.ElapsedMilliseconds}ms)\n"

    // Convert the APs in the system to be strings
    let tsList = 
        tsList 
        |> List.map (fun ts -> TransitionSystem.mapAPs (fun (x: Expression) -> Expression.print x) ts)
    

    let hyperltl = 
        {
            HyperLTL.QuantifierPrefix = unfoldedHyperLTL.QuantifierPrefix
            LTLMatrix = LTL.map (fun (x: Expression, n) -> (Expression.print x, n)) unfoldedHyperLTL.LTLMatrix
        }

    verify config tsList hyperltl m


let booleanProgramVerification config systemPaths propPath m  = 
    let sw = System.Diagnostics.Stopwatch()
    let totalsw = System.Diagnostics.Stopwatch()
    totalsw.Start()
    sw.Start()

    let hyperltlcontent =
        try 
            File.ReadAllText propPath
        with 
            | _ -> 
                raise <| AnalysisException $"Could not open/read file %s{propPath}"
               
    let progcontents = 
        systemPaths
        |> List.map (fun x -> 
            try 
                File.ReadAllText  x
            with 
                | _ -> 
                    raise <| AnalysisException $"Could not open/read file %s{x}"
            )

    config.Logger [TWO; THREE; FOUR] $"Read Input in %i{sw.ElapsedMilliseconds}ms\n"

    sw.Restart()

    let hyperltl = 
        match HyperLTL.Parser.parseBooleanProgramNamedHyperLTL hyperltlcontent with
        | Result.Ok x ->
            NamedHyperLTL.toHyperLTL x
        | Result.Error err -> 
            raise <| AnalysisException $"The provided HyperLTL formula could not be parsed: %s{err}"

    if HyperLTL.isConsistent hyperltl |> not then
        raise <| AnalysisException "HyperLTL formula is not consistent"

    let progList = 
        progcontents
        |> List.map (fun s -> 
            match TransitionSystemLib.BooleanProgramSystem.Parser.parseBooleanProgram s with 
                | Ok x -> x
                | Error msg -> 
                    raise <| AnalysisException $"The boolean program could not be parsed: %s{msg}"
            )

    config.Logger [TWO; THREE; FOUR] $"Parsed Input in %i{sw.ElapsedMilliseconds}ms\n"

    progList
    |> List.iteri (fun i x -> 
        match BooleanProgram.findError x with 
        | None -> ()
        | Some msg -> 
            raise <| AnalysisException $"Found error in the %i{i}th system: %s{msg}"
        )

    sw.Restart()

    let tsList = 
        if progList.Length = 1 then 
            // Use the same system for all traces
            let prog = progList[0]

            let relevantAps = 
                hyperltl.LTLMatrix
                |> LTL.allAtoms
                |> Set.map (fun (x, _) -> x)
                |> Set.toList
                
            relevantAps
            |> List.iter (fun (v, i) ->
                if prog.DomainMap.ContainsKey v && prog.DomainMap.[v] > i |> not then
                    raise <| AnalysisException $"AP (%A{v}, %i{i}) is used in the HyperLTL property but variable %A{v} does not exists or has not the required bit width"
                )

            let ts = 
                BooleanProgram.convertBooleanProgramToTransitionSystem prog relevantAps
                |> TransitionSystem.mapAPs (fun (n, i) -> n + "_" + string(i))

            List.init (hyperltl.QuantifierPrefix.Length) (fun _ -> ts)
        else 
            // Different system for all traces 
            if progList.Length <> hyperltl.QuantifierPrefix.Length then 
                raise <| AnalysisException "The number of systems given does not match the property"

            [0..hyperltl.QuantifierPrefix.Length-1]
            |> List.map (fun i ->   
                let relevantAps = 
                    hyperltl.LTLMatrix
                    |> LTL.allAtoms
                    |> Set.filter (fun (_, j) ->  i = j) // Only those atom used in this copy
                    |> Set.map (fun (x, _) -> x)
                    |> Set.toList
                    
                    
                relevantAps
                |> List.iter (fun (v, j) ->
                    if progList.[i].DomainMap.ContainsKey v && progList.[i].DomainMap.[v] > j |> not then
                        raise <| AnalysisException $"AP (%A{v}, %i{j}) is used in the HyperLTL property but variable %A{v} does not exists or has not the required bit width. Aborting."
                    )
                BooleanProgram.convertBooleanProgramToTransitionSystem progList.[i] relevantAps
                |> TransitionSystem.mapAPs (fun (n, i) -> n + "_" + string(i))
            )

    config.Logger [TWO; THREE; FOUR] $"Compiled Program to explicit-state TS in %i{sw.ElapsedMilliseconds}ms\n"
    config.Logger [TWO; THREE; FOUR] $"System Sizes: %A{tsList |> List.map (fun ts -> ts.States.Count)}\n"
    config.Logger [ONE] $"System Sizes: %A{tsList |> List.map (fun ts -> ts.States.Count)} (t: %i{sw.ElapsedMilliseconds}ms)\n"

    let hyperltl = 
        {
            HyperLTL.QuantifierPrefix = hyperltl.QuantifierPrefix
            HyperLTL.LTLMatrix = hyperltl.LTLMatrix |> LTL.map (fun ((n, i), j) -> n + "_" + string(i), j)
        }
    verify config tsList hyperltl m
