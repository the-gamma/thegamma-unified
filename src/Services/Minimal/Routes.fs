module TheGamma.Services.Minimal.Routes

open System
open System.IO
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Common.JsonHelpers

type GenericType = { name:string; ``params``:obj[] }
type TypePrimitive = { kind:string; ``type``:obj; endpoint:string }
type TypeNested = { kind:string; endpoint:string }
type Member = { name:string; returns:obj; trace:string[] }

let (|Contains|_|) k s = if Set.contains k s then Some() else None

let handler : HttpHandler =
  choose [
    route "/" >=>
      ( [| { name="London"; returns={kind="nested"; endpoint="/city"}; trace=[|"London"|] };
           { name="New York"; returns={kind="nested"; endpoint="/city"}; trace=[|"NYC"|] }
           { name="Cambrdige"; returns={kind="nested"; endpoint="/city"}; trace=[|"NYC"|] } |]
        |> toJson |> text )

    route "/city" >=>
      ( [| { name="Population"; trace=[|"Population"|]
             returns={kind="primitive"; ``type``="int"; endpoint="/data"};  };
           { name="Settled"; trace=[|"Settled"|]
             returns={kind="primitive"; ``type``="int"; endpoint="/data"}} |]
        |> toJson |> text )

    route "/data" >=> fun next ctx -> task {
      use reader = new StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync()
      match set (body.Split('&')) with
      | Contains "London" & Contains "Population" -> return! text "538689" next ctx
      | Contains "NYC" & Contains "Population" -> return! text "550405" next ctx
      | Contains "London" & Contains "Settled" -> return! text "-43" next ctx
      | Contains "NYC" & Contains "Settled" -> return! text "1624" next ctx
      | _ -> return! RequestErrors.BAD_REQUEST "Wrong trace" next ctx } ]
