module TheGamma.Gallery.Routes

open System
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open Giraffe
open Microsoft.AspNetCore.Http
open DotLiquid
open TheGamma.Gallery.GalleryLogic
open TheGamma.Gallery.Filters
open TheGamma.SnippetService.SnippetAgent

// --------------------------------------------------------------------------------------
// DotLiquid rendering helpers
// --------------------------------------------------------------------------------------

let mutable templateDir = ""
let mutable baseUrl = ""

let initGallery templDir bUrl recaptcha =
  templateDir <- templDir
  baseUrl <- bUrl
  recaptchaSecret <- recaptcha
  Template.NamingConvention <- NamingConventions.CSharpNamingConvention()
  Template.RegisterFilter(typeof<Filters.FiltersType>)
  updateCurrentVersion()

let renderTemplate (name:string) (model:obj) =
  let templatePath = Path.Combine(templateDir, name)
  let templateContent = File.ReadAllText(templatePath)
  let template = Template.Parse(templateContent)
  let hash = Hash.FromAnonymousObject(model)
  template.Render(hash)

let dotLiquidPage (name:string) (model:obj) : HttpHandler =
  fun next ctx -> task {
    let html = renderTemplate name model
    ctx.SetContentType "text/html; charset=utf-8"
    let! _ = ctx.WriteStringAsync(html)
    return Some ctx }

// --------------------------------------------------------------------------------------
// Create page model
// --------------------------------------------------------------------------------------

type CreateModel =
  { PastedData : string
    TransformSource : string
    DataTags : string[]
    VizSource : string
    ChartType : string
    UploadId : string
    UploadPasscode : string
    UploadError : string
    CurrentVersion : string }

// --------------------------------------------------------------------------------------
// Snippet insertion handler
// --------------------------------------------------------------------------------------

let insertSnippetHandler (form:Map<string,string>) : HttpHandler =
  fun next ctx -> task {
    let! valid = validateRecaptcha form |> Async.StartAsTask
    if not valid then
      let msg = "Human validation using ReCaptcha failed."
      return! dotLiquidPage "error.html" (box msg) next ctx
    else
      match form with
      | Lookup "title" (NonEmpty title) &
        Lookup "description" (NonEmpty descr) &
        Lookup "source" (NonEmpty source) &
        Lookup "author" (NonEmpty author) &
        Lookup "link" link & Lookup "twitter" twitter ->
          let version = defaultArg (form.TryFind("version")) currentVersion
          let newSnip : NewSnippet =
            { title = title; description = descr; author = author;
              twitter = twitter.TrimStart('@'); link = link; compiled = ""; code = source;
              hidden = false; config = defaultArg (form.TryFind "config") ""; version = version }
          let! id = snippetAgent.PostAndAsyncReply(fun ch -> InsertSnippet(newSnip, ch)) |> Async.StartAsTask
          let url = sprintf "/gallery/%d/%s" id (cleanTitle title)
          return! redirectTo false url next ctx
      | _ ->
          let msg = "Some of the inputs for the snippet were not valid."
          return! dotLiquidPage "error.html" (box msg) next ctx }

// --------------------------------------------------------------------------------------
// Create page handler
// --------------------------------------------------------------------------------------

let readFormFields (ctx:HttpContext) = task {
  let form = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
  if ctx.Request.HasFormContentType then
    let! formCollection = ctx.Request.ReadFormAsync()
    for kvp in formCollection do
      form.[kvp.Key] <- kvp.Value.ToString()
    for file in formCollection.Files do
      use reader = new StreamReader(file.OpenReadStream())
      let! content = reader.ReadToEndAsync()
      form.[file.Name] <- content
  return form }

let createPageHandler : HttpHandler =
  fun next ctx -> task {
    updateCurrentVersion()
    let! (formDict:Dictionary<string,string>) = readFormFields ctx
    let inputs =
      formDict
      |> Seq.map (fun (kvp:KeyValuePair<string,string>) -> kvp.Key, kvp.Value)
      |> Map.ofSeq

    let model =
      { DataTags = [||]
        VizSource = defaultArg (inputs.TryFind("viz-source")) ""
        TransformSource = defaultArg (inputs.TryFind("transform-source")) ""
        ChartType = defaultArg (inputs.TryFind("chart-type")) ""
        PastedData = defaultArg (inputs.TryFind("pasted-data")) ""
        UploadId = defaultArg (inputs.TryFind("upload-id")) ""
        UploadPasscode = defaultArg (inputs.TryFind("upload-passcode")) ""
        UploadError = defaultArg (inputs.TryFind("upload-error")) ""
        CurrentVersion = currentVersion }

    match inputs.TryFind "nextstep" with
    | Some "step5" ->
        let dstags =
          match (formDict:Dictionary<string,string>).TryGetValue("dstags") with
          | true, v -> v.Split(',') |> Array.toList
          | _ -> []
        let settings, source =
          match inputs, dstags, model.UploadId, model.UploadPasscode with
          | Lookup "dstitle" (NonEmpty title) &
            Lookup "dssource" (NonEmpty src) &
            Lookup "dsdescription" (NonEmpty description), (NonEmpty _ :: _),
              NonEmpty id, NonEmpty passcode ->
              let csv : CsvFile =
                { id = id; hidden = false; date = DateTime.UtcNow; source = src; passcode = passcode
                  title = title.Replace("'", ""); description = description; tags = Array.ofList dstags }
              updateFileInfo csv
              let month = DateTime.UtcNow.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
              "", (model.VizSource.Replace("uploaded", sprintf "shared.'by date'.'%s'.'%s'" month csv.title))
          | _, _, NonEmpty uploadId, _ ->
              sprintf """{ "providers": [ ["uploaded", "pivot", "%s/csv/providers/csv/%s"] ] }""" baseUrl uploadId,
              model.VizSource
          | _ -> "", model.VizSource
        let form =
          inputs
          |> Map.add "source" source
          |> Map.add "config" settings
        return! insertSnippetHandler form next ctx

    | Some "step4" ->
        let! tagsList = tags.PostAndAsyncReply id |> Async.StartAsTask
        return! dotLiquidPage "create-step4.html" (box { model with DataTags = tagsList }) next ctx

    | Some "step3" ->
        let op =
          match model.ChartType with
          | "bar-chart" ->
              "chart.bar(data)\n  .set(title=\"Enter chart title\", colors=[\"#77aae0\"])\n  .set(fontName=\"Roboto\", fontSize=13)\n  .legend(position=\"none\")"
          | "line-chart" ->
              "chart.line(data.setProperties(seriesName=\"Enter data series description\"))\n  .set(title=\"Enter chart title\", colors=[\"#5588e0\"])\n  .set(fontName=\"Roboto\", fontSize=13)\n  .legend(position=\"bottom\")"
          | _ -> "table.create(data)"
        let model =
          if not (inputs.ContainsKey "gotoviz") && not (String.IsNullOrWhiteSpace model.VizSource) then model
          else
            let src = "let data =" + Regex.Replace("\n" + model.TransformSource.Trim(), "[\r\n]+", "\n  ") + "\n\n" + op
            { model with VizSource = src }
        return! dotLiquidPage "create-step3.html" (box model) next ctx

    | Some "step2" ->
        let skip, model =
          match inputs.TryFind("uploadcsv"), inputs.TryFind("usepasted"), inputs.TryFind("uploaddata"), inputs.TryFind("usesample"), inputs.TryFind("samplesource"), inputs.TryFind("skipsample") with
          | Some (NonEmpty data), _, _, _, _, _ ->
              match uploadData data with
              | Choice2Of2 error -> false, { model with PastedData = data; UploadError = error }
              | Choice1Of2 csv -> false, { model with PastedData = ""; TransformSource = "uploaded\n  .'drop columns'.then\n  .'get the data'"; UploadId = csv.id; UploadPasscode = csv.passcode }
          | _, Some _, _, _, _, _ ->
              match inputs.TryFind("uploaddata") with
              | Some (NonEmpty data) ->
                  match uploadData data with
                  | Choice2Of2 error -> false, { model with PastedData = data; UploadError = error }
                  | Choice1Of2 csv -> false, { model with PastedData = data; TransformSource = "uploaded\n  .'drop columns'.then\n  .'get the data'"; UploadId = csv.id; UploadPasscode = csv.passcode }
              | _ -> false, model
          | _, _, _, Some _, Some (NonEmpty sample), _ -> false, { model with TransformSource = sample }
          | _, _, _, _, _, Some _ -> true, { model with VizSource = "chart.line(series.values([1,2,0]))" }
          | _ -> false, model
        if skip then return! dotLiquidPage "create-step3.html" (box model) next ctx
        elif model.UploadError = "" then return! dotLiquidPage "create-step2.html" (box model) next ctx
        else return! dotLiquidPage "create-step1.html" (box model) next ctx

    | _ -> return! dotLiquidPage "create-step1.html" (box model) next ctx }

// --------------------------------------------------------------------------------------
// Insert page handler
// --------------------------------------------------------------------------------------

let insertPageHandler : HttpHandler =
  fun next ctx -> task {
    let! (formDict:Dictionary<string,string>) = readFormFields ctx
    let form = formDict |> Seq.map (fun (kvp:KeyValuePair<string,string>) -> kvp.Key.ToLower(), kvp.Value) |> Map.ofSeq
    return! insertSnippetHandler form next ctx }

// --------------------------------------------------------------------------------------
// Routes
// --------------------------------------------------------------------------------------

let handler : HttpHandler =
  choose [
    GET >=> route "/" >=> fun next ctx -> task {
      let! snips = snippetAgent.PostAndAsyncReply(fun ch -> ListSnippets(8, ch)) |> Async.StartAsTask
      return! dotLiquidPage "home.html" (box snips) next ctx }

    GET >=> route "/all" >=> fun next ctx -> task {
      let! snips = snippetAgent.PostAndAsyncReply(fun ch -> ListSnippets(Int32.MaxValue, ch)) |> Async.StartAsTask
      return! dotLiquidPage "home.html" (box snips) next ctx }

    POST >=> route "/create" >=> createPageHandler
    GET >=> route "/create" >=> createPageHandler
    POST >=> route "/insert" >=> insertPageHandler

    GET >=> routef "/%i/%s/embed" (fun (id, _) ->
      fun next ctx -> task {
        let! snip = snippetAgent.PostAndAsyncReply(fun ch -> GetSnippet(id, ch)) |> Async.StartAsTask
        match snip with
        | Some snip -> return! dotLiquidPage "embed.html" (box snip) next ctx
        | _ ->
          ctx.SetStatusCode 404
          return! dotLiquidPage "404.html" (box "") next ctx })

    GET >=> routef "/%i/%s" (fun (id, _) ->
      fun next ctx -> task {
        let! snip = snippetAgent.PostAndAsyncReply(fun ch -> GetSnippet(id, ch)) |> Async.StartAsTask
        match snip with
        | Some snip -> return! dotLiquidPage "snippet.html" (box snip) next ctx
        | _ ->
          ctx.SetStatusCode 404
          return! dotLiquidPage "404.html" (box "") next ctx })
  ]
