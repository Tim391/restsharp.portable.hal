﻿namespace RestSharp.Portable.Hal.CSharp

open RestSharp.Portable.Hal
open System.Collections.Generic
open System.Threading.Tasks
open System.Reflection

type Resource internal (inner:Client.Resource) = 
    member this.Parse<'T>() = 
        inner.Parse<'T>()
and
    RequestContext internal (inner:Client.RequestContext) = 
    
    member this.GetAsync () = 
        let work = async {
            let! result = inner.GetAsync()
            return Resource(result)
        }
        work |> Async.StartAsTask
    
    member this.GetAsync<'T> () = 
        inner.GetAsync<'T>() |> Async.StartAsTask

    member this.Follow (rel:string) = 
        RequestContext(inner.Follow rel)
    
    member this.Follow (rel:string, segments:System.Object) = 
        let properties = 
            segments.GetType().GetRuntimeProperties()
            |> Seq.map (fun o -> o.Name => o.GetValue(segments))
            |> List.ofSeq
        RequestContext (inner.Follow(rel, properties))

type HalClient internal (inner:Client.HalClient) = 
    member this.From domain = 
        inner.From domain
        |> fun rc -> RequestContext(rc)

type HalClientFactory private (inner: Client.HalClientFactory) = 
    new() = HalClientFactory(Client.HalClientFactory())

    member this.Accept x = 
        inner.Accept x
        |> fun c -> HalClientFactory(c)

    member this.CreateHalClient domain = 
        inner.CreateHalClient domain
        |> fun c -> HalClient(c)