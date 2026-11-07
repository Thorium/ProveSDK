/// Every call against a fake handler; nothing leaves the machine. The handler seam is global, and tests in one module
/// run one at a time, so all tests that use it stay in this module.
module ClientTests

open System
open System.Net
open System.Net.Http
open System.Globalization
open System.Text
open System.Text.Json
open Xunit
open ProveSDK

type Sent =
    { Url: string
      Path: string
      Authorization: string
      Consent: string
      Body: byte[] }

let private tokenJson = """{"access_token":"t","expires_in":7200,"id_token":"i","session_state":"s","token_type":"bearer"}"""
let private successJson = """{"requestId":"r","status":0,"description":"Success.","response":{"verified":true,"trustScore":700}}"""

/// Answers sign-ins as signIn says and everything else as respond says, and records the two apart.
type private Fake(signIn: HttpStatusCode * string, respond: Sent -> HttpStatusCode * string) =
    inherit HttpMessageHandler()
    let seen = Collections.Generic.List<Sent>()
    let signIns = Collections.Generic.List<Sent>()
    member _.Seen = List.ofSeq seen
    member _.SignIns = List.ofSeq signIns

    override _.SendAsync(request, _) =
        task {
            let! body = request.Content.ReadAsByteArrayAsync()
            let path = request.RequestUri.AbsolutePath

            let header (name: string) =
                match request.Headers.TryGetValues name with
                | true, values -> String.Join(",", values)
                | _ -> ""

            let sent =
                { Url = string request.RequestUri
                  Path = path
                  Authorization = header "Authorization"
                  Consent = header "Consent-Status"
                  Body = body }

            let status, answer =
                if path = "/token" then
                    signIns.Add sent
                    signIn
                else
                    seen.Add sent
                    respond sent

            return new HttpResponseMessage(status, Content = new StringContent(answer))
        }

let private runFake signIn respond (call: Async<'T>) =
    let fake = new Fake(signIn, respond)
    let original = Request.createHandler
    Request.createHandler <- fun () -> fake
    try
        let result = call |> Async.RunSynchronously
        result, fake
    finally
        Request.createHandler <- original

let private withFake respond call =
    let result, fake = runFake (HttpStatusCode.OK, tokenJson) respond call
    result, fake.Seen

let private signingIn signIn call =
    let result, fake = runFake signIn (fun _ -> HttpStatusCode.OK, successJson) call
    result, fake.Seen

let private answering status body = withFake (fun _ -> status, body)
let private succeeding call = answering HttpStatusCode.OK successJson call

let private field (name: string) (sent: Sent) =
    use document = JsonDocument.Parse(sent.Body: byte[])
    match document.RootElement.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> ValueSome(value.ToString())
    | _ -> ValueNone

let private serviceLicense =
    { Environment = ProveEnvironment.Staging
      ProveUser = "user"
      ProvePassword = "the-password"
      ClientId = "client" }

let private smsLicense : Sms.License =
    { Environment = ProveEnvironment.Staging
      ClientId = "client"
      LicenseKey = "the-license-key" }

let private linkLicense : InstantLink.License =
    { Environment = ProveEnvironment.Staging
      ApiClientId = "the-api-client"
      SubClientId = Some "the-sub-client" }

let private verifyWith consent phone first last =
    Identity.callProveVerify serviceLicense consent phone first last (Some "Austin") (Some "78285") (Some "TX") (Some(DateTime(1984, 12, 10))) (Some "565-22-8370")

let private verify phone first last = verifyWith ConsentStatus.OptedIn phone first last

let private prefillWith consent phone =
    Prefill.provePrefillIdentity serviceLicense consent phone (DateTime(1984, 12, 10)) (Some "565-22-8370")

let private prefill phone = prefillWith ConsentStatus.OptedIn phone

let private trustWith consent phone = TrustScore.callProveTrustScore serviceLicense consent phone
let private trust phone = trustWith ConsentStatus.OptedIn phone

[<Fact>]
let ``the handler made by default doesn't follow redirects`` () =
    Assert.False((Request.createHandler () :?> HttpClientHandler).AllowAutoRedirect)

[<Fact>]
let ``verify sends each address part in its own field`` () =
    let _, seen = succeeding (verify "+1 200 100 1686" "Tod" "Weedall")
    let sent = List.exactlyOne seen
    Assert.Equal("/identity/verify/v2", sent.Path)
    Assert.Equal(ValueSome "TX", field "region" sent)
    Assert.Equal(ValueNone, field "extendedAddress" sent)
    Assert.Equal(ValueSome "78285", field "postalCode" sent)
    Assert.Equal(ValueSome "Austin", field "city" sent)
    Assert.Equal(ValueSome "1984-12-10", field "dob" sent)
    Assert.Equal(ValueSome "565228370", field "ssn" sent)

[<Fact>]
let ``prefill sends the date of birth and ssn in their own fields`` () =
    let _, seen = succeeding (prefill "12001001686")
    let sent = List.exactlyOne seen
    Assert.Equal(ValueSome "1984-12-10", field "dob" sent)
    Assert.Equal(ValueSome "565228370", field "ssn" sent)

[<Fact>]
let ``names outside ASCII reach Prove intact`` () =
    let _, seen = succeeding (verify "12001001686" "Zoë" "Müller")
    let sent = List.exactlyOne seen
    Assert.Equal(ValueSome "Zoë", field "firstName" sent)
    Assert.Equal(ValueSome "Müller", field "lastName" sent)

[<Theory>]
[<InlineData("+44 7700 900123", "447700900123")>]
[<InlineData("00447700900123", "447700900123")>]
[<InlineData("2001001686", "2001001686")>]
let ``a number that can be sent keeps its digits`` (typed: string, expected: string) =
    let _, seen = succeeding (verify typed "Tod" "Weedall")
    Assert.Equal(ValueSome expected, field "phoneNumber" (List.exactlyOne seen))

[<Fact>]
let ``verify refuses a national number instead of sending a different one`` () =
    let (isDone, parsed, message), seen = succeeding (verify "07700900123" "Tod" "Weedall")
    Assert.Empty seen
    Assert.False isDone
    Assert.True(Option.isNone parsed)
    Assert.Contains("not in correct format", message)

[<Fact>]
let ``prefill refuses a national number instead of sending a different one`` () =
    let (parsed, message), seen = succeeding (prefill "07700900123")
    Assert.Empty seen
    Assert.True(Option.isNone parsed)
    Assert.Contains("not in correct format", message)

[<Fact>]
let ``trust score refuses a national number instead of sending a different one`` () =
    let (_, message), seen = succeeding (trust "07700900123")
    Assert.Empty seen
    Assert.Contains("not in correct format", message)

[<Fact>]
let ``every identity call sends the consent the caller holds`` () =
    [ ConsentStatus.OptedIn, "optedIn"
      ConsentStatus.OptedOut, "optedOut"
      ConsentStatus.NotCollected, "notCollected"
      ConsentStatus.Unknown, "unknown" ]
    |> List.iter (fun (consent, header) ->
        let _, verifySeen = succeeding (verifyWith consent "12001001686" "Tod" "Weedall")
        let _, prefillSeen = succeeding (prefillWith consent "12001001686")
        let _, trustSeen = succeeding (trustWith consent "12001001686")
        Assert.Equal(header, (List.exactlyOne verifySeen).Consent)
        Assert.Equal(header, (List.exactlyOne prefillSeen).Consent)
        Assert.Equal(header, (List.exactlyOne trustSeen).Consent))

let private generate =
    InstantLink.generate linkLicense "https://example.test/back" None "+447700900123" "session"

[<Theory>]
[<InlineData("<html><body>Bad Gateway</body></html>")>]
[<InlineData("")>]
[<InlineData("null")>]
[<InlineData("[1,2]")>]
let ``an unreadable error from Prove becomes an error naming the status`` (body: string) =
    let result, _ = answering HttpStatusCode.BadGateway body generate
    match result with
    | Error(InstantLink.UnknownError message) -> Assert.Contains("502", message)
    | other -> failwith $"Unexpected: {other}"

[<Theory; InlineData("<html><body>Bad Gateway</body></html>"); InlineData("")>]
let ``an unreadable error on SMS delivery becomes an error naming the status`` (body: string) =
    let result, _ = answering HttpStatusCode.BadGateway body (Sms.send smsLicense "+447700900123" "Hello")
    match result with
    | Error(Sms.UnknownError message) -> Assert.Contains("502", message)
    | other -> failwith $"Unexpected: {other}"

[<Fact>]
let ``Prove's own error keeps its message`` () =
    let body = """{"type":"t","failureCode":1,"responseCode":400,"message":"Bad number"}"""
    let result, _ = answering HttpStatusCode.BadRequest body generate
    match result with
    | Error(InstantLink.UnknownError message) ->
        Assert.Contains("Bad number", message)
        Assert.Contains("400", message)
        Assert.Contains(string body.Length, message)
    | other -> failwith $"Unexpected: {other}"

[<Fact>]
let ``a redirect is still reported and not followed`` () =
    let result, seen = answering HttpStatusCode.TemporaryRedirect "" generate
    Assert.Single seen |> ignore
    match result with
    | Error(InstantLink.UnknownError message) -> Assert.Contains("307", message)
    | other -> failwith $"Unexpected: {other}"

[<Fact>]
let ``printing a service license hides the password`` () =
    let printed = [ $"%A{serviceLicense}"; string serviceLicense; $"{serviceLicense}"; $"%O{serviceLicense}" ]
    printed |> List.iter (fun text -> Assert.DoesNotContain("the-password", text))
    printed |> List.iter (fun text -> Assert.Contains("user", text))

[<Fact>]
let ``printing an SMS license hides the key`` () =
    [ $"%A{smsLicense}"; string smsLicense; $"{smsLicense}" ]
    |> List.iter (fun text -> Assert.DoesNotContain("the-license-key", text))

[<Fact>]
let ``printing an InstantLink license hides the private client keys`` () =
    [ $"%A{linkLicense}"; string linkLicense; $"{linkLicense}" ]
    |> List.iter (fun text ->
        Assert.DoesNotContain("the-api-client", text)
        Assert.DoesNotContain("the-sub-client", text))

/// The JSON forms phoneMatch could take, all of them read through validate.
[<Theory>]
[<InlineData("true", "Yes")>]
[<InlineData("false", "No")>]
[<InlineData("\"true\"", "Yes")>]
[<InlineData("\"false\"", "No")>]
[<InlineData("null", "Indeterminate")>]
let ``phone match is read from a JSON boolean or string`` (json: string, expected: string) =
    let respond sent =
        let requestId = field "requestId" sent |> ValueOption.get
        HttpStatusCode.OK,
        $"""{{"requestId":"{requestId}","status":0,"description":"Success.","response":{{"linkClicked":true,"ipAddressMatch":true,"phoneNumber":"447700900123","phoneMatch":{json}}}}}"""

    let result, _ = withFake respond (InstantLink.validate linkLicense "session" "fingerprint")
    match result with
    | Ok read -> Assert.Equal(expected, string read.PhoneMatch)
    | Error other -> failwith $"Unexpected: {other}"

[<Theory>]
[<InlineData(401, "")>]
[<InlineData(500, "")>]
[<InlineData(500, "   ")>]
[<InlineData(400, """{"error":"invalid_grant"}""")>]
[<InlineData(307, "")>]
[<InlineData(200, "")>]
[<InlineData(200, "<html>proxy</html>")>]
[<InlineData(200, """{"token_type":"bearer"}""")>]
[<InlineData(200, """{"access_token":"","expires_in":7200,"id_token":"i","session_state":"s","token_type":"bearer"}""")>]
[<InlineData(200, """{"access_token":"  ","expires_in":7200,"id_token":"i","session_state":"s","token_type":"bearer"}""")>]
let ``a failed or empty sign-in posts nothing and says so, on all three calls`` (status: int, body: string) =
    let signIn = enum<HttpStatusCode> status, body

    let (isDone, verified, verifyMessage), verifySeen = signingIn signIn (verify "12001001686" "Tod" "Weedall")
    Assert.Empty verifySeen
    Assert.False isDone
    Assert.True(Option.isNone verified)
    Assert.False(String.IsNullOrWhiteSpace verifyMessage)

    let (filled, prefillMessage), prefillSeen = signingIn signIn (prefill "12001001686")
    Assert.Empty prefillSeen
    Assert.True(Option.isNone filled)
    Assert.False(String.IsNullOrWhiteSpace prefillMessage)

    let (score, trustMessage), trustSeen = signingIn signIn (trust "12001001686")
    Assert.Empty trustSeen
    Assert.Equal(ValueNone, score)
    Assert.False(String.IsNullOrWhiteSpace trustMessage)

[<Fact>]
let ``calls carry the token from the sign-in`` () =
    let _, verifySeen = succeeding (verify "12001001686" "Tod" "Weedall")
    let _, prefillSeen = succeeding (prefill "12001001686")
    let _, trustSeen = succeeding (trust "12001001686")
    (verifySeen @ prefillSeen @ trustSeen) |> List.iter (fun sent -> Assert.Equal("Bearer t", sent.Authorization))

[<Fact>]
let ``an empty failure on a call still names the status`` () =
    let (_, _, message), _ = answering HttpStatusCode.InternalServerError "" (verify "12001001686" "Tod" "Weedall")
    Assert.Contains("500", message)

[<Fact>]
let ``a redirect on a signed-in call is reported and not followed`` () =
    let (isDone, _, message), seen = answering HttpStatusCode.TemporaryRedirect "" (verify "12001001686" "Tod" "Weedall")
    Assert.Single seen |> ignore
    Assert.False isDone
    Assert.Contains("307", message)

[<Fact>]
let ``a redirect on sign-in is reported and not followed`` () =
    let (_, message), seen = signingIn (HttpStatusCode.TemporaryRedirect, "") (trust "12001001686")
    Assert.Empty seen
    Assert.Contains("307", message)

let private formValues (sent: Sent) =
    (Encoding.UTF8.GetString sent.Body).Split '&'
    |> Array.map (fun pair ->
        let parts = pair.Split '='
        Assert.Equal(2, parts.Length)
        parts[0], WebUtility.UrlDecode parts[1])
    |> Map.ofArray

[<Fact>]
let ``the sign-in form encodes its values`` () =
    let awkward =
        { serviceLicense with
            ProveUser = "a user&x=1"
            ProvePassword = "p+w&d=%41 é"
            ClientId = "client&id" }

    let call = TrustScore.callProveTrustScore awkward ConsentStatus.OptedIn "12001001686"
    let _, fake = runFake (HttpStatusCode.OK, tokenJson) (fun _ -> HttpStatusCode.OK, successJson) call
    let form = formValues (List.exactlyOne fake.SignIns)
    Assert.Equal<Map<string, string>>(
        Map.ofList
            [ "username", "a user&x=1"
              "password", "p+w&d=%41 é"
              "grant_type", "password"
              "client_id", "client&id" ],
        form
    )

[<Theory>]
[<InlineData("\t07700900123")>]
[<InlineData("\n07700900123")>]
[<InlineData("\r\n07700900123")>]
[<InlineData(" 07700900123")>]
[<InlineData("0007700900123")>]
[<InlineData("00 07700 900123")>]
[<InlineData("+44 (0)7700 900123")>]
[<InlineData("+44(0)7700900123")>]
[<InlineData("+44 (07700) 900123")>]
[<InlineData("(0)7700 900123")>]
[<InlineData("(07700) 900123")>]
[<InlineData("0012")>]
[<InlineData("00")>]
[<InlineData("001234567890123456")>]
[<InlineData("")>]
[<InlineData("not a number")>]
[<InlineData("+44 0")>]
[<InlineData("+44 0123")>]
[<InlineData("+44 00 7700 900123")>]
[<InlineData("+44 07700 900123456")>]
[<InlineData("+44 0abcdefghi")>]
[<InlineData("0044 0")>]
[<InlineData("0044 00 7700 900123")>]
[<InlineData("44 07700 900123")>]
[<InlineData("4407700900123")>]
let ``a number that would change or can't be read is refused, on all three calls`` (typed: string) =
    let (_, _, verifyMessage), verifySeen = succeeding (verify typed "Tod" "Weedall")
    let (_, prefillMessage), prefillSeen = succeeding (prefill typed)
    let (_, trustMessage), trustSeen = succeeding (trust typed)
    Assert.Empty verifySeen
    Assert.Empty prefillSeen
    Assert.Empty trustSeen
    [ verifyMessage; prefillMessage; trustMessage ] |> List.iter (fun message -> Assert.Contains("not in correct format", message))

[<Theory>]
[<InlineData("+1 (200) 100-1686", "12001001686")>]
[<InlineData("(212) 555-1234", "2125551234")>]
[<InlineData("\t+44 7700 900123\r\n", "447700900123")>]
[<InlineData("+44-7700-900123", "447700900123")>]
[<InlineData("0044 7700 900123", "447700900123")>]
[<InlineData("+39 06 1234 5678", "390612345678")>]
[<InlineData("+49 0170 1234567", "4901701234567")>]
[<InlineData("+1 440 555 0100", "14405550100")>]
[<InlineData("4400555010", "4400555010")>]
let ``punctuation goes and the digits stay, on all three calls`` (typed: string, expected: string) =
    let _, verifySeen = succeeding (verify typed "Tod" "Weedall")
    let _, prefillSeen = succeeding (prefill typed)
    let _, trustSeen = succeeding (trust typed)
    (verifySeen @ prefillSeen @ trustSeen)
    |> List.iter (fun sent -> Assert.Equal(ValueSome expected, field "phoneNumber" sent))

[<Fact>]
let ``each call goes to its own address`` () =
    let _, verifySeen = succeeding (verify "12001001686" "Tod" "Weedall")
    let _, prefillSeen = succeeding (prefill "12001001686")
    let _, trustSeen = succeeding (trust "12001001686")
    Assert.Equal("https://api.staging.payfone.com/identity/verify/v2", (List.exactlyOne verifySeen).Url)
    Assert.Equal("https://api.staging.payfone.com/identity/v2", (List.exactlyOne prefillSeen).Url)
    Assert.Equal("https://api.staging.payfone.com/trust/v2", (List.exactlyOne trustSeen).Url)

[<Fact>]
let ``trust score returns the score Prove gave`` () =
    let (score, _), _ = succeeding (trust "12001001686")
    Assert.Equal(ValueSome 700, score)

[<Fact>]
let ``trust score gives no score for a refused number`` () =
    let (score, _), _ = succeeding (trust "07700900123")
    Assert.Equal(ValueNone, score)

[<Theory>]
[<InlineData("{}")>]
[<InlineData("""{"error":"gateway"}""")>]
[<InlineData("""{"message":""}""")>]
[<InlineData("""{"message":null}""")>]
let ``an error body without a message still names the status`` (body: string) =
    let linkResult, _ = answering HttpStatusCode.BadGateway body generate
    match linkResult with
    | Error(InstantLink.UnknownError message) -> Assert.Contains("502", message)
    | other -> failwith $"Unexpected: {other}"

    let smsResult, _ = answering HttpStatusCode.BadGateway body (Sms.send smsLicense "+447700900123" "Hello")
    match smsResult with
    | Error(Sms.UnknownError message) -> Assert.Contains("502", message)
    | other -> failwith $"Unexpected: {other}"

[<Fact>]
let ``a consent status left at its default doesn't assert consent`` () =
    Assert.Equal("unknown", Unchecked.defaultof<ConsentStatus>.AsHeaderValue())
    let zeroed: ConsentStatus[] = Array.zeroCreate 1
    Assert.Equal("unknown", zeroed[0].AsHeaderValue())

let private smsRequest : SmsDelivery.Request =
    { ClientId = "client"
      ClientAcctId = None
      License = "the-license-key"
      App = SmsDelivery.AppName
      ClientContext = "context"
      Data =
        { PhoneNumber = "447700900123"
          NamedData = { MessageText = "Hello" } } }

let private authUrlRequest : GetAuthUrl.Request =
    { RequestId = Guid.Empty
      SessionId = "session"
      ApiClientId = "the-api-client"
      SubClientId = Some "the-sub-client"
      SourceIp = None
      FinalTargetUrl = "https://example.test/back"
      MobileNumber = "+447700900123"
      SubscriptionCustomerId = None }

let private linkResultRequest : InstantLinkResult.Request =
    { RequestId = Guid.Empty
      SessionId = "session"
      ApiClientId = "the-api-client"
      SubClientId = Some "the-sub-client"
      VerificationFingerprint = "fingerprint" }

[<Fact>]
let ``printing an SMS request hides the key`` () =
    [ $"%A{smsRequest}"; string smsRequest; $"{smsRequest}" ]
    |> List.iter (fun text -> Assert.DoesNotContain("the-license-key", text))

[<Fact>]
let ``printing InstantLink requests hides the private client keys`` () =
    [ $"%A{authUrlRequest}"
      string authUrlRequest
      $"{authUrlRequest}"
      $"%A{linkResultRequest}"
      string linkResultRequest
      $"{linkResultRequest}" ]
    |> List.iter (fun text ->
        Assert.DoesNotContain("the-api-client", text)
        Assert.DoesNotContain("the-sub-client", text))

[<Fact>]
let ``the keys still go on the wire, and nothing added for printing does`` () =
    let refusing call = answering HttpStatusCode.BadRequest "{}" call

    let _, smsSeen = refusing (SmsDelivery.execute ProveEnvironment.Staging smsRequest)
    let sms = List.exactlyOne smsSeen
    Assert.Equal(ValueSome "the-license-key", field "license" sms)
    Assert.Equal(ValueNone, field "display" sms)

    let _, authUrlSeen = refusing (GetAuthUrl.execute ProveEnvironment.Staging authUrlRequest)
    let authUrl = List.exactlyOne authUrlSeen
    Assert.Equal(ValueSome "the-api-client", field "apiClientId" authUrl)
    Assert.Equal(ValueNone, field "display" authUrl)

    let _, resultSeen = refusing (InstantLinkResult.execute ProveEnvironment.Staging linkResultRequest)
    let result = List.exactlyOne resultSeen
    Assert.Equal(ValueSome "the-api-client", field "apiClientId" result)
    Assert.Equal(ValueNone, field "display" result)

let private inCulture (name: string) (run: unit -> unit) =
    let original = CultureInfo.CurrentCulture
    CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo name
    try
        run ()
    finally
        CultureInfo.CurrentCulture <- original

[<Fact>]
let ``dates of birth go out in the Gregorian calendar whatever the culture`` () =
    inCulture "th-TH" (fun () ->
        let _, verifySeen = succeeding (verify "12001001686" "Tod" "Weedall")
        let _, prefillSeen = succeeding (prefill "12001001686")
        Assert.Equal(ValueSome "1984-12-10", field "dob" (List.exactlyOne verifySeen))
        Assert.Equal(ValueSome "1984-12-10", field "dob" (List.exactlyOne prefillSeen)))

[<Fact>]
let ``the added timestamp is Gregorian whatever the culture`` () =
    inCulture "th-TH" (fun () ->
        let (_, _, full), _ = succeeding (verify "12001001686" "Tod" "Weedall")
        use document = JsonDocument.Parse full
        let timestamp = document.RootElement.GetProperty("timestamp").GetString()
        Assert.StartsWith(DateTime.UtcNow.Year.ToString CultureInfo.InvariantCulture, timestamp))

[<Theory>]
[<InlineData("+44 07700 900123")>]
[<InlineData("+4407700900123")>]
[<InlineData("+44-07700-900123")>]
[<InlineData("(+44) 07700 900123")>]
[<InlineData("\t+44 07700 900123\r\n")>]
[<InlineData("0044 07700 900123")>]
[<InlineData("00 44 07700 900123")>]
let ``a UK number loses its trunk zero, on all three calls`` (typed: string) =
    let _, verifySeen = succeeding (verify typed "Tod" "Weedall")
    let _, prefillSeen = succeeding (prefill typed)
    let _, trustSeen = succeeding (trust typed)

    [ verifySeen; prefillSeen; trustSeen ]
    |> List.iter (fun seen -> Assert.Equal(ValueSome "447700900123", field "phoneNumber" (List.exactlyOne seen)))

[<Fact>]
let ``a UK number of another length loses its trunk zero`` () =
    let _, nineDigits = succeeding (trust "+44 0169 77 1234")
    let _, sevenDigits = succeeding (trust "+44 0800 1111")
    Assert.Equal(ValueSome "44169771234", field "phoneNumber" (List.exactlyOne nineDigits))
    Assert.Equal(ValueSome "448001111", field "phoneNumber" (List.exactlyOne sevenDigits))

let private smsNumber (sent: Sent) =
    use document = JsonDocument.Parse(sent.Body: byte[])
    document.RootElement.GetProperty("data").GetProperty("phoneNumber").GetString()

[<Theory>]
[<InlineData("+44 07700 900123", "+447700900123")>]
[<InlineData("0044 07700 900123", "+447700900123")>]
[<InlineData("+447700900123", "+447700900123")>]
[<InlineData("2001001686", "2001001686")>]
[<InlineData("+39 06 1234 5678", "+39 06 1234 5678")>]
[<InlineData("+1 (200) 100-1686", "+1 (200) 100-1686")>]
let ``SMS and InstantLink send what was typed, except a UK trunk zero`` (typed: string, expected: string) =
    let _, smsSeen = answering HttpStatusCode.BadRequest "{}" (Sms.send smsLicense typed "Hello")
    let _, linkSeen = answering HttpStatusCode.BadRequest "{}" (InstantLink.generate linkLicense "https://example.test/back" None typed "session")
    Assert.Equal(expected, smsNumber (List.exactlyOne smsSeen))
    Assert.Equal(ValueSome expected, field "mobileNumber" (List.exactlyOne linkSeen))

[<Theory>]
[<InlineData("+44 0")>]
[<InlineData("0044 00 7700 900123")>]
[<InlineData("+44 (0)7700 900123")>]
[<InlineData("07700 900123")>]
[<InlineData("44 07700 900123")>]
[<InlineData("not a number")>]
[<InlineData("")>]
let ``SMS and InstantLink refuse what Identity refuses`` (typed: string) =
    let smsResult, smsSeen = succeeding (Sms.send smsLicense typed "Hello")
    let linkResult, linkSeen = succeeding (InstantLink.generate linkLicense "https://example.test/back" None typed "session")
    Assert.Empty smsSeen
    Assert.Empty linkSeen

    match smsResult, linkResult with
    | Error(Sms.UnknownError smsMessage), Error(InstantLink.UnknownError linkMessage) ->
        Assert.Contains("not in correct format", smsMessage)
        Assert.Contains("not in correct format", linkMessage)
    | other -> failwith $"Unexpected: {other}"

/// Each public data call, reduced to whether it failed and its message. An exception fails the test.
let private dataCalls: (string * Async<ValueOption<string>>) list =
    let failed (result: Async<Result<'T, string>>) =
        async {
            match! result with
            | Error message -> return ValueSome message
            | Ok _ -> return ValueNone
        }

    let ofError (call: Async<Result<'T, ProveErrorResponse>>) =
        failed (async {
            let! result = call
            return result |> Result.mapError _.Message
        })

    [ "verify",
      async {
          let! isDone, parsed, message = verify "12001001686" "Tod" "Weedall"
          return if not isDone && Option.isNone parsed then ValueSome message else ValueNone
      }
      "prefill",
      async {
          let! parsed, message = prefill "12001001686"
          return if Option.isNone parsed then ValueSome message else ValueNone
      }
      "trust score",
      async {
          let! score, message = trust "12001001686"
          return if score |> ValueOption.isNone then ValueSome message else ValueNone
      }
      "generate",
      failed (async {
          let! result = generate
          return result |> Result.mapError string
      })
      "validate",
      failed (async {
          let! result = InstantLink.validate linkLicense "session" "fingerprint"
          return result |> Result.mapError string
      })
      "sms",
      failed (async {
          let! result = Sms.send smsLicense "+447700900123" "Hello"
          return result |> Result.mapError string
      })
      "SmsDelivery.execute", ofError (SmsDelivery.execute ProveEnvironment.Staging smsRequest)
      "GetAuthUrl.execute", ofError (GetAuthUrl.execute ProveEnvironment.Staging authUrlRequest)
      "InstantLinkResult.execute", ofError (InstantLinkResult.execute ProveEnvironment.Staging linkResultRequest) ]

let private failureOf status body (name: string, call) =
    match answering status body call with
    | ValueSome message, seen ->
        Assert.Single seen |> ignore
        message
    | ValueNone, _ -> failwith $"{name} took this as a success"

[<Theory>]
[<InlineData("<html><body>Bad Gateway</body></html>")>]
[<InlineData("Service unavailable")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("null")>]
[<InlineData("[1,2]")>]
[<InlineData("\"text\"")>]
[<InlineData("""{"requestId":""")>]
let ``a 200 that can't be read fails with its status, on every data call`` (body: string) =
    dataCalls |> List.iter (fun call -> Assert.Contains("200", failureOf HttpStatusCode.OK body call))

[<Fact>]
let ``the other 2xx statuses are named too`` () =
    dataCalls
    |> List.iter (fun call -> Assert.Contains("204", failureOf HttpStatusCode.NoContent "" call))

[<Fact>]
let ``an unreadable 200 gives the body's length and kind and none of its text`` () =
    let body = "<html><title>Blocked by proxy</title>" + String('x', 5000) + "</html>"

    dataCalls
    |> List.iter (fun call ->
        let message = failureOf HttpStatusCode.OK body call
        Assert.Contains(string body.Length, message)
        Assert.Contains("markup", message)
        Assert.DoesNotContain("Blocked", message)
        Assert.True(message.Length < 300, message))

[<Theory>]
[<InlineData("<html>Sorry jane.doe@example.test, 447700900123 and 565-22-8370 and 1984-12-10</html>")>]
[<InlineData("""{"requestId":"r","response":{"phoneNumber":"447700900123","individual":{"firstName":"Jack","lastName":"Brown","ssn":"565228370","emailAddresses":["jane.doe@example.test"],"dob":"1984-12-10" """)>]
[<InlineData("""<person><firstName>Jack</firstName><lastName>Brown</lastName></person>""")>]
[<InlineData("""prefix {"firstName":"Jack","lastName":"Brown"}""")>]
[<InlineData("\uFEFF\uFEFF{\"firstName\":\"Jack\",\"lastName\":\"Brown\"}")>]
let ``an unreadable 200 keeps no personal data`` (body: string) =
    dataCalls
    |> List.iter (fun call ->
        let message = failureOf HttpStatusCode.OK body call

        [ "447700900123"; "565"; "8370"; "1984"; "jane.doe"; "example.test"; "Jack"; "Brown" ]
        |> List.iter (fun detail -> Assert.DoesNotContain(detail, message)))

[<Theory>]
[<InlineData("""{"requestId":"r","phoneNumber":"447700900123","firstName":"Jack","lastName":"Brown","ssn":"565228370","email":"jane.doe@example.test"}""")>]
[<InlineData("""{"message":"447700900123 jane.doe@example.test","firstName":"Jack","lastName":"Brown"}""")>]
[<InlineData("""{"code":"447700900123","description":"565-22-8370"}""")>]
[<InlineData("""<person><firstName>Jack</firstName><lastName>Brown</lastName></person>""")>]
[<InlineData("""prefix {"firstName":"Jack","lastName":"Brown"}""")>]
[<InlineData("\uFEFF\uFEFF{\"firstName\":\"Jack\",\"lastName\":\"Brown\"}")>]
[<InlineData("""Sorry Jack Brown, 447700900123""")>]
let ``a non-2xx answer keeps none of its body but named fields`` (body: string) =
    dataCalls
    |> List.iter (fun call ->
        let message = failureOf HttpStatusCode.BadGateway body call
        Assert.Contains("502", message)
        Assert.Contains("characters", message)

        [ "447700900123"; "565"; "8370"; "jane.doe"; "example.test"; "firstName"; "Jack"; "Brown" ]
        |> List.iter (fun detail -> Assert.DoesNotContain(detail, message)))

[<Fact>]
let ``a non-2xx answer gives Prove's parsed fields`` () =
    let body = """{"requestId":"r","status":1000,"description":"Parameter is invalid.","additionalInfo":"dob invalid.","firstName":"Jack"}"""
    let (_, _, message), _ = answering HttpStatusCode.BadRequest body (verify "12001001686" "Tod" "Weedall")
    Assert.Contains("status=1000", message)
    Assert.Contains("Parameter is invalid.", message)
    Assert.Contains("dob invalid.", message)
    Assert.DoesNotContain("Jack", message)

[<Fact>]
let ``a failed sign-in keeps only named fields`` () =
    let (_, _, message), _ =
        signingIn (HttpStatusCode.BadRequest, """{"error":"invalid_grant","username":"jane.doe@example.test"}""") (verify "12001001686" "Tod" "Weedall")

    Assert.Contains("invalid_grant", message)
    Assert.DoesNotContain("jane.doe", message)

[<Fact>]
let ``phoneNumber invalid in a JSON error stays a finished answer, and in plain text it doesn't`` () =
    let json = """{"status":1000,"additionalInfo":"phoneNumber invalid"}"""
    let (isDone, parsed, _), _ = answering HttpStatusCode.BadRequest json (verify "12001001686" "Tod" "Weedall")
    let (score, _), _ = answering HttpStatusCode.BadRequest json (trust "12001001686")
    Assert.True isDone
    Assert.True(Option.isNone parsed)
    Assert.Equal(ValueSome 0, score)

    let (textDone, _, _), _ = answering HttpStatusCode.BadRequest "phoneNumber invalid" (verify "12001001686" "Tod" "Weedall")
    Assert.False textDone

[<Fact>]
let ``a refused number isn't echoed whole, on any call`` () =
    let typed = "07700 900123"
    let (_, _, verifyMessage), _ = succeeding (verify typed "Tod" "Weedall")
    let (_, prefillMessage), _ = succeeding (prefill typed)
    let (_, trustMessage), _ = succeeding (trust typed)

    let smsMessage =
        match succeeding (Sms.send smsLicense typed "Hello") |> fst with
        | Error(Sms.UnknownError message) -> message
        | other -> failwith $"Unexpected: {other}"

    let linkMessage =
        match succeeding (InstantLink.generate linkLicense "https://example.test/back" None typed "session") |> fst with
        | Error(InstantLink.UnknownError message) -> message
        | other -> failwith $"Unexpected: {other}"

    [ verifyMessage; prefillMessage; trustMessage; smsMessage; linkMessage ]
    |> List.iter (fun message ->
        Assert.DoesNotContain("900123", message)
        Assert.Contains("11 digits", message)
        Assert.Contains("not in correct format", message))

[<Fact>]
let ``words in an unreadable 200 aren't taken as Prove's answer`` () =
    let body = "<html>phoneNumber invalid</html>"
    let (isDone, _, _), _ = answering HttpStatusCode.OK body (verify "12001001686" "Tod" "Weedall")
    let (score, _), _ = answering HttpStatusCode.OK body (trust "12001001686")
    Assert.False isDone
    Assert.Equal(ValueNone, score)

[<Theory>]
[<InlineData("""{"status":0,"response":5}""")>]
[<InlineData("""{"status":0,"response":[]}""")>]
[<InlineData("""{"status":0,"response":"text"}""")>]
let ``a response that isn't an object fails, on the identity calls`` (body: string) =
    dataCalls
    |> List.take 3
    |> List.iter (fun call -> Assert.Contains("200", failureOf HttpStatusCode.OK body call))

[<Theory>]
[<InlineData("""{"status":0,"response":{}}""")>]
[<InlineData("""{"status":0,"response":{"trustScore":"high"}}""")>]
let ``a trust score that can't be read fails`` (body: string) =
    let (score, message), _ = answering HttpStatusCode.OK body (trust "12001001686")
    Assert.Equal(ValueNone, score)
    Assert.Contains("200", message)

[<Theory; InlineData("{}"); InlineData("""{"status":"unknown"}""")>]
let ``prefill fails on a status it can't read`` (body: string) =
    let (parsed, message), _ = answering HttpStatusCode.OK body (prefill "12001001686")
    Assert.True(Option.isNone parsed)
    Assert.Contains("200", message)

[<Fact>]
let ``a success status without a response is a failure on InstantLink`` () =
    let echoing sent =
        HttpStatusCode.OK, $"""{{"requestId":"{field "requestId" sent |> ValueOption.get}","status":0}}"""

    let generated, _ = withFake echoing generate
    let validated, _ = withFake echoing (InstantLink.validate linkLicense "session" "fingerprint")

    match generated, validated with
    | Error(InstantLink.UnknownError first), Error(InstantLink.UnknownError second) ->
        Assert.Contains("200", first)
        Assert.Contains("200", second)
    | other -> failwith $"Unexpected: {other}"

[<Theory; InlineData(""); InlineData(""","status":null""")>]
let ``an InstantLink answer without a status fails instead of reading as a match`` (status: string) =
    let respond sent =
        let requestId = field "requestId" sent |> ValueOption.get
        HttpStatusCode.OK,
        $"""{{"requestId":"{requestId}"{status},"response":{{"linkClicked":true,"ipAddressMatch":true,"phoneNumber":"447700900123","phoneMatch":"true"}}}}"""

    let result, _ = withFake respond (InstantLink.validate linkLicense "session" "fingerprint")

    match result with
    | Error(InstantLink.UnknownError message) -> Assert.Contains("200", message)
    | other -> failwith $"Unexpected: {other}"

[<Theory>]
[<InlineData("""{"response":{}}""")>]
[<InlineData("""{"status":0,"response":{}}""")>]
[<InlineData("""{"status":0,"response":{"verified":"maybe"}}""")>]
let ``verify fails on a response without a readable verified, with no exception for the caller`` (body: string) =
    let (isDone, parsed, message), _ = answering HttpStatusCode.OK body (verify "12001001686" "Tod" "Weedall")
    Assert.False isDone
    Assert.True(Option.isNone parsed)
    Assert.Contains("200", message)

[<Fact>]
let ``a readable refusal from Prove stays what it was`` () =
    let body = """{"requestId":"r","status":1000,"description":"Parameter is invalid."}"""
    let (isDone, parsed, _), _ = answering HttpStatusCode.OK body (verify "12001001686" "Tod" "Weedall")
    let (filled, _), _ = answering HttpStatusCode.OK body (prefill "12001001686")
    let (score, full), _ = answering HttpStatusCode.OK body (trust "12001001686")
    Assert.False isDone
    Assert.True(Option.isNone parsed)
    Assert.True(Option.isSome filled)
    Assert.Equal(ValueNone, score)
    Assert.Contains("Parameter is invalid", full)
