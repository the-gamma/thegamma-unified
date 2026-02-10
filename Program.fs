module TheGamma.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe

let webApp = choose [
    route "/" >=> text "TheGamma services are running..."

    subRoute "/csv"          TheGamma.CsvService.Routes.handler
    subRoute "/snippets"     TheGamma.SnippetService.Routes.handler
    subRoute "/gallery"      TheGamma.Gallery.Routes.handler
    subRoute "/expenditure"  TheGamma.Expenditure.Routes.handler
    subRoute "/log"          TheGamma.Logging.Routes.handler
    subRoute "/worldbank"    TheGamma.Services.WorldBank.Routes.handler
    subRoute "/olympics"     TheGamma.Services.Olympics.Routes.handler
    subRoute "/pdata"        TheGamma.Services.PivotData.Routes.handler
    subRoute "/pivot"        TheGamma.Services.Pivot.Routes.handler
    subRoute "/smlouvy"      TheGamma.Services.Smlouvy.Routes.handler
    subRoute "/adventure"    TheGamma.Services.Adventure.Routes.handler
    subRoute "/minimal"      TheGamma.Services.Minimal.Routes.handler
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

[<EntryPoint>]
let main args =
    let baseUrl = Environment.GetEnvironmentVariable("THEGAMMA_BASE_URL")
    let baseUrl = if String.IsNullOrEmpty baseUrl then "http://localhost:8080" else baseUrl
    let storageRoot = Environment.GetEnvironmentVariable("THEGAMMA_STORAGE_ROOT")
    let storageRoot = if String.IsNullOrEmpty storageRoot then Path.Combine(Directory.GetCurrentDirectory(), "storage") else storageRoot
    let recaptchaSecret = Environment.GetEnvironmentVariable("RECAPTCHA_SECRET")
    let recaptchaSecret = if isNull recaptchaSecret then "" else recaptchaSecret

    let dataRoot = Path.Combine(Directory.GetCurrentDirectory(), "data")

    // Initialize storage services
    TheGamma.CsvService.Storage.initStorage storageRoot
    TheGamma.CsvService.Routes.initService baseUrl storageRoot
    TheGamma.SnippetService.SnippetAgent.initStorage storageRoot
    TheGamma.Logging.LogAgent.initStorage storageRoot

    // Initialize data-dependent services
    TheGamma.Services.Adventure.Routes.initData dataRoot
    TheGamma.Expenditure.Routes.initData dataRoot
    TheGamma.Services.WorldBank.Routes.initData (Path.Combine(dataRoot, "worldbank"))
    TheGamma.Services.Olympics.Routes.initData dataRoot
    TheGamma.Services.PivotData.Routes.initAllData dataRoot
    TheGamma.Services.Smlouvy.Routes.initData dataRoot

    // Initialize gallery
    let templateDir = Path.Combine(Directory.GetCurrentDirectory(), "templates")
    TheGamma.Gallery.Routes.initGallery templateDir baseUrl recaptchaSecret

    Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .Configure(configureApp)
                .ConfigureServices(configureServices)
            |> ignore)
        .Build()
        .Run()
    0
