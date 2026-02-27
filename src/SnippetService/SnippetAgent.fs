module TheGamma.SnippetService.SnippetAgent

open System
open System.IO
open Newtonsoft.Json

// --------------------------------------------------------------------------------------
// Data we store about snippets
// --------------------------------------------------------------------------------------

type Snippet =
  { id : int
    likes : int
    posted : DateTime
    title : string
    description : string
    author : string
    twitter : string
    link : string
    code : string
    compiled : string
    version : string
    config : string
    hidden : bool }

type NewSnippet =
  { title : string
    description : string
    author : string
    twitter : string
    link : string
    compiled : string
    code : string
    hidden : bool
    config : string
    version : string }

// --------------------------------------------------------------------------------------
// Reading & writing snippets using local file storage
// --------------------------------------------------------------------------------------

let mutable storageRoot = ""
let initStorage root = storageRoot <- root

let serializer = JsonSerializer.Create()

let toJson value =
  let sb = System.Text.StringBuilder()
  use tw = new StringWriter(sb)
  serializer.Serialize(tw, value)
  sb.ToString()

let fromJson<'R> str : 'R =
  use tr = new StringReader(str)
  serializer.Deserialize(tr, typeof<'R>) :?> 'R

let ensureDir dir =
  if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

let readSnippets source =
  let dir = Path.Combine(storageRoot, "snippets", source)
  let filePath = Path.Combine(dir, "snippets.json")
  if File.Exists filePath then
    let json = File.ReadAllText(filePath, Text.Encoding.UTF8)
    json, json |> fromJson<Snippet[]>
  else
    failwithf "Snippets file not found: %s (storageRoot=%s)" filePath storageRoot

let writeSnippets source (snippets:Snippet[]) =
  let dir = Path.Combine(storageRoot, "snippets", source)
  ensureDir dir
  let json = snippets |> toJson
  File.WriteAllText(Path.Combine(dir, "snippets.json"), json, Text.Encoding.UTF8)

// --------------------------------------------------------------------------------------
// Keeping current snippets using an agent
// --------------------------------------------------------------------------------------

type Message =
  | GetSnippets of string * AsyncReplyChannel<string>
  | AddSnippet of string * NewSnippet * AsyncReplyChannel<int>
  | LikeSnippet of string * int

let agent = MailboxProcessor.Start(fun inbox ->
  let rec loop snippets = async {
    let! msg = inbox.Receive()
    match msg with
    | GetSnippets(source, res) ->
        match Map.tryFind source snippets with
        | Some(json, _) ->
            res.Reply(json)
            return! loop snippets
        | None ->
            let json, snips = readSnippets source
            res.Reply(json)
            return! loop (Map.add source (json, snips) snippets)
    | AddSnippet(source, snip, res) ->
        let _, snips =
          match Map.tryFind source snippets with
          | Some s -> s
          | None -> readSnippets source
        let id = 1 + (snips |> Seq.map (fun s -> s.id) |> Seq.fold max 0)
        let snippet =
          { id = id; likes = 0; posted = DateTime.Now; author = snip.author
            version = snip.version; hidden = snip.hidden; title = snip.title
            compiled = snip.compiled; code = snip.code; config = snip.config
            description = snip.description; twitter = snip.twitter; link = snip.link }
        let snips = Array.append snips [| snippet |]
        writeSnippets source snips
        res.Reply(id)
        return! loop (Map.add source (toJson snips, snips) snippets)
    | LikeSnippet(source, id) ->
        let snips =
          match Map.tryFind source snippets with
          | Some(_, s) -> s
          | None -> readSnippets source |> snd
        let snips = snips |> Array.map (fun s ->
            if s.id = id then { s with likes = s.likes + 1 } else s)
        writeSnippets source snips
        return! loop (Map.add source (toJson snips, snips) snippets) }
  async {
    while true do
      try return! loop Map.empty
      with e -> eprintfn "SNIPPET AGENT ERROR: %A" e })
