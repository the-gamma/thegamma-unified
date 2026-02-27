module TheGamma.Services.Olympics.Routes

#nowarn "1104"
open System
open System.IO
open FSharp.Data
open System.Collections.Generic
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Common.JsonHelpers
open TheGamma.Common.Facets

let [<Literal>] Root = __SOURCE_DIRECTORY__ + "/../../../data/medals-expanded.csv"
type Medals = CsvProvider<Root, Schema="Gold=int, Silver=int, Bronze=int">

let mutable dataRoot = ""

let initData dir =
  dataRoot <- dir

type Codes = FSharp.Data.HtmlProvider<const(__SOURCE_DIRECTORY__ + "/../../../data/countrycodes.html")>

let medals = lazy Medals.Load(Path.Combine(dataRoot, "medals-expanded.csv"))

let countries = lazy (
  [ yield "KOS", "Kosovo"
    yield "SRB", "Serbia"
    yield "TTO", "Trinidad and Tobago"
    for r in Codes.Parse(File.ReadAllText(Path.Combine(dataRoot, "countrycodes.html"))).Tables.``3-Digit Country Codes``.Rows do
      yield r.Code, r.Country.TrimEnd('*') ] |> dict )

let sports = lazy (
  medals.Value.Rows
  |> Seq.map (fun o -> o.Sport)
  |> Seq.distinct
  |> Seq.mapi (fun i s -> sprintf "sport-%d" i, s)
  |> dict )

let nocs = lazy (
  medals.Value.Rows
  |> Seq.map (fun o -> o.Team)
  |> Seq.distinct
  |> Seq.mapi (fun i s -> sprintf "noc-%d" i, countries.Value.[s])
  |> dict )

let facets : Lazy<list<string * Facet<Medals.Row>>> = lazy (
  [ // Single-choice
    yield "game", Filter("game", false, fun r -> Some(r.Games, makeThingSchema "City" r.Games))
    yield "medal", Filter("medal", false, fun r -> Some(r.Medal, noSchema))
    yield "gender", Filter("gender", false, fun r -> Some(r.Gender, noSchema))
    yield "team", Filter("team", false, fun r -> Some(r.Team, makeThingSchema "Country" r.Team))
    yield "discipline", Filter("discipline", false, fun r -> Some(r.Discipline, makeThingSchema "SportsEvent" r.Sport))

    // Multi-choice
    yield "games", Filter("game", true, fun r -> Some(r.Games, makeThingSchema "City" r.Games))
    yield "medals", Filter("medal", true, fun r -> Some(r.Medal, noSchema))
    yield "teams", Filter("teams", true, fun r -> Some(r.Team, makeThingSchema "Country" r.Team))
    yield "disciplines", Filter("disciplines", true, fun r -> Some(r.Discipline, makeThingSchema "SportsEvent" r.Sport))

    // Multi-level facet with/without multi-choice
    let athleteChoice multi =
      [ for (KeyValue(k,v)) in nocs.Value ->
          k, v, makeThingSchema "Country" v, Filter("athlete-" + k, multi, fun (r:Medals.Row) ->
            if r.Team = v then Some(r.Athlete, makeThingSchema "Person" r.Athlete) else None) ]
    let sportChoice multi =
      [ for (KeyValue(k,v)) in sports.Value ->
          k, v, makeThingSchema "SportsEvent" v, Filter("event" + k, multi, fun (r:Medals.Row) ->
            if r.Sport = v then Some(r.Event, makeThingSchema "SportsEvent" r.Event) else None) ]

    yield "athlete", Choice("country", athleteChoice false)
    yield "athletes", Choice("country", athleteChoice true)
    yield "sport", Choice("sport", sportChoice false)
    yield "sports", Choice("sport", sportChoice true) ] )

let getFld i v = Microsoft.FSharp.Reflection.FSharpValue.GetTupleField(v, i)

let handler : HttpHandler = fun next ctx -> task {
  let app =
    createFacetApp
      (medals.Value.Rows |> Seq.map (fun r ->
          Medals.Row
            ( r.Games, r.Year, r.Sport, r.Discipline, r.Athlete, countries.Value.[r.Team],
              r.Gender, r.Event, r.Medal, r.Gold, r.Silver, r.Bronze) ) |> Array.ofSeq )
      (medals.Value.Headers.Value |> Array.mapi (fun i h ->
          match h with
          | "Year" | "Gold" | "Silver" | "Bronze" -> h, "int", getFld i
          | h -> h, "string", getFld i)) facets.Value
  return! app next ctx }
