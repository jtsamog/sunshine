module FeedTrigger

open Microsoft.Extensions.Logging
open Microsoft.Azure.WebJobs

open FeedData
open Microsoft.WindowsAzure.Storage.Table
open Microsoft.Azure.EventHubs
open System.Text
open FSharp.Azure.Storage.Table
open System
open AzureTableUtils

type FeedRaw =
     { [<PartitionKey>] DateStamp: string
       [<RowKey>] MessageId: string
       CorrelationId: string
       RawMessage: string }

type Feed =
     { [<PartitionKey>] DateStamp: string
       [<RowKey>] Id: string // this is the timestamp as a string
       CorrelationId: string
       Value: decimal
       Timestamp: DateTime }

[<FunctionName("FeedTrigger")>]
let trigger
    ([<EventHubTrigger("feed", Connection = "IoTHubConnectionString")>] eventData: EventData)
    ([<Table("Feed", Connection = "DataStorageConnectionString")>] feedTable: CloudTable)
    ([<Table("FeedData", Connection = "DataStorageConnectionString")>] feedDataTable: CloudTable)
    (logger: ILogger) =
    async {
        let message = Encoding.UTF8.GetString eventData.Body.Array
        let feed = PgridFeedDevice.Parse message
        printfn "Feed: %A" <| feed
        let correlationId = string eventData.Properties.["correlationId"]
        let messageId = string eventData.Properties.["messageId"]

        let raw = { CorrelationId = correlationId
                    MessageId = messageId
                    RawMessage = message
                    DateStamp = DateTime.Now.ToString("yyy-MM-dd") }

        let! _ = raw |> Insert |> inTableToClientAsync feedTable

        let tableBatch items = inTableAsBatchAsync feedDataTable.ServiceClient feedDataTable.Name items

        let ops = feed.Datastreams.Pgrid.Data
                  |> Array.map((fun d ->
                                   let ds = DateTime.Parse(d.Timestamp.ToString().Replace("\"", ""))
                                   { CorrelationId = correlationId
                                     Id = string d.Timestamp
                                     Value = d.Value
                                     Timestamp = ds
                                     DateStamp = ds.ToString("yyyy-MM-dd")}) >> InsertOrMerge)
                  |> Array.toSeq
                  |> autobatch
                  |> Seq.map tableBatch

        let! _ = Async.Parallel ops

        logger.LogInformation(sprintf "%s: Processed feed" correlationId)
    } |> Async.StartAsTask