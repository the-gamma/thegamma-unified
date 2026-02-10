module TheGamma.Gallery.GalleryLogic

open System
open Newtonsoft.Json
open FSharp.Data
open TheGamma.SnippetService.SnippetAgent

// --------------------------------------------------------------------------------------
// JSON helpers
// --------------------------------------------------------------------------------------

let serializer = JsonSerializer.Create()

let fromJson<'R> str : 'R =
  use tr = new System.IO.StringReader(str)
  serializer.Deserialize(tr, typeof<'R>) :?> 'R

let toJson value =
  let sb = System.Text.StringBuilder()
  use tw = new System.IO.StringWriter(sb)
  serializer.Serialize(tw, value)
  sb.ToString()

// --------------------------------------------------------------------------------------
// Active patterns
// --------------------------------------------------------------------------------------

let (|Lookup|_|) k (map:Map<string,string>) = Map.tryFind k map
let (|NonEmpty|_|) v = if String.IsNullOrWhiteSpace(v) then None else Some v

// --------------------------------------------------------------------------------------
// Snippet types (Gallery's own view of snippets)
// --------------------------------------------------------------------------------------

type GallerySnippet =
  { id : int
    likes : int
    posted : DateTime
    title : string
    description : string
    author : string
    twitter : string
    link : string
    code : string
    version : string
    config : string
    hidden : bool }

// --------------------------------------------------------------------------------------
// Snippet operations (direct calls to SnippetAgent)
// --------------------------------------------------------------------------------------

let mutable currentVersion = "latest"

let updateCurrentVersion () =
  try
    currentVersion <- Http.RequestString("http://thegamma.net/lib/latest.txt")
  with _ -> ()

let readSnippets () = async {
  let! json = agent.PostAndAsyncReply(fun ch -> GetSnippets("thegamma", ch))
  let snips =
    fromJson<GallerySnippet[]> json
    |> Array.sortByDescending (fun s -> s.posted)
    |> Array.map (fun s ->
      { s with
            twitter = if String.IsNullOrWhiteSpace s.twitter then null else s.twitter
            link = if String.IsNullOrWhiteSpace s.link then null else s.link })
  return snips }

let postSnippet (snip:NewSnippet) = async {
  let! id = agent.PostAndAsyncReply(fun ch -> AddSnippet("thegamma", snip, ch))
  return
    { GallerySnippet.id = id; likes = 0; posted = DateTime.UtcNow;
      title = snip.title; description = snip.description;
      link = if String.IsNullOrWhiteSpace snip.link then null else snip.link
      twitter = if String.IsNullOrWhiteSpace snip.twitter then null else snip.twitter
      author = snip.author; code = snip.code; config = snip.config;
      version = snip.version; hidden = snip.hidden } }

// --------------------------------------------------------------------------------------
// Gallery snippet agent (caches snippets in memory)
// --------------------------------------------------------------------------------------

type SnippetMessage =
  | GetSnippet of int * AsyncReplyChannel<GallerySnippet option>
  | ListSnippets of int * AsyncReplyChannel<GallerySnippet[]>
  | InsertSnippet of NewSnippet * AsyncReplyChannel<int>

let snippetAgent = MailboxProcessor.Start(fun inbox ->
  let rec loop snips = async {
    let! msg = inbox.Receive()
    match msg with
    | GetSnippet(id, ch) ->
        ch.Reply(snips |> Array.tryFind (fun s -> s.id = id))
        return! loop snips
    | ListSnippets(max, ch) ->
        ch.Reply(snips |> Array.truncate max)
        return! loop snips
    | InsertSnippet(snip, ch) ->
        let! snip = postSnippet snip
        ch.Reply(snip.id)
        return! loop (Array.append [| snip |] snips) }
  async {
    while true do
      try
        let! snips = readSnippets()
        return! loop snips
      with e -> printfn "Gallery snippet agent has failed: %A" e })

// --------------------------------------------------------------------------------------
// CSV upload (direct calls to CsvService)
// --------------------------------------------------------------------------------------

type CsvFile =
  { id : string
    hidden : bool
    date : DateTime
    source : string
    title : string
    description : string
    tags : string[]
    passcode : string }

let tags = MailboxProcessor.Start(fun inbox ->
  let rec loop (time:DateTime) tags = async {
    if (DateTime.Now - time).TotalSeconds > 300. then
      let! files = TheGamma.CsvService.Storage.Uploads.getRecords()
      let allTags =
        files
        |> Seq.collect (fun f -> f.tags)
        |> Seq.distinct
        |> Array.ofSeq
      return! loop DateTime.Now allTags
    else
      let! (repl:AsyncReplyChannel<_>) = inbox.Receive()
      repl.Reply(tags)
      return! loop time tags }
  async {
    while true do
      try return! loop DateTime.MinValue [||]
      with e -> printfn "Tags agent failed %A" e })

let updateFileInfo (csv:CsvFile) =
  let data = toJson csv
  TheGamma.CsvService.Storage.Uploads.updateRecord data

let uploadData data =
  try
    let result = TheGamma.CsvService.Storage.Uploads.uploadFile data |> Async.RunSynchronously
    match result with
    | Choice1Of2 json -> fromJson<CsvFile> json |> Choice1Of2
    | Choice2Of2 msg -> Choice2Of2 msg
  with e ->
    Choice2Of2 e.Message

// --------------------------------------------------------------------------------------
// reCAPTCHA validation
// --------------------------------------------------------------------------------------

type RecaptchaResponse = JsonProvider<"""{"success":true}""">

let mutable recaptchaSecret = ""

let validateRecaptcha form = async {
  let response = match form with Lookup "g-recaptcha-response" re -> re | _ -> ""
  let! response =
      Http.AsyncRequestString
        ( "https://www.google.com/recaptcha/api/siteverify", httpMethod="POST",
          body=HttpRequestBody.FormValues ["secret", recaptchaSecret; "response", response])
  return RecaptchaResponse.Parse(response).Success }
