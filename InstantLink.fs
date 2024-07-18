namespace ProveSDK

open System
open ProveSDK

module private InstantLinkEndpoints =
    let [<Literal>] GetAuthUrl = "/fortified/2015/06/01/getAuthUrl"
    let [<Literal>] InstantLinkResult = "/fortified/2015/06/01/instantLinkResult"
        
module GetAuthUrl =
    /// As the starting point for Instant Link, the /getAuthUrl endpoint provides an authentication URL
    /// (appended with the first, unique verification fingerprint) that can be sent via SMS to initiate
    /// the middle authentication step.
    type Request =
        { /// Unique identifier associated with this request. This identifier must be unique for each transaction.
          RequestId: Guid
          
          /// Session identifier associated with this request. Max length is 128 bytes. 
          SessionId: string
          
          /// Prove-issued unique, private key that identifies the API Client.
          ApiClientId: string
          
          /// Prove-issued unique, private key that identifies Sub Client.
          SubClientId: string option
          
          /// The IP address of the device the request initiates from.
          /// This adds an additional fraud detection indicator, as Prove can use this value to complete
          /// an IP comparison with the IP of the device that actually clicks the link sent.
          SourceIp: string option
          
          /// The URL of the client server that will be called back by the phone, providing the VFP for the result call.
          FinalTargetUrl: string
          
          /// The mobile number that is being sent the SMS link.
          /// Formatted in E.164 formatting for international numbers including the leading plus sign.
          MobileNumber: string
          
          /// A unique identifier for our customer's subscribers for Identity Manager.
          SubscriptionCustomerId: string option }
        
    type Response =
        { /// Fortified URL to be sent via SMS to the mobile device to be clicked on by the device to be authenticated.
          /// The max length returned is 2048 bytes.
          AuthenticationUrl: string
          
          /// The carrier related to the phone number.
          MobileOperatorName: string }
        
    let execute (env: ProveEnvironment) (request: Request) =
        Request.executeLegacyApi<Request, Response> env InstantLinkEndpoints.GetAuthUrl request
        
module InstantLinkResult =
    /// To complete the Instant Link flow, the /instantLinkResult endpoint passes the second,
    /// unique verification fingerprint returned by the mobile device, and identifies whether
    /// Instant Link was completed with or without carrier authentication, whether
    /// the link was clicked within the expiration time, and whether the input device was where the link was clicked.
    type Request =
        { /// Unique identifier associated with this request. This identifier must be unique for each transaction.
          RequestId: Guid
          
          /// Session identifier associated with this request. Passed in to the getAuthUrl call to identify the session.
          /// Max length is 128 bytes.
          SessionId: string
          
          /// Prove-issued unique, private key that identifies the API Client.
          ApiClientId: string
          
          /// Prove-issued unique, private key that identifies Sub Client.
          SubClientId: string option
          
          /// The VFP value returned by the mobile device after authentication.
          VerificationFingerprint: string }
        
    type Response =
        { /// Unique transaction identifier used to identify the results of the request.
          TransactionId: string
          
          /// The phone number of the originating phone
          PhoneNumber: string
          
          /// The carrier related to the phone number
          Carrier: string
          
          /// Line type associated with the phone number.
          LineType: string
          
          /// The country code associated with the phone number
          CountryCode: string
          
          /// Boolean informing of whether the Instant Link was clicked before it expired (5-minute limit).
          LinkClicked: bool
          
          /// Indicates if, after the link was clicked and carrier authentication successfully performed,
          /// the link was clicked on by the intended mobile device.
          PhoneMatch: string
          
          /// Boolean informing as to whether the link was clicked on the device that has the same
          /// public IP address as the web application that started the Instant Link process.
          ///
          /// The goal is to understand if the application visitor is on the same network
          /// as the person who received and clicked on the instant link on the mobile device.
          IpAddressMatch: bool
          
          /// Session identifier associated with this request flow.
          /// It should be the same session ID as the /getAuthUrl call.
          SessionId: string
          
          /// An optional output (must be configured at the merchant level and not available for all customers)
          /// that returns the device's IP where the link was clicked.
          /// This can be compared to the sourceIp submitted in the /getAuthUrl call.
          DeviceIp: string }
        
    let execute (env: ProveEnvironment) (request: Request) =
        Request.executeLegacyApi<Request, Response> env InstantLinkEndpoints.InstantLinkResult request

module InstantLink =
    open GetAuthUrl

    type License =
        { Environment: ProveEnvironment
          ApiClientId: string
          SubClientId: string option }

    type InstantLinkError =
        | UnknownError of string
        | MismatchedResponse
        | ProveError of status: ProveStatus * description: string

    type InstantLink = InstantLink of string

    [<Struct>]
    type PhoneMatch =
        | Yes
        | No
        | Indeterminate

    type InstantLinkResult =
        { LinkClicked: bool
          PhoneMatch: PhoneMatch
          IpAddressMatch: bool }

    let generate (license: License) (redirectUrl: string) (ipAddress: string option) (phoneNumber: string) (sessionId: string) =
        async {
            let requestId = Guid.NewGuid()
            let redirectUrl = $"{redirectUrl}?sessionId={sessionId}"

            let request =
                { RequestId = requestId
                  SessionId = sessionId
                  ApiClientId = license.ApiClientId
                  SubClientId = license.SubClientId
                  SourceIp = ipAddress
                  FinalTargetUrl = redirectUrl
                  MobileNumber = phoneNumber
                  SubscriptionCustomerId = None }

            let! response = execute license.Environment request

            match response with
            | Error error ->
                return Error (UnknownError error.Message)

            | Ok data ->
                if data.RequestId <> requestId then
                    return Error MismatchedResponse
                elif data.Status <> ProveStatus.Success then
                    return Error (ProveError(data.Status, data.Description))
                else
                    return Ok (InstantLink data.Response.AuthenticationUrl)
        }
        
    open InstantLinkResult

    let validate (license: License) (sessionId: string) (verificationFingerprint: string) =
        async {
            let requestId = Guid.NewGuid()

            let request =
                { RequestId = requestId
                  SessionId = sessionId
                  ApiClientId = license.ApiClientId
                  SubClientId = license.SubClientId
                  VerificationFingerprint = verificationFingerprint }

            let! response = execute license.Environment request

            match response with
            | Error error ->
                return Error (UnknownError error.Message)

            | Ok data ->
                if data.RequestId <> requestId then
                    return Error MismatchedResponse
                elif data.Status <> ProveStatus.Success then
                    return Error (ProveError(data.Status, data.Description))
                else
                    return
                        Ok
                            { LinkClicked = data.Response.LinkClicked
                              PhoneMatch =
                                match data.Response.PhoneMatch with
                                | "true" -> PhoneMatch.Yes
                                | "false" -> PhoneMatch.No
                                | _ -> PhoneMatch.Indeterminate
                              IpAddressMatch = data.Response.IpAddressMatch }
        }
