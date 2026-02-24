module TheGamma.Services.WorldBank.Routes

#nowarn "1104"
open System
open System.IO
open System.Collections.Generic
open FSharp.Data
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Common.JsonHelpers
open TheGamma.Services.WorldBank

let mutable cacheDir = ""

let worldBank = lazy CacheSerializer.readCache cacheDir

let initData dir =
  cacheDir <- dir

type ThingSchema = { ``@context``:string; ``@type``:string; name:string; }
type GenericType = { name:string; ``params``:obj[] }
type TypePrimitive = { kind:string; ``type``:obj; endpoint:string }
type TypeNested = { kind:string; endpoint:string }
type Member = { name:string; returns:obj; trace:string[]; schema:ThingSchema }

let noSchema = Unchecked.defaultof<ThingSchema>
let makeSchemaThing kind name =
  { ``@context`` = "http://schema.org/"; ``@type`` = kind; name = name }
let makeSchemaExt kind name =
  { ``@context`` = "http://thegamma.net/worldbank"; ``@type`` = kind; name = name }

let memberRoute s f : HttpHandler =
  route s >=> fun next ctx -> task {
    let result = f() |> Array.ofSeq |> toJson
    return! text result next ctx }

let memberRoutef fmt f : HttpHandler =
  routef fmt (fun arg -> fun next ctx -> task {
    let result = f arg |> Array.ofSeq |> toJson
    return! text result next ctx })

let (|Lookup|_|) k (dict:IDictionary<_,_>) =
  match dict.TryGetValue k with
  | true, v -> Some v
  | _ -> None

let handler : HttpHandler =
  choose [
    memberRoute "/" (fun () ->
      [ { name="byYear"; returns= {kind="nested"; endpoint="/pickYear"}
          trace=[| |]; schema = noSchema }
        { name="byCountry"; returns= {kind="nested"; endpoint="/pickCountry"}
          trace=[| |]; schema = noSchema } ])

    memberRoute "/pickCountry" (fun () ->
      [ for (KeyValue(id, country)) in worldBank.Value.Countries ->
          { name=country.Name; returns={kind="nested"; endpoint="/byCountry/pickTopic"}
            trace=[|"country=" + id |]; schema = makeSchemaThing "Country" country.Name } ])

    memberRoute "/pickYear" (fun () ->
      [ for (KeyValue(id, year)) in worldBank.Value.Years ->
          { name=year.Year; returns={kind="nested"; endpoint="/byYear/pickTopic"}
            trace=[|"year=" + id |]; schema = makeSchemaExt "Year" year.Year } ])

    memberRoutef "/%s/pickTopic" (fun by ->
      [ for (KeyValue(id, top)) in worldBank.Value.Topics ->
          { name=top.Name; returns={kind="nested"; endpoint="/" + by + "/pickIndicator/" + id}
            trace=[||]; schema = makeSchemaExt "Topic" top.Name } ])

    memberRoutef "/%s/pickIndicator/%s" (fun (by, topic) ->
      [ for ikey in worldBank.Value.Topics.[topic].Indicators ->
          let ind = worldBank.Value.Indicators.[ikey]
          let typ =
              if by = "byYear" then { name="tuple"; ``params``=[| "string"; "float" |] }
              elif by = "byCountry" then { name="tuple"; ``params``=[| "int"; "float" |] }
              else failwith "bad request"
          let typ = { name="seq"; ``params``=[| typ |]}
          { name=ind.Name; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
            trace=[|"indicator=" + ikey |]; schema = makeSchemaExt "Indicator" ind.Name } ])

    route "/data" >=> fun next ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      let trace =
        [ for kvps in body.Split('&') ->
            match kvps.Split('=') with
            | [| k; v |] -> k, v
            | _ -> failwith "wrong trace" ] |> dict

      let result =
        match trace with
        | (Lookup "year" y) & (Lookup "indicator" i) ->
            let ydet, idet = worldBank.Value.Years.[y], worldBank.Value.Indicators.[i]
            worldBank.Value.Data
            |> Seq.filter (fun dt -> dt.Year = ydet.Index && dt.Indicator = idet.Index )
            |> Seq.choose (fun dt ->
                match worldBank.Value.CountriesByIndex.TryGetValue(dt.Country) with
                | true, country when country.RegionName = "Aggregates" -> None
                | true, country -> Some(country.Name, dt.Value)
                | _ -> None )
            |> formatPairSeq JsonValue.String
        | (Lookup "country" c) & (Lookup "indicator" i) ->
            let cdet, idet = worldBank.Value.Countries.[c], worldBank.Value.Indicators.[i]
            worldBank.Value.Data
            |> Seq.filter (fun dt -> dt.Country = cdet.Index && dt.Indicator = idet.Index )
            |> Seq.map (fun dt -> worldBank.Value.YearsByIndex.[dt.Year].Year, dt.Value)
            |> formatPairSeq JsonValue.String
        | _ -> failwith "wrong trace"
      return! text result next ctx } ]
