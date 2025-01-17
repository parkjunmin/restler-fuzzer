﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

module Restler.Annotations

open System.Collections.Generic
open Restler.Grammar
open System
open System.IO
open Newtonsoft.Json.Linq
open Restler.Utilities.JsonParse
open Restler.XMsPaths

type ExceptConsumerUserAnnotation =
    {
       consumer_endpoint : string
       consumer_method : string
    }

type ProducerConsumerUserAnnotation =
    {
        // The endpoint and method of the producer
        producer_endpoint : string
        producer_method : string

        // The producer resource name and consumer parameter
        // These may be omitted in the case of an ordering constraint
        // specification between methods
        producer_resource_name : string option
        consumer_param : string option

        consumer_endpoint : string option
        consumer_method : string option
        except : obj option
    }

let parseAnnotation (ann:JToken) =
    let annJson = ann.ToString(Newtonsoft.Json.Formatting.None)

    match Microsoft.FSharpLu.Json.Compact.tryDeserialize<ProducerConsumerUserAnnotation>
            annJson with
    | Choice2Of2 error ->
        failwith (sprintf "Invalid producer annotation: %s (%s)" error annJson)
    | Choice1Of2 annotation ->
        let xMsPath = getXMsPath annotation.producer_endpoint
        let producer_endpoint =
            match xMsPath with
            | None -> annotation.producer_endpoint
            | Some xMsPath ->
                xMsPath.getNormalizedEndpoint()
        let producerRequestId = {
                                    endpoint = producer_endpoint
                                    method = getOperationMethodFromString annotation.producer_method
                                    xMsPath = xMsPath
                                }
        let consumerRequestId =
            match annotation.consumer_endpoint with
            | None -> None
            | Some ace ->
                if annotation.consumer_method.IsNone then
                    failwith (sprintf "Invalid annotation: if consumer_endpoint is specified, consumer_method must be specified")
                let xMsPath = getXMsPath ace
                let consumer_endpoint =
                    match xMsPath with
                    | None -> ace
                    | Some xMsPath ->
                        xMsPath.getNormalizedEndpoint()
                Some
                    {
                        endpoint = consumer_endpoint
                        method = getOperationMethodFromString annotation.consumer_method.Value
                        xMsPath = xMsPath
                    }

        // Initialize the consumer parameter based on whether a path or name is specified.
        let consumerParameter =
            match annotation.consumer_param with
            | None -> None
            | Some acp ->
                match AccessPaths.tryGetAccessPathFromString acp with
                | Some p ->
                    Some (ResourcePath p)
                | None ->
                    Some (ResourceName acp)
        let producerParameter =
            match annotation.producer_resource_name with
            | None -> None
            | Some app ->
                match AccessPaths.tryGetAccessPathFromString app with
                | Some p ->
                    Some (ResourcePath p)
                | None ->
                    Some (ResourceName app)

        let getExceptProperty (o:JObject) (exceptConsumer:obj) =
            {
                consumer_endpoint =
                    match getPropertyAsString o "consumer_endpoint" with
                    | None ->
                        failwith (sprintf "Invalid except clause specified in annotation: %A" exceptConsumer)
                    | Some ep -> ep
                consumer_method =
                    match getPropertyAsString o "consumer_method" with
                    | None ->
                        failwith (sprintf "Invalid except clause specified in annotation: %A" exceptConsumer)
                    | Some ep -> ep
            }

        let exceptConsumerId =
            match annotation.except with
            | None -> None
            | Some exceptConsumer ->
                let exceptConsumer =
                    match exceptConsumer  with
                    | :? JArray as je ->
                        je.Children()
                        |> Seq.map (fun x ->
                                        let o = x.Value<JObject>()
                                        getExceptProperty o je)
                        |> Seq.toList
                    | :? JObject as jo ->
                        [ getExceptProperty jo jo ]
                    | _ ->
                        failwith (sprintf "Invalid except clause specified in annotation: %A" exceptConsumer)

                exceptConsumer
                |> List.map (fun ec ->
                                let xMsPath = getXMsPath ec.consumer_endpoint
                                let endpoint =
                                    match xMsPath with
                                    | None -> ec.consumer_endpoint
                                    | Some xMsPath ->
                                        xMsPath.getNormalizedEndpoint()

                                {
                                    endpoint = endpoint
                                    method = getOperationMethodFromString ec.consumer_method
                                    xMsPath = xMsPath
                                })
                |> Some
        Some {  ProducerConsumerAnnotation.producerId = producerRequestId
                consumerId = consumerRequestId
                consumerParameter = consumerParameter
                producerParameter = producerParameter
                exceptConsumerId = exceptConsumerId
             }

/// Gets annotation data from Json
/// This applies if the user specifies a separate file with annotations only
let getAnnotationsFromJson (annotationJson:JToken) =
    try
        annotationJson.Children()
        |> Seq.choose (fun ann -> parseAnnotation ann)
        |> Seq.toList
    with e ->
        printfn "ERROR: malformed annotations specified. %A" e.Message
        raise e

/// Gets the REST-ler dependency annotation from the extension data
/// The 'except' clause indicates that "all consumer IDs with resource name 'workflowName'
/// should be resolved to this producer, except for the indicated consumer endpoint (which
/// should use the dependency in order of resolution, e.g. custom dictionary entry.)
//{
///        "producer_resource_name": "name",
///        "producer_method": "PUT",
///        "consumer_param": "workflowName",
///        "producer_endpoint": "/subscriptions/{subscriptionId}/providers/Microsoft.Logic/workflows/{workflowName}",
///        "except": {
///            "consumer_endpoint": "/subscriptions/{subscriptionId}/providers/Microsoft.Logic/workflows/{workflowName}",
///            "consumer_method": "PUT"
///        }
///    },
///
let getAnnotationsFromExtensionData (extensionData:IDictionary<_, obj>) annotationKey  =
    let getAnnotationProperty (aDict:IDictionary<string, obj>) propertyName =
        match Restler.Utilities.Dict.tryGetString aDict propertyName with
        | None ->
            printfn "ERROR: Malformed annotation, no value specified for %s"  propertyName
            // Special error message for renamed properties.
            // (For now, we do not need to maintain backwards compatibility but this may change in the future.)
            if propertyName = "param" then
                printfn "Did you mean 'consumer_param'?"
            None
        | Some v ->
            Some v

    if isNull extensionData then Seq.empty
    else
        match extensionData |> Seq.tryFind (fun kvp -> kvp.Key = annotationKey) with
        | None -> Seq.empty
        | Some annotations ->
            match annotations.Value with
            | :? seq<obj> as annotationList ->
                annotationList
                |> Seq.choose (fun aDict ->
                                    let jObject = JObject.FromObject(aDict)
                                    parseAnnotation jObject)
            | _  ->
                printfn "%s" "ERROR: malformed annotation format"
                Seq.empty

let getGlobalAnnotationsFromFile filePath =
    if File.Exists filePath then
        let annFileText = System.IO.File.ReadAllText(filePath)
        let globalAnnotationsJson = JObject.Parse(annFileText)
        let globalAnnotationKey = "x-restler-global-annotations"
        match Restler.Utilities.JsonParse.getProperty globalAnnotationsJson globalAnnotationKey with
        | Some globalAnn ->
            getAnnotationsFromJson globalAnn
        | None ->
            printfn "ERROR: invalid annotation file: x-restler-global-annotations must be the key"
            raise (ArgumentException("invalid annotation file"))
    else
        List.empty