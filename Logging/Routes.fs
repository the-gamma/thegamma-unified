module TheGamma.Logging.Routes

open System.IO
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.Logging.LogAgent

let handler : HttpHandler =
  choose [
    GET >=> route "/" >=> text "Logging service is running..."
    POST >=> routef "/log/%s" (fun name ->
      fun next ctx -> task {
        let name = name.TrimEnd('/')
        if name |> Seq.exists (fun c -> c < 'a' || c > 'z') then
          return! RequestErrors.BAD_REQUEST "Wrong log name" next ctx
        else
          use reader = new StreamReader(ctx.Request.Body)
          let! line = reader.ReadToEndAsync()
          do! writeLog name line |> Async.StartAsTask
          return! text "" next ctx })
  ]
