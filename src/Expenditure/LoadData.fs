module TheGamma.Expenditure.LoadData

open System
open System.Collections.Generic
open System.IO
open FSharp.Data

type DataPoint(service:string, subservice:string, account: string, valueInTermsOf:string, year:string, value:float, parentid: string, level: string) =
     member x.Service = service
     member x.Subservice = subservice
     member x.Account = account
     member x.ValueInTermsOf = valueInTermsOf
     member x.Year = year
     member x.Value = value
     member x.Parent = parentid
     member x.Level = level

type Dictionaries =
    {
        Services: IDictionary<string, (string * string * string)>
        SubServices: IDictionary<string, (string * string * string)>
        SubServiceSeq: (string * (string * string * string)) list
        Years:IDictionary<string, string>
        Accounts:IDictionary<string, string>
        Terms:IDictionary<string, string>
        Data : DataPoint list
    }

let [<Literal>] yearsDictCsv = __SOURCE_DIRECTORY__ + "/../../data/expenditure/headers/years.csv"
let [<Literal>] servicesDictCsv = __SOURCE_DIRECTORY__ + "/../../data/expenditure/headers/subservices.csv"
let [<Literal>] Y20112015Csv = __SOURCE_DIRECTORY__ + "/../../data/expenditure/headers/Table5-2.csv"
let [<Literal>] Y19992015Csv = __SOURCE_DIRECTORY__ + "/../../data/expenditure/headers/Table4-2.csv"

type ServiceDictProvider = CsvProvider<servicesDictCsv, Schema = "string,string,string,string">
type YearDictProvider = CsvProvider<yearsDictCsv, Schema = "string,string">
type Y2011ServiceProvider = CsvProvider<Y20112015Csv, Schema = "string, float, float, float, float, float">
type Y1999ServiceProvider = CsvProvider<Y19992015Csv, Schema = "string, float, float, float, float, float, float, float, float, float, float, float, float, float, float, float, float, float">

let getKeyOfTerm value keyValueList =
    match List.tryFind (fun (k,v) -> (v = value)) keyValueList with
    | Some (key, _) -> key
    | None -> ""

let getKeyOfObject value keyValueList =
    match List.tryFind (fun (k,(parent, level, v)) -> (v = value)) keyValueList with
    | Some (key, _ ) -> key
    | None -> ""

let getLevelOfObject value keyValueList =
    match List.tryFind (fun (k,(parent, level, v)) -> (v = value)) keyValueList with
    | Some (_, (_, level, _) ) -> level
    | None -> ""

let getParentOfObject value keyValueList =
    match List.tryFind (fun (k,(parent, level, v)) -> (v = value)) keyValueList with
    | Some (_, (parent, _, _) ) -> parent
    | None -> ""

let ofWhichAreMainServices serviceDictionary =
    serviceDictionary |> Seq.filter (fun (KeyValue(index, (parent, level, name))) -> level="Service")

let getChildrenWithParentIDAtLevel parentID itemLevel serviceDictionary =
    serviceDictionary |> Seq.filter (fun (KeyValue(id, (parentid, level, _))) -> level = itemLevel && parentid = parentID)

let getGrandchildrenOfServiceID serviceID aSeq =
    let total = new Dictionary<string, (string * string * string)>()
    let children = getChildrenWithParentIDAtLevel serviceID "Subservice" aSeq
    let grandchildren = children |> Seq.map (fun (KeyValue(id, _)) -> getChildrenWithParentIDAtLevel id "Component of Subservice" aSeq)
    grandchildren |> Seq.iter (fun x -> x |> Seq.iter (fun (KeyValue(id, (parentid,level,name))) -> total.Add(id, (parentid, level, name))))
    total

let retrieveData dataDirectory =
    let servicesPath = Path.Combine(dataDirectory, "headers/services.csv")
    let subservicesPath = Path.Combine(dataDirectory, "headers/subservices.csv")
    let yearsPath = Path.Combine(dataDirectory, "headers/years.csv")
    let termsPath = Path.Combine(dataDirectory, "headers/terms.csv")
    let accountsPath = Path.Combine(dataDirectory, "headers/accounts.csv")
    let table4Dot2Path = Path.Combine(dataDirectory, "headers/Table4-2.csv")
    let table4Dot3Path = Path.Combine(dataDirectory, "headers/Table4-3.csv")
    let table4Dot4Path = Path.Combine(dataDirectory, "headers/Table4-4.csv")
    let table5Dot4Dot1Path = Path.Combine(dataDirectory, "headers/Table5-4-1.csv")
    let table5Dot4Dot2Path = Path.Combine(dataDirectory, "headers/Table5-4-2.csv")
    let table5Dot2Path = Path.Combine(dataDirectory, "headers/Table5-2.csv")

    let servicesCSV = ServiceDictProvider.Load(servicesPath)
    let yearsCSV = YearDictProvider.Load(yearsPath)
    let termsCSV = YearDictProvider.Load(termsPath)
    let accountsCSV = YearDictProvider.Load(accountsPath)
    let subservicesCSV = ServiceDictProvider.Load(subservicesPath)

    let serviceCsvToList (csvfile:ServiceDictProvider) =
        [for row in csvfile.Rows ->
            (row.Index, (row.ParentIndex, row.Level, row.Name))]

    let yearCsvToList (csvfile:YearDictProvider) =
        [for row in csvfile.Rows ->
            (row.Index, row.Name)]

    let serviceSeq = serviceCsvToList servicesCSV
    let serviceDict = serviceSeq |> dict
    let subserviceSeq = serviceCsvToList subservicesCSV
    let subserviceDict = subserviceSeq |> dict
    let yearSeq = yearCsvToList yearsCSV
    let yearDict = yearSeq |> dict
    let termSeq = yearCsvToList termsCSV
    let termDict = termSeq |> dict
    let accountSeq = yearCsvToList accountsCSV
    let accountDict = accountSeq |> dict

    let table4dot2 = Y1999ServiceProvider.Load(table4Dot2Path)
    let table4dot3 = Y1999ServiceProvider.Load(table4Dot3Path)
    let table4dot4 = Y1999ServiceProvider.Load(table4Dot4Path)
    let table5dot4dot1 = Y2011ServiceProvider.Load(table5Dot4Dot1Path)
    let table5dot4dot2 = Y2011ServiceProvider.Load(table5Dot4Dot2Path)
    let table5dot2 = Y2011ServiceProvider.Load(table5Dot2Path)

    let getDataServices account term serviceSequence (csvtable:Y2011ServiceProvider) =
        let keyOfAccount = getKeyOfTerm account accountSeq
        let keyOfTerm = getKeyOfTerm term termSeq
        List.concat
            [ for row in csvtable.Rows ->
                let keyOfService = getKeyOfObject row.Service serviceSequence
                let keyOfSubservice = ""
                let keyOfParent = getParentOfObject row.Service serviceSequence
                let level = getLevelOfObject row.Service serviceSequence
                let dataRows = [row.``2011``; row.``2012``; row.``2013``; row.``2014``; row.``2015``]
                let dataHeaders = csvtable.Headers.Value.[1..]
                List.mapi (fun i x ->
                    DataPoint(keyOfService, keyOfSubservice, keyOfAccount, keyOfTerm, dataHeaders.[i], x, keyOfParent, level))
                    dataRows ]

    let getOldDataServices account term serviceSequence (csvtable:Y1999ServiceProvider) =
        let keyOfAccount = getKeyOfTerm account accountSeq
        let keyOfTerm = getKeyOfTerm term termSeq
        List.concat
            [ for row in csvtable.Rows ->
                let keyOfService = getKeyOfObject row.Service serviceSequence
                let keyOfSubservice = ""
                let keyOfParent = getParentOfObject row.Service serviceSequence
                let level = getLevelOfObject row.Service serviceSequence
                let dataRows = [row.``1999``; row.``2000``; row.``2001``; row.``2002``; row.``2003``; row.``2004``; row.``2005``; row.``2006``; row.``2007``; row.``2008``; row.``2009``; row.``2010``; row.``2011``; row.``2012``; row.``2013``; row.``2014``; row.``2015``]
                let dataHeaders = csvtable.Headers.Value.[1..]
                List.mapi (fun i x ->
                    DataPoint(keyOfService, keyOfSubservice, keyOfAccount, keyOfTerm, dataHeaders.[i], x, keyOfParent, level))
                    dataRows ]

    let nominalData = getOldDataServices "" "Nominal" serviceSeq table4dot2
    let adjustedData = getOldDataServices "" "Adjusted" serviceSeq table4dot3
    let gdpData = getOldDataServices "" "GDP" serviceSeq table4dot4
    let currentData = getDataServices "Current" "" serviceSeq table5dot4dot1
    let capitalData = getDataServices "Capital" "" serviceSeq table5dot4dot2
    let subserviceData = getDataServices "" "Nominal" subserviceSeq table5dot2

    let allValues = List.concat [nominalData; adjustedData; gdpData; currentData; capitalData; subserviceData]

    {
        Services=serviceDict;
        SubServices=subserviceDict;
        SubServiceSeq=subserviceSeq;
        Years=yearDict;
        Accounts=accountDict;
        Terms=termDict;
        Data=allValues;
    }
