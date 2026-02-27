module TheGamma.SnippetService.Routes

open System.IO
open Giraffe
open Microsoft.AspNetCore.Http
open TheGamma.SnippetService.SnippetAgent

let handler : HttpHandler =
  choose [
    GET >=> route "/" >=> text "Snippet service is running..."

    GET >=> routef "/%s/%i/like" (fun (source, id) ->
      fun next ctx -> task {
        agent.Post(LikeSnippet(source, id))
        return! text "Liked" next ctx })

    POST >=> routef "/%s" (fun source ->
      fun next ctx -> task {
        use reader = new StreamReader(ctx.Request.Body)
        let! body = reader.ReadToEndAsync()
        let snip = fromJson<NewSnippet> body
        let! id = agent.PostAndAsyncReply(fun ch -> AddSnippet(source, snip, ch))
        ctx.SetStatusCode 201
        return! text (string id) next ctx })

    GET >=> routef "/%s" (fun source ->
      fun next ctx -> task {
        let! json = agent.PostAndAsyncReply(fun ch -> GetSnippets(source, ch))
        return! text json next ctx })
  ]
