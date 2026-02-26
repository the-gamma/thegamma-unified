module TheGamma.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe

let rio2016Redirect : HttpHandler =
    fun next ctx ->
        if ctx.Request.Host.Host = "rio2016.thegamma.net" &&
           not (ctx.Request.Path.Value.StartsWith("/rio2016")) then
            redirectTo false ("/rio2016" + ctx.Request.Path.Value) next ctx
        else
            next ctx

let webApp = choose [
    rio2016Redirect
    subRoute "/services" (choose [
        subRoute "/csv"          TheGamma.CsvService.Routes.handler
        subRoute "/snippets"     TheGamma.SnippetService.Routes.handler
        subRoute "/expenditure"  TheGamma.Expenditure.Routes.handler
        subRoute "/log"          TheGamma.Logging.Routes.handler
        subRoute "/worldbank"    TheGamma.Services.WorldBank.Routes.handler
        subRoute "/olympics"     TheGamma.Services.Olympics.Routes.handler
        subRoute "/pdata"        TheGamma.Services.PivotData.Routes.handler
        subRoute "/pivot"        TheGamma.Services.Pivot.Routes.handler
        subRoute "/smlouvy"      TheGamma.Services.Smlouvy.Routes.handler
        subRoute "/adventure"    TheGamma.Services.Adventure.Routes.handler
        subRoute "/minimal"      TheGamma.Services.Minimal.Routes.handler
    ])
    subRoute "/rio2016"   TheGamma.Olympics.Routes.handler
    TheGamma.Gallery.Routes.handler
]

let configureApp (app: IApplicationBuilder) =
    app
        .UseCors(fun builder ->
            builder
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
            |> ignore)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services: IServiceCollection) =
    services.AddCors() |> ignore
    services.AddGiraffe() |> ignore

let timed label f =
    printfn "[%s] %s..." (DateTime.Now.ToString("HH:mm:ss.fff")) label
    let sw = Diagnostics.Stopwatch.StartNew()
    let result = f ()
    printfn "[%s] %s done (%.0fms)" (DateTime.Now.ToString("HH:mm:ss.fff")) label sw.Elapsed.TotalMilliseconds
    result

[<EntryPoint>]
let main args =
    let baseUrl = Environment.GetEnvironmentVariable("THEGAMMA_BASE_URL")
    let baseUrl = if String.IsNullOrEmpty baseUrl then "http://localhost:5000" else baseUrl
    let storageRoot = Environment.GetEnvironmentVariable("THEGAMMA_STORAGE_ROOT")
    let storageRoot = if String.IsNullOrEmpty storageRoot then Path.Combine(Directory.GetCurrentDirectory(), "storage") else storageRoot
    let recaptchaSecret = Environment.GetEnvironmentVariable("RECAPTCHA_SECRET")
    let recaptchaSecret = if isNull recaptchaSecret then "" else recaptchaSecret

    let dataRoot = Path.Combine(Directory.GetCurrentDirectory(), "data")

    // Initialize storage services
    timed "Storage"   (fun () -> TheGamma.CsvService.Storage.initStorage storageRoot)
    timed "CsvService" (fun () -> TheGamma.CsvService.Routes.initService baseUrl storageRoot)
    timed "Snippets"  (fun () -> TheGamma.SnippetService.SnippetAgent.initStorage storageRoot)
    timed "Logging"   (fun () -> TheGamma.Logging.LogAgent.initStorage storageRoot)

    // Initialize data-dependent services
    timed "Adventure"  (fun () -> TheGamma.Services.Adventure.Routes.initData dataRoot)
    timed "Expenditure" (fun () -> TheGamma.Expenditure.Routes.initData (Path.Combine(dataRoot, "expenditure")))
    timed "WorldBank"  (fun () -> TheGamma.Services.WorldBank.Routes.initData (Path.Combine(dataRoot, "worldbank")))
    timed "Olympics"   (fun () -> TheGamma.Services.Olympics.Routes.initData dataRoot)
    timed "PivotData"  (fun () -> TheGamma.Services.PivotData.Routes.initAllData dataRoot)
    timed "Smlouvy"    (fun () -> TheGamma.Services.Smlouvy.Routes.initData dataRoot)

    // Initialize gallery
    let templateDir = Path.Combine(Directory.GetCurrentDirectory(), "templates")
    timed "Gallery"    (fun () -> TheGamma.Gallery.Routes.initGallery templateDir baseUrl recaptchaSecret)

    // Initialize Olympics web
    let olympicsDocsDir = Path.Combine(Directory.GetCurrentDirectory(), "olympics-docs")
    let olympicsTemplDir = Path.Combine(Directory.GetCurrentDirectory(), "olympics-templates")
    timed "OlympicsWeb" (fun () -> TheGamma.Olympics.Routes.initData olympicsDocsDir olympicsTemplDir baseUrl)

    Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .Configure(configureApp)
                .ConfigureServices(configureServices)
            |> ignore)
        .Build()
        .Run()
    0
