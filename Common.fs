namespace ProveSDK

open System
open System.Collections.Generic
open System.Net
open System.Net.Http.Headers
open Newtonsoft.Json.Serialization
open System.Net.Http
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open FSharp.Data.JsonProvider

type OptionConverter() =
    inherit JsonConverter()
    
    override x.CanConvert(t) = 
        let canConvert = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>
        canConvert

    override x.WriteJson(writer, value, serializer) =
        let value = 
            if isNull value then null
            else 
                let _,fields = FSharpValue.GetUnionFields(value, value.GetType())
                fields.[0]  
        serializer.Serialize(writer, value)

    override x.ReadJson(reader, t, existingValue, serializer) =        
        let innerType = t.GetGenericArguments().[0]
        let innerType = 
            if innerType.IsValueType then (typedefof<Nullable<_>>).MakeGenericType([|innerType|])
            else innerType        
        let value = serializer.Deserialize(reader, innerType)
        let cases = FSharpType.GetUnionCases(t)
        if isNull value then FSharpValue.MakeUnion(cases.[0], [||])
        else FSharpValue.MakeUnion(cases.[1], [|value|])

module internal Links =
    let [<Literal>] LegacyStagingApi = "https://api.staging.payfone.com"
    let [<Literal>] LegacyProductionApi = "https://api.payfone.com"
    let [<Literal>] StagingApi = "https://api.uat.proveapis.com"
    let [<Literal>] ProductionApi = "https://api.proveapis.com"
    let [<Literal>] StagingMfa = "https://uat.mfa.proveapis.com"
    let [<Literal>] ProductionMfa = "https://mfa.proveapis.com"
    
[<RequireQualifiedAccess>]
type ProveEnvironment =
    | Staging
    | Production
    
    member this.AsLegacyApiUrl() =
        match this with
        | Staging -> Links.LegacyStagingApi
        | Production -> Links.LegacyProductionApi
        
    member this.AsApiUrl() =
        match this with
        | Staging -> Links.StagingApi
        | Production -> Links.ProductionApi
    
    member this.AsMfaUrl() =
        match this with
        | Staging -> Links.StagingMfa
        | Production -> Links.ProductionMfa
    
type ProveStatus =
    | Success = 0
    | RequestCannotBeProcessed = 1
    | RequestParseFailure = 2
    | RequestTimeout = 3
    | ParameterInvalid = 1000
    | CannotIdentifySubscriber = 1001
    | SubscriberNotFoundInWhitelist = 1002
    | SubscriberNotSupported = 1003
    | ApiClientOrOperatorNotEnabled = 1005
    | SubscriberSuspended = 1006
    | SubscriberDeactivated = 1007
    | SubscriberNotEligible = 1010
    | NoCRMDataAvailable = 1012
    | CarrierMismatch = 1038
    | TransactionNotCompleted = 1039
    | ExternalProviderCannotBeReached = 1102
    | ExternalProviderCannotBeReached' = 1103
    | InvalidOrInactiveNumber = 1120
    
type ProveErrorResponse =
    { Type: string
      FailureCode: int
      ResponseCode: int
      Message: string
      Headers: string[] }
        
type ProveResponse<'T> =
    { /// The requestId from the request, reflected back for tracking purposes.
      RequestId: Guid
      
      /// The status of the request. A response of 0 indicates success.
      /// Any non-0 response is an error indication.
      /// For more information on status codes, see the Error and Status Codes section.
      Status: ProveStatus
      
      /// A text string that defines the cause of the status code.
      Description: string
      
      /// More detailed information about the status code.
      Response: 'T }
    

type ServiceLicense =
    { Environment: ProveEnvironment
      ProveUser: string
      ProvePassword: string
      ClientId: string }

module internal Request =
    let settings =
        JsonSerializerSettings(
            NullValueHandling = NullValueHandling.Ignore,
            Converters = List(seq { OptionConverter() :> JsonConverter }),
            ContractResolver = CamelCasePropertyNamesContractResolver()
        )

    let execute<'Request, 'SuccessResponse, 'ErrorResponse> (url: string) (request: 'Request) =
        async {
            use client = new HttpClient()
            client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            
            let requestJson = JsonConvert.SerializeObject(
                request, settings
            )
            
            use requestContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
            
            let! response = client.PostAsync(url, requestContent) |> Async.AwaitTask
            
            let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            
            match response.StatusCode with
            | HttpStatusCode.OK ->
                let response = JsonConvert.DeserializeObject<'SuccessResponse>(responseJson)
                return Ok response
            | _ ->
                let error = JsonConvert.DeserializeObject<'ErrorResponse>(responseJson)
                return Error error
        }

    let executeProve<'Request, 'Response> (url: string) (request: 'Request) =
        execute<'Request, ProveResponse<'Response>, ProveErrorResponse> url request
        
    let executeLegacyApi<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsLegacyApiUrl() + endpoint
        executeProve<'Request, 'Response> url request
        
    let executeApi<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsApiUrl() + endpoint
        executeProve<'Request, 'Response> url request
        
    let executeMfa<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsMfaUrl() + endpoint
        execute<'Request, 'Response, ProveErrorResponse> url request

module internal ServiceCall =

    open System.IO

    type internal ProveAuthResponse = FSharp.Data.JsonProvider<"""{
        "access_token": "123hbGciOi...vB_PmVfkZQ",
        "expires_in": 7200,
        "id_token": "123hb...5cKTfbLbg",
        "session_state": "12328548-ad03-1234-1236-12324c74e21a",
        "token_type": "bearer"
        }""">

    [<Struct>]
    type PostRequestTypes =
    | ApplicationJson
    | ApplicationUrlForm

    /// Make a post-web-request, with custom headers
    let makePostRequestWithHeaders (reqType: PostRequestTypes) (url : string) (requestBody : string) (headers) =
        let timeoutMs = 8000
        let req = WebRequest.CreateHttp url
        headers |> Seq.iter(fun (h:string,k:string) ->
            if not (String.IsNullOrEmpty h) then
                if h.ToLower() = "user-agent" then
                    req.UserAgent <- k
                else
                    req.Headers.Add(h,k)
        )
        req.CookieContainer <- new CookieContainer()
        req.Method <- "POST"
        let timeout =  timeoutMs // Timeout has to be smaller than DTC timeout
        req.Timeout <- timeout
        req.ProtocolVersion <- HttpVersion.Version10
        let postBytes = requestBody |> System.Text.Encoding.ASCII.GetBytes
        req.ContentLength <- postBytes.LongLength
        let reqtype =
            match reqType with
            | ApplicationJson -> "application/json"
            | ApplicationUrlForm -> "application/x-www-form-urlencoded"
        req.ContentType <- reqtype
        let asynccall =
            async {
                let! res =
                    async{
                        let! reqStream = req.GetRequestStreamAsync() |> Async.AwaitTask
                        do! reqStream.WriteAsync(postBytes, 0, postBytes.Length) |> Async.AwaitIAsyncResult |> Async.Ignore
                        reqStream.Close()
                        let! res =
                            async { // Async methods are not using req.Timeout
                                let! child = Async.StartChild(req.AsyncGetResponse(), timeout)
                                return! child
                            }
                        use stream = res.GetResponseStream()
                        use reader = new StreamReader(stream)
                        let! rdata = reader.ReadToEndAsync() |> Async.AwaitTask
                        return rdata
                    } |> Async.Catch
                match res with
                | Choice1Of2 x -> return x, None
                | Choice2Of2 e ->
                    match e with
                    | :? WebException as wex when not(isNull wex.Response) ->
                        use stream = wex.Response.GetResponseStream()
                        use reader = new StreamReader(stream)
                        let err = reader.ReadToEnd()
                        return err, Some e
                    | :? TimeoutException as e ->
                        return failwith(e.ToString())
                    | _ ->
                        // System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw()
                        return failwith(e.ToString())
            }
        asynccall

    let proveAuth (license:ServiceLicense) =
        async {
            let bodyPart = "username=" + license.ProveUser + "&password=" + license.ProvePassword + "&grant_type=password&client_id=" + license.ClientId
            let! res = makePostRequestWithHeaders PostRequestTypes.ApplicationUrlForm (license.Environment.AsLegacyApiUrl() + "/token") bodyPart []
            match res with
            | r, None ->
                let tokenResp = ProveAuthResponse.Load (Serializer.Deserialize r)
                return tokenResp.AccessToken, ""
            | err, Some r ->
                return "", r.Message
        }

    let addTimeStamp str =
        try
            let jsonObj = Newtonsoft.Json.Linq.JObject.Parse str
            if not <| jsonObj.ContainsKey "timestamp" then
                jsonObj.["timestamp"] <- Newtonsoft.Json.Linq.JValue(DateTime.UtcNow.ToString "yyyy-MM-ddTHH:mm:ss.fff")
            jsonObj.ToString Newtonsoft.Json.Formatting.None
        with
        | _ -> str
