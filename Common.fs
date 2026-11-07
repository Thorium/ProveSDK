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
            if innerType.IsValueType then (typedefof<Nullable<_>>).MakeGenericType [|innerType|]
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
      /// An answer without one can't be read: left at its default it would read as Success.
      [<JsonProperty(Required = Required.Always)>]
      Status: ProveStatus
      
      /// A text string that defines the cause of the status code.
      Description: string
      
      /// More detailed information about the status code.
      Response: 'T }
    

/// Printing one (%A, ToString) hides the password.
[<StructuredFormatDisplay("{Display}")>]
type ServiceLicense =
    { Environment: ProveEnvironment
      ProveUser: string
      ProvePassword: string
      ClientId: string }

    member this.Display =
        $"{{ Environment = {this.Environment}; ProveUser = {this.ProveUser}; ClientId = {this.ClientId}; ProvePassword hidden }}"

    override this.ToString() = this.Display

/// The consent you hold from the person to share their data with Prove. Sent as the Consent-Status header.
/// The first case is what an uninitialized value reads as, so it must not assert consent.
[<RequireQualifiedAccess; Struct>]
type ConsentStatus =
    | Unknown
    | OptedIn
    | OptedOut
    | NotCollected

    member this.AsHeaderValue() =
        match this with
        | OptedIn -> "optedIn"
        | OptedOut -> "optedOut"
        | NotCollected -> "notCollected"
        | Unknown -> "unknown"

module internal Request =
    let settings =
        JsonSerializerSettings(
            NullValueHandling = NullValueHandling.Ignore,
            Converters = List(seq { OptionConverter() :> JsonConverter }),
            ContractResolver = CamelCasePropertyNamesContractResolver()
        )

    /// Makes the handler behind every call. Only tests replace it.
    let mutable internal createHandler =
        fun () -> new HttpClientHandler(AllowAutoRedirect = false) :> HttpMessageHandler

    /// A client that doesn't follow redirects.
    let internal newHttpClient () =
        new HttpClient(createHandler ())

    /// An error Prove didn't word itself, in the shape callers read.
    let localError (kind: string) (status: int) (message: string) =
        { Type = kind
          FailureCode = status
          ResponseCode = status
          Message = message
          Headers = null }

    /// Words from Prove's error, which can repeat what was sent: email addresses and digits masked, at most 200 characters.
    /// A name in them can't be recognised.
    let private maskText (text: string) =
        if isNull text then
            null
        else
            let noEmails = System.Text.RegularExpressions.Regex.Replace(text, @"\S*@\S*", "(email)")
            let masked = System.Text.RegularExpressions.Regex.Replace(noEmails, @"\d", "#")
            if masked.Length > 200 then masked.Substring(0, 200) else masked

    /// A body's size and kind, without anything it says.
    let private describeBody (body: string) =
        let text = if isNull body then "" else body.Trim()

        if text = "" then
            "an empty body"
        else
            let kind =
                match text[0] with
                | '{' | '[' -> "JSON"
                | '<' -> "markup"
                | _ -> "text"

            $"a body of {body.Length} characters opening like {kind}"

    /// A code as Prove words one: at most five digits, or a word with at most five digits after it. Anything longer can be a person's number.
    let private readCode (token: Newtonsoft.Json.Linq.JToken) =
        match token.Type with
        | Newtonsoft.Json.Linq.JTokenType.Integer
        | Newtonsoft.Json.Linq.JTokenType.String when System.Text.RegularExpressions.Regex.IsMatch(token.ToString(), @"^[A-Za-z_\-]{0,32}\d{0,5}$") ->
            ValueSome(token.ToString())
        | _ -> ValueNone

    let private readWords (token: Newtonsoft.Json.Linq.JToken) =
        if token.Type = Newtonsoft.Json.Linq.JTokenType.String then ValueSome(maskText (token.ToString())) else ValueNone

    /// The only parts of an error body that reach failure text, and only from a body that is a JSON object.
    let private errorFields (body: string) =
        let readFields (root: Newtonsoft.Json.Linq.JObject) read names =
            names
            |> List.choose (fun (name: string) ->
                match root.GetValue(name, StringComparison.OrdinalIgnoreCase) with
                | null -> None
                | token ->
                    match read token with
                    | ValueSome value when not (String.IsNullOrWhiteSpace value) -> Some $"{name}={value}"
                    | _ -> None)

        try
            let root = Newtonsoft.Json.Linq.JObject.Parse body

            readFields root readCode [ "status"; "code"; "failureCode"; "responseCode"; "error" ]
            @ readFields root readWords [ "description"; "message"; "additionalInfo"; "error_description" ]
        with
        | :? JsonException
        | :? ArgumentNullException -> []

    /// Failure text for a non-2xx answer: the status, the body's size and kind, and the fields errorFields allows. Never the body's text.
    let failureText (status: int) (body: string) =
        match errorFields body with
        | [] -> $"Prove answered HTTP {status} with {describeBody body}."
        | fields ->
            let joined = String.Join("; ", fields)
            $"Prove answered HTTP {status} with {describeBody body}: {joined}"

    /// The error a non-200 answer means: Prove's own if the body holds one with a message, with its words masked, otherwise a local one.
    /// Message is failureText either way.
    let errorFromBody (status: int) (body: string) =
        let unreadable () = localError "unreadable" status (failureText status body)

        try
            let error = JsonConvert.DeserializeObject<ProveErrorResponse> body

            if isNull (box error) || String.IsNullOrWhiteSpace error.Message then
                unreadable ()
            else
                { error with
                    Type = maskText error.Type
                    Message = failureText status body
                    Headers = if isNull error.Headers then null else Array.map maskText error.Headers }
        with :? JsonException ->
            unreadable ()

    /// The error a 2xx answer means when its body can't be read: the status and the body's size and kind. Nothing from the body, whose
    /// words can't be taken as Prove's answer and which can hold a person's details.
    let unreadableMessage (status: int) (body: string) =
        $"Prove answered HTTP {status} with {describeBody body} that can't be read."

    /// The answer in a 200's body, or an error naming the status if the body doesn't hold one.
    let successFromBody<'SuccessResponse> (status: int) (body: string) =
        let unreadable () = Error(localError "unreadable" status (unreadableMessage status body))

        try
            let response = JsonConvert.DeserializeObject<'SuccessResponse> body
            if isNull (box response) then unreadable () else Ok response
        with :? JsonException ->
            unreadable ()

    let execute<'Request, 'SuccessResponse> (url: string) (request: 'Request) =
        async {
            use client = newHttpClient ()
            client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            
            let requestJson = JsonConvert.SerializeObject(
                request, settings
            )
            
            use requestContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
            
            let! response = client.PostAsync(url, requestContent) |> Async.AwaitTask
            
            let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            
            match response.StatusCode with
            | HttpStatusCode.OK ->
                return successFromBody<'SuccessResponse> (int response.StatusCode) responseJson
            | status when int status >= 300 && int status < 400 ->
                return Error(localError "redirect" (int status) $"Prove answered HTTP {int status}, a redirect, which isn't followed.")
            | status ->
                return Error(errorFromBody (int status) responseJson)
        }

    /// A success status without a response is an error: callers read the response on success.
    let executeProve<'Request, 'Response> (url: string) (request: 'Request) =
        async {
            match! execute<'Request, ProveResponse<'Response>> url request with
            | Ok answer when answer.Status = ProveStatus.Success && isNull (box answer.Response) ->
                return Error(localError "unreadable" 200 "Prove answered HTTP 200 with a success status and no response.")
            | result -> return result
        }
        
    let executeLegacyApi<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsLegacyApiUrl() + endpoint
        executeProve<'Request, 'Response> url request
        
    let executeApi<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsApiUrl() + endpoint
        executeProve<'Request, 'Response> url request
        
    let executeMfa<'Request, 'Response> (env: ProveEnvironment) (endpoint: string) (request: 'Request) =
        let url = env.AsMfaUrl() + endpoint
        execute<'Request, 'Response> url request

module internal ServiceCall =

    open System.Globalization
    open System.IO
    open System.Text

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

    /// A 2xx answer to a data call that can't be read. Its message is Request.unreadableMessage, never Prove's own words to act on:
    /// "phoneNumber invalid" in such a body means nothing.
    type UnreadableAnswer(message: string) =
        inherit Exception(message)

    /// Throws unless the body is what every data call needs: a JSON object with a "status" or a "response", whose "response", if it
    /// has one, is an object. read then throws on whatever else the call needs of it.
    let checkAnswer (read: string -> unit) (body: string) =
        let root = Newtonsoft.Json.Linq.JObject.Parse body
        let holds (name: string) = not (isNull root[name]) && root[name].Type <> Newtonsoft.Json.Linq.JTokenType.Null

        if not (holds "status" || holds "response") then
            invalidOp "Neither a status nor a response."

        match root["response"] with
        | null -> ()
        | response when response.Type = Newtonsoft.Json.Linq.JTokenType.Object || response.Type = Newtonsoft.Json.Linq.JTokenType.Null -> ()
        | _ -> invalidOp "The response isn't an object."

        read body

    /// Make a post-web-request, with custom headers. A 2xx body goes through check; if that throws, the call fails with UnreadableAnswer.
    let makePostRequestWithHeaders (check: string -> unit) (reqType: PostRequestTypes) (url : string) (requestBody : string) headers =
        let timeoutMs = 8000
        let timeout =  timeoutMs // Timeout has to be smaller than DTC timeout
        let reqtype =
            match reqType with
            | ApplicationJson -> "application/json"
            | ApplicationUrlForm -> "application/x-www-form-urlencoded"
        let asynccall =
            async {
                let! res =
                    async{
                        use client = Request.newHttpClient ()
                        client.Timeout <- TimeSpan.FromMilliseconds(float timeout)

                        use request = new HttpRequestMessage(HttpMethod.Post, url)
                        request.Version <- Version(1, 0)

                        headers |> Seq.iter(fun (h:string,k:string) ->
                            if not (String.IsNullOrEmpty h) then
                                request.Headers.TryAddWithoutValidation(h, k) |> ignore
                        )

                        request.Content <- new StringContent(requestBody, Encoding.UTF8, reqtype)

                        let! response = client.SendAsync(request) |> Async.AwaitTask
                        let! rdata = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                        if response.IsSuccessStatusCode then
                            try
                                check rdata
                            with _ ->
                                raise (UnreadableAnswer(Request.unreadableMessage (int response.StatusCode) rdata))

                            return rdata
                        elif int response.StatusCode >= 300 && int response.StatusCode < 400 then
                            return failwith $"Prove answered HTTP {int response.StatusCode}, a redirect, which isn't followed."
                        else
                            return failwith (Request.failureText (int response.StatusCode) rdata)
                    } |> Async.Catch
                match res with
                | Choice1Of2 x -> return x, None
                | Choice2Of2 e when not (isNull e.Message) ->
                    return e.Message, Some e
                | Choice2Of2 e ->
                    match e with
                    | :? WebException as wex when not(isNull wex.Response) ->
                        use stream = wex.Response.GetResponseStream()
                        use reader = new StreamReader(stream)
                        let! err = reader.ReadToEndAsync() |> Async.AwaitTask
                        return err, Some e
                    | :? TimeoutException as e ->
                        return failwith(e.ToString())
                    | _ ->
                        // System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw()
                        return failwith(e.ToString())
            }
        asynccall

    /// Signs in. Ok holds a token that isn't blank. On Error, callers must send nothing.
    let proveAuth (license:ServiceLicense) =
        async {
            let encode (value: string) = Uri.EscapeDataString value
            let bodyPart = "username=" + encode license.ProveUser + "&password=" + encode license.ProvePassword + "&grant_type=password&client_id=" + encode license.ClientId
            match! makePostRequestWithHeaders ignore PostRequestTypes.ApplicationUrlForm (license.Environment.AsLegacyApiUrl() + "/token") bodyPart [] with
            | r, None ->
                let token =
                    try
                        (ProveAuthResponse.Load (Serializer.Deserialize r)).AccessToken
                    with
                    | _ -> ""

                if String.IsNullOrWhiteSpace token then
                    return Error "Prove answered the sign-in without an access token."
                else
                    return Ok token
            | err, Some r ->
                return Error r.Message
        }

    let private startsWith (prefix: string) (text: string) = text.StartsWith(prefix, StringComparison.Ordinal)

    /// The typed number's digits, and whether a "+" opens it.
    let private readTyped (phoneNumber: string) =
        let written = phoneNumber |> String.filter (fun c -> not (Char.IsWhiteSpace c || c = '-' || c = '(' || c = ')'))
        written.Replace("+", ""), startsWith "+" written

    /// An international number without a UK trunk zero: 4407700900123 is 447700900123. E.164 drops that zero for +44, and a UK national
    /// number is 7 to 10 digits not starting with zero, so anything else after 440 is ValueNone. Other country codes keep every digit:
    /// Italy's +39 06 keeps its zero, and nothing here says which countries drop theirs.
    let private withoutUkTrunkZero (international: string) =
        if startsWith "440" international then
            let national = international.Substring 3
            if national.Length >= 7 && national.Length <= 10 && not (startsWith "0" national) then ValueSome("44" + national) else ValueNone
        else
            ValueSome international

    /// The number to send for a phone number typed with or without whitespace and punctuation. ValueNone if it isn't digits, or if a
    /// trunk zero in it can't be removed: parsing drops a leading zero and a bracketed one stays mid-number, and either leaves a different
    /// number. After "+44" or "0044" see withoutUkTrunkZero. A "00" international prefix goes only when a whole international number
    /// follows: 7 to 15 digits, not starting with zero. Without "+" or "00", 12 or 13 digits opening 440 are a UK number with its trunk
    /// zero and are refused; shorter ones can be something else, such as a US number in area code 440.
    let tryParsePhone (phoneNumber: string) =
        let compact = phoneNumber |> String.filter (fun c -> not (Char.IsWhiteSpace c || c = '-' || c = '+'))
        let digits, opensWithPlus = readTyped phoneNumber

        let toSend =
            if compact.Contains "(0" then
                ValueNone
            elif startsWith "00" digits then
                let number = digits.Substring 2
                if number.Length >= 7 && number.Length <= 15 && not (startsWith "0" number) then withoutUkTrunkZero number else ValueNone
            elif startsWith "0" digits then
                ValueNone
            elif opensWithPlus then
                withoutUkTrunkZero digits
            elif startsWith "440" digits && digits.Length >= 12 then
                ValueNone
            else
                ValueSome digits

        toSend
        |> ValueOption.bind (fun number ->
            match Int64.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, parsed -> ValueSome parsed
            | _ -> ValueNone)

    /// The number for a call that sends what was typed: ValueNone for every number tryParsePhone refuses. A number with "+44" or "0044"
    /// and then a trunk zero goes in E.164 with its plus. Anything else stays as typed.
    let tryAsTyped (phoneNumber: string) =
        let digits, opensWithPlus = readTyped phoneNumber
        let international = if opensWithPlus then digits elif startsWith "00" digits then digits.Substring 2 else ""

        tryParsePhone phoneNumber
        |> ValueOption.map (fun number ->
            if startsWith "440" international then "+" + number.ToString CultureInfo.InvariantCulture else phoneNumber)

    /// The failure for a refused number: how many digits it holds and, from seven up, its last two. Never the whole number.
    let refusedNumber (phoneNumber: string) =
        let digits = phoneNumber |> String.filter (fun c -> Char.IsDigit c)

        if digits.Length >= 7 then
            let ending = digits.Substring(digits.Length - 2)
            $"phoneNumber of {digits.Length} digits ending {ending} was not in correct format"
        else
            $"phoneNumber of {digits.Length} digits was not in correct format"

    /// A date as Prove reads it, in the Gregorian calendar whatever the current culture.
    let isoDate (date: DateTime) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let addTimeStamp str =
        try
            let jsonObj = Newtonsoft.Json.Linq.JObject.Parse str
            if not <| jsonObj.ContainsKey "timestamp" then
                jsonObj.["timestamp"] <- Newtonsoft.Json.Linq.JValue(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
            jsonObj.ToString Formatting.None
        with
        | _ -> str
