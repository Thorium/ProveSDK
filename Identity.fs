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

    type ProveVerifyResponse = FSharp.Data.JsonProvider<"prove-verify-response.json", SampleIsList=true>

    /// Call Prove Verify.
    /// Returns: Is call done, result, error
    let callProveVerify license (phonenr:string) firstname lastname city zipCode stateCode (dateOfBirth:DateTime option) (ssn:string option) =
        async {
            let! auth, err = ServiceCall.proveAuth license
            if err <> "" then
                return false, None, err
            else

            let isOk, phone = phonenr.Replace(" ", "").Replace("-", "").Replace("+", "").Replace("(", "").Replace(")", "") |> Int64.TryParse
            if not isOk then
                return false, None, $"phoneNumber {phonenr} was not in correct format"
            else

            let req = ProveVerifyRequest.Root(Guid.NewGuid().ToString(), phone, firstname, lastname, None, city, Some true,
                        dateOfBirth |> Option.map(fun dob -> dob.ToString("yyyy-MM-dd")), stateCode, zipCode, None,
                        ssn |> Option.map(fun ssnNr -> ssnNr.Replace(" ", "").Replace("-", ""))).JsonValue |> Serializer.Serialize
            let! res = ServiceCall.makePostRequestWithHeaders ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/identity/verify/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", "optedIn"] // optedIn / optedOut / notCollected / unknown 
            match res with
            | r, None ->
                let parsedResp = ProveVerifyResponse.Load (Serializer.Deserialize r)
                match parsedResp.Response with
                | Some response -> return true, Some response, r
                | None -> return false, None, r
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

    type ProveIdentityResponse = FSharp.Data.JsonProvider< """[{
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

    /// Calls Pre-Fill
    let provePrefillIdentity license (phonenr:string) (dateOfBirth:DateTime) (ssn:string option) =
        async {

            let! auth, err = ServiceCall.proveAuth license
            if err <> "" then
                return None, err
            else

            let isOk, phone = phonenr.Replace(" ", "").Replace("-", "").Replace("+", "").Replace("(", "").Replace(")", "") |> Int64.TryParse
            if not isOk then
                return None, $"phoneNumber {phonenr} was not in correct format"
            else
            let ssnnr = ssn |> Option.map(fun s -> s.Replace(" ", "").Replace("-", "").Replace("(","").Replace(")","").Replace("+",""))
            let req = ProveIdentityRequest.Root(Guid.NewGuid().ToString(), phone, (Some (dateOfBirth.ToString("yyyy-MM-dd"))), ssnnr).JsonValue |> Serializer.Serialize
            let! res = ServiceCall.makePostRequestWithHeaders ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/identity/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", "optedIn"]
            match res with
            | r, None ->
                let parsedResp =
                    try
                        let resp = ProveIdentityResponse.Load (Serializer.Deserialize r)
                        if resp.Status <> 0 && (resp.Status < 1000 || resp.Status >= 2000) then
                            None, r
                        else
                            Some resp, ""
                    with
                    | e ->
                        None, (e.ToString())
                return parsedResp
            | err, Some r ->
                return None, err
        }
