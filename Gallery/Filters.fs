module TheGamma.Gallery.Filters

open System
open System.Text.RegularExpressions

let cleanTitle (title:string) =
  let t = Regex.Replace(title.ToLower(), "[^a-z0-9 ]", "")
  let t = Regex.Replace(t, " +", "-")
  System.Web.HttpUtility.UrlEncode t

// DotLiquid requires a .NET type with static methods for filters
type FiltersType() =
  static member IsHome (obj:System.Collections.IEnumerable) =
    (obj |> Seq.cast<obj> |> Seq.length) <= 8

  static member JsEncode (s:string) =
    System.Web.HttpUtility.JavaScriptStringEncode s

  static member HtmlEncode (s:string) =
    System.Web.HttpUtility.HtmlEncode s

  static member UrlEncode (url:string) =
    System.Web.HttpUtility.UrlEncode(url)

  static member MailEncode (url:string) =
    System.Web.HttpUtility.UrlEncode(url).Replace("+", "%20")

  static member CleanTitle (title:string) =
    cleanTitle title

  static member ModTwo (n:int) = n % 2

  static member NiceDate (dt:DateTime) =
    let ts = DateTime.UtcNow - dt
    if ts.TotalSeconds < 0.0 then "just now"
    elif ts.TotalSeconds < 60.0 then sprintf "%d secs ago" (int ts.TotalSeconds)
    elif ts.TotalMinutes < 60.0 then sprintf "%d mins ago" (int ts.TotalMinutes)
    elif ts.TotalHours < 24.0 then sprintf "%d hours ago" (int ts.TotalHours)
    elif ts.TotalHours < 48.0 then "yesterday"
    elif ts.TotalDays < 30.0 then sprintf "%d days ago" (int ts.TotalDays)
    elif ts.TotalDays < 365.0 then sprintf "%d months ago" (int ts.TotalDays / 30)
    else sprintf "%d years ago" (int ts.TotalDays / 365)
