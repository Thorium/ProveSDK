namespace ProveSDK

open System
open ProveSDK

module TrustScore = 

    type ProveTrustscoreRequest = FSharp.Data.JsonProvider< """[{
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
            let! auth = ServiceCall.proveAuth license

            let phone = phonenr.Replace(" ", "").Replace("-", "") |> Int64.Parse
            let req = ProveTrustscoreRequest.Root(Guid.NewGuid().ToString(), (Some phone), None, false, None).ToString()
            let! res = ServiceCall.makePostRequestWithHeaders ServiceCall.PostRequestTypes.ApplicationJson (license.Environment.AsLegacyApiUrl() + "/trust/v2") req ["Authorization", "Bearer " + auth; "Consent-Status", "optedIn"]
            match res with
            | r, None ->
                let parsedResp = ProveTrustscoreResponse.Parse r
                match parsedResp.Response with
                | Some response -> return Some(response.TrustScore), r
                | None -> return None, r
            | err, Some r ->
                if err.ToString().Contains "phoneNumber invalid" then
                    return (Some 0), err
                else
                return None, err
        }
