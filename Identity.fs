namespace ProveSDK

open System
open ProveSDK
open FSharp.Data.JsonProvider

module Identity =

    type internal ProveVerifyRequest = FSharp.Data.JsonProvider< """[{
        "requestId": "1233-b0c4-90e0-90b3-11e1-1230200c9a66",
        "phoneNumber": "12339998877",
        "firstName": "Bob",
        "lastName": "Smith"},{
        "requestId": "1233-b0c4-90e0-90b3-11e1-1230200c9a66",
        "phoneNumber": "12339998877",
        "firstName": "Bob",
        "lastName": "Smith",
        "address": "123 Main St",
        "extendedAddress": "Apt. 201",
        "city": "San Francisco",
        "dob": "2022-01-15x",
        "ssn": "123456780x",
        "region": "CA",
        "postalCode": "93015x",
        "details": false}]""", SampleIsList=true>

    type internal ProveVerifyResponse = FSharp.Data.JsonProvider<"""[{
  "requestId": "1233-b0c4-90e0-9123-11e10800200c9123",
  "response": {
    "troubleshootingId": "1234567123",
    "verified": true,
    "name": {
      "firstName": 100,
      "lastName": 100,
      "nameScore": 100
    },
    "address": {
      "addressScore": 100
    },
    "numberInfo": {
      "carrier": "EE",
      "countryCode": "GB",
      "lineType": "mobile",
      "accountType": "postpay"
    }
  },
  "status": 0
},{
"requestId": "1233-b0c4-90e0-90b3-11e10800200c9123",
"response": {
  "troubleshootingId": "1234567890",
  "verified": true,
  "name": {
    "firstName": 100,
    "lastName": 100,
    "nameScore": 100
  },
  "address": {
    "addressScore": 100
  },
  "dob": true,
  "identifiers": [
    {
      "country": "br",
      "idType": "cpf",
      "idVerified": false
    }
  ],
  "numberInfo": {
    "carrier": "EE",
    "countryCode": "GB",
    "lineType": "mobile",
    "accountType": "prepay"
  }
},
"status": 0
},{
"requestId": "1233-b0c4-90e0-90b3-11e10800200c9123",
"response": {
  "troubleshootingId": "1234567890",
  "verified": false,
  "name": {
    "firstName": 0,
    "lastName": 0,
    "nameScore": 0
  },
  "address": {
    "addressScore": 0
  },
  "dob": false,
  "numberInfo": {
    "carrier": "EE",
    "countryCode": "GB",
    "lineType": "mobile",
    "accountType": "prepay"
  },
  "indicators": [
    "ND"
  ]
},
"status": 0
},
{"requestId":"1232b60b-1e10-4c6e-955a-1236fc734123","status":1000,"description":"Parameter is invalid.","additionalInfo":"Unsupported country."},
{
    "description": "Success",
    "requestId": "1233-b0c4-90e0-90b3-11e1-0800200c9123",
    "response": {
        "transactionId": "1234567890",
        "phoneNumber": "13039991234",
        "lineType": "mobile",
        "carrier": "AT&T Wireless",
        "countryCode": "US",
        "verified": true,
        "address": {
            "streetNumber": 100,
            "street": true,
            "city": true,
            "region": true,
            "postalCode": true,
            "distance": 0.0,
            "addressScore": 100
        },
        "name": {
            "firstName": 100,
            "lastName": 100,
            "nameScore": 100
        },
    "identifiers": { 
      "last4": true, 
	  "ssn": true,
      "dob": true,
      "driversLicenseState": true, 
      "driversLicenseNumber": true
      },
      "knowYourCustomer": {
        "TotalHits": 0
      },   
        "email": {
            "emailAddress": true      
        },
        "reasonCodes": [ "NA", "P9", "OL" ]
  },
    "status": 0
},{
    "description": "Success",
    "requestId": "1233-b0c4-90e0-1233-11e1-0800200c9123",
    "response": {
        "transactionId": "1234567890",
        "phoneNumber": "13039991234",
        "lineType": "mobile",
        "carrier": "AT&T Wireless",
        "countryCode": "US",
        "verified": true,
        "address": {
            "addressScore": 100
        },
        "name": {
            "nameScore": 100
        },
    "identifiers": { 
      },   
	"email": {
		"emailAddress": true      
	},
	"reasonCodes": [ "NA", "P9", "OL" ]
  },
    "status": 0
},
{
    "description": "Success",
    "requestId": "1233-b0c4-90e0-90b3-11e1-0800200c9123",
    "response": {
		"verified":false,"transactionId":"12312311123","payfoneAlias":"ABC123","phoneNumber":"12001001234","lineType":"Mobile","carrier":"T-Mobile USA","countryCode":"US","cipConfidence":"low","reasonCodes":["NC"]}
},
{    "requestId": "1231d473-f80b-40b1-93f4-123807d93123",    "status": 0,    "description": "Success.",    "response": {      "transactionId": "12336852123",      "payfoneAlias": "123456789ABCXYZC4E9F512309B957C8D0MEK123J9C9X2B64D173C627C215123AA25E251A88A5123F6G358A77123CE2C8D681239853028EA6FD01234",      "phoneNumber": "12335135123",      "lineType": "Mobile",      "carrier": "Verizon",      "countryCode": "US",      "verified": true,      "cipConfidence": "low",      "name": {        "firstName": 27,        "lastName": 100,        "nameScore": 71      },      "address": {        "city": true,        "postalCode": true,        "distance": 5.37,        "addressScore": 16      },      "identifiers": {        "ssn": false,        "dob": false      },      "reasonCodes": [        "OL",        "P5",        "RM",        "UV"      ]    },"timestamp": "2024-06-08T00:54:12.911"}
]""", SampleIsList=true>

    type ProveVerifyResponseRoot = ProveVerifyResponse.Root

    /// Loads call verify response from Json
    let loadResponseFromString json =
         ProveVerifyResponse.Load (Serializer.Deserialize json) : ProveVerifyResponseRoot

    /// Call Prove Verify.
    /// Returns: Is call done, strongly typed response (if successful), and full response as string
    let callProveVerify license (consent: ConsentStatus) (phonenr:string) firstname lastname city zipCode stateCode (dateOfBirth:DateTime option) (ssn:string option) =
        async {
            match! ServiceCall.proveAuth license with
            | Error err ->
                return false, None, err
            | Ok auth ->

            match ServiceCall.tryParsePhone phonenr with
            | ValueNone ->
                return false, None, ServiceCall.refusedNumber phonenr
            | ValueSome phone ->

            let req =
                ProveVerifyRequest.Root(
                    requestId = Guid.NewGuid().ToString(),
                    phoneNumber = phone,
                    firstName = firstname,
                    lastName = lastname,
                    address = None,
                    city = city,
                    details = Some true,
                    dob = (dateOfBirth |> Option.map ServiceCall.isoDate),
                    extendedAddress = None,
                    postalCode = zipCode,
                    region = stateCode,
                    ssn = (ssn |> Option.map(fun ssnNr -> ssnNr.Replace(" ", "").Replace("-", ""))))
                    .JsonValue
                |> Serializer.Serialize
            let readVerified (body: string) = (loadResponseFromString body).Response |> Option.iter (fun response -> response.Verified |> ignore)
            let! res = ServiceCall.makePostRequestWithHeaders (ServiceCall.checkAnswer readVerified) ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/identity/verify/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", consent.AsHeaderValue()]
            match res with
            | fullResponse, None ->
                let parsedResp = loadResponseFromString fullResponse : ProveVerifyResponseRoot
                let timestamped = ServiceCall.addTimeStamp fullResponse
                match parsedResp.Response with
                | Some response -> return true, Some response, timestamped
                | None -> return false, None, timestamped
            | err, Some(:? ServiceCall.UnreadableAnswer) ->
                return false, None, err
            | err, Some r ->
                if err.ToString().Contains "phoneNumber invalid" then
                    return true, None, err
                else
                    return false, None, err
        }

module Prefill =
    open System

    type internal ProveIdentityRequest = FSharp.Data.JsonProvider< """[{
        "requestId":"1233-b0c4-90e0-90b3-12310800200c9a66",
        "phoneNumber":"15556667777"
    },{
        "requestId":"1234-b0c4-90e0-90b3-12310800200c9a66",
        "phoneNumber":"15556667777",
        "dob":"2021-01-01x",
        "ssn":"1234-56-7890"
    }]""", SampleIsList=true> //"last4":"1234"

    type internal ProveIdentityResponse = FSharp.Data.JsonProvider< """[{
      "requestId": "ND-1234563341",
      "status": 0,
      "description": "Success.",
      "response": {
        "transactionId": "12315435221",
        "phoneNumber": "12345434789",
        "lineType": "Mobile",
        "carrier": "T-Mobile USA",
        "countryCode": "US",
        "trustScore": 930,
        "reasonCodes": [ "PT", "OU" ],
        "individual": {
          "firstName": "Jack",
          "lastName": "Brown",
          "addresses": [
            { "address": "123 Main Street", "extendedAddress": "Apt. 42B", "city": "San Francisco", "region": "CA", "postalCode": "12315" },
            { "address": "1234 Fleming Way", "extendedAddress": "", "city": "Port Royal", "region": "VA","postalCode": "12335" },
            { "address": "12323 Rocky Mountain St","city": "Reno","region": "NV","postalCode": "12306-1230" }
          ],
          "emailAddresses": [
            "test@test1.com",
            "test2@test2.com"
          ],
          "ssn": "1234567890",
          "dob": "1981-01-15"
        }
      }
    },{"requestId":"1230544e-0f2d-1230-8149-123658bad63d","status":1000,"description":"Parameter is invalid.","additionalInfo":"dob invalid.","timestamp": "2024-06-08T00:54:12.911"}]""", SampleIsList=true>

    type ProveIdentityResponseRoot = ProveIdentityResponse.Root

    /// Loads call verify response from Json
    let loadResponseFromString json =
         ProveIdentityResponse.Load (Serializer.Deserialize json) : ProveIdentityResponseRoot

    /// Calls Pre-Fill
    /// Returns: Strongly typed response (if successful), and full response as string
    let provePrefillIdentity license (consent: ConsentStatus) (phonenr:string) (dateOfBirth:DateTime) (ssn:string option) =
        async {

            match! ServiceCall.proveAuth license with
            | Error err ->
                return None, err
            | Ok auth ->

            match ServiceCall.tryParsePhone phonenr with
            | ValueNone ->
                return None, ServiceCall.refusedNumber phonenr
            | ValueSome phone ->
            let ssnnr = ssn |> Option.map(fun s -> s.Replace(" ", "").Replace("-", "").Replace("(","").Replace(")","").Replace("+",""))
            let req = ProveIdentityRequest.Root(Guid.NewGuid().ToString(), phone, (Some (ServiceCall.isoDate dateOfBirth)), ssnnr).JsonValue |> Serializer.Serialize
            let readStatus (body: string) = (loadResponseFromString body).Status |> ignore
            match! ServiceCall.makePostRequestWithHeaders (ServiceCall.checkAnswer readStatus) ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/identity/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", consent.AsHeaderValue()] with
            | fullResponse, None ->
                let resp = loadResponseFromString fullResponse
                let timestamped = ServiceCall.addTimeStamp fullResponse
                if resp.Status <> 0 && (resp.Status < 1000 || resp.Status >= 2000) then
                    return None, timestamped
                else
                    return Some resp, timestamped
            | err, Some r ->
                return None, err
        }
