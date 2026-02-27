module TheGamma.Logging.LogAgent

open System
open System.IO
open System.Collections.Generic

// --------------------------------------------------------------------------------------
// Local file-based logging (replacing Azure append blobs)
// --------------------------------------------------------------------------------------

let mutable storageRoot = ""
let initStorage root = storageRoot <- root

let ensureDir dir =
  if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

let createLogFile name =
  let dir = Path.Combine(storageRoot, "logs", name)
  ensureDir dir
  let logName = sprintf "logs-%s.log" (DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss"))
  Path.Combine(dir, logName)

// Dispatches writes to log files based on the name of the container
let logAgent = MailboxProcessor<string * string * AsyncReplyChannel<unit>>.Start(fun inbox -> async {
  let logFiles = Dictionary<string, string>()
  while true do
    let! name, line, repl = inbox.Receive()
    if not (logFiles.ContainsKey name) then
      logFiles.Add(name, createLogFile name)
    File.AppendAllText(logFiles.[name], line + "\n", Text.Encoding.UTF8)
    repl.Reply() })

let writeLog name line =
  logAgent.PostAndAsyncReply(fun ch -> name, line, ch)
