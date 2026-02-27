module TheGamma.CsvService.Storage

open System
open System.IO
open System.Collections.Generic
open Newtonsoft.Json
open TheGamma.CsvService.Pivot

// --------------------------------------------------------------------------------------
// JSON helpers
// --------------------------------------------------------------------------------------

let serializer = JsonSerializer.Create()

let toJson value =
  let sb = System.Text.StringBuilder()
  use tw = new StringWriter(sb)
  serializer.Serialize(tw, value)
  sb.ToString()

let fromJson<'R> str : 'R =
  use tr = new StringReader(str)
  serializer.Deserialize(tr, typeof<'R>) :?> 'R

// --------------------------------------------------------------------------------------
// Local file storage (replacing Azure Blob Storage)
// --------------------------------------------------------------------------------------

let mutable storageRoot = ""
let initStorage root = storageRoot <- root

let ensureDir dir =
  if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

let containerPath container = Path.Combine(storageRoot, container)

let generateId (date:DateTime) i =
  sprintf "%s/file_%d.csv" (date.ToString("yyyy-MM-dd")) i

let uploadCsv container (id:string) data =
  let dir = containerPath container
  ensureDir dir
  let filePath = Path.Combine(dir, id.Replace('/', Path.DirectorySeparatorChar))
  ensureDir (Path.GetDirectoryName filePath)
  File.WriteAllText(filePath, (data:string), Text.Encoding.UTF8)
  id

let downloadCsv container (id:string) =
  let filePath = Path.Combine(containerPath container, id.Replace('/', Path.DirectorySeparatorChar))
  if File.Exists filePath then Some(File.ReadAllText(filePath, Text.Encoding.UTF8))
  else None

let readMetadata<'T> container =
  let metaPath = Path.Combine(containerPath container, "files.json")
  if File.Exists metaPath then
    File.ReadAllText(metaPath, Text.Encoding.UTF8) |> fromJson<'T[]>
  else [||]

let writeMetadata container (files:'T[]) =
  let dir = containerPath container
  ensureDir dir
  let metaPath = Path.Combine(dir, "files.json")
  File.WriteAllText(metaPath, toJson files, Text.Encoding.UTF8)

// --------------------------------------------------------------------------------------
// Keep list of CSV files and cache recently accessed
// --------------------------------------------------------------------------------------

type ParsedFile = (string * string)[] * (string * Value)[][]

type Message<'T> =
  | UploadFile of (string -> 'T) * string * AsyncReplyChannel<'T>
  | FetchFile of string * AsyncReplyChannel<option<ParsedFile>>
  | UpdateRecord of 'T
  | GetRecords of AsyncReplyChannel<'T[]>

let createCacheAgent<'T> container getId getPass : MailboxProcessor<Message<'T>> = MailboxProcessor.Start(fun inbox ->
  let worker () = async {
    let cache = new Dictionary<_, DateTime * _>()
    let files = new Dictionary<_, _>()
    for f in readMetadata container do files.Add(getId f, f)

    while true do
      let! msg = inbox.TryReceive(timeout=1000*60)
      let remove = [ for (KeyValue(k, (t, _))) in cache do if (DateTime.Now - t).TotalMinutes > 5. then yield k ]
      for k in remove do cache.Remove(k) |> ignore
      match msg with
      | None -> ()
      | Some(GetRecords ch) ->
          ch.Reply (Array.ofSeq files.Values)

      | Some(UpdateRecord(file)) ->
          if files.ContainsKey(getId file) && getPass (files.[getId file]) = (getPass file : string) then
            files.[getId file] <- file
            writeMetadata container (Array.ofSeq files.Values)

      | Some(UploadFile(createMeta, data, repl)) ->
          let meta = Seq.initInfinite (generateId DateTime.Today) |> Seq.filter (files.ContainsKey >> not) |> Seq.head |> createMeta
          if files.ContainsKey (getId meta) then repl.Reply(files.[getId meta]) else
          let csv = uploadCsv container (getId meta) data |> createMeta
          files.Add(getId csv, csv)
          writeMetadata container (Array.ofSeq files.Values)
          repl.Reply(csv)

      | Some(FetchFile(id, repl)) ->
          if not (files.ContainsKey id) then repl.Reply(None) else
          if not (cache.ContainsKey id) then
              match downloadCsv container id with
              | Some data -> cache.Add(id, (DateTime.Now, readCsvFile data))
              | None -> ()
          match cache.TryGetValue id with
          | true, (_, res) ->
              cache.[id] <- (DateTime.Now, res)
              repl.Reply(Some res)
          | _ -> repl.Reply None }
  async {
    while true do
      try return! worker ()
      with e -> printfn "Agent failed: %A" e })

// --------------------------------------------------------------------------------------
// Uploaded CSV file handling
// --------------------------------------------------------------------------------------

type UploadedCsvFile =
  { id : string
    hidden : bool
    date : DateTime
    source : string
    title : string
    description : string
    tags : string[]
    passcode : string }
  static member Create(id) =
    { id = id; hidden = true; date = DateTime.Today
      title = ""; source = ""; description = ""; tags = [||];
      passcode = System.Guid.NewGuid().ToString("N") }

let uploads = lazy createCacheAgent "uploads" (fun csv -> csv.id) (fun csv -> csv.passcode)

module Uploads =
  let fetchFile source =
    uploads.Value.PostAndAsyncReply(fun ch -> FetchFile(source, ch))

  let getRecords () =
    uploads.Value.PostAndAsyncReply(GetRecords)

  let updateRecord (body:string) =
    let file = fromJson<UploadedCsvFile> body
    uploads.Value.Post(UpdateRecord file)

  let uploadFile (data:string) = async {
    try
      ignore (readCsvFile data)
      let! file = uploads.Value.PostAndAsyncReply(fun ch -> UploadFile(UploadedCsvFile.Create, data, ch))
      return Choice1Of2 (toJson file)
    with ParseError msg ->
      return Choice2Of2 msg }

// --------------------------------------------------------------------------------------
// Cached CSV file handling
// --------------------------------------------------------------------------------------

type CachedCsvFile =
  { id : string
    url : string }

let cache = lazy createCacheAgent "cache" (fun csv -> csv.id) (fun _ -> "")

module Cache =
  let sha256 = System.Security.Cryptography.SHA256.Create()
  let hash url =
    Uri(url).Host.Replace(".", "-") + "-" +
    ( sha256.ComputeHash(System.Text.UTF8Encoding.UTF8.GetBytes url)
      |> Seq.map (fun s -> s.ToString("x2"))
      |> String.concat "" )

  let fetchFile id =
    cache.Value.PostAndAsyncReply(fun ch -> FetchFile(id, ch))

  let uploadFile url data kind = async {
    try
      ignore (readCsvFile data)
      let mkmeta _ = { id = ((hash url)+kind); url = url }
      let! file = cache.Value.PostAndAsyncReply(fun ch -> UploadFile(mkmeta, data, ch))
      return Choice1Of2 ((hash url)+kind)
    with ParseError msg ->
      return Choice2Of2(msg) }
