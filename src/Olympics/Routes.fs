module TheGamma.Olympics.Routes

open System
open System.IO
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open Giraffe
open Microsoft.AspNetCore.Http
open DotLiquid
open FSharp.Formatting.Markdown
open TheGamma.SnippetService.SnippetAgent

// --------------------------------------------------------------------------------------
// Data types
// --------------------------------------------------------------------------------------

type Article =
  { id : string
    index : int
    heading : string
    category : string
    before : string
    code : string
    compiled : string
    after : string
    author : string
    twitter : string
    plaintext : bool }

type Category =
  { mainArticle : Article
    moreArticles : Article seq }

type Home =
  { debug : bool
    categories : Category seq }

type Main =
  { debug : bool
    mainArticle : Article
    moreArticles : Article seq }

// --------------------------------------------------------------------------------------
// DotLiquid rendering (separate template dir from Gallery)
// --------------------------------------------------------------------------------------

let mutable olympicsTemplateDir = ""

/// File system that resolves {% include %} / {% extends %} by trying multiple directories in order.
/// Used to serve both Gallery templates (default.html, etc.) and Olympics templates (page.html, etc.)
/// without conflicts — set once at startup, no per-request state.
type CombinedTemplateFileSystem(roots:string list) =
  interface DotLiquid.FileSystems.IFileSystem with
    member _.ReadTemplateFile(_context, templateName) =
      let name = templateName.Trim('"')
      roots
      |> List.tryPick (fun root ->
          let path = Path.Combine(root, name)
          if File.Exists(path) then Some (File.ReadAllText(path)) else None)
      |> Option.defaultWith (fun () -> failwithf "Template '%s' not found in any template directory" name)

let renderTemplate (name:string) (model:obj) =
  if not (isNull model) then TheGamma.Gallery.Routes.registerTypeTree (model.GetType())
  let templateContent = File.ReadAllText(Path.Combine(olympicsTemplateDir, name))
  let template = Template.Parse(templateContent)
  let hash = Hash()
  hash.["model"] <- model
  template.Render(hash)

let dotLiquidPage (name:string) (model:obj) : HttpHandler =
  fun next ctx -> task {
    let html = renderTemplate name model
    ctx.SetContentType "text/html; charset=utf-8"
    let! _ = ctx.WriteStringAsync(html)
    return Some ctx }

// --------------------------------------------------------------------------------------
// Markdown loading
// --------------------------------------------------------------------------------------

let docs =
  [ "athlete", "medals-per-athlete"
    "timeline", "countries-timeline"
    "timeline", "distance-run-timeline"
    "timeline", "disciplines-timeline"
    "country", "top-5-countries"
    "country", "long-distance-medals"
    "country", "medals-per-country"
    "phelps", "phelps-as-country"
    "phelps", "athlete-drill-down"
    "phelps", "athlete-break-down"
    "data", "about-the-data" ]

let mutable loadedArticles : Article[] = [||]
let mutable loadedCategories : Category[] = [||]
let mutable servicesUrl = "https://services.thegamma.net/services"

let split (pars:MarkdownParagraph list) =
  let rec before head acc = function
    | MarkdownParagraph.CodeBlock(code=code)::MarkdownParagraph.CodeBlock(code=compiled)::ps ->
        head, List.rev acc, (code, compiled), ps
    | MarkdownParagraph.CodeBlock(code=code)::ps ->
        head, List.rev acc, (code, ""), ps
    | MarkdownParagraph.HorizontalRule _ ::ps ->
        head, List.rev acc, ("", ""), ps
    | p::ps -> before head (p::acc) ps
    | [] -> head, List.rev acc, ("", ""), []
  match pars with
  | MarkdownParagraph.Heading(body=[MarkdownSpan.Literal(text=head)])::ps ->
      before head [] ps
  | MarkdownParagraph.Heading(body=spans)::ps ->
      let head = spans |> List.choose (function MarkdownSpan.Literal(text=t) -> Some t | _ -> None) |> String.concat ""
      before head [] ps
  | _ -> failwith "Invalid document: No heading found"

let readArticle i (category, id) (docsDir:string) =
  let file = Path.Combine(docsDir, id + ".md")
  let doc = Markdown.Parse(File.ReadAllText(file))
  let head, beforePars, (code, compiled), afterPars = split doc.Paragraphs
  let format pars = Markdown.ToHtml(MarkdownDocument(pars, doc.DefinedLinks))
  { id = id; category = category; code = code; index = i
    author = ""; twitter = ""; plaintext = String.IsNullOrEmpty code
    compiled =
      compiled
        .Replace("http://thegamma-services.azurewebsites.net/pivot",
                 servicesUrl + "/pivot")
        .Replace("http://thegamma-services.azurewebsites.net/olympics",
                 servicesUrl + "/olympics")
        .Replace("html.img(\"/img/", "html.img(\"/rio2016/img/")
    heading = head
    before = format beforePars
    after = format afterPars }

let initData (docsDir:string) (templDir:string) (baseUrl:string) =
  olympicsTemplateDir <- templDir
  servicesUrl <- baseUrl + "/services"
  // Replace Gallery's template file system with a combined one that also serves Olympics templates.
  // Gallery templates extend "default.html"; Olympics templates extend "page.html" — no filename conflicts.
  Template.FileSystem <- CombinedTemplateFileSystem([TheGamma.Gallery.Routes.templateDir; templDir])
  let articles = docs |> List.mapi (fun i entry -> readArticle i entry docsDir) |> Array.ofList
  loadedArticles <- articles
  loadedCategories <-
    articles
    |> Array.groupBy (fun a -> a.category)
    |> Array.map (fun (_, arts) ->
        { mainArticle = arts.[0]
          moreArticles = arts.[1..] :> Article seq })

// --------------------------------------------------------------------------------------
// Page model helpers
// --------------------------------------------------------------------------------------

let loadPage (id:string) =
  let first = loadedArticles |> Array.tryFind (fun a -> a.id = id)
  match first with
  | Some first ->
      let others =
        loadedArticles
        |> Array.filter (fun a -> a.id <> first.id)
        |> Array.sortBy (fun a -> (if a.category <> first.category then 1 else 0), a.index)
      { debug = false; mainArticle = first; moreArticles = others }
  | None ->
      { debug = false; mainArticle = loadedArticles.[0]; moreArticles = loadedArticles.[1..] }

let loadHome () =
  { debug = false; categories = loadedCategories }

// --------------------------------------------------------------------------------------
// Shared snippets
// --------------------------------------------------------------------------------------

let titleToUrl (s:string) =
  let mutable lastDash = false
  let chars =
    [| for c in s.ToLower() do
        if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') then
          lastDash <- false; yield c
        elif not lastDash then
          lastDash <- true; yield '-' |]
  String(chars).Trim('-')

let loadShared (json:string) (idOpt:int option) =
  let snips = fromJson<Snippet[]> json
  let filtered = snips |> Array.filter (fun s -> not s.hidden)
  if filtered.Length = 0 then
    { debug = false; mainArticle = { id = "shared"; index = 0; heading = "Shared Visualizations"
                                     category = "shared"; before = "<p>No visualizations have been shared yet.</p>"
                                     code = ""; compiled = ""; after = ""; author = ""; twitter = "";
                                     plaintext = true }
      moreArticles = [||] }
  else
    let sorted =
      filtered
      |> Array.sortByDescending (fun snip -> Some snip.id = idOpt, snip.likes)
      |> Array.mapi (fun i snip ->
          let info = Markdown.Parse(snip.description)
          let pars = info.Paragraphs |> List.filter (function MarkdownParagraph.InlineHtmlBlock _ -> false | _ -> true)
          let infoHtml = Markdown.ToHtml(MarkdownDocument(pars, info.DefinedLinks))
          let outid = sprintf "outshared-%d-%s" snip.id (titleToUrl snip.title)
          { id = sprintf "shared/%d/%s" snip.id (titleToUrl snip.title)
            index = i; heading = snip.title; category = "shared"
            compiled = snip.compiled.Replace("output-id-placeholder", outid)
            author = snip.author; twitter = snip.twitter.TrimStart('@')
            before = infoHtml; code = snip.code; after = ""; plaintext = false })
    { debug = false; mainArticle = sorted.[0]; moreArticles = sorted.[1..] }

// --------------------------------------------------------------------------------------
// Route handlers
// --------------------------------------------------------------------------------------

let handler : HttpHandler =
  choose [
    GET >=> choose [ route "/"; route "" ] >=> fun next ctx -> task {
      return! dotLiquidPage "home.html" (box (loadHome ())) next ctx }

    GET >=> routef "/shared/%i/%s" (fun (id, _) ->
      fun next ctx -> task {
        let! json = agent.PostAndAsyncReply(fun ch -> GetSnippets("olympics", ch)) |> Async.StartAsTask
        return! dotLiquidPage "main.html" (box (loadShared json (Some id))) next ctx })

    GET >=> route "/shared" >=> fun next ctx -> task {
      let! json = agent.PostAndAsyncReply(fun ch -> GetSnippets("olympics", ch)) |> Async.StartAsTask
      return! dotLiquidPage "main.html" (box (loadShared json None)) next ctx }

    GET >=> routef "/%s" (fun id ->
      fun next ctx -> task {
        if loadedArticles |> Array.exists (fun a -> a.id = id) then
          return! dotLiquidPage "main.html" (box (loadPage id)) next ctx
        else
          return! next ctx })
  ]
