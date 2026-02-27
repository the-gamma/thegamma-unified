module TheGamma.Expenditure.Routes

open System
open System.IO
open System.Collections.Generic
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Data
open TheGamma.Common.JsonHelpers
open TheGamma.Expenditure.LoadData

type ThingSchema = { ``@context``:string; ``type``:string; name:string; }
type GenericType = { name:string; ``params``:obj[] }
type TypePrimitive = { kind:string; ``type``:obj; endpoint:string }
type TypeNested = { kind:string; endpoint:string }
type Member = { name:string; returns:obj; trace:string[]; schema:ThingSchema }
let noSchema = Unchecked.defaultof<ThingSchema>

let mutable allData : Dictionaries option = None

let initData dataDirectory =
  allData <- Some (retrieveData dataDirectory)

let memberRoute s f : HttpHandler =
  route s >=> ( f() |> Array.ofSeq |> toJson |> text )

let memberRoutef fmt f : HttpHandler =
  routef fmt (f >> Array.ofSeq >> toJson >> text)

let (|Lookup|_|) k (dict:IDictionary<_,_>) =
  match dict.TryGetValue k with
  | true, v -> Some v
  | _ -> None

let handler : HttpHandler =
  fun next ctx -> task {
    let d = allData.Value
    let app : HttpHandler =
      choose [
        memberRoute "/" (fun () ->
          [ { name="byService"; returns= {kind="nested"; endpoint="/pickService"}
              trace=[| |]; schema = noSchema }
            { name="byYear"; returns= {kind="nested"; endpoint="/pickYear"}
              trace=[| |]; schema = noSchema } ])

        memberRoute "/pickYear" (fun () ->
          [ for (KeyValue(id, year)) in d.Years ->
              { name=year; returns={kind="nested"; endpoint="/byYear/pickSlice"}
                trace=[|"year=" + id |]; schema = noSchema } ])

        memberRoute "/byYear/pickSlice" (fun () ->
          [ { name="byAccount"; returns= {kind="nested"; endpoint="/byYear/pickAccount"}
              trace=[| |]; schema = noSchema }
            { name="inTermsOf"; returns= {kind="nested"; endpoint="/byYear/pickTerms"}
              trace=[| |]; schema = noSchema }
            { name="byService"; returns= {kind="nested"; endpoint="/byYear/pickService"}
              trace=[| |]; schema = noSchema } ])

        memberRoute "/byYear/pickService" (fun () ->
          let mainServices = ofWhichAreMainServices d.Services
          [ for (KeyValue(id, (_,_,service))) in mainServices ->
              let typ = { name="tuple"; ``params``=[| "string"; "float" |] }
              let typ = { name="seq"; ``params``=[| typ |]}
              { name=service; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
                trace=[|"service=" + id |]; schema = noSchema } ])

        memberRoute "/pickService" (fun () ->
          let mainServices = ofWhichAreMainServices d.Services
          [ for (KeyValue(id, (_,_,service))) in mainServices ->
              { name=service; returns={kind="nested"; endpoint="/byService/" + id + "/pickOptions"}
                trace=[| |]; schema = noSchema } ])

        memberRoutef "/byService/%s/pickOptions" (fun serviceid ->
              [ { name="bySubService"; returns= {kind="nested"; endpoint="/byService/" + serviceid + "/pickSubService"}
                  trace=[| "service=" + serviceid |]; schema = noSchema }
                { name="bySubServiceComponents"; returns= {kind="nested"; endpoint="/byService/" + serviceid + "/pickSubServiceComponents"}
                  trace=[| |]; schema = noSchema }
                { name="byAccount"; returns= {kind="nested"; endpoint="/byService/pickAccount"}
                  trace=[| "service=" + serviceid |]; schema = noSchema }
                { name="inTermsOf"; returns= {kind="nested"; endpoint="/byService/pickTerms"}
                  trace=[| "service=" + serviceid |]; schema = noSchema }
                { name="ofWhichComponentIs"; returns= {kind="nested"; endpoint="/byService/"+serviceid+"/pickComponents"}
                  trace=[| |]; schema = noSchema } ])

        memberRoutef "/byService/%s/pickSlice" (fun serviceid ->
          [ { name="bySubService"; returns= {kind="nested"; endpoint="/byService/" + serviceid + "/pickSubService"}
              trace=[| |]; schema = noSchema }
            { name="byAccount"; returns= {kind="nested"; endpoint="/byService/pickAccount"}
              trace=[| |]; schema = noSchema }
            { name="inTermsOf"; returns= {kind="nested"; endpoint="/byService/pickTerms"}
              trace=[| |]; schema = noSchema } ])

        memberRoutef "/byService/%s/pickSubService" (fun serviceid ->
          let childrenOfService = getChildrenWithParentIDAtLevel serviceid "Subservice" d.SubServices
          [ for (KeyValue(id, (parent, level, subservice))) in childrenOfService ->
              let typ = { name="tuple"; ``params``=[| "int"; "float" |] }
              let typ = { name="seq"; ``params``=[| typ |]}
              { name=subservice; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
                trace=[|"service=" + id;"level=Subservice"|]; schema = noSchema } ])

        memberRoutef "/byService/%s/pickSubServiceComponents" (fun serviceid ->
          let subservices = getGrandchildrenOfServiceID serviceid d.SubServices
          [ for (KeyValue(id, (parent, level, service))) in subservices ->
              let typ = { name="tuple"; ``params``=[| "int"; "float" |] }
              let typ = { name="seq"; ``params``=[| typ |]}
              { name=service; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
                trace=[|"service=" + id;"level=Component of Subservice"|]; schema = noSchema } ])

        memberRoutef "/%s/pickAccount" (fun byX ->
          [ for (KeyValue(id, account)) in d.Accounts ->
              let typ =
                  if byX = "byYear" then { name="tuple"; ``params``=[| "string"; "float" |] }
                  elif byX = "byService" then { name="tuple"; ``params``=[| "int"; "float" |] }
                  else failwith "bad request"
              let typ = { name="seq"; ``params``=[| typ |]}
              { name=account; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
                trace=[|"account=" + id |]; schema = noSchema } ])

        memberRoutef "/%s/pickTerms" (fun byX ->
          [ for (KeyValue(id, term)) in d.Terms ->
              let typ =
                  if byX = "byYear" then { name="tuple"; ``params``=[| "string"; "float" |] }
                  elif byX = "byService" then { name="tuple"; ``params``=[| "int"; "float" |] }
                  else failwith "bad request"
              let typ = { name="seq"; ``params``=[| typ |]}
              { name=term; returns={ kind="primitive"; ``type``= typ; endpoint="/data"}
                trace=[|"inTermsOf=" + id |]; schema = noSchema } ])

        memberRoutef "/byService/%s/pickComponents" (fun serviceid ->
          let componentsOfService = getChildrenWithParentIDAtLevel serviceid "Component of Service" d.Services
          [ for (KeyValue(id, (_,_,service))) in componentsOfService ->
              { name=service; returns={kind="nested"; endpoint="/byService/" + id + "/pickSlice"}
                trace=[|"service=" + id;"level=Component of Service"|]; schema = noSchema } ])

        route "/data" >=> fun next ctx -> task {
          use reader = new StreamReader(ctx.Request.Body)
          let! body = reader.ReadToEndAsync()
          let trace =
            [ for kvps in body.Split('&') ->
                match kvps.Split('=') with
                | [| k; v |] -> k, v
                | _ -> failwith "bad trace" ] |> dict

          let result =
            match trace with
            | (Lookup "service" s) & (Lookup "account" a) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Service = s && dt.Account = a)
                |> Seq.map (fun dt -> d.Years.[dt.Year], dt.Value)
                |> formatPairSeq JsonValue.String
            | (Lookup "service" s) & (Lookup "inTermsOf" t) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Service = s && dt.ValueInTermsOf = t)
                |> Seq.map (fun dt -> d.Years.[dt.Year], dt.Value)
                |> formatPairSeq JsonValue.String
            | (Lookup "service" s) & (Lookup "level" a) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Service = s && dt.Level = a)
                |> Seq.map (fun dt -> d.Years.[dt.Year], dt.Value)
                |> formatPairSeq JsonValue.String
            | (Lookup "year" y) & (Lookup "account" a) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Year = y && dt.Account = a && dt.Level = "Service")
                |> Seq.map (fun dt ->
                  let (_,_,service) = d.Services.[dt.Service]
                  (service, dt.Value))
                |> formatPairSeq JsonValue.String
            | (Lookup "year" y) & (Lookup "inTermsOf" t) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Year = y && dt.ValueInTermsOf = t && dt.Level = "Service")
                |> Seq.map (fun dt ->
                  let (_,_,service) = d.Services.[dt.Service]
                  (service, dt.Value))
                |> formatPairSeq JsonValue.String
            | (Lookup "year" y) & (Lookup "service" s) ->
              d.Data
                |> Seq.filter (fun dt -> dt.Year = y && dt.Parent = s && dt.Level = "Subservice")
                |> Seq.map (fun dt ->
                  let (_,_,serviceName) = d.SubServices.[dt.Service]
                  (serviceName, dt.Value))
                |> formatPairSeq JsonValue.String
            | _ -> failwith "bad trace"
          return! text result next ctx }
      ]
    return! app next ctx
  }
