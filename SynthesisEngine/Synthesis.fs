module Synthesis

open FSharpx.Collections
open Microsoft.Z3
open Microsoft.Z3.FSharp.Common
open Microsoft.Z3.FSharp.Bool
open Data
open FunctionEncoding
open ShortestPaths

let private constraintsBitVec ctor (m : Model) (d : FuncDecl) =
    let x = System.Int32.Parse(m.[d].ToString())
    (ctor (d.Name.ToString())) <>. x

let private constraintsCircuitVar (m : Model) (ds : FuncDecl []) =
    Or <| Array.map (constraintsBitVec makeCircuitVar m) ds

let private buildGraph edges =
    let build graph (u, v) =
        let neighbours = if Map.containsKey u graph then v :: Map.find u graph else [v]
        Map.add u neighbours graph

    Seq.fold build Map.empty edges

let private askNonTransition gene geneNames aVars rVars state =
    let nonTransitionEnforced = makeEnforcedVar ("enforced_" + state.Name)

    let encoding, same = circuitEvaluatesToSame gene geneNames aVars rVars state
    (encoding &&. If (same, nonTransitionEnforced =. 1, nonTransitionEnforced =. 0),
     nonTransitionEnforced)

let private manyNonTransitionsEnforced gene geneNames aVars rVars statesWithoutGeneTransitions numNonTransitionsEnforced =
    if numNonTransitionsEnforced = 0 then True
    else
        let askNonTransitions, enforceVars = Array.unzip << Array.ofSeq <| Seq.map (askNonTransition gene geneNames aVars rVars) statesWithoutGeneTransitions
        let askNonTransitions = And askNonTransitions
        let manyEnforced = Array.reduce (+) enforceVars >=. numNonTransitionsEnforced

        askNonTransitions &&. manyEnforced

let private findAllowedEdges (solver : Solver) gene geneNames maxActivators maxRepressors threshold (statesWithGeneTransitions : Set<State * State>) statesWithoutGeneTransitions =
    let temp1, temp2 = statesWithGeneTransitions |> Set.toList |> List.unzip
    let edges = ref (Set.ofList temp1 + Set.ofList temp2)
    let circuitEncoding, aVars, rVars = encodeUpdateFunction gene geneNames maxActivators maxRepressors

    let numNonTransitionsEnforced =
        let max = statesWithoutGeneTransitions |> Set.count
        max * threshold / 100

    let manyNonTransitionsEnforced = manyNonTransitionsEnforced gene geneNames aVars rVars statesWithoutGeneTransitions numNonTransitionsEnforced

    let encodeTransition state =
        circuitEvaluatesToDifferent gene geneNames aVars rVars state

    let encodeTransitions states =
        let encodings, evaluations = Seq.map encodeTransition states |> Array.ofSeq |> Array.unzip
        And encodings &&. Or evaluations

    let stop = ref false
    set [ while not !stop do
              if Set.isEmpty !edges then
                  stop := true
              else
                  solver.Reset()
                  solver.Add (circuitEncoding,
                              manyNonTransitionsEnforced,
                              encodeTransitions !edges)

                  if solver.Check() = Status.SATISFIABLE then
                      let m = solver.Model

                      let edgeNames = Set.map (fun (s : State) -> s.Name) !edges

                      let edgeDecls = Array.filter (fun (d : FuncDecl) -> d.Name.ToString().StartsWith "circuit_" && Set.contains (d.Name.ToString().Replace("circuit_", "")) edgeNames) m.ConstDecls
                                   |> Array.filter (fun d -> System.Boolean.Parse(m.[d].ToString()) = (let state = Seq.find (fun (a : State) -> a.Name = (d.Name.ToString().Replace("circuit_", ""))) !edges
                                                                                                       in not (Map.find gene state.Values)))
                                   |> Array.map (fun (d : FuncDecl) -> d.Name.ToString().Replace("circuit_", ""))
                                   |> Set.ofArray
                      let trueEdges = !edges |> Set.filter (fun a -> Set.contains a.Name edgeDecls)

                      edges := !edges - trueEdges
                      for (a, b) in statesWithGeneTransitions do
                          if Set.contains a trueEdges then yield (a, b)
                          if Set.contains b trueEdges then yield (b, a)
                  else
                      stop := true ]

let private findFunctions (solver : Solver) gene geneNames maxActivators maxRepressors threshold shortestPaths
                          statesWithGeneTransitions statesWithoutGeneTransitions =
    let circuitEncoding, aVars, rVars = encodeUpdateFunction gene geneNames maxActivators maxRepressors
    
    let numNonTransitionsEnforced =
        let max = statesWithoutGeneTransitions |> Set.count
        max * threshold / 100

    let encodeTransition (stateA, stateB) =
        if not (Set.contains (stateA, stateB) statesWithGeneTransitions || Set.contains (stateB, stateA) statesWithGeneTransitions)
        then
            True
        else
            let differentA = (let e, v = circuitEvaluatesToDifferent gene geneNames aVars rVars stateA in e &&. v)
            differentA

    let encodePath path =
        let f (formula, u) v = (And [| formula; encodeTransition (u, v) |], v)
        List.fold f (True, List.head path) (List.tail path) |> fst
    
    let pathsEncoding = if Seq.isEmpty shortestPaths then True else
                        And [| for paths in shortestPaths do
                                   if Seq.isEmpty paths then yield True
                                   else
                                       yield Or [| for path in paths do
                                                       yield encodePath path |] |]

    solver.Reset()
    solver.Add (circuitEncoding,
                pathsEncoding,
                manyNonTransitionsEnforced gene geneNames aVars rVars statesWithoutGeneTransitions numNonTransitionsEnforced)

    seq { while solver.Check() = Status.SATISFIABLE do
              let m = solver.Model

              let activatorDecls = Array.filter (fun (d : FuncDecl) ->
                                                   Set.contains (d.Name.ToString()) activatorVars) m.ConstDecls
                                                   |> Array.sortBy (fun d -> d.Name.ToString().Remove(0,1) |> int)
              let repressorDecls = Array.filter (fun (d : FuncDecl) ->
                                                   Set.contains (d.Name.ToString()) repressorVars) m.ConstDecls
                                                   |> Array.sortBy (fun d -> d.Name.ToString().Remove(0,1) |> int)

              let circuitDecls = activatorDecls ++ repressorDecls
              solver.Add(constraintsCircuitVar m circuitDecls)

              let enforceDecls = Array.filter (fun (d : FuncDecl) -> d.Name.ToString().StartsWith "enforced") m.ConstDecls
              let numEnforced = List.sum <| [ for d in enforceDecls do yield System.Int32.Parse (string m.[d]) ]

              let activatorAssignment = activatorDecls |> Seq.map (fun d -> System.Int32.Parse(m.[d].ToString()))
              let repressorAssignment = repressorDecls |> Seq.map (fun d -> System.Int32.Parse(m.[d].ToString()))

              let circuit = solutionToCircuit geneNames activatorAssignment repressorAssignment
              let max = statesWithoutGeneTransitions |> Set.count

              yield sprintf "%i / %i\t%s" numEnforced max (Circuit.printCircuit circuit) }

let synthesise geneParameters statesWithGeneTransitions statesWithoutGeneTransitions initialStates targetStates outputDir oneSolution =
    let solver = Solver()

    let geneNames = Map.keys geneParameters
    let geneParameters = Map.toSeq geneParameters

    let allowedEdges = geneParameters |> Seq.map (fun (g, (a, r, t)) ->
                                           findAllowedEdges solver g geneNames a r t (Map.find g statesWithGeneTransitions) (Map.find g statesWithoutGeneTransitions))
                                      |> Set.unionMany

    let reducedStateGraph = buildGraph allowedEdges

    let shortestPaths = initialStates |> Array.map (fun initial ->
        let targetStates = targetStates |> Set.ofArray |> Set.remove initial
        shortestPathMultiSink reducedStateGraph initial targetStates)

    let invertedPaths = [| for i in 0 .. Array.length targetStates - 1 do
                               yield [ for j in 0 .. Array.length initialStates - 1 do
                                           for path in shortestPaths.[j] do
                                               match path with
                                               | [] -> ()
                                               | l -> if List.nth l (List.length l - 1) = targetStates.[i] then yield l ] |]

    geneParameters |> Seq.iter (fun (g, (a, r, t)) ->
                                  let file = outputDir + "/" + g + ".txt"
                                  System.IO.File.WriteAllText(file, "")
                                  let circuits = findFunctions solver g geneNames a r t invertedPaths (Map.find g statesWithGeneTransitions) (Map.find g statesWithoutGeneTransitions)
                                  let circuits = if oneSolution then Seq.truncate 1 circuits else circuits
                                  for circuit in circuits do
                                      System.IO.File.AppendAllText(file, circuit + "\n"))
