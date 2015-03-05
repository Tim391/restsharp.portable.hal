﻿namespace RestSharp.Portable.Hal.Helpers

[<AutoOpen>]
module IRestResponseExtensions = 
    open RestSharp.Portable
    open RestSharp.Portable.Hal
    open System.Net
    open Newtonsoft.Json.Linq
          

    let verifyResponse (attemptedRequest:IRestRequest) (response:IRestResponse) (body:string) (jo:JObject) : Unit = 
        match response.IsSuccess with
        | true -> ()
        | false ->
            match response.StatusCode with
            | HttpStatusCode.BadRequest ->
                match jo with
                | null ->
                     //bad request, but non json body. throw up.
                     raise <| UnexpectedResponseException(response, body, jo, attemptedRequest)
                | _ ->
                    if jo.Value("type") = "validation" then
                       let validationError = parseValidationErrors(jo)
                       raise <| RemoteValidationException(validationError, response, body, jo, attemptedRequest)
                    else raise <| UnexpectedResponseException(response, body, jo, attemptedRequest)
            | _ ->  raise <| UnexpectedResponseException(response, body, jo, attemptedRequest)


