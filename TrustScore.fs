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
    type internal ProveTrustscoreResponse = FSharp.Data.JsonProvider<"""[{
  "status": 0,
  "requestId": "6af2c685-defc-4ef2-a780-3501a2a2874...",
  "response": {
    "indicators": [
      "LS",
      "ND"
    ],
    "troubleshootingId": "954ec607-6994fdd...",
    "trustScore": 700,
    "numberInfo": {
      "carrier": "O2",
      "countryCode": "GB",
      "lineType": "mobile"
    }
  }
},{
  "status": 0,
  "requestId": "6af2c685-defc-4ef2-a780-3501a2a2874...",
  "response": {
    "indicators": [
      "LS",
      "ND"
    ],
    "troubleshootingId": "954ec607-6994fdd...",
    "trustScore": 700,
    "details": {
      "riskLevel": "1",
      "simTimestamp": "2018-10-19T19:50:15Z"
    },
    "numberInfo": {
      "carrier": "O2",
      "countryCode": "GB",
      "lineType": "mobile"
    }
  }
},{
  "requestId": "string",
  "status": 0
},
{"requestId":"1234608b-fb05-4ca9-9249-123ed9c30123","status":0,"description":"Success.","response":{"transactionId":"12313023123","payfoneAlias":"ABC123","phoneNumber":"12312001666","lineType":"Mobile","carrier":"T-Mobile USA","countryCode":"US","isBaselined":true,"trustScore":925,"reasonCodes":["ND"]}},
{"requestId":"123c1ed8-8ff3-4c9f-a439-123c878a5fc8","status":1002,"description":"Subscriber is not found on the required whitelist."},
{    "requestId": "123c9ad1-3454-4803-b57a-123b2baa2397",    "status": 0,    "description": "Success.",    "response": {      "transactionId": "12332767001",      "payfoneAlias": "123456789ABCXYZA1CD98C1124938D9C40MEK29NJ9C9P7D73E201644341D388E193D4289DE14D813F6G358CA032C517601A5167F921B0F2C5B039123",      "phoneNumber": "12327166123",      "lineType": "Mobile",      "carrier": "T-Mobile USA",      "countryCode": "US",      "isBaselined": true,      "trustScore": 1000    }, "timestamp": "2024-06-08T00:54:12.911" }

]""", SampleIsList=true>

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
