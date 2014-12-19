﻿namespace RestSharp.Portable.Hal
open RestSharp.Portable
open Newtonsoft.Json.Linq

[<AutoOpen>]
module Client =

    let inline (=>) (left:string) (right) =
        (left, right.ToString())

    let merge (jo:JObject) data : JObject = 
        let newJo = jo.DeepClone() :?> JObject
        let newData = JObject.FromObject(data)
        let mergeSettings = new JsonMergeSettings()
        mergeSettings.MergeArrayHandling <- MergeArrayHandling.Replace

        newJo.Merge(newData, mergeSettings) |> ignore
        newJo.Remove("_links") |> ignore
        newJo.Remove("_embedded") |> ignore

        newJo

    type Follow =
        | LinkFollow of string * Parameter list
        | HeaderFollow of string
    and 
        RequestParameters = { rootUrl : string; follow: Follow list; urlSegments : Parameter list; }
    and
        EnvironmentParameters = { domain : string; headers : Parameter list; httpClientFactory : IHttpClientFactory option }
    
    type Resource  = 
        { requestContext : RequestContext; response : IRestResponse; data: JObject}
        member this.Parse<'T>() = 
            this.data.ToObject<'T>()
       
        member this.Follow (next:Follow) rest : RequestContext = 
            let (nextUrl, nextUrlSegments) = 
                match next with
                | LinkFollow(rel, segments) -> this.data.["_links"].[rel].Value<string>("href") , segments
                | HeaderFollow(header) -> (this.response.Headers.GetValues(header) |> Seq.head, [])

            let newRequestParameters = 
                {
                    this.requestContext.requestParameters 
                        with rootUrl = nextUrl;                          
                         urlSegments = this.requestContext.requestParameters.urlSegments @ nextUrlSegments;
                         follow = rest 
                }

            {this.requestContext with requestParameters = newRequestParameters}
    and
        RequestContext = 
        { environment: EnvironmentParameters; requestParameters : RequestParameters}
        member private this.parse (response:IRestResponse) : JObject = 
            let encodingStr = 
    //HOLY KRAP BATMAN...WHY Contains throw exceptionz?!?!?!
    //            match this.response.Headers.Contains("Content-Encoding") with
    //            | true -> 
    //                this.response.Headers.GetValues("Content-Encoding")
    //                |> Seq.head
    //            | _ -> 
                "UTF-8"
                             
            let encoding = System.Text.Encoding.GetEncoding encodingStr
            let str = encoding.GetString(response.RawBytes, 0, response.RawBytes.Length)
            
            match str with
            | "" -> null 
            | _ -> JObject.Parse(str)

        member private this.getResponse () : Async<Resource> = 
            async {
                let client = RestClient(this.environment.domain)
                
                match this.environment.httpClientFactory with
                | Some f -> client.HttpClientFactory <- f
                | _ -> ()

                let restRequest = RestRequest(this.requestParameters.rootUrl) :> IRestRequest
                
                let parameters = this.environment.headers @ this.requestParameters.urlSegments

                let req = 
                    parameters
                    |> List.fold (fun (state:IRestRequest) p -> state.AddParameter(p)) restRequest 

                let! res = client.Execute(req) |> Async.AwaitTask
                
                let data = this.parse(res)
                return { Resource.requestContext = this; response = res; data=data}
            } 

        static member private getEmbeddedResource (data:JObject) (follow:Follow) : Option<JToken> = 
            match follow with
            | HeaderFollow(_) -> None
            | LinkFollow(rel, segments) ->
                let embedded = data.["_embedded"]
                if embedded = null then
                    None
                else if embedded.Type = Newtonsoft.Json.Linq.JTokenType.Null then None
                     else
                        let target = embedded.[rel]
                        if target = null then
                            None
                        else if target.Type = Newtonsoft.Json.Linq.JTokenType.Null then None
                            else Some target
                
        member private this.getNext (resource:Resource) : Async<Resource> = 
            async {
                let final = 
                    match resource.requestContext.requestParameters.follow with
                    | [] -> resource
                    | x :: xs -> 
                        let embedded = RequestContext.getEmbeddedResource resource.data x
                        match embedded with
                        | None -> 
                            let newRequest : RequestContext = resource.Follow x xs
                            newRequest.GetAsync() |> Async.RunSynchronously
                        | Some jt -> 
                            let rp = {resource.requestContext.requestParameters with follow = xs }
                            let rc = {resource.requestContext with requestParameters=rp }
                            let nextResource = {resource with data=jt :?> JObject; requestContext = rc }

                            this.getNext nextResource |> Async.RunSynchronously

                return final
            }

        member this.GetAsync () : Async<Resource> = 
            async {
                let! rootResponse = this.getResponse()
                return! this.getNext(rootResponse)
            }

        member this.GetAsync<'T> () : Async<'T> = 
            async{
                let! response = this.GetAsync()
                return response.Parse<'T>()
            }

        member private this.submitAsync (``method`` : System.Net.Http.HttpMethod) data : Async<Resource> =
            async {
                let! resource = this.GetAsync()
                let form = resource.data
                let merged = merge form data
                let url = resource.data.["_links"].["self"].Value<string>("href")

                let client = RestClient(resource.requestContext.environment.domain)
                let restRequest = 
                    RestRequest(url, ``method``)
                        .AddJsonBody(merged) 
                
                let parameters = this.environment.headers @ this.requestParameters.urlSegments

                let req = 
                    parameters
                    |> List.fold (fun (state:IRestRequest) p -> state.AddParameter(p)) restRequest 

                let! response = client.Execute(req) |> Async.AwaitTask

                return {Resource.data = this.parse(response); Resource.response = response; Resource.requestContext = resource.requestContext}
            }

        member private this.submitAsyncAndParse<'T> (``method`` : System.Net.Http.HttpMethod) data : Async<'T> = 
            async {
                let! resource = this.submitAsync ``method`` data
                return resource.Parse<'T>()
            }

        member this.PostAsync data =  this.submitAsync System.Net.Http.HttpMethod.Post data
        member this.PutAsync data =  this.submitAsync System.Net.Http.HttpMethod.Put data
        member this.DeleteAsync data =  this.submitAsync System.Net.Http.HttpMethod.Delete data
        
        member this.PostAsyncAndParse data  = this.submitAsyncAndParse System.Net.Http.HttpMethod.Post data
        member this.PutAsyncAndParse data  = this.submitAsyncAndParse System.Net.Http.HttpMethod.Put data
        member this.DeleteAsyncAndParse data  = this.submitAsyncAndParse System.Net.Http.HttpMethod.Delete data

        member private this.getUrlSegments(urlSegments: (string*string) list) : Parameter list = 
            let segments = 
                urlSegments 
                |> List.map (
                    fun (k, v) -> 
                        let p = new Parameter()
                        p.Name <- k
                        p.Value <- v
                        p.Type <- ParameterType.UrlSegment
                        p
                    )
            segments

        member this.Follow (rel:string, urlSegments: (string*string) list) : RequestContext =
            let rp = this.requestParameters
            let segments = this.getUrlSegments urlSegments

            let newRp = {rp with follow = rp.follow @ [LinkFollow(rel, segments)]}
            {this with requestParameters = newRp}
        
        member this.Follow (rel:string) : RequestContext =
            this.Follow (rel, [])

        member this.Follow (rels:string list) : RequestContext = 
            match rels with
            | [] -> this
            | x::xs -> this.Follow(x).Follow(xs)

        member this.FollowHeader (headerName:string) : RequestContext =
            let rp = this.requestParameters
            let newRp = {rp with follow = rp.follow @ [HeaderFollow(headerName)]}
            {this with requestParameters = newRp}

        member this.FollowLocation () = this.FollowHeader "Location"

        member this.UrlSegments (urlSegments: (string*string) list) : RequestContext = 
           let rp = this.requestParameters
           let segments = this.getUrlSegments urlSegments
           let newRp = {rp with urlSegments = rp.urlSegments @ segments}
           {this with requestParameters = newRp} 

    type HalClient (env:EnvironmentParameters) = 
        member this.From (apiRelativeRoot:string) : RequestContext = 
            {
                RequestContext.environment = env;
                requestParameters = {rootUrl = apiRelativeRoot; follow = []; urlSegments = []; }
            }

    type HalClientFactory private (headers : Parameter list, httpClientFactory:IHttpClientFactory option) = 
        new() = HalClientFactory([], None)
        
        member x.CreateHalClient(domain:string) : HalClient = 
            HalClient({EnvironmentParameters.domain = domain; headers = headers; httpClientFactory = httpClientFactory})

        member x.HttpClientFactory httpClientFactory = 
            HalClientFactory(headers, httpClientFactory)

        member x.Header (key:string) (value:string) : HalClientFactory = 
            let p = new Parameter()
            p.Name <- key
            p.Value <- value
            p.Type <- ParameterType.HttpHeader

            HalClientFactory(p :: headers, httpClientFactory)

        member x.Accept(headerValue:string) = 
            x.Header "Accept" headerValue






   



