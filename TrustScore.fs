namespace ProveSDK

open System
open ProveSDK
open FSharp.Data.JsonProvider

module TrustScore = 

    type internal ProveTrustscoreRequest = FSharp.Data.JsonProvider< """[{
         "requestId": "sample-request-id",
         "phoneNumber": "447111111111",
         "country": "GB",
         "details": false
    },{
         "requestId": "sample-request-id",
         "payfoneId": "123b29e-796d-123f-81e3-1232e5fd5123",
         "details": false
    }]""", SampleIsList=true>
    type internal ProveTrustscoreResponse = FSharp.Data.JsonProvider<"prove-trustscore-response.json", SampleIsList=true>

    /// Calls TrustScore. Returns a score, the number from 0-1000 or possible error.
    let callProveTrustScore license (phonenr:string) =
        async {
            let! auth, err = ServiceCall.proveAuth license
            if err <> "" then
                return ValueNone, err
            else

            let isOk, phone = phonenr.Replace(" ", "").Replace("-", "").Replace("+", "").Replace("(", "").Replace(")", "") |> Int64.TryParse
            if not isOk then
                return (ValueSome 0), $"phoneNumber {phonenr} was not in correct format"
            else
            let req = ProveTrustscoreRequest.Root(Guid.NewGuid().ToString(), (Some phone), None, false, None).JsonValue |> Serializer.Serialize
            let! res = ServiceCall.makePostRequestWithHeaders ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/trust/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", "optedIn"]
            match res with
            | r, None ->
                let parsedResp = ProveTrustscoreResponse.Load (Serializer.Deserialize r)
                match parsedResp.Response with
                | Some response -> return ValueSome(response.TrustScore), r
                | None -> return ValueNone, r
            | err, Some r ->
                if err.ToString().Contains "phoneNumber invalid" then
                    return (ValueSome 0), err
                else
                return ValueNone, err
        }
