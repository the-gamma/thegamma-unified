module TheGamma.CsvService.Listing

open System
open TheGamma.CsvService.Storage

let getTagId (s:string) =
  let rec loop (sb:System.Text.StringBuilder) dash i =
    if i = s.Length then sb.ToString()
    elif Char.IsLetterOrDigit s.[i] then loop (sb.Append(Char.ToLower s.[i])) false (i+1)
    elif dash then loop sb true (i+1)
    else loop (sb.Append '-') true (i+1)
  loop (System.Text.StringBuilder()) true 0
