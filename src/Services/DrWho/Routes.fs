module TheGamma.Services.DrWho.Routes

open System
open System.IO
open System.Text.RegularExpressions
open System.Collections.Generic
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Common.JsonHelpers

// ----------------------------------------------------------------------------
// Internal graph data types
// ----------------------------------------------------------------------------

type Node = { Id:int; Label:string; Properties:Map<string,string> }
type Edge = { From:int; To:int; Relation:string }

// ----------------------------------------------------------------------------
// Types for REST provider JSON responses
// ----------------------------------------------------------------------------

type TypeNested = { kind:string; endpoint:string }
type Member = { name:string; returns:obj; trace:string[] }
type LinkedMember = { id:string; name:string; properties:Map<string,string>; trace:string[]; returns:obj }

type FieldDef = { name:string; ``type``:string }
type RecordType = { name:string; fields:FieldDef[] }
type SeqType = { name:string; ``params``:obj[] }
type TypePrimitive = { kind:string; ``type``:obj; endpoint:string }

// ----------------------------------------------------------------------------
// Indexes populated at startup by initData
// ----------------------------------------------------------------------------

let mutable nodeById : Map<int, Node> = Map.empty
let mutable nodeByName : Map<string, Node> = Map.empty
let mutable nodesByLabel : Map<string, Node[]> = Map.empty
let mutable neighborsByNodeId : Map<int, (string * int)[]> = Map.empty
let mutable orderedLabels : string[] = [||]
let mutable serviceBaseUrl : string = "http://localhost:5000"

let initData (dataRoot:string) (baseUrl:string) =
  serviceBaseUrl <- baseUrl
  let lines = File.ReadAllLines(Path.Combine(dataRoot, "drwho", "drwho.cypher"))
  let nodeRe = Regex(@"^CREATE \(_(\d+):DoctorWho:(\w+) \{(.*)\}\)$")
  let propRe = Regex(@"`(\w+)`:(""[^""]*""|\d+)")
  let edgeRe = Regex(@"^CREATE \(_(\d+)\)-\[:`(\w+)`\]->\(_(\d+)\)$")

  let parseProps (s:string) =
    propRe.Matches(s)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value.Trim('"'))
    |> Map.ofSeq

  let nodes =
    lines |> Array.choose (fun line ->
      let m = nodeRe.Match(line)
      if m.Success then
        Some { Id = int m.Groups.[1].Value
               Label = m.Groups.[2].Value
               Properties = parseProps m.Groups.[3].Value }
      else None)

  let edges =
    lines |> Array.choose (fun line ->
      let m = edgeRe.Match(line)
      if m.Success then
        Some { From = int m.Groups.[1].Value
               Relation = m.Groups.[2].Value
               To = int m.Groups.[3].Value }
      else None)

  nodeById     <- nodes |> Array.map (fun n -> n.Id, n) |> Map.ofArray
  nodeByName   <- nodes |> Array.choose (fun n ->
    let key = n.Label.ToLower()
    n.Properties |> Map.tryFind key
    |> Option.orElse (n.Properties |> Map.tryFind "title")
    |> Option.map (fun nm -> nm, n)) |> Map.ofArray
  nodesByLabel <- nodes |> Array.groupBy (fun n -> n.Label) |> Map.ofArray

  // Build undirected neighbor list: directed edge A→B adds (rel,B) to A and (rel,A) to B
  let neighborDict = Dictionary<int, ResizeArray<string * int>>()
  for n in nodes do neighborDict.[n.Id] <- ResizeArray()
  for e in edges do
    neighborDict.[e.From].Add(e.Relation, e.To)
    neighborDict.[e.To].Add(e.Relation, e.From)
  neighborsByNodeId <-
    neighborDict
    |> Seq.map (fun kv -> kv.Key, kv.Value.ToArray())
    |> Map.ofSeq

  // Labels in insertion order (deduplicated)
  let seen = HashSet<string>()
  orderedLabels <-
    nodes
    |> Array.map (fun n -> n.Label)
    |> Array.filter (fun lbl -> seen.Add(lbl))

// ----------------------------------------------------------------------------
// Graph query helpers
// ----------------------------------------------------------------------------

let nodeName (n:Node) =
  let key = n.Label.ToLower()
  let byKey = n.Properties |> Map.tryFind key
  let byTitle = n.Properties |> Map.tryFind "title"
  // Prefer title over a purely numeric label-key value (e.g. Episode.episode = "1")
  match byKey, byTitle with
  | Some v, Some t when v |> Seq.forall Char.IsDigit -> t
  | Some v, _ -> v
  | None, Some t -> t
  | None, None -> string n.Id

// Build the property schema (field name + type) for a set of nodes.
// Order: label-key prop, synthetic name, synthetic label, remaining string props sorted, float props sorted.
let propsSchema (nodes:Node[]) : (string * string)[] =
  if nodes.Length = 0 then [||]
  else
    let labelKey = nodes.[0].Label.ToLower()
    // Collect all non-labelKey property keys across nodes, preserving first-seen order
    let allOtherKeys =
      let seen = HashSet<string>()
      [| for n in nodes do
           for KeyValue(k, _) in n.Properties do
             if k <> labelKey && seen.Add(k) then yield k |]
    // Classify each key: float if any node has a purely-numeric value for it
    let isFloat k =
      nodes |> Array.exists (fun n ->
        n.Properties |> Map.tryFind k
        |> Option.exists (fun v -> v.Length > 0 && v |> Seq.forall Char.IsDigit))
    let stringProps = allOtherKeys |> Array.filter (fun k -> not (isFloat k)) |> Array.sort
    let floatProps  = allOtherKeys |> Array.filter isFloat |> Array.sort
    [| yield labelKey, "string"
       yield "name",   "string"
       yield "label",  "string"
       yield! stringProps |> Array.map (fun k -> k, "string")
       yield! floatProps  |> Array.map (fun k -> k, "float") |]

// Traverse the full trace and return all paths.
// Trace format: "Label&selector[&relation&selector]*"
// Each path is an array of nodes, one per (label/name) position in the trace.
let pathsForTrace (body:string) : Node[][] =
  let segs = body.Split('&')
  if segs.Length < 2 then [||]
  else
    let startNodes =
      match segs.[1] with
      | "[any]" -> nodesByLabel |> Map.tryFind segs.[0] |> Option.defaultValue [||]
      | nm      -> nodeByName   |> Map.tryFind nm       |> Option.map Array.singleton |> Option.defaultValue [||]
    let mutable paths = startNodes |> Array.map (fun n -> [| n |])
    let mutable i = 2
    while i + 1 < segs.Length do
      let rel = segs.[i]
      let sel = segs.[i + 1]
      paths <-
        paths |> Array.collect (fun path ->
          let lastNode = path.[path.Length - 1]
          let neighbors =
            neighborsByNodeId |> Map.tryFind lastNode.Id |> Option.defaultValue [||]
            |> Array.filter (fun (r, _) -> r = rel)
            |> Array.map (fun (_, toId) -> nodeById.[toId])
          let filtered =
            match sel with
            | "[any]" -> neighbors
            | nm      -> neighbors |> Array.filter (fun n -> nodeName n = nm)
          filtered |> Array.map (fun n -> Array.append path [| n |]))
      i <- i + 2
    paths

// Compute schema per node position across all paths.
let schemasForPaths (paths:Node[][]) : (int * (string * string)[])[] =
  if paths.Length = 0 then [||]
  else
    let numPositions = paths.[0].Length
    [| for i in 0 .. numPositions - 1 ->
         i, propsSchema (paths |> Array.map (fun p -> p.[i])) |]

// ----------------------------------------------------------------------------
// REST provider member builders
// ----------------------------------------------------------------------------

// Build the get_properties Member for a trace string (may be multi-hop).
// Field names follow "node{pos+1}.{key}" convention; data keys use "{pos}-{key}".
let mkGetPropertiesMember (tr:string) : Member =
  let posSchemas = schemasForPaths (pathsForTrace tr)
  let fields =
    posSchemas |> Array.collect (fun (pos, schema) ->
      schema |> Array.map (fun (k, t) ->
        { name=sprintf "node%d.%s" (pos + 1) k; ``type``=t }))
  let recordType = box { RecordType.name="record"; fields=fields }
  let seqType    = box { SeqType.name="seq"; ``params``=[| recordType |] }
  { name="get_properties"; trace=[||]
    returns=box { TypePrimitive.kind="primitive"; ``type``=seqType; endpoint="/get_properties_of_node" } }

// Serialize one path as a data record with "{pos}-{key}" prefixed keys.
// Missing properties → JSON 0. Synthetic "name" and "label" always included.
let pathToRecord (posSchemas:(int * (string * string)[])[]) (path:Node[]) : Dictionary<string, obj> =
  let d = Dictionary<string, obj>()
  for (pos, schema) in posSchemas do
    if pos < path.Length then
      let n = path.[pos]
      for (k, t) in schema do
        let v : obj =
          match k with
          | "name"  -> box (nodeName n)
          | "label" -> box n.Label
          | _ ->
            match n.Properties |> Map.tryFind k with
            | Some raw -> if t = "float" then box (float raw) else box raw
            | None -> box 0
        d.[sprintf "%d-%s" pos k] <- v
  d

// Build the explore_properties Member for a given trace string.
// tr is already URL-decoded (e.g. "Actor&[any]"); encode the whole CSV URL once.
let mkExplorePropertiesMember (tr:string) : Member =
  let csvUrl     = sprintf "%s/services/drwho/%s/get_propertiescsv" serviceBaseUrl tr
  let uploadPath = sprintf "%s/services/csv/providers/data/upload/%s" serviceBaseUrl (Uri.EscapeDataString(csvUrl))
  { name="explore_properties"; trace=[||]
    returns=box { kind="nested"; endpoint=uploadPath } }

// Generate a CSV from paths using per-position schemas.
let pathsToCsv (posSchemas:(int * (string * string)[])[]) (paths:Node[][]) : string =
  let escape (v:string) = if v.Contains(",") || v.Contains("\"") then sprintf "\"%s\"" (v.Replace("\"","\"\"")) else v
  let header =
    posSchemas |> Array.collect (fun (pos, schema) ->
      schema |> Array.map (fun (k, _) -> sprintf "%d-%s" pos k))
    |> String.concat ","
  let rows =
    paths |> Array.map (fun path ->
      posSchemas |> Array.collect (fun (pos, schema) ->
        if pos >= path.Length then schema |> Array.map (fun _ -> "0")
        else
          let n = path.[pos]
          schema |> Array.map (fun (k, _) ->
            match k with
            | "name"  -> escape (nodeName n)
            | "label" -> n.Label
            | _ ->
              match n.Properties |> Map.tryFind k with
              | Some raw -> escape raw
              | None     -> "0"))
      |> String.concat ",")
  String.concat "\n" [| yield header; yield! rows |]

// ----------------------------------------------------------------------------
// Navigation functions
// ----------------------------------------------------------------------------

let allNodes () : Member[] =
  orderedLabels |> Array.map (fun lbl ->
    { name=lbl; trace=[| lbl |]
      returns=box { kind="nested"; endpoint=sprintf "/%s/nodes_of_type/%s" lbl lbl } })

let nodesOfType (tr:string) (lbl:string) : Member[] =
  let nodes = nodesByLabel |> Map.tryFind lbl |> Option.defaultValue [||]
  [| for n in nodes do
       let name = nodeName n
       yield { name=name; trace=[| name |]
               returns=box { kind="nested"; endpoint=sprintf "%s&%s/links_from_node/%s" tr name name } }
     yield { name="[any]"; trace=[| "[any]" |]
             returns=box { kind="nested"; endpoint=sprintf "%s&[any]/links_from_any_node/%s" tr lbl } } |]

let linksFromNode (tr:string) (name:string) : Member[] =
  let node = nodeByName.[name]
  let neighbors = neighborsByNodeId |> Map.tryFind node.Id |> Option.defaultValue [||]
  let seen = HashSet<string>()
  let rels = neighbors |> Array.map fst |> Array.filter (fun r -> seen.Add(r))
  let relMembers =
    rels |> Array.map (fun rel ->
      { name=rel; trace=[| rel |]
        returns=box { kind="nested"; endpoint=sprintf "%s&%s/linked_from_node/%s/%s" tr rel name rel } })
  [| yield mkGetPropertiesMember tr
     yield mkExplorePropertiesMember tr
     yield! relMembers |]

let linksFromAnyNode (tr:string) (lbl:string) : Member[] =
  let nodes = nodesByLabel |> Map.tryFind lbl |> Option.defaultValue [||]
  let seen = HashSet<string>()
  let rels =
    nodes
    |> Array.collect (fun n -> neighborsByNodeId |> Map.tryFind n.Id |> Option.defaultValue [||])
    |> Array.map fst
    |> Array.filter (fun r -> seen.Add(r))
  let relMembers =
    rels |> Array.map (fun rel ->
      { name=rel; trace=[| rel |]
        returns=box { kind="nested"; endpoint=sprintf "%s&%s/linked_from_node/any/%s" tr rel rel } })
  [| yield mkGetPropertiesMember tr
     yield mkExplorePropertiesMember tr
     yield! relMembers |]

let linkedFromNode (tr:string) (name:string) (rel:string) : obj[] =
  // Collect (srcNode, dstNode) pairs for the given relation
  let pairs =
    if name = "any" then
      nodeById |> Map.toSeq
      |> Seq.collect (fun (id, srcNode) ->
        neighborsByNodeId |> Map.tryFind id |> Option.defaultValue [||]
        |> Array.filter (fun (r, _) -> r = rel)
        |> Array.map (fun (_, toId) -> srcNode, nodeById.[toId]))
    else
      let srcNode = nodeByName.[name]
      neighborsByNodeId |> Map.tryFind srcNode.Id |> Option.defaultValue [||]
      |> Array.filter (fun (r, _) -> r = rel)
      |> Array.map (fun (_, toId) -> srcNode, nodeById.[toId])
      |> Seq.ofArray

  // Deduplicate by destination node id, preserving traversal order
  let seenIds = HashSet<int>()
  let deduped =
    pairs
    |> Seq.choose (fun (src, dst) -> if seenIds.Add(dst.Id) then Some(src, dst) else None)
    |> Array.ofSeq

  // Label for [any] = label of first source node encountered
  let firstSrcLabel =
    if deduped.Length > 0 then (fst deduped.[0]).Label else "Character"

  [| for (_, dst) in deduped do
       let dstName = nodeName dst
       yield box { id=string dst.Id; name=dstName; properties=dst.Properties; trace=[| dstName |]
                   returns=box { kind="nested"; endpoint=sprintf "%s&%s/links_from_node/%s" tr dstName dstName } }
     yield box { Member.name="[any]"; trace=[| "[any]" |]
                 returns=box { kind="nested"; endpoint=sprintf "%s&[any]/links_from_any_node/%s" tr firstSrcLabel } } |]

// ----------------------------------------------------------------------------
// Giraffe handler
// ----------------------------------------------------------------------------

let handler : HttpHandler =
  choose [
    route "/"
      >=> fun next ctx -> allNodes () |> toJson |> fun s -> text s next ctx
    routef "/%s/nodes_of_type/%s"       (fun (tr, lbl)     ->
      nodesOfType tr lbl |> toJson |> text)
    routef "/%s/links_from_node/%s"     (fun (tr, name)    ->
      linksFromNode tr name |> toJson |> text)
    routef "/%s/links_from_any_node/%s" (fun (tr, lbl)     ->
      linksFromAnyNode tr lbl |> toJson |> text)
    routef "/%s/linked_from_node/%s/%s" (fun (tr, nm, rel) ->
      linkedFromNode tr nm rel |> toJson |> text)
    routef "/%s/get_propertiescsv"      (fun tr ->
      fun next ctx ->
        let paths   = pathsForTrace tr
        let schemas = schemasForPaths paths
        text (pathsToCsv schemas paths) next ctx)
    route "/get_properties_of_node"     >=> fun next ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      let paths   = pathsForTrace body
      let schemas = schemasForPaths paths
      let result  = paths |> Array.map (pathToRecord schemas) |> toJson
      return! text result next ctx } ]
