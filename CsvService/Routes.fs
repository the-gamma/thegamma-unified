module TheGamma.CsvService.Routes

open System
open System.IO
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Data
open TheGamma.Common
open TheGamma.CsvService.Storage
open TheGamma.CsvService.Pivot
open TheGamma.CsvService.Listing
open TheGamma.CsvService.WebScrape

let mutable baseUrl = ""
let initService url storageRoot =
  baseUrl <- url
  Storage.initStorage storageRoot

let xcookie (f: System.Collections.Generic.IDictionary<string, string> -> HttpHandler) : HttpHandler =
  fun next ctx -> task {
    match ctx.Request.Headers.TryGetValue("x-cookie") with
    | true, values ->
        let v = values.ToString()
        let cks = v.Split([|"&"|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun k ->
          match k.Split('=') with [|k;v|] -> k, System.Web.HttpUtility.UrlDecode v | _ -> failwith "Wrong cookie!") |> dict
        return! f cks next ctx
    | _ -> return None }

let handler : HttpHandler =
  choose [
    GET >=> route "/" >=> text "Service is running..."
    POST >=> route "/update" >=> fun next ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      Uploads.updateRecord body
      return! text "" next ctx }
    POST >=> route "/upload" >=> fun next ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! data = reader.ReadToEndAsync()
      let! result = Uploads.uploadFile data
      match result with
      | Choice1Of2 json -> return! text json next ctx
      | Choice2Of2 msg -> return! RequestErrors.BAD_REQUEST msg next ctx }
    GET >=> route "/tags" >=> fun next ctx -> task {
      let! files = Uploads.getRecords ()
      let tags =
        files
        |> Seq.collect (fun f -> f.tags)
        |> Seq.distinct
        |> Seq.map (fun t -> JsonValue.Array [| JsonValue.String (getTagId t); JsonValue.String t |])
        |> Array.ofSeq
        |> JsonValue.Array
      return! text (tags.ToString()) next ctx }

    // CORS + data routes
    route "/providers/data/" >=>
      ( Serializer.serializeMembers [
          Member("loadTable", Some [Parameter("url", Type.Named("string"), false, ParameterKind.Static("url"))], Result.Nested("/upload"), [], [])
          Member("scrapeLists", Some [Parameter("url", Type.Named("string"), false, ParameterKind.Static("url"))], Result.Nested("/getAllEntries"), [], [])
          Member("scrapeDatedLists",
            Some [
              Parameter("url", Type.Named("string"), false, ParameterKind.Static("url"));
              Parameter("year", Type.Named("int"), false, ParameterKind.Static("year"))],
            Result.Nested("/getDatedEntries"), [], [])
          Member("scrape", Some [Parameter("url", Type.Named("string"), false, ParameterKind.Static("url"))], Result.Nested("/getAllEntries"), [], [])
        ] |> text )

    route "/providers/data/upload" >=> xcookie (fun ck ->
      fun next ctx -> task {
        use wc = new System.Net.WebClient()
        let url = ck.["url"]
        let! file = wc.AsyncDownloadString(Uri(url))
        let! upload = Cache.uploadFile url file "uploadedCSV"
        match upload with
        | Choice2Of2 msg -> return! RequestErrors.BAD_REQUEST msg next ctx
        | Choice1Of2 id ->
            let result = Serializer.serializeMembers [
              Member("preview", None, Result.Nested("/null"), [], [])
              Member("explore", None, Result.Provider("pivot", baseUrl + "/csv/providers/data/query/" + id), [], [])
            ]
            return! text result next ctx })

    routef "/providers/data/upload/%s" (fun url ->
      fun next ctx -> task {
        use wc = new System.Net.WebClient()
        let url = if url.StartsWith("http:/") && not (url.StartsWith("http://")) then url.Replace("http:/", "http://") else url
        let url = if url.StartsWith("https:/") && not (url.StartsWith("https://")) then url.Replace("https:/", "https://") else url
        let! file = wc.AsyncDownloadString(Uri(url))
        let! upload = Cache.uploadFile url file "uploadedCSV"
        match upload with
        | Choice2Of2 msg -> return! RequestErrors.BAD_REQUEST msg next ctx
        | Choice1Of2 id ->
            let result = Serializer.serializeMembers [
              Member("preview", None, Result.Nested("/null"), [], [])
              Member("explore", None, Result.Provider("pivot", baseUrl + "/csv/providers/data/query/" + id), [], [])
            ]
            return! text result next ctx })

    routef "/providers/data/query/%s" (fun id ->
      fun next ctx -> task {
        let! file = Cache.fetchFile id
        match file with
        | None -> return! RequestErrors.BAD_REQUEST "File has not been uploaded." next ctx
        | Some(meta, data) ->
            let qs = ctx.Request.QueryString.Value
            let query = if System.String.IsNullOrEmpty(qs) then [] else qs.TrimStart('?').Split('&') |> Array.map (fun s -> s.Split('=').[0]) |> List.ofArray
            let result = Pivot.handleRequest meta data query
            return! text result next ctx })

    route "/providers/data/getAllEntries" >=> xcookie (fun ck ->
      fun next ctx -> task {
        let url = ck.["url"]
        let csv = getAllEntries url
        let! upload = Cache.uploadFile url (csv.SaveToString()) "allEntries"
        match upload with
        | Choice2Of2 msg -> return! RequestErrors.BAD_REQUEST msg next ctx
        | Choice1Of2 id ->
            let result = Serializer.serializeMembers [
              Member("preview", None, Result.Nested("/null"), [],
                [ Schema("http://schema.org", "WebPage", ["url", JsonValue.String url ])
                  Schema("http://schema.thegamma.net", "CompletionItem", ["hidden", JsonValue.Boolean true ]) ])
              Member("explore", None, Result.Provider("pivot", baseUrl + "/csv/providers/data/query/" + id), [], [])
            ]
            return! text result next ctx })

    route "/providers/data/getDatedEntries" >=> xcookie (fun ck ->
      fun next ctx -> task {
        let url = ck.["url"]
        let year = ck.["year"]
        let csv = getDatedEntries year url
        let! upload = Cache.uploadFile url (csv.SaveToString()) ("fixed-datedEntries-3-" + year)
        match upload with
        | Choice2Of2 msg -> return! RequestErrors.BAD_REQUEST msg next ctx
        | Choice1Of2 id ->
            let result = Serializer.serializeMembers [
              Member("preview", None, Result.Nested("/null"), [],
                [ Schema("http://schema.org", "WebPage", ["url", JsonValue.String url ])
                  Schema("http://schema.thegamma.net", "CompletionItem", ["hidden", JsonValue.Boolean true ]) ])
              Member("explore", None, Result.Provider("pivot", baseUrl + "/csv/providers/data/query/" + id), [], [])
            ]
            return! text result next ctx })

    routef "/providers/listing%s" (fun _ ->
      fun next ctx -> task {
        let! files = Uploads.getRecords ()
        let visibleFiles = files |> Seq.filter (fun f -> not f.hidden)
        let tags = visibleFiles |> Seq.collect (fun f -> f.tags) |> Seq.map (fun t -> getTagId t, t) |> dict
        let dates = visibleFiles |> Seq.map (fun f -> f.date.Year, f.date.Month) |> Seq.distinct |> Seq.sort
        let localPath = ctx.Request.Path.Value
        let result =
          if localPath.EndsWith("/providers/listing/") || localPath.EndsWith("/providers/listing") then
            Serializer.serializeMembers [
              Member("by date", None, Result.Nested("date/"), [], [])
              Member("by tag", None, Result.Nested("tag/"), [], [])
            ]
          elif localPath.Contains("/providers/listing/date/") then
            let datePart = localPath.Substring(localPath.IndexOf("/date/") + 6).TrimEnd('/')
            if System.String.IsNullOrEmpty(datePart) then
              Serializer.serializeMembers [
                for y, m in dates ->
                  let name = System.Globalization.DateTimeFormatInfo.InvariantInfo.GetMonthName(m)
                  Member(name + " " + string y, None, Result.Nested("date/" + string y + "-" + string m + "/"), [], [])
              ]
            else
              let parts = datePart.Split('-')
              let y, m = int parts.[0], int parts.[1]
              Serializer.serializeMembers [
                for file in visibleFiles do
                  if file.date.Year = y && file.date.Month = m then
                    yield Member(file.title, None, Result.Provider("pivot", baseUrl + "/csv/providers/csv/" + file.id), [], [])
              ]
          elif localPath.Contains("/providers/listing/tag/") then
            let tagPart = localPath.Substring(localPath.IndexOf("/tag/") + 5).TrimEnd('/')
            if System.String.IsNullOrEmpty(tagPart) then
              Serializer.serializeMembers [
                for (KeyValue(tid, t)) in tags ->
                  Member(t, None, Result.Nested("tag/" + tid + "/"), [], [])
              ]
            else
              Serializer.serializeMembers [
                for file in visibleFiles do
                  if file.tags |> Seq.exists (fun t -> getTagId t = tagPart) then
                    yield Member(file.title, None, Result.Provider("pivot", baseUrl + "/csv/providers/csv/" + file.id), [], [])
              ]
          else
            Serializer.serializeMembers []
        return! text result next ctx })

    routef "/providers/csv/%s" (fun source ->
      fun next ctx -> task {
        let! file = Uploads.fetchFile source
        match file with
        | None -> return! RequestErrors.BAD_REQUEST (sprintf "File with id '%s' does not exist!" source) next ctx
        | Some (meta, data) ->
            let qs = ctx.Request.QueryString.Value
            let query = if System.String.IsNullOrEmpty(qs) then [] else qs.TrimStart('?').Split('&') |> Array.map (fun s -> s.Split('=').[0]) |> List.ofArray
            let result = Pivot.handleRequest meta data query
            return! text result next ctx })
  ]
