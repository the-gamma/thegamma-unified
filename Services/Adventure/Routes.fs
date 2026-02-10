module TheGamma.Services.Adventure.Routes

open System
open System.IO
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Common.JsonHelpers

type TypeNested = { kind:string; endpoint:string }
type Member = { name:string; documentation:string; returns:obj; trace:string[] }

type PageEntry = { Key: string; Text : string; Choices : (string * string) list }

let parseLine (line:string) =
  let split = line.Split('|')
  let choices = split.[2].Split('¿')
  let niceKey (s:string) = s.ToLower().Replace(" ", "-").Trim()
  { Key = niceKey split.[0]
    Text = split.[1]
    Choices =
       if choices.Length <= 1 then []
       else [ for i in 0..2..choices.Length-1 -> (niceKey choices.[i], choices.[i+1]) ] }

let mutable lookup : System.Collections.Generic.IDictionary<string, PageEntry> = null

let initData dataRoot =
  lookup <-
    File.ReadAllLines(Path.Combine(dataRoot, "adventure-sample.txt"))
    |> Seq.map parseLine
    |> Seq.map (fun p -> p.Key, p)
    |> dict

let handler : HttpHandler =
  choose [
    route "/" >=>
      ( fun next ctx ->
          [| { name="Start the adventure..."; returns={kind="nested"; endpoint="/intro"};
               documentation=lookup.["intro"].Text; trace=[||] } |]
          |> toJson |> fun s -> text s next ctx )
    routef "/%s" (fun section ->
        [| for key, txt in lookup.[section].Choices ->
            { name=txt; returns={kind="nested"; endpoint="/"+key};
              documentation=lookup.[key].Text; trace=[||] } |]
        |> toJson |> text) ]
